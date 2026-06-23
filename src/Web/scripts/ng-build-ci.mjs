#!/usr/bin/env node
// Wrapper around `ng build` that GUARANTEES the process exits.
//
// Why this exists: the Angular application (esbuild) builder can finish a SUCCESSFUL production
// build — write the full dist + print "Application bundle generation complete" — and then fail to
// terminate (a lingering worker/handle keeps the Node event loop alive). Observed deterministically
// after the tracker-beta addition pushed the build past some threshold. A hung `ng build` wedges the
// Docker `RUN npm run build`, every CI/workflow verify step, and local builds (they sit until killed).
//
// This wrapper streams ng's output unchanged, and: on the completion marker (+ a real dist) it exits 0;
// on a compile error it exits 1; if ng exits on its own it honors that code; a hard timeout is a backstop.
// The produced artifact is identical — we just stop waiting on a process that won't quit.

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';

const args = process.argv.slice(2);
const ngBin = resolve('node_modules/@angular/cli/bin/ng.js');
const distIndex = resolve('dist/ccusage-web/browser/index.html');
const HARD_TIMEOUT_MS = 12 * 60 * 1000;

const child = spawn(process.execPath, [ngBin, 'build', ...args], { stdio: ['inherit', 'pipe', 'pipe'] });

let buf = '';
let settled = false;

const onData = (chunk) => {
  const s = chunk.toString();
  process.stdout.write(s);
  buf += s;
  if (buf.length > 1_000_000) buf = buf.slice(-500_000); // bound memory on chatty builds
  evaluate();
};
child.stdout.on('data', onData);
child.stderr.on('data', onData);
child.on('exit', (code) => settle(code ?? 1, `ng exited (${code})`));
child.on('error', (err) => settle(1, `spawn error: ${err.message}`));

const hardTimer = setTimeout(
  () => settle(existsSync(distIndex) ? 0 : 1, 'hard timeout'),
  HARD_TIMEOUT_MS,
);

function evaluate() {
  if (settled) return;
  if (/Application bundle generation complete/.test(buf) && existsSync(distIndex)) {
    settle(0, 'bundle complete + dist present');
  } else if (/error TS\d|✘ \[ERROR\]|^ERROR /m.test(buf)) {
    settle(1, 'build error detected');
  }
}

function settle(code, reason) {
  if (settled) return;
  settled = true;
  clearTimeout(hardTimer);
  console.error(`\n[ng-build-ci] ${code === 0 ? 'OK' : 'FAIL'} — ${reason}`);
  try { child.kill('SIGKILL'); } catch { /* already gone */ }
  setTimeout(() => process.exit(code), 150); // let stdio flush
}
