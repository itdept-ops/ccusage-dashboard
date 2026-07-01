import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { ChallengeStore } from '../../core/challenge-store';
import {
  CreateHardTaskRequest, HardDayDto, HardDayTaskDto, HardLeaderboardRowDto, HardSharedPersonDto,
  HardTaskDto, NudgeKind, PERM, UpdateHardTaskRequest, UpsertHardDayRequest,
} from '../../core/models';
import {
  BetaBottomSheet, BetaFab, BetaPullRefresh, BetaSegmentedControl, BetaSkeleton, BetaSvgRing,
  BetaToaster, ToastController, type Segment,
} from '../beta-ui';

/** Max future cheat days the backend accepts (kept in sync with the live page + HardChallengeEndpoints). */
const MAX_CHEAT_DAYS = 10;

/** The draft for a new custom task (the add-task form) — mirrors the live page's NewTaskDraft. */
interface NewTaskDraft {
  label: string;
  measurable: boolean;
  targetValue: number | null;
  unit: string;
  pointValue: number;
  partialCredit: boolean;
}
function emptyDraft(): NewTaskDraft {
  return { label: '', measurable: false, targetValue: 10, unit: '', pointValue: 10, partialCredit: false };
}

/** The icon shown for each auto-source / a generic one for manual + custom tasks (mirrors live challenge.ts). */
const AUTO_ICON: Record<string, string> = {
  Diet: 'restaurant',
  Water: 'local_drink',
  Workout: 'fitness_center',
  NoAlcohol: 'no_drinks',
  None: 'task_alt',
};

/**
 * 75 HARD — Streak (mobile twin of the live `/challenge` page) — the mobile-first, native-feel
 * re-presentation of the configurable 75 Hard challenge, rebuilt on the shared beta-ui "Strata" kit
 * (`@use '../beta-ui/beta-kit'`). One signature accent — a FLAME orange → ember red — re-skins the whole
 * screen via the per-page accent contract.
 *
 * An immersive header floats a streak/completion {@link BetaSvgRing} behind a HUGE Clash Display streak
 * numeral with the day-N / points line. A {@link BetaSegmentedControl} flips the body between TODAY (the
 * six daily tasks as big tappable toggle rows, with measurable PARTIAL bars + a manual checkbox/value) and
 * a compact LEADERBOARD (the caller + sharing contacts, ranked by points, names only — NEVER email — with
 * a friendly Nudge). Pull-to-refresh re-fetches; loading skeletons + an elevated empty/error/no-challenge
 * state round it out (it renders cleanly with ZERO data — the harness mocks the API).
 *
 * DATA PARITY + PRIVACY: every figure flows through the SAME {@link ChallengeStore} / {@link Api} the live
 * page uses — `challenge` (the day grid), `upsertChallengeDay` (manual task toggles), `challengeLeaderboard`,
 * `challengeShared`, and `nudge`. The server computes all points (incl. partial) + auto-scored tasks live
 * from the tracker; this twin never re-derives anything. Leaderboard rows carry userId + display NAME only.
 *
 * ISOLATION: gated by `platform.mobile` + the SAME tracker permissions the live `/challenge` route carries;
 * it consumes the kit + the SAME read-write store/Api as the live counterpart. No live page is imported or
 * modified. Reduced-motion collapses the reveals via the kit a11y killswitch; layout is mobile-first
 * (44px targets, safe-area insets, no 390px overflow) and centers on desktop.
 */
