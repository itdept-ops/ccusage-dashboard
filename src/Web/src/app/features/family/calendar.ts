import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import {
  CalendarEvent, CalendarEventInput, CalendarRecurrence, CalendarStatus, HouseholdMember, ScheduleAiEvent,
} from '../../core/models';
import { FamilyConfirmDialog, ConfirmData } from './confirm-dialog';
import { EventEditorDialog, EventEditorData, EventEditorResult } from './event-editor-dialog';
import { FindTimeData, FindTimeDialog, FindTimeResultSlot } from './find-time-dialog';

/* The Google OAuth code client (loaded via the GIS script in index.html). */
declare const google: any;

/** An event positioned within a single day of the week grid. */
interface DayEvent {
  ev: CalendarEvent;
  /** "h:mm a" local start (timed) or "All day". */
  timeLabel: string;
}

/** One AI-proposed event the family member can confirm/edit before it's created on their calendar. */
interface ProposedEvent {
  ai: ScheduleAiEvent;
  /** A friendly "Tue, Jun 23 · 4:00 – 5:00 PM" (or "All day") when-label in the viewer's local zone. */
  whenLabel: string;
  /** A short repeat label ("Every week") or '' for a one-off — drives the recurrence chip. */
  repeatLabel: string;
  /** True while THIS card's "Add to calendar" is creating the event. */
  saving: boolean;
}

/** One column of the week view: its date + the events that fall on it (all-day first, then by start). */
interface DayColumn {
  date: Date;
  iso: string;            // "YYYY-MM-DD" local
  weekdayLabel: string;   // "Mon"
  dayNum: number;         // 1..31
  isToday: boolean;
  events: DayEvent[];
}

type ViewMode = 'week' | 'agenda';

/**
 * Family Hub F6 — the family calendar. Until the caller connects their Google Calendar we show a warm
 * "Connect" panel (or a gentle "not set up on the server yet" note when the server has no OAuth secret).
 * Connecting uses Google Identity Services' OAuth CODE client (offline access, minimal calendar.events
 * scope); the one-time code is POSTed to the server, which stores an encrypted refresh token — the secret
 * and token never touch the client.
 *
 * Once connected: a week grid + an agenda list of the caller's OWN events for the visible range, with
 * prev/next/today navigation and a today marker. Create an event (title, date, time or all-day, location,
 * notes); click one to edit or delete. Mobile-friendly; reuses the family design tokens. No other-person
 * identity is ever rendered.
 */
@Component({
  selector: 'app-family-calendar',
  imports: [
    FormsModule, RouterLink, MatIconModule, MatButtonModule, MatButtonToggleModule, MatTooltipModule,
    MatProgressSpinnerModule, MatFormFieldModule, MatInputModule, MatSnackBarModule,
  ],
  templateUrl: './calendar.html',
  styleUrls: ['./family.scss', './calendar.scss'],
})
export class FamilyCalendar implements OnDestroy {
  private api = inject(Api);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  /** Auto-poll cadence while connected + the tab is visible. */
  private static readonly POLL_MS = 60_000;

  /** null while the initial status check is in flight. */
  readonly status = signal<CalendarStatus | null>(null);
  readonly loadingStatus = signal(true);
  readonly statusError = signal(false);
  readonly connecting = signal(false);

  readonly events = signal<CalendarEvent[]>([]);
  readonly loadingEvents = signal(false);
  readonly eventsError = signal(false);

  /** Household members for the Find-a-time picker (avatar + name only; never an email). */
  readonly members = signal<HouseholdMember[]>([]);

  readonly view = signal<ViewMode>('week');

  /** The Monday (local midnight) that anchors the visible week. */
  readonly weekStart = signal<Date>(this.mondayOf(new Date()));

  /** Epoch ms of the last successful events fetch (null = never). Drives the "updated Xm ago" hint. */
  readonly lastUpdated = signal<number | null>(null);
  /** A ticking clock (epoch ms) so the relative "updated" label re-renders without a new fetch. */
  private readonly nowTick = signal<number>(Date.now());

