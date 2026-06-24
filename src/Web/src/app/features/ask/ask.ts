import {
  Component,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  ElementRef,
  ChangeDetectionStrategy,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';

import { Api } from '../../core/api';
import { AskResponse } from '../../core/models';

/** One turn in the session-local Q&A transcript. */
interface AskTurn {
  /** The question the user asked (already trimmed). */
  question: string;
  /** The grounded answer (or the deterministic plain floor when `aiUsed` is false). */
  answer: string;
  /** False ⇒ the plain floor was returned because AI is off/unavailable (we badge it). */
  aiUsed: boolean;
  /** Which domains the snapshot drew on (e.g. tracker, sleep, bills) — chips under the answer. */
  domains: string[];
}

/** Friendly labels for the domain chips the server reports it drew on. */
const DOMAIN_LABEL: Record<string, string> = {
  tracker: 'Food & fitness',
  sleep: 'Sleep',
  hard75: '75 Hard',
  bills: 'Bills',
  family: 'Family',
  usage: 'Token usage',
};

/**
 * "Ask my life" — a focused page that answers plain-language questions GROUNDED in the caller's OWN tracked
 * data (POST /api/ai/ask). The page sends only the question; the server assembles a perm-filtered,
 * caller-scoped snapshot (tracker / sleep / 75-Hard / bills / family / usage — only what the caller can see)
 * and Gemini answers strictly from it. It NEVER proposes or writes anything — answer-only.
 *
 * Gated by the SAME tracker.ai permission as its route guard. The endpoint always returns 200: when AI is
 * off/unavailable it floors to a deterministic plain summary (`aiUsed:false`), which the page badges plainly
 * rather than showing a raw error. A real network failure shows a gentle inline error with a retry — never a
 * dead-end. Keeps a session-local transcript of the Q&A (newest last), plus a few suggested-question chips to
 * seed the box. Mobile-first; styled with the --tech-* tokens.
 */
@Component({
  selector: 'app-ask',
  standalone: true,
  imports: [
    FormsModule,
    MatIconModule,
    MatButtonModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './ask.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './ask.scss',
})
export class Ask {
  private api = inject(Api);

  /** The composer text. */
  readonly question = signal('');
  /** Session-local transcript (oldest first; we render newest at the bottom and auto-scroll). */
  readonly turns = signal<AskTurn[]>([]);
  /** True while a question is in flight (drives the spinner + disables the composer). */
  readonly loading = signal(false);
  /** A gentle inline error (network failure only — the endpoint itself never 503s). */
  readonly error = signal('');
  /** sr-only live-region announcement. */
  readonly announce = signal('');

  private readonly scrollAnchor = viewChild<ElementRef<HTMLElement>>('anchor');

  /** A few seed questions; clicking one fills the box and submits. */
  readonly suggestions = [
    'How did my week go?',
    'Did I hit my protein goal?',
    'How has my sleep been lately?',
    "What's on the family calendar today?",
    'How much have I spent on AI this month?',
  ];

  /** Whether the composer can submit (non-empty + idle). */
  readonly canSubmit = computed(() => this.question().trim().length > 0 && !this.loading());

  constructor() {
    // Keep the newest turn / spinner in view as the transcript grows.
    effect(() => {
      this.turns();
      this.loading();
      queueMicrotask(() =>
        this.scrollAnchor()?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'end' }),
      );
    });
  }

  /** Fill the box from a suggestion chip and immediately ask. */
  askSuggestion(text: string): void {
    if (this.loading()) return;
    this.question.set(text);
    void this.submit();
  }

  /**
   * Submit the current question. Trims + guards empty/in-flight. On success appends the turn (grounded answer
   * or plain floor) and clears the box; on a network failure shows the inline error and KEEPS the question so
   * the user can retry without retyping.
   */
  async submit(): Promise<void> {
    const q = this.question().trim();
    if (q.length === 0 || this.loading()) return;
    this.loading.set(true);
    this.error.set('');
    this.announce.set('Asking…');
    try {
      const res: AskResponse = await firstValueFrom(this.api.askMyLife(q));
      this.turns.update((t) => [
        ...t,
        {
          question: q,
          answer: res.answer,
          aiUsed: res.aiUsed,
          domains: res.domains ?? [],
        },
      ]);
      this.question.set('');
      this.announce.set('Answer ready.');
    } catch {
      this.error.set("I couldn't reach the assistant just now. Please try again.");
      this.announce.set('Something went wrong. Try again.');
    } finally {
      this.loading.set(false);
    }
  }

  /** Enter submits; Shift+Enter inserts a newline (textarea). */
  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.submit();
    }
  }

  /** Friendly label for a domain key (falls back to the raw key). */
  domainLabel(key: string): string {
    return DOMAIN_LABEL[key] ?? key;
  }
}
