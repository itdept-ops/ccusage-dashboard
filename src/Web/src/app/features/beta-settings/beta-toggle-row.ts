import {
  ChangeDetectionStrategy, Component, computed, input, output, signal,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * BETA-SETTINGS ToggleRow — a polished, NATIVE-feel settings switch row, local to the beta-settings page
 * (the shared beta-ui kit ships no toggle primitive, so this is a page-local component that consumes the
 * kit tokens off the host cascade — it adds NO dependency and does not modify the kit).
 *
 * A row carries an optional tinted leading icon, a title + optional subtitle, and a hand-rolled animated
 * switch (a sliding knob that springs across a track that fills with the page accent gradient when on).
 * The whole row is the click target (a `<button>` with `role="switch"` + `aria-checked`), press-sinks on
 * pointerdown, fires a single `toggle` event with the new value, and supports a `busy`/`disabled` state.
 * Honors reduced-motion via the page-host killswitch. Replaces the Angular-Material slide-toggle so the
 * page has no Material dependency and a fully on-brand switch.
 *
 * Inputs:  checked (boolean), title (string, required), subtitle (string, default ''),
 *          icon (string Material ligature, default ''), disabled (boolean, default false),
 *          busy (boolean, default false — dims + blocks while a save is in flight)
 * Outputs: toggle (boolean — the NEW value the user requested)
 */
@Component({
  selector: 'app-beta-toggle-row',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="tr" role="switch"
            [class.is-on]="checked()"
            [class.is-busy]="busy()"
            [class.sink]="pressed()"
            [attr.aria-checked]="checked()"
            [attr.aria-label]="title()"
            [disabled]="disabled() || busy()"
            (pointerdown)="onDown()" (pointerup)="onUp()" (pointercancel)="onCancel()" (pointerleave)="onCancel()"
            (click)="emit()">
      @if (icon()) {
        <span class="tr-ic" aria-hidden="true"><mat-icon>{{ icon() }}</mat-icon></span>
      }
      <span class="tr-text">
        <span class="tr-title">{{ title() }}</span>
        @if (subtitle()) { <span class="tr-sub">{{ subtitle() }}</span> }
      </span>
      <span class="tr-switch" aria-hidden="true">
        <span class="tr-knob"></span>
      </span>
    </button>
  `,
  styles: [`
    :host { display: block; }
    .tr {
      display: flex; align-items: center; gap: 12px; width: 100%;
      min-height: 56px; padding: 10px 14px;
      background: transparent; border: none; text-align: left;
      color: var(--ink); font-family: var(--font-ui);
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      transition: background 140ms var(--ease-out), transform 120ms var(--ease-out);
    }
    .tr.sink { transform: scale(.992); }
    .tr:disabled { cursor: default; }
    .tr.is-busy { opacity: .6; }
    .tr:focus-visible { outline: 2px solid var(--focus); outline-offset: -2px; border-radius: 12px; }

    .tr-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 34px; height: 34px; border-radius: 11px;
      background: color-mix(in srgb, var(--accent-a) 16%, transparent);
      border: 1px solid color-mix(in srgb, var(--accent-a) 24%, var(--hairline));
    }
    .tr-ic mat-icon {
      font-size: 19px; width: 19px; height: 19px;
      color: color-mix(in srgb, var(--accent-a) 78%, var(--ink));
    }

    .tr-text { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .tr-title {
      font-size: 15px; font-weight: 650; letter-spacing: -.01em; color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .tr-sub {
      font-size: 12px; font-weight: 550; line-height: 1.35; color: var(--ink-dim);
    }

    /* Hand-rolled switch: a pill track that fills with the accent gradient when on, knob springs across. */
    .tr-switch {
      flex: 0 0 auto; position: relative; width: 50px; height: 30px; border-radius: var(--r-pill);
      background: var(--bg-sink); border: 1px solid var(--hairline);
      box-shadow: var(--press);
      transition: background 240ms var(--ease-out), border-color 240ms var(--ease-out);
    }
    .tr.is-on .tr-switch {
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b));
      border-color: transparent;
      box-shadow: 0 4px 14px color-mix(in srgb, var(--accent-a) 36%, transparent),
                  inset 0 1px 0 rgba(255, 255, 255, .18);
    }
    .tr-knob {
      position: absolute; top: 50%; left: 3px; width: 24px; height: 24px; border-radius: 50%;
      background: #f6f7ff; box-shadow: 0 2px 6px rgba(4, 6, 20, .5);
      transform: translate(0, -50%);
      transition: transform 320ms var(--ease-spring-up);
      will-change: transform;
    }
    .tr.is-on .tr-knob { transform: translate(20px, -50%); }

    @media (prefers-reduced-motion: reduce) {
      .tr-knob, .tr-switch { transition: none; }
    }
  `],
})
export class BetaToggleRow {
  /** The current on/off state. */
  readonly checked = input<boolean>(false);
  /** The row title. */
  readonly title = input.required<string>();
  /** Optional secondary line. */
  readonly subtitle = input<string>('');
  /** Optional leading Material ligature (accent-tinted chip). */
  readonly icon = input<string>('');
  /** Inert when true. */
  readonly disabled = input<boolean>(false);
  /** Dim + block while a save is in flight. */
  readonly busy = input<boolean>(false);
  /** Fired with the NEW value the user requested. */
  readonly toggle = output<boolean>();

  protected readonly pressed = signal(false);
  /** Mirror of disabled||busy for the press handlers. */
  private readonly inert = computed(() => this.disabled() || this.busy());

  protected onDown(): void { if (!this.inert()) this.pressed.set(true); }
  protected onUp(): void { this.pressed.set(false); }
  protected onCancel(): void { this.pressed.set(false); }
  protected emit(): void { if (!this.inert()) this.toggle.emit(!this.checked()); }
}
