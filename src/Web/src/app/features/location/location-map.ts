import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  input,
  output,
  signal,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import type * as L from 'leaflet';

import { loadLeaflet, OSM_ATTRIBUTION, OSM_TILE_URL } from '../../shared/leaflet-loader';

/**
 * One pin on the map. `kind` drives the marker colour (user = accent, machine = a distinct amber so the
 * fleet's IP-geo pins read apart from people). `id` is echoed back on click so the parent can select.
 */
export interface MapPin {
  id: string;
  lat: number;
  lng: number;
  /** A short popup title (e.g. a name) — kept free of email per the privacy rule. */
  title: string;
  /** A second popup line (e.g. "City · 3m ago"). Optional. */
  subtitle?: string;
  kind?: 'user' | 'machine';
  /** True to render this pin emphasised (e.g. the selected user's latest). */
  emphasis?: boolean;
}

/** An ordered polyline (a user's history trail), drawn faintly under the pins. */
export interface MapTrail {
  points: [number, number][];
  /** Optional stroke colour (e.g. a per-member replay colour). Defaults to the muted blue. */
  color?: string;
  /** Optional stroke opacity (0–1). Defaults to 0.45. */
  opacity?: number;
  /** Optional stroke weight in px. Defaults to 2. */
  weight?: number;
}

/**
 * A thin Leaflet wrapper. Leaflet is DYNAMIC-imported (see leaflet-loader) so it never bloats the main
 * bundle. The component owns the map instance + layers and reconciles them whenever the `pins`/`trails`
 * inputs change; it emits `pinClick` with the pin id. OpenStreetMap tiles (free, no API key) — Google
 * Maps is a planned future swap that would touch only the loader + this file.
 */
@Component({
  selector: 'app-location-map',
  standalone: true,
  template: `<div #host class="leaflet-host" tabindex="0" role="application" [attr.aria-label]="mapLabel()"></div>
    @if (!ready()) {
      <div class="leaflet-loading">Loading map…</div>
    }
    <!-- Text-alternative: a non-visual list of every plotted location so the map's data is NOT
         pointer-locked. Keyboard/SR users can read all pins (and Enter selects one). -->
    @if (pins().length) {
      <ul class="sr-only" aria-label="Plotted locations">
        @for (p of pins(); track p.id) {
          <li>
            <button type="button" (click)="pinClick.emit(p.id)">
              {{ p.title }}{{ p.subtitle ? ' — ' + p.subtitle : '' }}
              (latitude {{ p.lat | number: '1.0-4' }}, longitude {{ p.lng | number: '1.0-4' }})
            </button>
          </li>
        }
      </ul>
    }`,
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [
    `
      :host {
        position: relative;
        display: block;
        width: 100%;
        height: 100%;
        min-height: 320px;
      }
      .leaflet-host {
        width: 100%;
        height: 100%;
        min-height: 320px;
        border-radius: var(--tech-r-control, 10px);
      }
      .leaflet-loading {
        position: absolute;
        inset: 0;
        display: grid;
        place-items: center;
        font-family: var(--tech-font-mono);
        font-size: var(--tech-fs-label, 11px);
        color: var(--tech-text-tertiary, #5e6c82);
        pointer-events: none;
      }
      /* Visually hidden but available to screen readers / keyboard — the map's text alternative. */
      .sr-only {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: -1px;
        padding: 0;
        overflow: hidden;
        clip: rect(0 0 0 0);
        clip-path: inset(50%);
        white-space: nowrap;
        border: 0;
      }
    `,
  ],
})
export class LocationMap implements AfterViewInit, OnDestroy {
  /** Pins to render. */
  readonly pins = input<MapPin[]>([]);
  /** Optional history trails (polylines). */
  readonly trails = input<MapTrail[]>([]);
  /**
   * When false, the map stops auto-fitting/recentring as the pins change — the caller owns the
   * viewport. Used by the replay scrubber, where markers move every frame and an auto-fit would
   * yank the view on every tick. Defaults to true (the live finder's recentre-on-change behaviour).
   */
  readonly fitOnChange = input<boolean>(true);
  /** Fired with a pin's id when the user clicks its marker. */
  readonly pinClick = output<string>();

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');

  readonly ready = signal(false);
  private leaflet: typeof L | null = null;
  private map: L.Map | null = null;
  private markerLayer: L.LayerGroup | null = null;
  private trailLayer: L.LayerGroup | null = null;
  private resizeObserver: ResizeObserver | null = null;
  /** A fit requested via {@link fitTo} before the map finished initialising — flushed once ready. */
  private pendingFit: [number, number][] | null = null;

  /** Accessible name for the focusable map host — also tells keyboard users how to drive it. */
  readonly mapLabel = computed(() => {
    const n = this.pins().length;
    return `Map of ${n} location${n === 1 ? '' : 's'}; use arrow keys to pan, plus and minus to zoom`;
  });

  /** A stable key for the current pin set so we only refit the view when the markers actually change. */
  private readonly pinKey = computed(() =>
    this.pins()
      .map((p) => `${p.id}:${p.lat.toFixed(4)},${p.lng.toFixed(4)}`)
      .join('|'),
  );
  private lastFitKey = '';

