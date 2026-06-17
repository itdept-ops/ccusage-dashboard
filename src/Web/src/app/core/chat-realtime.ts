import { Injectable, computed, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  JsonHubProtocol,
  LogLevel,
} from '@microsoft/signalr';

import { Api } from './api';
import { AuthService } from './auth';
import { ChatChannelDto, ChatMessageDto, NotificationDto } from './models';
import { firstValueFrom } from 'rxjs';

/** SignalR hub endpoint (JWT is appended by the client as ?access_token=...). */
const HUB_URL = '/api/hubs/chat';

/** Coarse connection state surfaced to the UI for the reconnecting indicator. */
export type ChatConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** One person currently typing in a channel (keyed by lower-cased email). */
export interface TypingUser {
  email: string;
  name: string;
}

/**
 * The single source of truth for live chat + notification state. Owns the SignalR connection
 * lifecycle (start/stop, JWT via accessTokenFactory from {@link AuthService}, automatic reconnect),
 * wires every hub client-event into a signal, exposes the hub server-method calls, and keeps a
 * per-channel message/typing/unread cache that the chat page renders from.
 *
 * Phase 2a-CORE: this is the shared foundation. The chat page component (next phase) injects this
 * service and reads its signals; it never touches the HubConnection directly.
 */
@Injectable({ providedIn: 'root' })
export class ChatRealtime {
  private api = inject(Api);
  private auth = inject(AuthService);

  private connection: HubConnection | null = null;

  /** Per-(channel,user) safety timers that auto-clear a stuck "is typing…" if no StopTyping arrives. */
  private readonly typingTimers = new Map<string, ReturnType<typeof setTimeout>>();
  /** How long a remote typing flag survives without a refresh before we auto-clear it. */
  private static readonly TYPING_SAFETY_MS = 6000;

  // ---- connection state ----
  private readonly _connection = signal<ChatConnectionState>('disconnected');
  /** Coarse connection state for the reconnecting indicator. */
  readonly connectionState = this._connection.asReadonly();
  /** True while live (fully connected). */
  readonly isConnected = computed(() => this._connection() === 'connected');
  /** True while connecting or reconnecting (show a small "reconnecting" indicator). */
  readonly isReconnecting = computed(() =>
    this._connection() === 'reconnecting' || this._connection() === 'connecting',
  );

  // ---- channels ----
  private readonly _channels = signal<ChatChannelDto[]>([]);
  /** All visible channels + DMs, ordered by most-recent activity (newest last-message first). */
  readonly channels = computed(() => {
    const list = [...this._channels()];
    return list.sort((a, b) => activityTime(b) - activityTime(a));
  });

  // ---- messages, keyed by channelId (oldest-first within each channel) ----
  private readonly _messages = signal<Record<number, ChatMessageDto[]>>({});
  /** All loaded messages for a channel, oldest-first (newest at the bottom). */
  messagesFor(channelId: number): ChatMessageDto[] {
    return this._messages()[channelId] ?? [];
  }
  /** Reactive read of the whole message map (for the chat page to derive a per-channel computed). */
  readonly messages = this._messages.asReadonly();

  // ---- typing, keyed by channelId (excludes the caller) ----
  private readonly _typing = signal<Record<number, TypingUser[]>>({});
  /** Reactive read of who is typing, keyed by channelId. */
  readonly typing = this._typing.asReadonly();
  typingFor(channelId: number): TypingUser[] {
    return this._typing()[channelId] ?? [];
  }

  // ---- per-channel unread MESSAGE counts (mirrors UnreadChanged) ----
  private readonly _unread = signal<Record<number, number>>({});
  readonly unread = this._unread.asReadonly();
  unreadFor(channelId: number): number {
    return this._unread()[channelId] ?? 0;
  }
  /** Total unread messages across all channels. */
  readonly totalUnreadMessages = computed(() =>
    Object.values(this._unread()).reduce((a, b) => a + b, 0),
  );

