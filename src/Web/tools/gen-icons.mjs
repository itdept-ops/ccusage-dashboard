#!/usr/bin/env node
// ---------------------------------------------------------------------------
// gen-icons.mjs — rasterize the OS-core brand mark into the PWA PNG icon set.
//
// Renders a self-contained SVG (the orbital OS-core: dark squircle + orbit
// ring + glowing violet core + cyan satellite) at each required size on a
// transparent canvas via the SAME Playwright/Chromium that tools/beta-shot.mjs
// uses, then writes the PNGs in place over src/Web/public/*.
//
// Two art variants:
//   • "any"  icons (192/512, apple-touch 180) — the framed squircle mark,
//     edge-to-edge, the icon IS the rounded tile.
//   • "maskable" 512 — the same core+orbit motif on a full-bleed dark radial
//     field with the mark pulled into the ~80% safe zone (so platform masks
//     that crop to a circle never clip the core).
//
// Usage:  node tools/gen-icons.mjs
// ---------------------------------------------------------------------------
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const __dirname = dirname(fileURLToPath(import.meta.url));
// This script lives at src/Web/tools/ — public/ is one level up at src/Web/public.
const WEB = resolve(__dirname, '..');
const PUBLIC = join(WEB, 'public');

const PLAYWRIGHT_NODE_MODULES =
  process.env.PW_NODE_MODULES ||
  '/c/Users/it_de/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules';

// --- the marks -------------------------------------------------------------
// Shared defs (gradients) — id-namespaced per call so multiple inline marks
// never collide. The core gradient is the brand: warm-white center → violet →
// deep indigo. The squircle is the dark obsidian tile.

/** The FRAMED mark — the rounded tile IS the icon (edge-to-edge "any" purpose).
 *  Drawn on a 64-unit grid; the squircle fills 2..62 so the corner radius reads
 *  at small sizes. */
function framedMark(id) {
  return `
  <defs>
    <radialGradient id="${id}_core" cx="50%" cy="36%" r="68%">
      <stop offset="0%" stop-color="#fbf3ff"/>
      <stop offset="34%" stop-color="#c79bff"/>
      <stop offset="68%" stop-color="#7e63f4"/>
      <stop offset="100%" stop-color="#4a37c4"/>
    </radialGradient>
    <linearGradient id="${id}_sq" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0%" stop-color="#241c52"/>
      <stop offset="46%" stop-color="#140f31"/>
      <stop offset="100%" stop-color="#08061c"/>
    </linearGradient>
    <radialGradient id="${id}_halo" cx="50%" cy="50%" r="50%">
      <stop offset="0%" stop-color="rgba(176,107,255,.55)"/>
      <stop offset="60%" stop-color="rgba(176,107,255,.16)"/>
      <stop offset="100%" stop-color="rgba(176,107,255,0)"/>
    </radialGradient>
    <filter id="${id}_soft" x="-40%" y="-40%" width="180%" height="180%">
      <feGaussianBlur stdDeviation="1.1"/>
    </filter>
  </defs>
  <!-- the dark squircle tile -->
  <rect x="2" y="2" width="60" height="60" rx="17" fill="url(#${id}_sq)"/>
  <rect x="2.5" y="2.5" width="59" height="59" rx="16.5" fill="none" stroke="rgba(176,107,255,.40)" stroke-width="1"/>
  <!-- inner radial halo behind the core -->
  <circle cx="32" cy="32" r="20" fill="url(#${id}_halo)"/>
  <!-- two concentric orbit rings -->
  <ellipse cx="32" cy="32" rx="20.5" ry="20.5" fill="none" stroke="rgba(176,107,255,.30)" stroke-width="1.4"/>
  <ellipse cx="32" cy="32" rx="13.5" ry="13.5" fill="none" stroke="rgba(52,227,232,.22)" stroke-width="1.2"/>
  <!-- the glowing OS core -->
  <circle cx="32" cy="32" r="9.2" fill="url(#${id}_core)"/>
  <circle cx="32" cy="32" r="9.2" fill="none" stroke="rgba(255,255,255,.32)" stroke-width="0.8"/>
  <!-- the single cyan satellite on the outer ring (upper-right, like the hero) -->
  <circle cx="46.5" cy="17.6" r="3.3" fill="#34e3e8" filter="url(#${id}_soft)"/>
  <circle cx="46.5" cy="17.6" r="2.4" fill="#9bf6f8"/>
  <!-- a faint violet satellite on the inner ring for depth -->
  <circle cx="20.4" cy="40.2" r="2" fill="rgba(176,107,255,.85)"/>
`;
}

/** The MASKABLE mark — full-bleed dark field, motif pulled into the safe zone.
 *  No tile corners (the platform mask supplies the shape). The core+orbit live
 *  inside ~64% of the canvas so a circular crop never touches them. */