  // ---- Schedule with AI ----
  /** The free-text scheduling box ("soccer every Tuesday at 4pm"). */
  readonly aiText = signal('');
  readonly aiBusy = signal(false);
  /** A friendly status line for the AI box (aria-live), e.g. an error or "couldn't find an event". */
  readonly aiStatus = signal('');
  /** The AI-proposed events awaiting the user's confirmation. */
  readonly proposals = signal<ProposedEvent[]>([]);

  readonly connected = computed(() => this.status()?.connected === true);
  readonly configured = computed(() => this.status()?.configured !== false);

  /** A tiny "updated just now / Xm ago" hint for the refresh control. '' until the first load. */
  readonly updatedLabel = computed<string>(() => {
    const at = this.lastUpdated();
    if (at === null) return '';
    const secs = Math.max(0, Math.round((this.nowTick() - at) / 1000));
    if (secs < 45) return 'updated just now';
    const mins = Math.round(secs / 60);
    if (mins < 60) return `updated ${mins}m ago`;
    const hrs = Math.round(mins / 60);
    return `updated ${hrs}h ago`;
  });

  /** The seven day-columns of the visible week with their events placed. */
  readonly days = computed<DayColumn[]>(() => {
    const start = this.weekStart();
    const todayIso = this.toLocalDate(new Date());
    const byDay = new Map<string, DayEvent[]>();
    for (const ev of this.events()) {
      for (const iso of this.spannedDays(ev)) {
        const arr = byDay.get(iso) ?? [];
        arr.push({ ev, timeLabel: this.timeLabel(ev) });
        byDay.set(iso, arr);
      }
    }
    const cols: DayColumn[] = [];
    for (let i = 0; i < 7; i++) {
      const date = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
      const iso = this.toLocalDate(date);
      const evs = (byDay.get(iso) ?? []).sort(this.compareDayEvents);
      cols.push({
        date, iso,
        weekdayLabel: date.toLocaleDateString(undefined, { weekday: 'short' }),
        dayNum: date.getDate(),
        isToday: iso === todayIso,
        events: evs,
      });
    }
    return cols;
  });

  /** The week's events flattened + sorted for the agenda list, grouped by day. */
  readonly agendaDays = computed<DayColumn[]>(() => this.days().filter(d => d.events.length > 0));

  /** A friendly "Jun 16 – 22, 2026" label for the visible week. */
  readonly rangeLabel = computed<string>(() => {
    const start = this.weekStart();
    const end = new Date(start.getFullYear(), start.getMonth(), start.getDate() + 6);
    const sameMonth = start.getMonth() === end.getMonth();
    const sameYear = start.getFullYear() === end.getFullYear();
    const fmtStart = start.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    const fmtEnd = end.toLocaleDateString(undefined,
      sameMonth ? { day: 'numeric', year: 'numeric' } : { month: 'short', day: 'numeric', year: 'numeric' });
    return sameYear ? `${fmtStart} – ${fmtEnd}` : `${fmtStart}, ${start.getFullYear()} – ${fmtEnd}`;
  });

  /** The auto-poll interval handle (browser timer id), or null when not polling. */
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  /** Re-renders the "updated Xm ago" label every ~30s without re-fetching. */
  private tickTimer: ReturnType<typeof setInterval> | null = null;
  /** Bound visibilitychange handler so we can detach it on destroy. */
  private readonly onVisibility = (): void => {
    if (document.visibilityState === 'visible') {
      this.startPolling();
      // Catch up immediately on becoming visible again (skipped while hidden).
      if (this.connected()) void this.loadEvents({ silent: true });
    } else {
      this.stopPolling();
    }
  };

