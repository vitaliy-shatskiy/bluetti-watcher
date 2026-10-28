// Records the real dashboard page through one simulated power cut, then writes demo.gif and demo.mp4.
// Run from docs/media: npm install && node record-demo.mjs (needs Chrome and ffmpeg on PATH).
// The page is src/BluettiWatcher/Web/station.html as shipped; only its /api/* answers are demo data made here.
import { readFileSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright-core';

const HERE = import.meta.dirname;
const PAGE = readFileSync(resolve(HERE, '../../src/BluettiWatcher/Web/station.html'), 'utf8');
const ORIGIN = 'http://127.0.0.1:18940';
const CAP = 2048; // demo station, Wh
const SIZE = { width: 1280, height: 860 };

// Demo story: minute 0..30 on mains, then a cut; the battery drains to the stop level.
const START = Date.parse('2026-10-20T18:00:00+03:00');
const CUT_AT = 30;
function reading(min) {
  const at = new Date(START + min * 60000);
  const load = 360 + Math.round(40 * Math.sin(min / 7));
  if (min < CUT_AT) return { at, soc: 100, inAc: load, inDc: 0, outAc: load, outDc: 0, volt: 228, hz: 50, mains: true };
  const eco = min >= CUT_AT + 3;
  const out = eco ? Math.round(load * 0.62) : load;
  const soc = Math.max(8, Math.round(100 - (min - CUT_AT) * 1.15));
  return { at, soc, inAc: 0, inDc: 0, outAc: out, outDc: 0, volt: null, hz: null, mains: false };
}
const iso = (d) => d.toISOString().replace('Z', '+00:00');
const row = (r) => ({ ...r, at: iso(r.at) });

let minute = 0;
function now() {
  const r = reading(minute);
  const cut = !r.mains;
  const eco = cut && minute >= CUT_AT + 3;
  const level = r.soc <= 10 ? 'stop' : r.soc <= 20 ? 'emergency' : 'ok';
  const hours = cut ? (r.soc / 100) * CAP * 0.85 / Math.max(r.outAc, 1) : null;
  return {
    type: 'AC200PL', capacityWh: CAP, efficiency: 0.85, mode: cut ? 'inverter output' : 'bypass output (D)', modeCode: cut ? 'INVERTER_OUTPUT' : 'BYPASS_OUTPUT_D',
    eco: eco ? 'eco' : '', ecoNote: null, manual: 'auto', modePending: null, modeError: '', modeErrorNote: null,
    level, cut, hibernateDelayedUntil: null, ecoAt: cut && !eco ? iso(new Date(START + (CUT_AT + 3) * 60000)) : null,
    grid: cut ? 'off' : 'on', silent: false, note: '', notes: [], dryRun: false,
    hoursLeft: hours, runtimeSource: cut ? 'model' : null, ratePerHour: cut ? 69 : null,
    hoursLeftLow: hours && hours * 0.85, hoursLeftHigh: hours && hours * 1.12, learnedEfficiency: 0.82,
    thresholds: { emergencyAt: 20, stopAt: 10, warnAt: 7, hibernateAt: 5 },
    last: row(r), wake: { everyMinutes: 0, floorPercent: 10, armed: false, wakeAt: null, disarmOwed: false, error: '' },
    dtekWatcher: null,
  };
}
function history(url) {
  const q = new URL(url).searchParams;
  const to = Number(q.get('to')), from = Number(q.get('from'));
  const rows = [];
  for (let t = Math.max(from, START - 6 * 3600000); t <= to; t += 300000) {
    const m = Math.floor((t - START) / 60000);
    rows.push(row(m < 0 ? { ...reading(0), at: new Date(t) } : { ...reading(Math.min(m, minute)), at: new Date(t) }));
  }
  return { step: 60, rows };
}

const json = (route, body) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
const videoDir = mkdtempSync(join(tmpdir(), 'bw-demo-'));
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const ctx = await browser.newContext({ viewport: SIZE, colorScheme: 'dark', locale: 'en-US', recordVideo: { dir: videoDir, size: SIZE } });
  await ctx.route(`${ORIGIN}/**`, (route) => {
    const u = route.request().url();
    if (u.includes('/api/now')) return json(route, now());
    if (u.includes('/api/history')) return json(route, history(u));
    if (u.includes('/api/mode')) return json(route, { ok: true });
    return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: PAGE });
  });
  const page = await ctx.newPage();
  await page.clock.setFixedTime(START);
  await page.goto(`${ORIGIN}/`);
  await page.waitForTimeout(1500);
  // Each step: the simulated clock moves, the page polls as it does every minute.
  for (minute = 20; minute <= CUT_AT + 80; minute += minute < CUT_AT ? 2 : 3) {
    await page.clock.setFixedTime(START + minute * 60000);
    await page.evaluate(() => load(true));
    await page.waitForTimeout(minute >= CUT_AT && minute <= CUT_AT + 4 ? 1400 : 600);
  }
  await page.waitForTimeout(1500);
  await ctx.close();
} finally {
  await browser.close();
}
const webm = join(videoDir, readdirSync(videoDir).find((f) => f.endsWith('.webm')));
const ff = (args) => execFileSync('ffmpeg', ['-y', '-v', 'error', ...args], { windowsHide: true, stdio: 'inherit', timeout: 300000 });
ff(['-i', webm, '-vf', 'fps=5,scale=720:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=64:stats_mode=diff[p];[b][p]paletteuse=dither=none:diff_mode=rectangle', join(HERE, 'demo.gif')]);
ff(['-i', webm, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', join(HERE, 'demo.mp4')]);
rmSync(videoDir, { recursive: true, force: true });
console.log('record-demo.mjs: wrote demo.gif, demo.mp4');