function maskableMark(id) {
  return `
  <defs>
    <radialGradient id="${id}_bg" cx="50%" cy="42%" r="75%">
      <stop offset="0%" stop-color="#1a1342"/>
      <stop offset="55%" stop-color="#0d0a26"/>
      <stop offset="100%" stop-color="#070418"/>
    </radialGradient>
    <radialGradient id="${id}_core" cx="50%" cy="36%" r="68%">
      <stop offset="0%" stop-color="#fbf3ff"/>
      <stop offset="34%" stop-color="#c79bff"/>
      <stop offset="68%" stop-color="#7e63f4"/>
      <stop offset="100%" stop-color="#4a37c4"/>
    </radialGradient>
    <radialGradient id="${id}_halo" cx="50%" cy="50%" r="50%">
      <stop offset="0%" stop-color="rgba(176,107,255,.50)"/>
      <stop offset="60%" stop-color="rgba(176,107,255,.14)"/>
      <stop offset="100%" stop-color="rgba(176,107,255,0)"/>
    </radialGradient>
    <filter id="${id}_soft" x="-40%" y="-40%" width="180%" height="180%">
      <feGaussianBlur stdDeviation="1.1"/>
    </filter>
  </defs>
  <!-- full-bleed dark field (covers the whole 64 grid; mask will round it) -->
  <rect x="0" y="0" width="64" height="64" fill="url(#${id}_bg)"/>
  <!-- everything below sits inside the ~80% safe zone (centred, scaled ~0.76) -->
  <g transform="translate(32 32) scale(0.74) translate(-32 -32)">
    <circle cx="32" cy="32" r="20" fill="url(#${id}_halo)"/>
    <ellipse cx="32" cy="32" rx="20.5" ry="20.5" fill="none" stroke="rgba(176,107,255,.30)" stroke-width="1.6"/>
    <ellipse cx="32" cy="32" rx="13.5" ry="13.5" fill="none" stroke="rgba(52,227,232,.22)" stroke-width="1.3"/>
    <circle cx="32" cy="32" r="9.2" fill="url(#${id}_core)"/>
    <circle cx="32" cy="32" r="9.2" fill="none" stroke="rgba(255,255,255,.32)" stroke-width="0.9"/>
    <circle cx="46.5" cy="17.6" r="3.3" fill="#34e3e8" filter="url(#${id}_soft)"/>
    <circle cx="46.5" cy="17.6" r="2.4" fill="#9bf6f8"/>
    <circle cx="20.4" cy="40.2" r="2" fill="rgba(176,107,255,.85)"/>
  </g>
`;
}

function svgDoc(inner, transparent) {
  // transparent background for "any" icons (the squircle is the shape); the
  // maskable already paints its own full-bleed field.
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="512" height="512">${inner}</svg>`;
}

const TARGETS = [
  { file: 'icon-192.png', size: 192, mark: framedMark },
  { file: 'icon-512.png', size: 512, mark: framedMark },
  { file: 'apple-touch-icon.png', size: 180, mark: framedMark },
  { file: 'icon-maskable-512.png', size: 512, mark: maskableMark, maskable: true },
];

async function main() {
  const require = createRequire(import.meta.url);
  const winPath = PLAYWRIGHT_NODE_MODULES
    .replace(/^\/c\//, 'C:/')
    .replace(/^\/([a-z])\//, (m, d) => d.toUpperCase() + ':/');
  require('module').Module.globalPaths.push(winPath);
  let playwright;
  try { playwright = require(join(winPath, 'playwright')); } catch { playwright = await import('playwright'); }
  const { chromium } = playwright;

  const browser = await chromium.launch();
  await mkdir(PUBLIC, { recursive: true });

  for (const t of TARGETS) {
    const id = 'm' + t.size + (t.maskable ? 'k' : '');
    const inner = t.mark(id);
    const svg = svgDoc(inner);
    const html = `<!doctype html><html><head><meta charset="utf-8"><style>
      *{margin:0;padding:0}
      html,body{background:transparent}
      #stage{width:${t.size}px;height:${t.size}px}
      #stage svg{width:100%;height:100%;display:block}
    </style></head><body><div id="stage">${svg}</div></body></html>`;

    const ctx = await browser.newContext({
      viewport: { width: t.size, height: t.size },
      deviceScaleFactor: 1,
    });
    const page = await ctx.newPage();
    await page.setContent(html, { waitUntil: 'networkidle' });
    const el = await page.$('#stage');
    const buf = await el.screenshot({ omitBackground: !t.maskable });
    await writeFile(join(PUBLIC, t.file), buf);
    console.log(`  wrote ${t.file}  (${t.size}x${t.size}${t.maskable ? ', maskable' : ''})`);
    await ctx.close();
  }

  await browser.close();
  console.log('icons regenerated.');
}

main().catch((e) => { console.error('FATAL', e); process.exit(1); });