@Component({
  selector: 'app-challenge-mobile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ToastController],
  imports: [
    FormsModule, MatIconModule, RouterLink,
    BetaBottomSheet, BetaFab, BetaPullRefresh, BetaSegmentedControl, BetaSkeleton, BetaSvgRing,
    BetaToaster,
  ],
  template: `
    <!-- ─────────────── PULL-TO-REFRESH OWNS THE SCROLL ─────────────── -->
    <app-bs-pull-refresh class="cm-ptr" [busy]="refreshing()" (refresh)="reload()">
      <div class="cm-scroll" aria-live="polite">

        <!-- ─── IMMERSIVE HEADER: streak ring + numeral ─── -->
        <header class="cm-hero">
          @if (loading()) {
            <div class="cm-hero__skel">
              <app-bs-skeleton width="138px" height="138px" [circle]="true" />
              <app-bs-skeleton width="60%" height="20px" radius="var(--r-pill)" />
            </div>
          } @else if (!hasChallenge()) {
            <!-- NO active challenge (or empty mock): a warm start state. -->
            <p class="cm-hero__kicker"><mat-icon aria-hidden="true">local_fire_department</mat-icon> 75 Hard</p>
            <span class="cm-hero__orb"><mat-icon aria-hidden="true">flag</mat-icon></span>
            @if (readOnly()) {
              <h1 class="cm-hero__title">No active challenge</h1>
              <p class="cm-hero__sub">This person doesn't have a 75 Hard in progress right now.</p>
              <button type="button" class="cm-hero__cta" (click)="viewSelf()">
                <mat-icon aria-hidden="true">arrow_back</mat-icon> Back to mine
              </button>
            } @else if (errored()) {
              <h1 class="cm-hero__title">Couldn't load</h1>
              <p class="cm-hero__sub">Pull to retry.</p>
            } @else {
              <h1 class="cm-hero__title">Start your 75 Hard</h1>
              <p class="cm-hero__sub">
                The classic set — diet, water, two workouts, read 10 pages, no alcohol. Make it yours after.
              </p>
              <div class="cm-start">
                <label class="cm-start__field">
                  <span class="cm-start__label">Start date</span>
                  <input class="cm-start__input" type="date" name="cmStartDate"
                         [ngModel]="startDate()" (ngModelChange)="startDate.set($event)" />
                </label>
                <button type="button" class="cm-hero__cta" (click)="start()" [disabled]="starting()">
                  @if (starting()) { <span class="cm-spin" aria-hidden="true"></span> Starting… }
                  @else { <mat-icon aria-hidden="true">rocket_launch</mat-icon> Start day 1 }
                </button>
              </div>
            }
          } @else {
            <p class="cm-hero__kicker">
              <mat-icon aria-hidden="true">local_fire_department</mat-icon>
              Day {{ currentDay() }} of {{ totalDays }}
            </p>

            <div class="cm-hero__ring">
              <app-bs-ring [value]="completionFrac()" [size]="150" [stroke]="9"
                           [signalOnFull]="finished()"
                           [label]="streak() + ' day streak, ' + fmt(totalPoints()) + ' points'">
                <span class="cm-hero__numeral">
                  <span class="cm-hero__n">{{ streak() }}</span>
                  <span class="cm-hero__of">day streak</span>
                </span>
              </app-bs-ring>
            </div>

            <div class="cm-hero__stats">
              <span class="cm-hero__stat">
                <b>{{ fmt(totalPoints()) }}</b><i>points</i>
              </span>
              <span class="cm-hero__sep" aria-hidden="true"></span>
              <span class="cm-hero__stat">
                <b>{{ fmt(todayPoints()) }}</b><i>today</i>
              </span>
              <span class="cm-hero__sep" aria-hidden="true"></span>
              <span class="cm-hero__stat">
                <b>{{ completedDays() }}</b><i>complete</i>
              </span>
            </div>
          }
        </header>

        @if (hasChallenge() && !loading()) {
          <!-- ─── PAGE HEADER: title + subtitle + shared-view switcher ─── -->
          <div class="cm-header">
            <div class="cm-header__text">
              <h2 class="cm-header__title">75 Hard</h2>
              <p class="cm-header__sub">Day {{ currentDay() }} of {{ totalDays }} · {{ streak() }}-day streak</p>
            </div>
            @if (shared().length) {
              <button type="button" class="cm-header__action" (click)="viewSheet.set(true)"
                      aria-label="Whose challenge to view">
                <mat-icon aria-hidden="true">visibility</mat-icon>
                {{ viewingUser() ? viewingUser()!.name : 'My 75 Hard' }}
                <mat-icon aria-hidden="true">expand_more</mat-icon>
              </button>
            } @else {
              <a class="cm-header__action" routerLink="/challenge">
                Full page <mat-icon aria-hidden="true">open_in_new</mat-icon>
              </a>
            }
          </div>

          @if (viewingUser(); as vu) {
            <!-- Read-only banner when viewing someone else. -->
            <div class="cm-readonly" role="status">
              <span class="cm-readonly__avatar" aria-hidden="true">{{ initials(vu) }}</span>
              <span class="cm-readonly__txt">Viewing <strong>{{ vu.name }}</strong>'s 75 Hard — read-only.</span>
              <button type="button" class="cm-readonly__back" (click)="viewSelf()">Back to mine</button>
            </div>
          }

          <!-- ─── TODAY | LEADERBOARD switch ─── -->
          <div class="cm-seg-wrap">
            <app-bs-segmented class="cm-seg"
              [segments]="tabs" [value]="tab()" label="Challenge view"
              (change)="setTab($event)" />
          </div>

          @if (tab() === 'today') {
            <!-- ─────────────── TODAY: the daily tasks ─────────────── -->
            <section class="cm-today">
              <!-- Day navigator within the challenge window. -->
              <div class="cm-datenav" role="group" aria-label="Choose day">
                <button type="button" class="cm-datenav__step" (click)="prevDay()" aria-label="Previous day">
                  <mat-icon aria-hidden="true">chevron_left</mat-icon>
                </button>
                <label class="cm-datenav__label">
                  <span class="cm-datenav__heading">{{ dateHeading() }}</span>
                  <input class="cm-datenav__input" type="date" name="cmDayDate"
                         [value]="store.date()" (change)="onDateInput($any($event.target).value)"
                         aria-label="Pick a date" />
                </label>
                <button type="button" class="cm-datenav__step" (click)="nextDay()" aria-label="Next day">
                  <mat-icon aria-hidden="true">chevron_right</mat-icon>
                </button>
                <button type="button" class="cm-datenav__today" (click)="goToday()">Today</button>
              </div>

              <div class="cm-today__head">
                <h2 class="cm-today__title">{{ dateHeading() }}</h2>
                <span class="cm-today__points">
                  {{ fmt(dayPoints()) }} / {{ fmt(maxPoints()) }} pts
                </span>
              </div>

              @if (day(); as d) {
                @if (d.dayNumber != null) {
                  <p class="cm-daynote">
                    Day {{ d.dayNumber }} of {{ totalDays }}
                    @if (d.complete) { · <span class="cm-pill is-ok">Complete</span> }
                    @if (d.isCheatDay) { · <span class="cm-pill is-cheat">Cheat day</span> }
                  </p>
                } @else {
                  <p class="cm-daynote is-out">This date is outside your 75-day window.</p>
                }
              }

              @if (!tasks().length) {
                <div class="cm-empty">
                  <span class="cm-empty__orb" aria-hidden="true">
                    <mat-icon>checklist</mat-icon>
                  </span>
                  <p class="cm-empty__title">No tasks yet</p>
                  <p class="cm-empty__hint">Set up your daily tasks on the full challenge page.</p>
                </div>
              } @else {
                @for (t of tasks(); track t.taskId; let i = $index) {
                  <button type="button"
                          class="cm-task cm-reveal"
                          [class.is-done]="t.complete"
                          [class.is-partial]="!t.complete && t.progress > 0"
                          [class.is-readonly]="readOnly() || isAuto(t)"
                          [style.--ri]="i"
                          [disabled]="busyTask() === t.key"
                          [attr.aria-pressed]="t.complete"
                          [attr.aria-label]="taskAria(t)"
                          (click)="onTaskTap(t)">
                    <span class="cm-task__check" aria-hidden="true">
                      @if (t.complete) {
                        <mat-icon>check_circle</mat-icon>
                      } @else if (t.progress > 0) {
                        <span class="cm-task__partial" [style.--p]="pct(t)">
                          <mat-icon>{{ taskIcon(t) }}</mat-icon>
                        </span>
                      } @else {
                        <mat-icon>{{ taskIcon(t) }}</mat-icon>
                      }
                    </span>

                    <span class="cm-task__body">
                      <span class="cm-task__label">{{ t.label }}</span>
                      <span class="cm-task__meta">
                        @if (isMeasurable(t)) {
                          {{ measuredLabel(t) }}
                        } @else if (isAuto(t)) {
                          {{ autoHint(t) }}
                        } @else {
                          {{ t.complete ? 'Done' : 'Tap to mark done' }}
                        }
                        @if (t.partialCredit && isMeasurable(t)) { · partial }
                      </span>
                      @if (isMeasurable(t)) {
                        <span class="cm-task__bar" aria-hidden="true">
                          <span class="cm-task__bar-fill" [style.width.%]="pct(t)"></span>
                        </span>
                      }
                    </span>

                    <span class="cm-task__pts">
                      <b>{{ fmt(t.points) }}</b>
                      <i>/ {{ fmt(t.pointValue) }}</i>
                    </span>
                    @if (!readOnly() && isManualMeasurable(t)) {
                      <mat-icon class="cm-task__edit" aria-hidden="true">tune</mat-icon>
                    }
                  </button>
                }
              }

              <!-- Day-level attestations + confession (own view, when the day is in-window). -->
              @if (!readOnly() && day()?.dayNumber != null) {
                @if (dietTaskEnabled()) {
                  <div class="cm-attest">
                    <div class="cm-attest__copy">
                      <span class="cm-attest__label">Diet result</span>
                      <span class="cm-attest__sub">
                        @if (dietOverride() === null) { Auto-scored — override if the day went differently. }
                        @else { Manually overridden. }
                      </span>
                    </div>
                    <div class="cm-attest__toggle" role="group" aria-label="Diet result override">
                      <button type="button" class="cm-attest__opt" [class.is-on]="dietOverride() === true"
                              (click)="setDietOverride('pass')">On plan</button>
                      <button type="button" class="cm-attest__opt" [class.is-on]="dietOverride() === false"
                              (click)="setDietOverride('fail')">Off plan</button>
                    </div>
                  </div>
                }
                @if (noAlcoholEnabled(); as _na) {
                  @if (day(); as d) {
                    <button type="button" class="cm-attest cm-attest--row"
                            [attr.aria-pressed]="d.noAlcohol" (click)="toggleNoAlcohol(!d.noAlcohol)">
                      <div class="cm-attest__copy">
                        <span class="cm-attest__label">No alcohol today</span>
                        <span class="cm-attest__sub">Drives the no-alcohol task's result.</span>
                      </div>
                      <span class="cm-switch" [class.is-on]="d.noAlcohol" aria-hidden="true"><span></span></span>
                    </button>
                  }
                }
                <label class="cm-confess">
                  <span class="cm-confess__label">Confession (optional)</span>
                  <textarea class="cm-confess__area" rows="2" maxlength="280" name="cmConfession"
                            [ngModel]="confessionDraft()" (ngModelChange)="confessionDraft.set($event)"
                            (blur)="saveConfession()"
                            placeholder="Missed a task? A short, honest note keeps your run counted."></textarea>
                  <span class="cm-confess__count" aria-hidden="true">{{ confessionDraft().length }} / 280</span>
                </label>
              } @else if (readOnly() && day()?.confession) {
                <p class="cm-confess__read">{{ day()!.confession }}</p>
              }

              @if (readOnly()) {
                <p class="cm-foot" aria-hidden="true">
                  Viewing a shared challenge — read-only.
                </p>
              } @else {
                <p class="cm-foot" aria-hidden="true">
                  Diet, water &amp; workouts score live from your tracker · tap manual tasks to log them
                </p>
              }
            </section>
          } @else {
            <!-- ─────────────── LEADERBOARD ─────────────── -->
            <section class="cm-board">
              <p class="cm-board__head">
                <mat-icon aria-hidden="true">leaderboard</mat-icon>
                Rankings
              </p>
              @if (!leaderboard().length) {
                <div class="cm-empty">
                  <span class="cm-empty__orb" aria-hidden="true">
                    <mat-icon>leaderboard</mat-icon>
                  </span>
                  <p class="cm-empty__title">No one on the board yet</p>
                  <p class="cm-empty__hint">Share your tracker with contacts to compare progress.</p>
                </div>
              } @else {
                @for (row of leaderboard(); track row.userId; let i = $index) {
                  <div class="cm-row cm-reveal"
                       [class.is-self]="row.isSelf"
                       [style.--ri]="i">
                    <span class="cm-row__rank" [attr.data-medal]="i < 3 ? i + 1 : null">{{ i + 1 }}</span>
                    <span class="cm-row__avatar" aria-hidden="true">
                      @if (row.picture) {
                        <img [src]="row.picture" alt="" referrerpolicy="no-referrer" />
                      } @else {
                        {{ initials(row) }}
                      }
                    </span>
                    <span class="cm-row__body">
                      <span class="cm-row__name">
                        {{ row.name }} @if (row.isSelf) { <i class="cm-row__you">you</i> }
                      </span>
                      <span class="cm-row__sub">
                        <mat-icon aria-hidden="true">local_fire_department</mat-icon>{{ row.currentStreak }}
                        · day {{ row.currentDay }} · {{ fmt(row.todayPoints) }} today
                      </span>
                    </span>
                    <span class="cm-row__pts">
                      <b>{{ fmt(row.totalPoints) }}</b><i>pts</i>
                    </span>
                    @if (canNudgeRow(row)) {
                      <button type="button" class="cm-row__nudge"
                              [disabled]="nudging() === row.userId"
                              [attr.aria-label]="'Nudge ' + row.name"
                              (click)="nudge(row)">
                        <mat-icon aria-hidden="true">waving_hand</mat-icon>
                      </button>
                    }
                  </div>
                }
              }
              <p class="cm-foot" aria-hidden="true">
                You &amp; the contacts who share their tracker · ranked by points
              </p>
            </section>
          }
        }
      </div>
    </app-bs-pull-refresh>

    <!-- FAB: customize tasks (own view, has a challenge, on the Today tab). -->
    @if (hasChallenge() && !loading() && !readOnly() && tab() === 'today') {
      <app-bs-fab icon="tune" label="Customize" [extended]="true" [fixed]="true" (action)="openConfig()" />
    }

    <!-- WHOSE CHALLENGE view switcher. -->
    <app-bs-sheet [(open)]="viewSheet" detent="half" label="Whose challenge to view">
      <div class="cs">
        <div class="cs__head"><h3 class="cs__title">View a challenge</h3></div>
        <button type="button" class="cs__pick" [class.is-on]="viewUser() === null" (click)="viewSelf(); viewSheet.set(false)">
          <mat-icon aria-hidden="true">person</mat-icon> My 75 Hard
        </button>
        @for (u of shared(); track u.userId) {
          <button type="button" class="cs__pick" [class.is-on]="viewUser() === u.userId"
                  (click)="viewOther(u.userId); viewSheet.set(false)">
            <span class="cs__pick-avatar" aria-hidden="true">{{ initials(u) }}</span> {{ u.name }}
          </button>
        }
      </div>
    </app-bs-sheet>

    <!-- MANUAL MEASURABLE task value entry. -->
    <app-bs-sheet [(open)]="valueSheet" detent="half" [dismissable]="!busyTask()" label="Log task value">
      @if (valueTask(); as t) {
        <div class="cs">
          <div class="cs__head">
            <h3 class="cs__title">{{ t.label }}</h3>
            <button type="button" class="cs__close" (click)="valueSheet.set(false)" aria-label="Close"><mat-icon aria-hidden="true">close</mat-icon></button>
          </div>
          <label class="cs__field">
            <span class="cs__label">Progress{{ t.unit ? ' (' + t.unit + ')' : '' }}</span>
            <input class="cs__input" type="number" min="0" step="1" name="cmManualValue"
                   [ngModel]="valueDraft()" (ngModelChange)="valueDraft.set($event)"
                   [attr.aria-label]="t.label + ' progress'" />
          </label>
          <p class="cs__hint">Target {{ fmt(t.targetValue ?? 0) }}{{ t.unit ? ' ' + t.unit : '' }}.</p>
          <div class="cs__quick">
            <button type="button" class="cs__quickbtn" (click)="valueDraft.set(0)">Clear</button>
            <button type="button" class="cs__quickbtn" (click)="valueDraft.set(t.targetValue ?? 0)">Fill target</button>
          </div>
          <button type="button" class="cs__save" [disabled]="busyTask() === t.key" (click)="saveManualValue()">
            @if (busyTask() === t.key) { <span class="cm-spin" aria-hidden="true"></span> Saving… }
            @else { <mat-icon aria-hidden="true">check</mat-icon> Save }
          </button>
        </div>
      }
    </app-bs-sheet>

    <!-- CONFIG: customize tasks + add custom task + cheat days (owner). -->
    <app-bs-sheet [(open)]="configSheet" detent="full" [dismissable]="!addingTask()" label="Customize tasks">
      <div class="cs">
        <div class="cs__head">
          <h3 class="cs__title">Customize tasks</h3>
          <button type="button" class="cs__close" (click)="configSheet.set(false)" aria-label="Close"><mat-icon aria-hidden="true">close</mat-icon></button>
        </div>

        @for (t of storeTasks(); track t.id) {
          <div class="cs-cfg" [class.is-off]="!t.enabled">
            <div class="cs-cfg__head">
              <mat-icon class="cs-cfg__glyph" aria-hidden="true">{{ taskIcon(t) }}</mat-icon>
              <input class="cs__input cs-cfg__label" [ngModel]="t.label" name="cfgLabel{{ t.id }}"
                     (blur)="saveTaskLabel(t, $any($event.target).value)" aria-label="Task label" />
              @if (isAuto(t)) { <span class="cs-cfg__auto"><mat-icon aria-hidden="true">bolt</mat-icon> auto</span> }
              <button type="button" class="cs-switch" [class.is-on]="t.enabled"
                      (click)="toggleTaskEnabled(t, !t.enabled)" [attr.aria-label]="'Enable ' + t.label"
                      [attr.aria-pressed]="t.enabled"><span></span></button>
              @if (!isAuto(t)) {
                <button type="button" class="cs-cfg__del" (click)="deleteTask(t)" [attr.aria-label]="'Remove ' + t.label"><mat-icon aria-hidden="true">delete_outline</mat-icon></button>
              }
            </div>
            <div class="cs-cfg__fields">
              @if (t.targetValue != null) {
                <label class="cs__field cs__field--sm"><span class="cs__label">Target</span>
                  <input class="cs__input" type="number" min="0" step="any" [ngModel]="t.targetValue" name="cfgTarget{{ t.id }}"
                         (blur)="saveTaskTarget(t, $any($event.target).valueAsNumber)" /></label>
              }
              @if (t.autoSource === 'Workout') {
                <label class="cs__field cs__field--sm"><span class="cs__label">Min minutes</span>
                  <input class="cs__input" type="number" min="1" step="1" [ngModel]="t.minMinutes" name="cfgMin{{ t.id }}"
                         (blur)="saveTaskMinMinutes(t, $any($event.target).valueAsNumber)" /></label>
                <label class="cs__field cs__field--sm"><span class="cs__label">Watch active cal</span>
                  <input class="cs__input" type="number" min="1" step="10" placeholder="300" [ngModel]="t.activeCalPerWorkout" name="cfgCal{{ t.id }}"
                         (blur)="saveTaskActiveCal(t, $any($event.target).valueAsNumber)" /></label>
              }
              <label class="cs__field cs__field--sm"><span class="cs__label">Points</span>
                <input class="cs__input" type="number" min="0" step="1" [ngModel]="t.pointValue" name="cfgPts{{ t.id }}"
                       (blur)="saveTaskPoints(t, $any($event.target).valueAsNumber)" /></label>
              @if (t.targetValue != null) {
                <button type="button" class="cs__toggle cs__toggle--sm" [class.is-on]="t.partialCredit"
                        (click)="toggleTaskPartial(t, !t.partialCredit)">
                  <mat-icon aria-hidden="true">{{ t.partialCredit ? 'check_box' : 'check_box_outline_blank' }}</mat-icon>
                  Partial credit
                </button>
              }
            </div>
          </div>
        }

        <!-- Add a custom task. -->
        <h4 class="cs__subhead">Add a custom task</h4>
        <label class="cs__field"><span class="cs__label">Task name</span>
          <input class="cs__input" maxlength="120" [ngModel]="newTask().label" name="ntLabel"
                 (ngModelChange)="patchDraft({ label: $event })" placeholder="e.g. Cold shower" /></label>
        <button type="button" class="cs__toggle" [class.is-on]="newTask().measurable"
                (click)="patchDraft({ measurable: !newTask().measurable })">
          <mat-icon aria-hidden="true">{{ newTask().measurable ? 'check_box' : 'check_box_outline_blank' }}</mat-icon>
          Measurable (a number, not just done/not)
        </button>
        @if (newTask().measurable) {
          <div class="cs__row">
            <label class="cs__field"><span class="cs__label">Target</span>
              <input class="cs__input" type="number" min="0" step="any" [ngModel]="newTask().targetValue" name="ntTarget"
                     (ngModelChange)="patchDraft({ targetValue: $event })" /></label>
            <label class="cs__field"><span class="cs__label">Unit</span>
              <input class="cs__input" maxlength="32" [ngModel]="newTask().unit" name="ntUnit"
                     (ngModelChange)="patchDraft({ unit: $event })" placeholder="reps" /></label>
          </div>
          <button type="button" class="cs__toggle" [class.is-on]="newTask().partialCredit"
                  (click)="patchDraft({ partialCredit: !newTask().partialCredit })">
            <mat-icon aria-hidden="true">{{ newTask().partialCredit ? 'check_box' : 'check_box_outline_blank' }}</mat-icon>
            Partial credit
          </button>
        }
        <label class="cs__field"><span class="cs__label">Points</span>
          <input class="cs__input" type="number" min="0" step="1" [ngModel]="newTask().pointValue" name="ntPts"
                 (ngModelChange)="patchDraft({ pointValue: $event })" /></label>
        <button type="button" class="cs__save" [disabled]="addingTask() || !newTask().label.trim()" (click)="addCustomTask()">
          @if (addingTask()) { <span class="cm-spin" aria-hidden="true"></span> Adding… }
          @else { <mat-icon aria-hidden="true">add</mat-icon> Add task }
        </button>

        <!-- Pre-declare cheat days. -->
        @if (!finished()) {
          <h4 class="cs__subhead">Pre-declare cheat days</h4>
          <p class="cs__hint">Future dates only, up to {{ maxCheatDays }} within your window. A cheat day keeps your run counted without completing.</p>
          @if (cheatDays().length) {
            <ul class="cs-cheat__list">
              @for (cd of cheatDays(); track cd.date) {
                <li class="cs-cheat__chip">
                  <mat-icon aria-hidden="true">free_breakfast</mat-icon>
                  {{ friendlyDate(cd.date) }}
                  <button type="button" (click)="removeCheatDay(cd)" [attr.aria-label]="'Remove cheat day ' + friendlyDate(cd.date)"><mat-icon aria-hidden="true">close</mat-icon></button>
                </li>
              }
            </ul>
          }
          <div class="cs__row">
            <label class="cs__field"><span class="cs__label">Add a future date</span>
              <input class="cs__input" type="date" name="cmCheatPick" [ngModel]="cheatPick()"
                     (ngModelChange)="cheatPick.set($event)" [min]="minCheatDate()" [max]="maxChallengeDate()" /></label>
            <button type="button" class="cs__addbtn" [disabled]="!cheatPick()" (click)="addCheatDay()">
              <mat-icon aria-hidden="true">add</mat-icon>
            </button>
          </div>
        }
      </div>
    </app-bs-sheet>

    <app-bs-toaster />
  `,
  styleUrl: './challenge-mobile.page.scss',
})
export class ChallengeMobilePage {
  readonly store = inject(ChallengeStore);
  private auth = inject(AuthService);
  private api = inject(Api);
  private toast = inject(ToastController);
  private destroyRef = inject(DestroyRef);

