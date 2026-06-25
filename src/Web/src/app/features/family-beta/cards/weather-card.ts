import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { FamilyWeather } from '../../../core/models';
import { UnitService } from '../../../core/unit.service';

/**
 * Hearth "Weather" glance card — rebuilt on the shared beta-ui foundation as a depth surface (sediment
 * `--bg-rise` + `--lift-2` + an accent gradient hairline edge), rendered ONLY when the page-owned snapshot
 * returned a non-null `weather` (the page guards `@if (today()?.weather)`), exactly like the live
 * family-home weather card. No network of its own; the weather object is passed in. The OpenWeather icon
 * URL is built with a small COPIED helper (no live import); °C/°F follows the user's unit preference.
 */
@Component({
  selector: 'fb-weather-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="wx">
      <span class="wx__edge" aria-hidden="true"></span>
      <img class="wx__icon" [src]="iconUrl()" [alt]="weather().description" referrerpolicy="no-referrer" />
      <div class="wx__text">
        <span class="wx__temp">{{ temp() }}°<span class="wx__unit">{{ unit() }}</span></span>
        <span class="wx__desc">{{ weather().description }}</span>
        <span class="wx__loc">{{ weather().location }} · feels {{ feels() }}° · {{ weather().humidityPct }}% humidity</span>
      </div>
    </section>
  `,
  styles: [`
    .wx {
      position: relative; isolation: isolate; overflow: hidden;
      display: flex; align-items: center; gap: 14px;
      border-radius: var(--r-card); padding: 16px;
      background:
        radial-gradient(120% 90% at 100% 0%, color-mix(in srgb, var(--accent-a) 12%, transparent), transparent 60%),
        var(--bg-rise);
      box-shadow: var(--lift-2); scroll-snap-align: start;
    }
    .wx__edge {
      position: absolute; inset: 0; border-radius: inherit; padding: 1px; pointer-events: none; z-index: 0;
      background: linear-gradient(150deg, color-mix(in srgb, var(--accent-a) 45%, transparent), var(--hairline) 40%);
      -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
      -webkit-mask-composite: xor; mask-composite: exclude;
    }
    .wx > :not(.wx__edge) { position: relative; z-index: 1; }
    .wx__icon { width: 56px; height: 56px; flex: 0 0 auto; }
    .wx__text { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
    .wx__temp { font-family: var(--font-display); font-size: 28px; font-weight: 600; letter-spacing: -.02em; color: var(--ink); line-height: 1; }
    .wx__unit { font-size: 16px; }
    .wx__desc { margin-top: 3px; font-size: 14px; font-weight: 600; color: var(--ink); text-transform: capitalize; }
    .wx__loc { font-size: 12px; color: var(--ink-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  `],
})
export class WeatherCard {
  private readonly units = inject(UnitService);

  readonly weather = input.required<FamilyWeather>();

  readonly iconUrl = computed(() => `https://openweathermap.org/img/wn/${this.weather().icon}@2x.png`);

  /** °C in Metric, °F in Imperial — the wire only carries Fahrenheit, so convert client-side. */
  readonly unit = computed(() => (this.units.imperial() ? 'F' : 'C'));
  readonly temp = computed(() => this.round(this.display(this.weather().tempF)));
  readonly feels = computed(() => this.round(this.display(this.weather().feelsLikeF)));

  /** Wire is Fahrenheit; show as-is in Imperial, convert F->C in Metric. */
  private display(f: number): number {
    return this.units.imperial() ? f : (f - 32) * (5 / 9);
  }

  private round(f: number): number {
    return Math.round(f);
  }
}
