import {
  ChangeDetectionStrategy, Component, computed, input, output,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { AutomationRule } from '../../../core/models';
import {
  ActionOpt, actionChips, conditionLabel, relativeTime, triggerOpt,
} from '../automations-beta.model';

/**
 * Relay RuleCard — one automation rendered as a readable WHEN → THEN flow: a trigger chip, an arrow,
 * then one or more action chips; a name + an optional condition pill; a meta footer (updated-relative
 * time, "own webhook" hint); and a native switch that enables/disables the rule. A disabled rule dims.
 *
 * Pure presentation: emits `toggle` (the page owns the optimistic write + toast) and `open` (tap the
 * body to edit later / inspect). The native <input type="checkbox" role="switch"> keeps it accessible +
 * keyboard-operable; its (change) stops propagation so it never also triggers the row's tap.
 */
@Component({
  selector: 'app-relay-rule-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule],
  template: `
    <article class="rk" [class.is-off]="!rule().enabled">
      <!-- Top row: name + the enable switch. -->
      <div class="rk-top">
        <button type="button" class="rk-name-btn" (click)="open.emit(rule())"
                [attr.aria-label]="'Edit ' + displayName()">
          <span class="rk-name">{{ displayName() }}</span>
          <span class="rk-status" [class.on]="rule().enabled">{{ rule().enabled ? 'Active' : 'Paused' }}</span>
        </button>

        <label class="sw" [attr.aria-label]="(rule().enabled ? 'Disable ' : 'Enable ') + displayName()">
          <input type="checkbox" role="switch" class="sw-in"
                 [checked]="rule().enabled"
                 (change)="onToggle($event)" (click)="$event.stopPropagation()" />
          <span class="sw-track" aria-hidden="true"><span class="sw-thumb"></span></span>
        </label>
      </div>

      <!-- WHEN → THEN flow. -->
      <div class="rk-flow">
        <span class="rk-chip rk-when">
          <span class="rk-chip-ic" aria-hidden="true"><mat-icon>{{ trig().icon }}</mat-icon></span>
          <span class="rk-chip-txt">
            <i class="rk-chip-kicker">When</i>
            <b class="rk-chip-lbl">{{ trig().chip }}</b>
          </span>
        </span>

        <span class="rk-arrow" aria-hidden="true"><mat-icon>arrow_forward</mat-icon></span>

        <span class="rk-thens">
          @for (a of thenChips(); track a.value) {
            <span class="rk-chip rk-then">
              <span class="rk-chip-ic" aria-hidden="true"><mat-icon>{{ a.icon }}</mat-icon></span>
              <span class="rk-chip-txt">
                <i class="rk-chip-kicker">Then</i>
                <b class="rk-chip-lbl">{{ a.chip }}</b>
              </span>
            </span>
          }
        </span>
      </div>

      <!-- Meta footer: condition pill + activity time + webhook hint. -->
      <div class="rk-meta">
        <span class="rk-cond" [class.is-always]="condIsAlways()">
          <mat-icon aria-hidden="true">{{ condIsAlways() ? 'all_inclusive' : 'rule' }}</mat-icon>
          {{ cond() }}
        </span>
        @if (rule().hasWebhook) {
          <span class="rk-dot" aria-hidden="true">•</span>
          <span class="rk-tag" title="Posts to this rule's own Discord webhook">
            <mat-icon aria-hidden="true">webhook</mat-icon> Own webhook
          </span>
        }
        @if (updated()) {
          <span class="rk-dot" aria-hidden="true">•</span>
          <span class="rk-updated">
            <mat-icon aria-hidden="true">schedule</mat-icon> Updated {{ updated() }}
          </span>
        }
      </div>
    </article>
  `,
  styles: [`
    :host { display: block; }
    .rk {
      display: flex; flex-direction: column; gap: 12px;
      padding: 15px 15px 13px; border-radius: var(--r-card);
      background: var(--bg-rise); border: 1px solid var(--hairline);
      box-shadow: var(--lift-1), inset 0 1px 0 rgba(255,255,255,.06);
      position: relative; overflow: hidden;
      transition: opacity 200ms var(--ease-out), box-shadow 160ms var(--ease-out),
                  transform 140ms var(--ease-spring);
    }
    .rk:hover {
      box-shadow: var(--lift-2), inset 0 1px 0 rgba(255,255,255,.09);
      transform: translateY(-1px);
    }
    /* Accent edge-glow on the active card's left rail. */
    .rk::before {
      content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: 3px;
      background: linear-gradient(180deg, var(--accent-a), var(--accent-b));
      opacity: .9; transition: opacity 200ms var(--ease-out);
    }
    .rk.is-off { opacity: .62; }
    .rk.is-off::before { opacity: .25; }

    .rk-top { display: flex; align-items: center; gap: 12px; }
    .rk-name-btn {
      flex: 1 1 auto; min-width: 0; display: flex; align-items: center; gap: 8px;
      padding: 0; border: none; background: transparent; text-align: left; cursor: pointer;
      -webkit-tap-highlight-color: transparent;
    }
    .rk-name-btn:focus-visible { outline: 2px solid var(--focus); outline-offset: 3px; border-radius: 8px; }
    .rk-name {
      font-size: 16px; font-weight: 800; letter-spacing: -.01em; color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0;
    }
    .rk-status {
      flex: 0 0 auto; font-size: 10px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase;
      padding: 2px 7px; border-radius: var(--r-pill);
      color: var(--ink-faint); background: color-mix(in srgb, var(--ink-faint) 14%, transparent);
    }
    .rk-status.on {
      color: var(--accent-a);
      background: color-mix(in srgb, var(--accent-a) 16%, transparent);
    }

    /* Native switch (checkbox role=switch) styled as an iOS-style toggle. */
    .sw { flex: 0 0 auto; display: inline-grid; place-items: center; cursor: pointer; -webkit-tap-highlight-color: transparent; }
    .sw-in {
      position: absolute; opacity: 0; width: 52px; height: 32px; margin: 0; cursor: pointer;
    }
    .sw-track {
      display: block; width: 52px; height: 32px; border-radius: var(--r-pill); padding: 3px;
      background: color-mix(in srgb, var(--ink-faint) 38%, var(--bg-sink));
      transition: background 200ms var(--ease-out);
    }
    .sw-thumb {
      display: block; width: 26px; height: 26px; border-radius: 50%; background: #fff;
      box-shadow: 0 2px 5px rgba(4, 6, 20, .5);
      transition: transform 220ms var(--ease-spring);
    }
    .sw-in:checked + .sw-track { background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); }
    .sw-in:checked + .sw-track .sw-thumb { transform: translateX(20px); }
    .sw-in:focus-visible + .sw-track { outline: 2px solid var(--focus); outline-offset: 2px; }

    /* WHEN → THEN flow. */
    .rk-flow { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .rk-chip {
      display: inline-flex; align-items: center; gap: 8px; min-width: 0;
      padding: 7px 11px 7px 8px; border-radius: 14px;
      border: 1px solid var(--hairline); background: var(--bg-sink);
    }
    .rk-when { border-color: color-mix(in srgb, var(--accent-a) 32%, transparent); }
    .rk-chip-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 30px; height: 30px; border-radius: 10px;
      background: color-mix(in srgb, var(--accent-a) 16%, var(--bg-rise)); color: var(--accent-a);
    }
    .rk-then .rk-chip-ic { background: color-mix(in srgb, var(--accent-b) 16%, var(--bg-rise)); color: var(--accent-b); }
    .rk-chip-ic mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .rk-chip-txt { display: flex; flex-direction: column; gap: 0; min-width: 0; }
    .rk-chip-kicker {
      font-style: normal; font-size: 9.5px; font-weight: 800; letter-spacing: .08em; text-transform: uppercase;
      color: var(--ink-faint); line-height: 1.1;
    }
    .rk-chip-lbl {
      font-size: 13px; font-weight: 700; color: var(--ink); line-height: 1.15;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .rk-arrow {
      flex: 0 0 auto; display: grid; place-items: center; color: var(--ink-faint);
    }
    .rk-arrow mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .rk-thens { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; min-width: 0; }

    /* Meta footer. */
    .rk-meta {
      display: flex; align-items: center; gap: 7px; flex-wrap: wrap;
      font-size: 12px; font-weight: 600; color: var(--ink-dim);
    }
    .rk-cond {
      display: inline-flex; align-items: center; gap: 5px;
      padding: 3px 9px 3px 7px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--accent-a) 12%, transparent); color: var(--accent-a);
      font-weight: 700;
    }
    .rk-cond.is-always { background: color-mix(in srgb, var(--ink-faint) 14%, transparent); color: var(--ink-dim); }
    .rk-cond mat-icon { font-size: 14px; width: 14px; height: 14px; }
    .rk-dot { color: var(--ink-faint); }
    .rk-tag, .rk-updated { display: inline-flex; align-items: center; gap: 4px; color: var(--ink-faint); }
    .rk-tag mat-icon, .rk-updated mat-icon { font-size: 14px; width: 14px; height: 14px; }
  `],
})
export class RelayRuleCard {
  /** The rule to render. */
  readonly rule = input.required<AutomationRule>();
  /** Fired when the enable switch flips, carrying the desired next enabled state. */
  readonly toggle = output<boolean>();
  /** Fired when the card body is tapped (open / inspect). */
  readonly open = output<AutomationRule>();

  protected readonly trig = computed(() => triggerOpt(this.rule().triggerKind));
  protected readonly thenChips = computed<ActionOpt[]>(() => actionChips(this.rule().action));
  protected readonly cond = computed(() => conditionLabel(this.rule()));
  protected readonly condIsAlways = computed(() => this.cond() === 'Always');
  protected readonly updated = computed(() => relativeTime(this.rule().updatedUtc));
  protected readonly displayName = computed(() => this.rule().name?.trim() || this.trig().label);

  protected onToggle(e: Event): void {
    const next = (e.target as HTMLInputElement).checked;
    this.toggle.emit(next);
  }
}
