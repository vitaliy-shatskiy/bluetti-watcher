// Records the real dashboard page through one simulated power cut, then writes demo.gif and demo.mp4 (gif.mjs).
// Run from docs/media: npm install && node record-demo.mjs (needs Chrome and ffmpeg on PATH).
// The page is src/BluettiWatcher/Web/station.html as shipped; only its /api/* answers are demo data (demo-data.mjs).
// The video is cut to start once the page has drawn, so the GIF's first frame is the full dashboard.
import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright-core';
import { ORIGIN, START, CUT_AT, serve } from './demo-data.mjs';
import { encode, ff } from './gif.mjs';

const HERE = import.meta.dirname;
const SIZE = { width: 1280, height: 860 };
const END = CUT_AT + 420; // seven hours into the cut, the charge near the stop level

let minute = 20;
let lead = 0;
const videoDir = mkdtempSync(join(tmpdir(), 'bw-demo-'));
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const ctx = await browser.newContext({ viewport: SIZE, colorScheme: 'dark', locale: 'en-US', recordVideo: { dir: videoDir, size: SIZE } });
  await serve(ctx, () => minute);
  const t0 = Date.now();
  const page = await ctx.newPage();
  await page.clock.setFixedTime(START + minute * 60000);
  await page.goto(`${ORIGIN}/`);
  await page.waitForTimeout(2000);
  lead = (Date.now() - t0) / 1000;
  await page.waitForTimeout(800);
  // Each step: the simulated clock moves, the page polls as it does every minute. Slow around the cut, fast on battery.
  while (minute < END) {
    minute += minute < CUT_AT - 2 ? 2 : minute < CUT_AT + 6 ? 1 : 12;
    await page.clock.setFixedTime(START + minute * 60000);
    await page.evaluate(() => load(true));
    await page.waitForTimeout(minute >= CUT_AT && minute <= CUT_AT + 6 ? 900 : 450);
  }
  await page.waitForTimeout(1800);
  await ctx.close();
} finally {
  await browser.close();
}
const webm = join(videoDir, readdirSync(videoDir).find((f) => f.endsWith('.webm')));
const cut = join(videoDir, 'cut.mp4');
ff(['-ss', lead.toFixed(2), '-i', webm, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '18', cut]);
const r = encode(cut, { out: join(HERE, 'demo'), width: 740, fps: 5, budget: 2 * 1024 * 1024 });
rmSync(videoDir, { recursive: true, force: true });
if (!r.ok) process.exitCode = 1;
