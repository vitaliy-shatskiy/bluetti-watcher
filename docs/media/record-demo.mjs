// Records the real dashboard page through one simulated power cut, then writes demo.webp and demo.mp4 (gif.mjs).
// Run from docs/media: npm install && node record-demo.mjs (needs Chrome and ffmpeg on PATH).
// The page is src/BluettiWatcher/Web/station.html as shipped; only its /api/* answers are demo data (demo-data.mjs).
// Screenshots at device scale 2, each kept for the wall time until the next one (no lossy screencast). A 904 px
// viewport is the narrowest desktop layout (the 900 px breakpoint), so the README's width="820" shrinks it by 0.91
// only: 12 px text shows at 11 px. 890 px tall = the header, the tiles and the chart, the rest of the page cut off.
// Capture starts once the page has drawn: the first frame is the full dashboard.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from 'playwright-core';
import { ORIGIN, START, CUT_AT, serve } from './demo-data.mjs';
import { encode, lossless } from './gif.mjs';

const HERE = import.meta.dirname;
const SIZE = { width: 904, height: 890 };
const END = CUT_AT + 420; // seven hours into the cut, the charge near the stop level

let minute = 20;
const dir = mkdtempSync(join(tmpdir(), 'bw-demo-'));
const frames = []; // { file, at } at = ms since the first frame
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const ctx = await browser.newContext({ viewport: SIZE, deviceScaleFactor: 2, colorScheme: 'dark', locale: 'en-US' });
  await serve(ctx, () => minute);
  const page = await ctx.newPage();
  await page.clock.setFixedTime(START + minute * 60000);
  await page.goto(`${ORIGIN}/`);
  await page.waitForTimeout(2000);
  const t0 = Date.now();
  // Screenshots until ms have passed since this hold began.
  const hold = async (ms) => {
    const until = Date.now() + ms;
    do {
      const file = join(dir, `${String(frames.length).padStart(5, '0')}.png`);
      frames.push({ file, at: Date.now() - t0 });
      await page.screenshot({ path: file });
    } while (Date.now() < until);
  };
  await hold(800);
  // Each step: the simulated clock moves, the page polls as it does every minute. Slow around the cut, fast on battery.
  while (minute < END) {
    minute += minute < CUT_AT - 2 ? 2 : minute < CUT_AT + 6 ? 1 : 12;
    await page.clock.setFixedTime(START + minute * 60000);
    await page.evaluate(() => load(true));
    await hold(minute >= CUT_AT && minute <= CUT_AT + 6 ? 900 : 450);
  }
  await hold(1800);
  frames.push({ at: Date.now() - t0 });
  await ctx.close();
} finally {
  await browser.close();
}
// ffconcat: every screenshot lasts until the next one was taken.
const list = ['ffconcat version 1.0'];
for (let i = 0; i < frames.length - 1; i++) list.push(`file '${frames[i].file.replaceAll('\\', '/')}'`, `duration ${((frames[i + 1].at - frames[i].at) / 1000).toFixed(3)}`);
list.push(`file '${frames.at(-2).file.replaceAll('\\', '/')}'`);
writeFileSync(join(dir, 'frames.txt'), list.join('\n'));
const src = lossless(['-f', 'concat', '-safe', '0', '-i', join(dir, 'frames.txt'), '-fps_mode', 'vfr'], join(dir, 'frames.mkv'));
const r = encode(src, { out: join(HERE, 'demo'), width: 1640, fps: 12, budget: 5 * 1024 * 1024 });
console.log(`record-demo: ${frames.length - 1} screenshots over ${(frames.at(-1).at / 1000).toFixed(1)} s`);
rmSync(dir, { recursive: true, force: true });
if (!r.ok) process.exitCode = 1;