  constructor() {
    document.addEventListener('visibilitychange', this.onVisibility);
    // Keep the relative "updated" label fresh while mounted.
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 30_000);
    void this.loadStatus();
  }

  ngOnDestroy(): void {
    document.removeEventListener('visibilitychange', this.onVisibility);
    this.stopPolling();
    if (this.tickTimer !== null) { clearInterval(this.tickTimer); this.tickTimer = null; }
  }

  // ---- Auto-poll (every ~60s while connected AND the tab is visible) ----

  /** Begin polling the visible week if connected + visible + not already running. */
  private startPolling(): void {
    if (this.pollTimer !== null) return;
    if (!this.connected() || document.visibilityState !== 'visible') return;
    this.pollTimer = setInterval(() => {
      if (this.connected() && document.visibilityState === 'visible') {
        void this.loadEvents({ silent: true });
      }
    }, FamilyCalendar.POLL_MS);
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) { clearInterval(this.pollTimer); this.pollTimer = null; }
  }

  /** Manual refresh: re-fetch the visible week now (with the toolbar spinner). */
  async refresh(): Promise<void> {
    if (!this.connected()) return;
    await this.loadEvents();
  }

  // ---- Status + connection lifecycle ----

  private async loadStatus(): Promise<void> {
    this.loadingStatus.set(true);
    this.statusError.set(false);
    try {
      const s = await firstValueFrom(this.api.calendarStatus());
      this.status.set(s);
      if (s.connected) {
        await this.loadEvents();
        void this.loadMembers();
      }
    } catch {
      this.statusError.set(true);
    } finally {
      this.loadingStatus.set(false);
    }
  }

  /** Best-effort: load household members so Find-a-time can offer them as chips. A failure just hides them. */
  private async loadMembers(): Promise<void> {
    try {
      const household = await firstValueFrom(this.api.getHousehold());
      this.members.set(household.members ?? []);
    } catch {
      this.members.set([]);
    }
  }

  /** Run the GIS OAuth code flow, then exchange the code on the server. */
  async connect(): Promise<void> {
    if (this.connecting()) return;
    this.connecting.set(true);
    try {
      const cfg = await firstValueFrom(this.auth.config());
      if (!cfg.googleClientId) {
        this.snack.open('Google sign-in is not configured on this server.', 'OK', { duration: 4000 });
        return;
      }
      await this.waitForGis();
      const code = await this.requestAuthCode(cfg.googleClientId);
      await firstValueFrom(this.api.connectCalendar(code, 'postmessage'));
      this.status.set({ configured: true, connected: true });
      await this.loadEvents();
      void this.loadMembers();
      this.snack.open('Calendar connected.', undefined, { duration: 2000 });
    } catch (e) {
      // A user-cancelled popup is not an error worth shouting about.
      const msg = (e as Error)?.message;
      if (msg !== 'cancelled') {
        this.snack.open(this.messageOf(e, "Couldn't connect your Google Calendar. Please try again."),
          'OK', { duration: 4500 });
      }
    } finally {
      this.connecting.set(false);
    }
  }

  /** Use the GIS code client to obtain a one-time auth code (offline access, calendar.events scope). */
  private requestAuthCode(clientId: string): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      const client = google.accounts.oauth2.initCodeClient({
        client_id: clientId,
        scope: 'https://www.googleapis.com/auth/calendar.events',
        ux_mode: 'popup',
        access_type: 'offline',
        prompt: 'consent',
        callback: (resp: { code?: string; error?: string }) => {
          if (resp?.code) resolve(resp.code);
          else reject(new Error(resp?.error || 'no_code'));
        },
        error_callback: (err: { type?: string }) => {
          reject(new Error(err?.type === 'popup_closed' ? 'cancelled' : (err?.type || 'oauth_failed')));
        },
      });
      client.requestCode();
    });
  }

  async disconnect(): Promise<void> {
    const ok = await this.confirm({
      title: 'Disconnect Google Calendar?',
      message: 'We\'ll forget your calendar connection. Your events stay in Google — this just stops showing them here.',
      confirmLabel: 'Disconnect',
      destructive: true,
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.disconnectCalendar());
      this.status.set({ configured: true, connected: false });
      this.events.set([]);
      this.proposals.set([]);
      this.lastUpdated.set(null);
      this.stopPolling();
      this.snack.open('Calendar disconnected.', undefined, { duration: 2000 });
    } catch {
      this.snack.open("Couldn't disconnect just now. Please try again.", 'OK', { duration: 4000 });
    }
  }

  private waitForGis(): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      let tries = 0;
      const timer = setInterval(() => {
        if ((window as unknown as { google?: any }).google?.accounts?.oauth2) {
          clearInterval(timer);
          resolve();
        } else if (++tries > 60) {
          clearInterval(timer);
          reject(new Error('Google Identity Services failed to load'));
        }
      }, 100);
    });
  }

  // ---- Events ----

  /**
   * Fetch the visible week's events. A `silent` load (auto-poll / visibility catch-up) skips the toolbar
   * spinner and leaves the current events on screen if the fetch fails, so a transient blip never blanks the
   * planner. A manual/navigation load shows the spinner + surfaces an error.
   */
  private async loadEvents(opts: { silent?: boolean } = {}): Promise<void> {
    const silent = opts.silent === true;
    if (!silent) this.loadingEvents.set(true);
    this.eventsError.set(false);
    const start = this.weekStart();
    const end = new Date(start.getFullYear(), start.getMonth(), start.getDate() + 7);
    try {
      const list = await firstValueFrom(this.api.calendarEvents(start.toISOString(), end.toISOString()));
      this.events.set(list);
      this.lastUpdated.set(Date.now());
      this.nowTick.set(Date.now());
      // First successful load kicks off the background poll.
      this.startPolling();
    } catch {
      if (!silent) {
        this.eventsError.set(true);
        this.events.set([]);
      }
      // A silent failure leaves the last-good week on screen; the next tick retries.
    } finally {
      if (!silent) this.loadingEvents.set(false);
    }
  }

  prevWeek(): void {
    this.shiftWeek(-7);
  }

  nextWeek(): void {
    this.shiftWeek(7);
  }

  today(): void {
    this.weekStart.set(this.mondayOf(new Date()));
    void this.loadEvents();
  }

  private shiftWeek(days: number): void {
    const s = this.weekStart();
    this.weekStart.set(new Date(s.getFullYear(), s.getMonth(), s.getDate() + days));
    void this.loadEvents();
  }

  setView(v: ViewMode): void {
    this.view.set(v);
  }

  // ---- Schedule with AI ----

  /**
   * Send the free-text scheduling request to Gemini and show the proposed event(s) as confirm cards. Creates
   * NOTHING — each card has its own "Add to calendar". Degrades gracefully: a 503 (AI unavailable / not
   * configured) or any error shows a friendly aria-live line; an empty result says so. Not-connected is
   * guarded by only rendering the box when connected.
   */
  async scheduleWithAi(): Promise<void> {
    const text = this.aiText().trim();
    if (text.length === 0 || this.aiBusy()) return;
    this.aiBusy.set(true);
    this.aiStatus.set('Reading your request…');
    this.proposals.set([]);
    try {
      const result = await firstValueFrom(this.api.scheduleAiEvents(text));
      const proposed = (result.events ?? []).map(ai => this.toProposed(ai));
      this.proposals.set(proposed);
      if (proposed.length === 0) {
        this.aiStatus.set(
          result.notes?.trim() || "I couldn't find an event in that. Try \"dentist next Friday at 9am\".");
      } else {
        const n = proposed.length;
        this.aiStatus.set(
          (result.notes?.trim() ? result.notes!.trim() + ' ' : '') +
          `Review ${n === 1 ? 'the event' : `these ${n} events`} below, then add ${n === 1 ? 'it' : 'them'} to your calendar.`);
      }
    } catch (e) {
      const status = (e as { status?: number })?.status;
      this.aiStatus.set(status === 503
        ? "AI scheduling isn't available right now. You can add the event yourself with the Event button."
        : this.messageOf(e, "I couldn't reach the AI just now. Please try again, or add the event manually."));
    } finally {
      this.aiBusy.set(false);
    }
  }

  /** Add one AI-proposed event to the calendar (passing its recurrence). Then drop the card. */
  async addProposal(p: ProposedEvent): Promise<void> {
    if (p.saving) return;
    this.setProposalSaving(p, true);
    try {
      await firstValueFrom(this.api.createEvent(this.inputFromProposal(p.ai)));
      this.dismissProposal(p);
      this.snack.open('Added to your calendar.', undefined, { duration: 2000 });
      await this.loadEvents();
    } catch (e) {
      this.setProposalSaving(p, false);
      this.snack.open(this.messageOf(e, "Couldn't add that event. Please try again."), 'OK', { duration: 4000 });
    }
  }

  /** Open the full editor prefilled from a proposed event so the user can tweak it before creating. */
  async editProposal(p: ProposedEvent): Promise<void> {
    const ai = p.ai;
    const result = await this.openEditor({
      event: null,
      seedTitle: ai.title,
      seedStartUtc: ai.startUtc,
      seedEndUtc: ai.endUtc,
      seedAllDay: ai.allDay,
      seedLocation: ai.location,
      seedDescription: ai.description,
      seedRecurrence: ai.recurrence,
    });
    if (result?.kind === 'save') {
      try {
        await firstValueFrom(this.api.createEvent(result.input));
        this.dismissProposal(p);
        await this.loadEvents();
      } catch (e) {
        this.snack.open(this.messageOf(e, "Couldn't save that event. Please try again."), 'OK', { duration: 4000 });
      }
    }
  }

  /** Discard a proposed event card without creating it. */
  dismissProposal(p: ProposedEvent): void {
    this.proposals.set(this.proposals().filter(x => x !== p));
  }

  /** Clear the AI box + any pending proposals. */
  clearAi(): void {
    this.aiText.set('');
    this.aiStatus.set('');
    this.proposals.set([]);
  }

  private setProposalSaving(p: ProposedEvent, saving: boolean): void {
    this.proposals.set(this.proposals().map(x => x === p ? { ...x, saving } : x));
  }

  /** Build a confirm-card view-model from a raw AI-proposed event. */
  private toProposed(ai: ScheduleAiEvent): ProposedEvent {
    return {
      ai,
      whenLabel: this.proposalWhenLabel(ai),
      repeatLabel: this.recurrenceLabel(ai.recurrence),
      saving: false,
    };
  }

  /** Map a proposed event to the create payload (carrying recurrence through to POST /events). */
  private inputFromProposal(ai: ScheduleAiEvent): CalendarEventInput {
    return {
      title: ai.title,
      startUtc: ai.startUtc,
      endUtc: ai.endUtc,
      allDay: ai.allDay,
      location: ai.location,
      description: ai.description,
      recurrence: ai.recurrence,
    };
  }

  /** "Tue, Jun 23 · 4:00 – 5:00 PM" (timed) or "Tue, Jun 23 · All day" in the viewer's local zone. */
  private proposalWhenLabel(ai: ScheduleAiEvent): string {
    const start = new Date(ai.startUtc);
    if (Number.isNaN(start.getTime())) return '';
    const day = start.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    if (ai.allDay) return `${day} · All day`;
    const from = start.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    const endDate = ai.endUtc ? new Date(ai.endUtc) : null;
    const to = endDate && !Number.isNaN(endDate.getTime())
      ? endDate.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' }) : null;
    return to ? `${day} · ${from} – ${to}` : `${day} · ${from}`;
  }

  /** A short, friendly repeat label for a recurrence chip ('' for a one-off). */
  recurrenceLabel(r: CalendarRecurrence | undefined): string {
    switch (r) {
      case 'daily': return 'Every day';
      case 'weekly': return 'Every week';
      case 'weekdays': return 'Weekdays';
      case 'monthly': return 'Every month';
      default: return '';
    }
  }

  /** Open the editor to create a new event, optionally seeded to a clicked day. */
  async create(seedDate?: string): Promise<void> {
    const result = await this.openEditor({ event: null, seedDate });
    if (result?.kind === 'save') {
      try {
        await firstValueFrom(this.api.createEvent(result.input));
        await this.loadEvents();
      } catch (e) {
        this.snack.open(this.messageOf(e, "Couldn't save that event. Please try again."), 'OK', { duration: 4000 });
      }
    }
  }

  /**
   * Open the Find-a-time tool. When the caller picks a candidate slot, flow straight into the event editor
   * prefilled to that slot so they can name + create it. Degrades cleanly when no members/calendars connect.
   */
  async openFindTime(): Promise<void> {
    const ref = this.dialog.open<FindTimeDialog, FindTimeData, FindTimeResultSlot>(
      FindTimeDialog, { data: { members: this.members() }, width: '520px', maxWidth: '94vw', autoFocus: false });
    const slot = await firstValueFrom(ref.afterClosed());
    if (!slot) return;
    await this.createFromSlot(slot);
  }

  /** Open the event editor seeded to a Find-a-time slot, then create it on save. */
  private async createFromSlot(slot: FindTimeResultSlot): Promise<void> {
    const result = await this.openEditor({
      event: null, seedStartUtc: slot.startUtc, seedEndUtc: slot.endUtc,
    });
    if (result?.kind === 'save') {
      try {
        await firstValueFrom(this.api.createEvent(result.input));
        await this.loadEvents();
      } catch (e) {
        this.snack.open(this.messageOf(e, "Couldn't save that event. Please try again."), 'OK', { duration: 4000 });
      }
    }
  }

  /** Click an event to edit (or delete from within the editor). */
  async edit(ev: CalendarEvent): Promise<void> {
    const result = await this.openEditor({ event: ev });
    if (!result) return;
    if (result.kind === 'delete') {
      await this.remove(ev);
      return;
    }
    try {
      await firstValueFrom(this.api.updateEvent(ev.id, result.input));
      await this.loadEvents();
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't save that event. Please try again."), 'OK', { duration: 4000 });
    }
  }

  private async remove(ev: CalendarEvent): Promise<void> {
    const ok = await this.confirm({
      title: 'Delete this event?',
      message: `“${ev.title}” will be removed from your calendar.`,
      destructive: true,
    });
    if (!ok) return;
    try {
      await firstValueFrom(this.api.deleteEvent(ev.id));
      await this.loadEvents();
    } catch {
      this.snack.open("Couldn't delete that event.", 'OK', { duration: 4000 });
    }
  }

  private openEditor(data: EventEditorData): Promise<EventEditorResult | undefined> {
    const ref = this.dialog.open<EventEditorDialog, EventEditorData, EventEditorResult>(
      EventEditorDialog, { data, width: '460px', maxWidth: '94vw', autoFocus: false });
    return firstValueFrom(ref.afterClosed());
  }

  private confirm(data: ConfirmData): Promise<boolean | undefined> {
    const ref = this.dialog.open<FamilyConfirmDialog, ConfirmData, boolean>(FamilyConfirmDialog, {
      data, width: '420px', maxWidth: '92vw',
    });
    return firstValueFrom(ref.afterClosed());
  }

  // ---- Date helpers (browser local zone) ----

  /** The Monday (local midnight) of the week containing `d`. */
  private mondayOf(d: Date): Date {
    const day = (d.getDay() + 6) % 7; // 0 = Monday
    return new Date(d.getFullYear(), d.getMonth(), d.getDate() - day);
  }

  private toLocalDate(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  /** The set of local "YYYY-MM-DD" days an event touches (handles multi-day + all-day spans). */
  private spannedDays(ev: CalendarEvent): string[] {
    if (!ev.startUtc) return [];
    const start = new Date(ev.startUtc);
    let end = ev.endUtc ? new Date(ev.endUtc) : new Date(start.getTime() + 60 * 60 * 1000);
    // The API's all-day end is exclusive — step back so the final day isn't double-counted.
    if (ev.allDay) end = new Date(end.getTime() - 1);
    const days: string[] = [];
    const cursor = new Date(start.getFullYear(), start.getMonth(), start.getDate());
    const last = new Date(end.getFullYear(), end.getMonth(), end.getDate());
    // Guard against pathological ranges.
    let guard = 0;
    while (cursor.getTime() <= last.getTime() && guard++ < 366) {
      days.push(this.toLocalDate(cursor));
      cursor.setDate(cursor.getDate() + 1);
    }
    return days.length ? days : [this.toLocalDate(start)];
  }

  /** "All day" or a "h:mm a" local start label. */
  private timeLabel(ev: CalendarEvent): string {
    if (ev.allDay) return 'All day';
    if (!ev.startUtc) return '';
    return new Date(ev.startUtc).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  /** A friendly "h:mm a – h:mm a" (or "All day") range for the agenda/edit hint. */
  rangeFor(ev: CalendarEvent): string {
    if (ev.allDay) return 'All day';
    if (!ev.startUtc) return '';
    const start = new Date(ev.startUtc).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    if (!ev.endUtc) return start;
    const end = new Date(ev.endUtc).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
    return `${start} – ${end}`;
  }

  dayHeading(col: DayColumn): string {
    return col.date.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' });
  }

  private compareDayEvents = (a: DayEvent, b: DayEvent): number => {
    if (a.ev.allDay !== b.ev.allDay) return a.ev.allDay ? -1 : 1;
    return (a.ev.startUtc ?? '').localeCompare(b.ev.startUtc ?? '');
  };

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