  readonly totalDays = 75;

  /** Which body the segmented control shows. */
  readonly tab = signal<'today' | 'board'>('today');
  readonly tabs: Segment[] = [
    { key: 'today', label: 'Today' },
    { key: 'board', label: 'Leaderboard' },
  ];

  readonly loading = signal(true);
  readonly refreshing = signal(false);
  readonly errored = signal(false);

  /** The stable `key` of the task whose toggle is mid-flight (guards a double-tap). */
  readonly busyTask = signal<string | null>(null);

  /** The userId whose Nudge is in flight (so the button can't double-fire). */
  readonly nudging = signal<number | null>(null);

  readonly maxCheatDays = MAX_CHEAT_DAYS;

  /** True while a start-challenge POST is in flight (guards a double submit). */
  readonly starting = signal(false);
  /** The chosen start date for a new challenge (ISO "YYYY-MM-DD"); defaults to today. */
  readonly startDate = signal<string>(this.todayIso());

  /** The confession draft for the selected day (bound to the textarea); resynced when the day changes. */
  readonly confessionDraft = signal<string>('');

  /** A future date to add as a cheat day (ISO "YYYY-MM-DD"); '' until picked. */
  readonly cheatPick = signal<string>('');

  /** Sheet open flags. */
  readonly viewSheet = signal(false);
  readonly configSheet = signal(false);
  readonly valueSheet = signal(false);