  // ---- notifications (inbox) — captured now; bell UI is Phase 2b ----
  private readonly _notifications = signal<NotificationDto[]>([]);
  readonly notifications = this._notifications.asReadonly();

  private readonly _inboxUnread = signal(0);
  /** Global unread NOTIFICATION count (mirrors InboxUnreadChanged). */
  readonly inboxUnread = this._inboxUnread.asReadonly();

  // =========================================================================
  // Connection lifecycle
  // =========================================================================

  /**
   * Start the hub connection (idempotent while connected). Pulls the JWT lazily from
   * {@link AuthService} on every (re)negotiation so a refreshed token is always used. No-op if a
   * connection already exists or the user is unauthenticated. After a {@link stop} (e.g. logout)
   * this.connection is null, so the next call builds a FRESH connection bound to the current user's
   * token — the prior user's connection is never reused.
   */
  async start(): Promise<void> {
    if (this.connection || !this.auth.isAuthenticated()) return;

    const connection = new HubConnectionBuilder()
      .withUrl(HUB_URL, {
        accessTokenFactory: () => this.auth.token ?? '',
      })
      .withAutomaticReconnect()
      .withHubProtocol(new JsonHubProtocol())
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection = connection;
    this.registerHandlers(connection);

    connection.onreconnecting(() => this._connection.set('reconnecting'));
    connection.onreconnected(() => {
      this._connection.set('connected');
      // After a reconnect the server replays nothing — re-pull channel list to resync.
      void this.refreshChannels();
    });
    connection.onclose(() => this._connection.set('disconnected'));

    this._connection.set('connecting');
    try {
      await connection.start();
      this._connection.set('connected');
      await this.refreshChannels();
    } catch {
      this._connection.set('disconnected');
      this.connection = null;
    }
  }

  /**
   * Fully tear down the connection and clear all cached state (call on logout, including the forced
   * 401/403 logout path). After this returns this.connection is null so a subsequent {@link start}
   * builds a fresh connection with the next user's token — the prior user's connection/token is never
   * reused, preventing cross-user identity/data leakage.
   */
  async stop(): Promise<void> {
    const c = this.connection;
    this.connection = null;
    this._connection.set('disconnected');
    if (c) {
      try { await c.stop(); } catch { /* ignore */ }
    }
    for (const t of this.typingTimers.values()) clearTimeout(t);
    this.typingTimers.clear();
    this._channels.set([]);
    this._messages.set({});
    this._typing.set({});
    this._unread.set({});
    this._notifications.set([]);
    this._inboxUnread.set(0);
  }

  // =========================================================================
  // Hub client-event handlers -> signals
  // =========================================================================

  private registerHandlers(c: HubConnection): void {
    c.on('ReceiveMessage', (msg: ChatMessageDto) => this.onReceiveMessage(msg));
    c.on('MessageEdited', (msg: ChatMessageDto) => this.onMessageEdited(msg));
    c.on('MessageDeleted', (channelId: number, messageId: number) => this.onMessageDeleted(channelId, messageId));
    c.on('TypingChanged', (channelId: number, userEmail: string, userName: string, isTyping: boolean) =>
      this.onTypingChanged(channelId, userEmail, userName, isTyping));
    c.on('ReceiveNotification', (n: NotificationDto) => this.onReceiveNotification(n));
    c.on('UnreadChanged', (channelId: number, unreadCount: number) => this.onUnreadChanged(channelId, unreadCount));
    c.on('InboxUnreadChanged', (totalUnread: number) => this._inboxUnread.set(totalUnread));
    c.on('ChannelAdded', (channel: ChatChannelDto) => this.onChannelAdded(channel));
  }

  private onReceiveMessage(msg: ChatMessageDto): void {
    this.appendMessage(msg);
    this.patchChannel(msg.channelId, ch => ({ ...ch, lastMessage: msg }));
  }

  private onMessageEdited(msg: ChatMessageDto): void {
    this.replaceMessage(msg);
    this.patchChannel(msg.channelId, ch =>
      ch.lastMessage?.id === msg.id ? { ...ch, lastMessage: msg } : ch);
  }

