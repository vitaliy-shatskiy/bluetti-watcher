// Records the real dashboard page through one simulated power cut, then writes demo.gif and demo.mp4.
// Run from docs/media: npm install && node record-demo.mjs (needs Chrome and ffmpeg on PATH).
// The page is src/BluettiWatcher/Web/station.html as shipped; only its /api/* answers are demo data (demo-data.mjs).
import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright-core';
import { ORIGIN, START, CUT_AT, serve } from './demo-data.mjs';

const HERE = import.meta.dirname;
const SIZE = { width: 1280, height: 860 };

let minute = 0;
const videoDir = mkdtempSync(join(tmpdir(), 'bw-demo-'));
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const ctx = await browser.newContext({ viewport: SIZE, colorScheme: 'dark', locale: 'en-US', recordVideo: { dir: videoDir, size: SIZE } });
  await serve(ctx, () => minute);
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