  /** The manual measurable task being edited in the value sheet, + its draft value. */
  readonly valueTask = signal<HardDayTaskDto | null>(null);
  readonly valueDraft = signal<number | null>(null);

  /** The add-custom-task draft (owner). */
  readonly newTask = signal<NewTaskDraft>(emptyDraft());
  /** True while an add-task POST is in flight. */
  readonly addingTask = signal(false);

  // ---- store-derived view state (parity with the live page) ----
  readonly challenge = computed(() => this.store.challenge());
  readonly hasChallenge = computed(() => !!this.store.challenge());
  readonly readOnly = computed(() => this.store.readOnly());
  readonly leaderboard = computed(() => this.store.leaderboard());

  readonly streak = computed(() => this.challenge()?.currentStreak ?? 0);
  readonly currentDay = computed(() => this.challenge()?.currentDay ?? 0);
  readonly totalPoints = computed(() => this.challenge()?.totalPoints ?? 0);
  readonly todayPoints = computed(() => this.challenge()?.todayPoints ?? 0);
  readonly completedDays = computed(() => this.challenge()?.completedDays ?? 0);
  readonly finished = computed(() => this.challenge()?.status === 'Completed');

  /** Completion fraction 0..1 toward 75 completed days, for the hero ring. */
  readonly completionFrac = computed(() => {
    const c = this.challenge();
    if (!c) return 0;
    return Math.min(1, c.completedDays / this.totalDays);
  });

