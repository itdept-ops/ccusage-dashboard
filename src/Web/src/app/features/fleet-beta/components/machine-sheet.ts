import {
  ChangeDetectionStrategy, Component, computed, input, model,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { FleetMachine } from '../../../core/models';
import { CompactPipe, timeAgo } from '../../../shared/format';
import { BetaBottomSheet, BetaStatTile } from '../../beta-ui';
import {
  agentIcon, agentLabel, compactUsd, geoSourceLabel, hasCoords, isLocalName, isOnline,
  locationLabel, mapUrl, ramLabel, systemLabel, uptimeLabel,
} from '../fleet-beta.model';

/** A single label→value detail line for the sheet's spec grid. */
interface DetailRow { icon: string; label: string; value: string; mono?: boolean; }

/**
 * BETA FLEET · MachineSheet — the tap-through detail BottomSheet for one machine, wrapping the kit
 * {@link BetaBottomSheet}. A hero block (icon + online pulse + name + spend/tokens StatTiles), a
 * "Linked users" chip row, then a spec grid of the agent's reported system metadata (IP/OS/CPU/RAM/
 * GPU/uptime/locale/version) and a location row that links out to OpenStreetMap. All from the EXISTING
 * `FleetMachine` DTO — read-only, no fetch.
 */
@Component({
  selector: 'app-fleet-machine-sheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, CompactPipe, BetaBottomSheet, BetaStatTile],
  template: `
    <app-bs-sheet [(open)]="open" detent="half" [label]="sheetLabel()">
      @if (machine(); as m) {
        <div class="ms">
          <!-- Hero -->
          <header class="ms__hero">
            <span class="ms__ico" aria-hidden="true">
              <mat-icon>{{ icon() }}</mat-icon>
              @if (online()) { <span class="ms__pulse"></span> }
            </span>
            <div class="ms__id">
              <h2 class="ms__name">{{ displayName() }}</h2>
              <p class="ms__state">
                @if (online()) { <span class="ms__live">Online now</span> }
                @else { <span>Last seen {{ seen() }}</span> }
                @if (agentLbl()) { <span class="ms__sep" aria-hidden="true">·</span><span>{{ agentLbl() }}</span> }
              </p>
            </div>
          </header>

          <div class="ms__tiles">
            <app-bs-stat-tile [value]="spendLabel()" label="Spend" />
            <app-bs-stat-tile [value]="(m.tokens | compact)" unit="tok" label="Tokens" />
            <app-bs-stat-tile [value]="(m.records | compact)" label="Records" />
          </div>

          <!-- Linked users -->
          <section class="ms__sec">
            <span class="ms__sec-h">Users on this machine</span>
            @if (m.users.length) {
              <div class="ms__chips">
                @for (u of m.users; track u) {
                  <span class="ms__chip"><mat-icon aria-hidden="true">person</mat-icon>{{ u }}</span>
                }
              </div>
            } @else {
              <span class="ms__none">No linked users.</span>
            }
          </section>

          <!-- Spec grid -->
          @if (specs().length) {
            <section class="ms__sec">
              <span class="ms__sec-h">Machine details</span>
              <dl class="ms__grid">
                @for (s of specs(); track s.label) {
                  <div class="ms__cell">
                    <dt><mat-icon aria-hidden="true">{{ s.icon }}</mat-icon> {{ s.label }}</dt>
                    <dd [class.mono]="s.mono">{{ s.value }}</dd>
                  </div>
                }
              </dl>
            </section>
          }

          <!-- Location -->
          @if (locLabel() || coords()) {
            <section class="ms__sec">
              <span class="ms__sec-h">
                Location
                @if (geoLbl()) { <span class="ms__geo" [class.is-gps]="geoLbl() === 'GPS'">{{ geoLbl() }}</span> }
              </span>
              <div class="ms__loc">
                @if (locLabel()) { <span class="ms__place">{{ locLabel() }}</span> }
                @if (coords()) {
                  <a class="ms__map" [href]="mapHref()" target="_blank" rel="noopener noreferrer">
                    Open map <mat-icon aria-hidden="true">open_in_new</mat-icon>
                  </a>
                }
              </div>
            </section>
          }
        </div>
      }
    </app-bs-sheet>
  `,
  styleUrl: './machine-sheet.scss',
})
export class FleetMachineSheet {
  /** Two-way open state — the page flips this when a card is tapped. */
  readonly open = model<boolean>(false);
  /** The machine being detailed (null collapses the sheet body). */
  readonly machine = input<FleetMachine | null>(null);

  protected readonly online = computed(() => isOnline(this.machine()?.lastSeenUtc ?? null));
  protected readonly icon = computed(() => agentIcon(this.machine()?.agent ?? null));
  protected readonly displayName = computed(() => {
    const m = this.machine();
    if (!m) return '';
    return isLocalName(m.name) ? 'local (file sync)' : m.name;
  });
  protected readonly agentLbl = computed(() => agentLabel(this.machine()?.agent ?? null));
  protected readonly seen = computed(() => timeAgo(this.machine()?.lastSeenUtc ?? null));
  protected readonly spendLabel = computed(() => compactUsd(this.machine()?.costUsd ?? 0));
  protected readonly sheetLabel = computed(() => `${this.displayName()} details`);

  protected readonly locLabel = computed(() => { const m = this.machine(); return m ? locationLabel(m) : ''; });
  protected readonly coords = computed(() => { const m = this.machine(); return !!m && hasCoords(m); });
  protected readonly geoLbl = computed(() => geoSourceLabel(this.machine()?.geoSource ?? null));
  protected readonly mapHref = computed(() => { const m = this.machine(); return m && hasCoords(m) ? mapUrl(m) : '#'; });

  /** The reported-metadata rows to show (drops blanks). */
  protected readonly specs = computed<DetailRow[]>(() => {
    const m = this.machine();
    if (!m) return [];
    const rows: DetailRow[] = [];
    const push = (icon: string, label: string, value: string | null | undefined, mono = false) => {
      if (value != null && String(value).trim().length) rows.push({ icon, label, value: String(value), mono });
    };
    push('lan', 'Local IP', m.localIp, true);
    push('public', 'Public IP', m.publicIp, true);
    push('computer', 'OS', [m.os, m.arch].filter((p) => !!p).join(' · '));
    push('account_circle', 'OS user', m.osUser, true);
    push('developer_board', 'CPU', m.cpuModel ?? null);
    if (!m.cpuModel) push('memory', 'CPU cores', m.cpuCount != null ? String(m.cpuCount) : null, true);
    push('sd_card', 'RAM', ramLabel(m.ramTotalMB) || null, true);
    push('videogame_asset', 'GPU', m.gpuModel ?? null);
    push('precision_manufacturing', 'System', systemLabel(m) || null);
    push('timelapse', 'Uptime', uptimeLabel(m.uptimeSec) || null, true);
    push('schedule', 'Time zone', m.timeZoneId ?? null);
    push('translate', 'Locale', m.culture ?? null, true);
    push('verified', 'Reporter', m.reporterVersion ? `v${m.reporterVersion}` : null, true);
    push('code', '.NET', m.frameworkVersion ?? null, true);
    push('history', 'First seen', m.firstSeenUtc ? timeAgo(m.firstSeenUtc) : null);
    return rows;
  });
}