  constructor() {
    // Reconcile layers whenever inputs change AND the map is ready.
    effect(() => {
      this.pins();
      this.trails();
      this.ready();
      if (this.map) this.render();
    });
  }

  async ngAfterViewInit(): Promise<void> {
    this.leaflet = await loadLeaflet();
    const Lm = this.leaflet;

    this.map = Lm.map(this.host().nativeElement, {
      center: [20, 0],
      zoom: 2,
      zoomControl: true,
      attributionControl: true,
      // Container is focusable (tabindex on the host) — enable Leaflet's built-in keyboard pan/zoom
      // (arrows pan, +/- zoom) so the map is operable without a pointer.
      keyboard: true,
    });
    Lm.tileLayer(OSM_TILE_URL, { maxZoom: 19, attribution: OSM_ATTRIBUTION }).addTo(this.map);
    this.markerLayer = Lm.layerGroup().addTo(this.map);
    this.trailLayer = Lm.layerGroup().addTo(this.map);
    this.ready.set(true);
    this.render();

    // Flush a fit requested before the map was ready (e.g. the replay's first-load fit-to-bounds).
    if (this.pendingFit) {
      const pts = this.pendingFit;
      this.pendingFit = null;
      this.fitTo(pts);
    }

    // Leaflet caches the container's pixel size at init; it doesn't observe later layout changes. Refresh
    // once after first paint (fonts/grid settle) and on any container resize so tiles never stay gray/
    // mis-centred after a responsive collapse.
    requestAnimationFrame(() => this.map?.invalidateSize());
    this.resizeObserver = new ResizeObserver(() => this.map?.invalidateSize());
    this.resizeObserver.observe(this.host().nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.map?.remove();
    this.map = null;
  }

  /**
   * Imperatively fit the viewport to a set of [lat, lng] points (e.g. all of a replay's trail points).
   * Used when `fitOnChange` is false but the caller still wants a ONE-TIME fit — the replay opens framed
   * on the trail extent instead of the world. If called before the map is ready, the fit is deferred and
   * flushed once init completes. Also primes `lastFitKey` so the subsequent per-frame render doesn't undo
   * this fit. Safe to call with 0/1 points.
   */
  fitTo(points: [number, number][]): void {
    if (!points.length) return;
    if (!this.leaflet || !this.map) {
      this.pendingFit = points;
      return;
    }
    const Lm = this.leaflet;
    // Don't let the next pinKey-driven auto-fit (if ever re-enabled) immediately re-fit over this.
    this.lastFitKey = this.pinKey();
    if (points.length === 1) {
      this.map.setView(points[0], 12);
    } else {
      const bounds = Lm.latLngBounds(points);
      this.map.fitBounds(bounds, { padding: [40, 40], maxZoom: 14 });
    }
  }

  private render(): void {
    const Lm = this.leaflet;
    if (!Lm || !this.map || !this.markerLayer || !this.trailLayer) return;

    this.trailLayer.clearLayers();
    for (const t of this.trails()) {
      if (t.points.length > 1) {
        Lm.polyline(t.points, {
          color: t.color ?? '#5b8def',
          weight: t.weight ?? 2,
          opacity: t.opacity ?? 0.45,
        }).addTo(this.trailLayer);
      }
    }

    this.markerLayer.clearLayers();
    const pins = this.pins();
    for (const p of pins) {
      const marker =
        p.kind === 'machine'
          ? Lm.circleMarker([p.lat, p.lng], {
              radius: p.emphasis ? 9 : 7,
              color: '#b9770e',
              fillColor: '#f0a020',
              fillOpacity: 0.85,
              weight: 2,
            })
          : Lm.marker([p.lat, p.lng], { riseOnHover: true });
      const safeTitle = this.escape(p.title);
      const safeSub = p.subtitle
        ? `<br><span class="lp-sub">${this.escape(p.subtitle)}</span>`
        : '';
      marker.bindPopup(`<strong>${safeTitle}</strong>${safeSub}`);
      marker.on('click', () => this.pinClick.emit(p.id));
      marker.addTo(this.markerLayer);
    }

    // Fit the view to the markers the first time we see this pin set (so re-selection within the same set
    // doesn't yank the viewport). One pin → a gentle zoom; many → fit bounds with padding.
    const key = this.pinKey();
    if (pins.length && this.fitOnChange() && key !== this.lastFitKey) {
      this.lastFitKey = key;
      if (pins.length === 1) {
        this.map.setView([pins[0].lat, pins[0].lng], 12);
      } else {
        const bounds = Lm.latLngBounds(pins.map((p) => [p.lat, p.lng] as [number, number]));
        this.map.fitBounds(bounds, { padding: [40, 40], maxZoom: 14 });
      }
    }
  }

  /** Minimal HTML-escape for popup text (titles/cities are server data, but belt-and-suspenders). */
  private escape(s: string): string {
    return s.replace(
      /[&<>"']/g,
      (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c,
    );
  }
}