  /** The selected (today's) day row, or null. */
  readonly day = computed<HardDayDto | null>(() => this.store.selectedDay());
  readonly tasks = computed<HardDayTaskDto[]>(() => this.day()?.tasks ?? []);
  readonly dayPoints = computed(() => this.day()?.dayPoints ?? 0);
  readonly maxPoints = computed(() => this.day()?.maxPoints ?? 0);

  /** A friendly heading for the selected date (Today / Yesterday / weekday). */
  readonly dateHeading = computed(() => {
    const d = new Date(this.store.date() + 'T00:00:00');
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const diff = Math.round((d.getTime() - today.getTime()) / 86_400_000);
    if (diff === 0) return 'Today';
    if (diff === -1) return 'Yesterday';
    if (diff === 1) return 'Tomorrow';
    return d.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' });
  });

  /** Whether the caller can nudge at all (chat.send); the action no-ops without it. */
  readonly canNudge = computed(() => this.auth.hasPermission(PERM.chatSend));

  // ---- config / shared / cheat (parity with the live page) ----

  /** The configurable task set for the loaded challenge. */
  readonly storeTasks = computed<HardTaskDto[]>(() => this.store.tasks());

  /** People whose challenge the caller may view read-only (for the shared-view selector). */
  readonly shared = computed<HardSharedPersonDto[]>(() => this.store.shared());