  private onMessageDeleted(channelId: number, messageId: number): void {
    this._messages.update(map => {
      const list = map[channelId];
      if (!list) return map;
      return {
        ...map,
        [channelId]: list.map(m => m.id === messageId ? { ...m, deleted: true, body: null } : m),
      };
    });
  }

  private onTypingChanged(channelId: number, userEmail: string, userName: string, isTyping: boolean): void {
    const me = this.auth.session()?.email?.toLowerCase();
    const key = userEmail.toLowerCase();
    if (me && key === me) return; // never show yourself typing
    this.setTyping(channelId, userEmail, userName, isTyping);

    // Safety net: a dropped StopTyping (e.g. the sender navigated away mid-typing) would otherwise
    // leave a stuck "is typing…". Auto-clear ~6s after the last true, refreshing the timer on each true.
    const timerKey = `${channelId}|${key}`;
    const existing = this.typingTimers.get(timerKey);
    if (existing) clearTimeout(existing);
    if (isTyping) {
      this.typingTimers.set(timerKey, setTimeout(() => {
        this.typingTimers.delete(timerKey);
        this.setTyping(channelId, userEmail, userName, false);
      }, ChatRealtime.TYPING_SAFETY_MS));
    } else {
      this.typingTimers.delete(timerKey);
    }
  }

  /** Add/remove a single (channel,user) typing entry, keyed by lower-cased email. */
  private setTyping(channelId: number, userEmail: string, userName: string, isTyping: boolean): void {
    const key = userEmail.toLowerCase();
    this._typing.update(map => {
      const current = (map[channelId] ?? []).filter(u => u.email.toLowerCase() !== key);
      const next = isTyping ? [...current, { email: userEmail, name: userName }] : current;
      return { ...map, [channelId]: next };
    });
  }

  private onReceiveNotification(n: NotificationDto): void {
    this._notifications.update(list => [n, ...list.filter(x => x.id !== n.id)]);
  }

  private onUnreadChanged(channelId: number, unreadCount: number): void {
    this._unread.update(map => ({ ...map, [channelId]: unreadCount }));
    this.patchChannel(channelId, ch => ({ ...ch, unreadCount }));
  }

  private onChannelAdded(channel: ChatChannelDto): void {
    this._channels.update(list =>
      list.some(c => c.id === channel.id) ? list : [...list, channel]);
    this._unread.update(map => ({ ...map, [channel.id]: channel.unreadCount }));
    // Critical: join so the live connection starts receiving this channel/DM's broadcasts.
    void this.joinChannel(channel.id);
  }

  // =========================================================================
  // Hub server-method calls
  // =========================================================================

  /** Send a message over the hub. mentionedEmails is null when there are no @mentions. */
  async sendMessage(channelId: number, body: string, mentionedEmails: string[] | null = null): Promise<void> {
    await this.invoke('SendMessage', channelId, body, mentionedEmails);
  }

  async startTyping(channelId: number): Promise<void> {
    await this.invoke('StartTyping', channelId);
  }

  async stopTyping(channelId: number): Promise<void> {
    await this.invoke('StopTyping', channelId);
  }

  /**
   * Mark a channel read up to a message on the server. The local unread badge is cleared by the
   * caller (the user action in the chat page) via {@link clearUnreadLocal}, not here — so this method
   * does not mutate unread state that an effect may also read.
   */
  async markRead(channelId: number, messageId: number): Promise<void> {
    await this.invoke('MarkRead', channelId, messageId);
  }

  /** Optimistically clear the local unread badge for a channel (call from the originating user action). */
  clearUnreadLocal(channelId: number): void {
    this.onUnreadChanged(channelId, 0);
  }

  /** Join a channel group so the live connection receives its broadcasts. */
  async joinChannel(channelId: number): Promise<void> {
    await this.invoke('JoinChannel', channelId);
  }