  /** Whose challenge is being viewed (null = own). */
  readonly viewUser = computed(() => this.store.viewUser());

  /** The person being viewed (a shared row or a synthesised fallback), or null on own view. */
  readonly viewingUser = computed<HardSharedPersonDto | null>(() => {
    const userId = this.store.viewUser();
    if (userId == null) return null;
    return this.store.shared().find((s) => s.userId === userId)
      ?? { userId, name: this.store.challenge()?.userName ?? 'Unknown user' };
  });

  /** The diet task in the loaded set, if enabled. */
  readonly dietTaskEnabled = computed(() => this.storeTasks().some((t) => t.autoSource === 'Diet' && t.enabled));
  /** The no-alcohol task in the loaded set, if enabled. */
  readonly noAlcoholEnabled = computed(() => this.storeTasks().some((t) => t.autoSource === 'NoAlcohol' && t.enabled));

  /** The future cheat days already declared (within the loaded window), oldest-first, for the chip list. */
  readonly cheatDays = computed<HardDayDto[]>(() => {
    const today = this.todayIso();
    return this.store.days().filter((d) => d.isCheatDay && d.date > today);
  });

  /** The earliest a cheat day may be (tomorrow), for the date input's min. */
  readonly minCheatDate = computed(() => {
    const d = new Date();
    d.setDate(d.getDate() + 1);
    return this.toLocalDate(d);
  });

  /** The challenge window end (start + 74 days), for the cheat-day input's max. */
  readonly maxChallengeDate = computed<string | null>(() => {
    const c = this.store.challenge();
    if (!c) return null;
    const d = new Date(c.startDate + 'T00:00:00');
    d.setDate(d.getDate() + this.totalDays - 1);
    return this.toLocalDate(d);
  });

  constructor() {
    this.store.goToday();
    void this.store.loadShared();
    this.reload();

    // Keep the confession textarea in sync with whichever day is selected (own view only — a viewer
    // never sees confessions, so the draft stays empty there).
    effect(() => {
      const d = this.day();
      this.confessionDraft.set(this.store.readOnly() ? '' : (d?.confession ?? ''));
    });
  }

  // ─────────────── LOAD ───────────────

  async reload(): Promise<void> {
    const wasLoaded = this.store.loaded();
    if (wasLoaded) this.refreshing.set(true); else this.loading.set(true);
    this.errored.set(false);
    try {
      await this.store.load();
      if (this.store.error()) this.errored.set(true);
      // Leaderboard is best-effort (the store swallows its own errors → []).
      await this.store.loadLeaderboard();
    } catch {
      this.errored.set(true);
    } finally {
      this.loading.set(false);
      this.refreshing.set(false);
    }
  }

  setTab(key: string): void {
    this.tab.set(key === 'board' ? 'board' : 'today');
  }

  // ─────────────── TODAY: task helpers (mirrors live challenge.ts) ───────────────

  taskIcon(t: { autoSource: string }): string {
    return AUTO_ICON[t.autoSource] ?? AUTO_ICON['None'];
  }

  /** Whether a task is auto-scored from the tracker (cannot be hand-toggled here). */
  isAuto(t: { autoSource: string }): boolean {
    return t.autoSource !== 'None';
  }

  /** Whether a task is MEASURABLE (has a numeric target → progress bar + value), else binary. */
  isMeasurable(t: { targetValue: number | null }): boolean {
    return t.targetValue != null;
  }

  /** Progress as a 0..100 percent for a task's bar. */
  pct(t: { progress: number }): number {
    return Math.round(Math.min(1, Math.max(0, t.progress)) * 100);
  }

  /** A short "X / target unit" measured label for a measurable task. */
  measuredLabel(t: HardDayTaskDto): string {
    const value = this.fmt(t.value ?? 0);
    const target = this.fmt(t.targetValue ?? 0);
    return `${value} / ${target}${t.unit ? ' ' + t.unit : ''}`;
  }

  /** A short scoring hint for an auto task (how it derives from the tracker). */
  autoHint(t: HardDayTaskDto): string {
    switch (t.autoSource) {
      case 'Diet': return 'within your tracker goals';
      case 'Water': return 'from your hydration log';
      case 'Workout': return 'logged workouts that hit the target';
      case 'NoAlcohol': return 'no-alcohol attestation';
      default: return '';
    }
  }

  /** Whether a MANUAL measurable task takes a value input (reading pages / custom). */
  isManualMeasurable(t: HardDayTaskDto): boolean {
    return t.autoSource === 'None' && t.targetValue != null;
  }

  taskAria(t: HardDayTaskDto): string {
    const state = t.complete ? 'complete' : t.progress > 0 ? `${this.pct(t)}% done` : 'not done';
    if (this.isAuto(t)) return `${t.label}, ${state}, scored from your tracker.`;
    if (this.isMeasurable(t)) return `${t.label}, ${this.measuredLabel(t)}, ${state}. Tap to enter a value.`;
    return `${t.label}, ${state}. Tap to toggle.`;
  }

  /**
   * Tap a task row. Manual BINARY tasks toggle done/not. Manual MEASURABLE tasks open a value sheet where
   * the caller types a partial value (not just fill/clear). Auto tasks (and read-only views) are inert —
   * they score live from the tracker, so we point the user at the tracker for those.
   */
  onTaskTap(t: HardDayTaskDto): void {
    if (this.readOnly()) return;
    if (this.busyTask()) return;
    if (this.isAuto(t)) {
      this.toast.show('This one scores from your tracker — log it there.', { tone: 'neutral' });
      return;
    }
    if (this.isManualMeasurable(t)) {
      this.valueTask.set(t);
      this.valueDraft.set(t.value ?? null);
      this.valueSheet.set(true);
    } else {
      void this.saveTask(t, { tasks: [{ key: t.key, done: !t.complete }] });
    }
  }

  /** Commit the typed value for the manual measurable task in the value sheet. */
  async saveManualValue(): Promise<void> {
    const t = this.valueTask();
    if (!t || this.readOnly()) return;
    const raw = this.valueDraft();
    const value = raw == null || isNaN(raw) ? 0 : Math.max(0, raw);
    await this.saveTask(t, { tasks: [{ key: t.key, value }] });
    if (!this.busyTask()) this.valueSheet.set(false);
  }

  private async saveTask(t: HardDayTaskDto, patch: Partial<UpsertHardDayRequest>): Promise<void> {
    this.busyTask.set(t.key);
    try {
      await this.store.upsertDay({ date: this.store.date(), ...patch });
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not save — try again.'), { tone: 'warn' });
    } finally {
      this.busyTask.set(null);
    }
  }

  // ─────────────── START + DAY NAV ───────────────

  /** Start a 75 Hard run (own; one active at a time). */
  async start(): Promise<void> {
    if (this.starting()) return;
    this.starting.set(true);
    try {
      await this.store.start({ startDate: this.startDate() || undefined });
      this.store.goToday();
      this.toast.show('Your 75 Hard has begun — day 1!', { tone: 'success' });
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not start your challenge.'), { tone: 'warn' });
      await this.store.load();
    } finally {
      this.starting.set(false);
    }
  }

  prevDay(): void { this.store.shiftDate(-1); }
  nextDay(): void { this.store.shiftDate(1); }
  goToday(): void { this.store.goToday(); }
  onDateInput(value: string): void { if (value) this.store.setDate(value); }

  // ─────────────── DAY-LEVEL ATTESTATIONS (own view) ───────────────

  /** Whether the diet override is forcing a result (true/false), or null when using the auto value. */
  dietOverride(): boolean | null {
    return this.day()?.dietOverride ?? null;
  }

  /** Set the diet override (On plan / Off plan). It WINS over the auto value. */
  setDietOverride(mode: 'pass' | 'fail'): void {
    if (this.readOnly()) return;
    void this.saveDay({ dietOverride: mode === 'pass' });
  }

  /** Toggle the no-alcohol rule for the day (drives the seeded no-alcohol task). */
  toggleNoAlcohol(checked: boolean): void {
    void this.saveDay({ noAlcohol: checked });
  }

  /** Save the confession draft for the selected day (owner). */
  saveConfession(): void {
    if (this.readOnly()) return;
    void this.saveDay({ confession: this.confessionDraft().trim() });
  }

  /** Upsert the manual portion of the selected day, then a gentle error path. */
  private async saveDay(patch: Partial<UpsertHardDayRequest>): Promise<void> {
    if (this.readOnly()) return;
    try {
      await this.store.upsertDay({ date: this.store.date(), ...patch });
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not save — try again.'), { tone: 'warn' });
    }
  }

  // ─────────────── SHARED VIEW ───────────────

  openConfig(): void { this.configSheet.set(true); }

  viewSelf(): void {
    void this.store.viewUserTracker(null).then(() => {
      this.store.goToday();
      void this.store.loadLeaderboard();
    });
  }
  viewOther(userId: number): void {
    void this.store.viewUserTracker(userId).then(() => {
      this.store.goToday();
      void this.store.loadLeaderboard();
    });
  }

  // ─────────────── TASK CONFIG (owner) ───────────────

  async toggleTaskEnabled(t: HardTaskDto, enabled: boolean): Promise<void> { await this.patchTask(t.id, { enabled }); }
  async toggleTaskPartial(t: HardTaskDto, partialCredit: boolean): Promise<void> { await this.patchTask(t.id, { partialCredit }); }