  /** Safe invoke: no-ops (does not throw) when the connection isn't live. */
  private async invoke(method: string, ...args: unknown[]): Promise<void> {
    const c = this.connection;
    if (!c || c.state !== HubConnectionState.Connected) return;
    try {
      await c.invoke(method, ...args);
    } catch {
      /* swallow — the UI surfaces connection state separately */
    }
  }

  // =========================================================================
  // REST-backed helpers (channel list, history) — fold results into the cache
  // =========================================================================

  /** (Re)load the channel list and join every channel so live broadcasts arrive. */
  async refreshChannels(): Promise<ChatChannelDto[]> {
    const list = await firstValueFrom(this.api.chatChannels());
    this._channels.set(list);
    this._unread.update(map => {
      const next = { ...map };
      for (const ch of list) next[ch.id] = ch.unreadCount;
      return next;
    });
    // Join each so the connection receives broadcasts for already-known channels.
    for (const ch of list) void this.joinChannel(ch.id);
    return list;
  }

  /**
   * Create a channel, fold it into the cache, and join it. ChannelAdded may also arrive over the
   * hub; both paths dedupe by id.
   */
  async createChannel(name: string, memberEmails: string[], opts: { topic?: string; isPrivate?: boolean } = {}): Promise<ChatChannelDto> {
    const ch = await firstValueFrom(this.api.createChannel({
      name, memberEmails, topic: opts.topic, isPrivate: opts.isPrivate ?? false,
    }));
    this.onChannelAdded(ch);
    return ch;
  }

  /** Open (or fetch the existing) DM with a user; folds it into the cache and joins it. */
  async openDirect(userEmail: string): Promise<ChatChannelDto> {
    const ch = await firstValueFrom(this.api.openDirect(userEmail));
    this.onChannelAdded(ch);
    return ch;
  }

  /**
   * Load a page of history into the cache. With no `before`, replaces the channel's message list
   * (initial open); with `before`, prepends older messages (scroll-to-top). Server returns
   * newest-first; we store oldest-first. Returns the number of messages fetched (0 = no more).
   */
  async loadHistory(channelId: number, before?: number, limit = 50): Promise<number> {
    const page = await firstValueFrom(this.api.chatMessages(channelId, { before, limit }));
    const ascending = [...page].reverse(); // newest-first -> oldest-first
    this._messages.update(map => {
      const existing = map[channelId] ?? [];
      const merged = before == null
        ? mergeById(ascending, existing)          // initial load (still dedupe against any live msgs)
        : mergeById(ascending, existing);         // prepend older; mergeById keeps order + dedupes
      return { ...map, [channelId]: merged };
    });
    return page.length;
  }

  // =========================================================================
  // private cache mutators
  // =========================================================================

  private appendMessage(msg: ChatMessageDto): void {
    this._messages.update(map => {
      const list = map[msg.channelId] ?? [];
      if (list.some(m => m.id === msg.id)) return map; // dedupe (e.g. own echo)
      return { ...map, [msg.channelId]: [...list, msg] };
    });
  }

  private replaceMessage(msg: ChatMessageDto): void {
    this._messages.update(map => {
      const list = map[msg.channelId];
      if (!list) return map;
      return { ...map, [msg.channelId]: list.map(m => m.id === msg.id ? msg : m) };
    });
  }

  private patchChannel(channelId: number, fn: (ch: ChatChannelDto) => ChatChannelDto): void {
    this._channels.update(list => list.map(ch => ch.id === channelId ? fn(ch) : ch));
  }
}

/** Sort key for channel ordering: last-message time, else 0 (newest activity first). */
function activityTime(ch: ChatChannelDto): number {
  const t = ch.lastMessage?.createdUtc;
  return t ? new Date(t).getTime() : 0;
}

/** Merge two oldest-first message lists by id, preserving ascending order and deduping. */
function mergeById(a: ChatMessageDto[], b: ChatMessageDto[]): ChatMessageDto[] {
  const byId = new Map<number, ChatMessageDto>();
  for (const m of a) byId.set(m.id, m);
  for (const m of b) byId.set(m.id, m);
  return [...byId.values()].sort((x, y) => x.id - y.id);
}