  async saveTaskTarget(t: HardTaskDto, value: number | null): Promise<void> {
    if (value == null || isNaN(value) || value <= 0) return;
    await this.patchTask(t.id, { targetValue: value });
  }
  async saveTaskMinMinutes(t: HardTaskDto, value: number | null): Promise<void> {
    if (value == null || isNaN(value) || value <= 0) return;
    await this.patchTask(t.id, { minMinutes: Math.round(value) });
  }
  async saveTaskActiveCal(t: HardTaskDto, value: number | null): Promise<void> {
    const next = value == null || isNaN(value) || value <= 0 ? null : Math.min(100000, Math.max(1, Math.round(value)));
    if (next === t.activeCalPerWorkout) return;
    await this.patchTask(t.id, { activeCalPerWorkout: next });
  }
  async saveTaskPoints(t: HardTaskDto, value: number | null): Promise<void> {
    if (value == null || isNaN(value)) return;
    await this.patchTask(t.id, { pointValue: Math.max(0, Math.round(value)) });
  }
  async saveTaskLabel(t: HardTaskDto, label: string): Promise<void> {
    const clean = label.trim();
    if (!clean || clean === t.label) return;
    await this.patchTask(t.id, { label: clean });
  }

  private async patchTask(id: number, body: UpdateHardTaskRequest): Promise<void> {
    if (this.readOnly()) return;
    try {
      await this.store.updateTask(id, body);
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not update that task.'), { tone: 'warn' });
    }
  }

  /** Delete a CUSTOM task (auto tasks can only be disabled). */
  async deleteTask(t: HardTaskDto): Promise<void> {
    if (this.readOnly() || this.isAuto(t)) return;
    try {
      await this.store.deleteTask(t.id);
      this.toast.show('Task removed.', { tone: 'success', durationMs: 1600 });
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not remove that task.'), { tone: 'warn' });
    }
  }

  /** Patch a field on the new-task draft. */
  patchDraft(patch: Partial<NewTaskDraft>): void {
    this.newTask.update((d) => ({ ...d, ...patch }));
  }

  /** Add the custom manual task from the draft (owner). */
  async addCustomTask(): Promise<void> {
    if (this.readOnly() || this.addingTask()) return;
    const d = this.newTask();
    const label = d.label.trim();
    if (!label) { this.toast.show('Give your task a name first.', { tone: 'warn' }); return; }
    const body: CreateHardTaskRequest = {
      label,
      pointValue: Math.max(0, Math.round(d.pointValue || 0)),
      targetValue: d.measurable ? Math.max(0.01, d.targetValue ?? 1) : null,
      unit: d.measurable ? d.unit.trim() || null : null,
      partialCredit: d.measurable ? d.partialCredit : false,
    };
    this.addingTask.set(true);
    try {
      await this.store.createTask(body);
      this.newTask.set(emptyDraft());
      this.toast.show('Custom task added.', { tone: 'success', durationMs: 1600 });
      void this.store.loadLeaderboard();
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not add that task.'), { tone: 'warn' });
    } finally {
      this.addingTask.set(false);
    }
  }

  // ─────────────── CHEAT DAYS (future-only, owner) ───────────────

  async addCheatDay(): Promise<void> {
    const date = this.cheatPick();
    if (this.readOnly() || !date) return;
    if (date <= this.todayIso()) { this.toast.show('Cheat days must be in the future.', { tone: 'warn' }); return; }
    if (this.cheatDays().length >= MAX_CHEAT_DAYS) {
      this.toast.show(`You can declare at most ${MAX_CHEAT_DAYS} cheat days.`, { tone: 'warn' });
      return;
    }
    try {
      await this.store.setCheatDays({ add: [date] });
      this.cheatPick.set('');
      this.toast.show('Cheat day added.', { tone: 'success', durationMs: 1600 });
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not add that cheat day.'), { tone: 'warn' });
    }
  }

  async removeCheatDay(d: HardDayDto): Promise<void> {
    if (this.readOnly()) return;
    try {
      await this.store.setCheatDays({ remove: [d.date] });
      this.toast.show('Cheat day cleared.', { tone: 'success', durationMs: 1600 });
    } catch (e) {
      this.toast.show(this.messageOf(e, 'Could not clear that cheat day.'), { tone: 'warn' });
    }
  }

  /** "Mon, Jun 22" friendly label from a plain ISO date. */
  friendlyDate(iso: string): string {
    const d = new Date(iso + 'T00:00:00');
    return isNaN(d.getTime())
      ? iso
      : d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  // ─────────────── LEADERBOARD ───────────────

  /** Whether a leaderboard row may be nudged: a non-self peer + the caller holds chat.send. */
  canNudgeRow(row: HardLeaderboardRowDto): boolean {
    return !row.isSelf && this.canNudge();
  }

  /** Send a canned NUDGE to a circle peer from their leaderboard row (mirrors the live page). */
  nudge(row: HardLeaderboardRowDto): void {
    if (!this.canNudgeRow(row) || this.nudging() != null) return;
    this.nudging.set(row.userId);
    const kind: NudgeKind = 'keepTheStreak';
    this.api
      .nudge(row.userId, kind)
      .pipe(catchError(() => of(null)))
      .subscribe((res) => {
        this.nudging.set(null);
        if (!res) {
          this.toast.show('Could not send your nudge. Try again.', { tone: 'warn' });
          return;
        }
        this.toast.show(
          res.delivered ? `Nudged ${row.name}!` : `${row.name} was already nudged recently.`,
          { tone: res.delivered ? 'success' : 'neutral' },
        );
      });
  }

  /** Two-letter initials for an avatar fallback (name only; no email — email-privacy). */
  initials(u: { name?: string }): string {
    const parts = (u.name || '').split(/[\s@.]+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }

  // ─────────────── misc ───────────────

  /** Format a (possibly fractional) points/value number with no trailing zeros.
   *  Null-safe: a missing/NaN value (incomplete data) formats to '0' instead of crashing on .toFixed(). */
  fmt(n: number | null | undefined): string {
    if (n == null || !Number.isFinite(n)) return '0';
    return Number.isInteger(n) ? String(n) : n.toFixed(1).replace(/\.0$/, '');
  }

  private toLocalDate(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  private todayIso(): string {
    return this.toLocalDate(new Date());
  }

  private messageOf(e: unknown, fallback: string): string {
    const err = e as { error?: { message?: string; detail?: string; title?: string } };
    const msg = err?.error?.message || err?.error?.detail || err?.error?.title;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
