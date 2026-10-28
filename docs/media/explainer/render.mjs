// Renders explainer.html frame by frame (English and Ukrainian) and writes explainer.gif/.mp4 (English) and explainer-uk.gif/.mp4 beside it.
// Run from docs/media: npm install && node explainer/render.mjs (needs Chrome and ffmpeg on PATH). No service, no live data.
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';
import { encode, ff } from '../gif.mjs';

const HERE = import.meta.dirname;
const FPS = 15;
const OUT = { en: 'explainer', uk: 'explainer-uk' }; // README.md shows the first, README.uk.md the second
const browser = await chromium.launch({ channel: 'chrome' });
try {
  for (const lang of ['en', 'uk']) {
    const dir = mkdtempSync(join(tmpdir(), 'bw-explainer-'));
    const page = await browser.newPage({ viewport: { width: 1100, height: 620 }, deviceScaleFactor: 1 });
    await page.goto(`${pathToFileURL(join(HERE, 'explainer.html')).href}?lang=${lang}`);
    const total = await page.evaluate(() => window.TOTAL);
    const frames = Math.ceil(total * FPS);
    for (let f = 0; f < frames; f++) {
      await page.evaluate((t) => window.render(t), f / FPS);
      await page.locator('#stage').screenshot({ path: join(dir, `${String(f).padStart(4, '0')}.png`) });
    }
    await page.close();
    const src = join(dir, 'frames.mp4');
    ff(['-framerate', String(FPS), '-i', join(dir, '%04d.png'), '-c:v', 'libx264', '-pix_fmt', 'yuv420p', src]);
    const r = encode(src, { out: join(HERE, OUT[lang]), width: 880, fps: FPS, budget: 2 * 1024 * 1024 });
    rmSync(dir, { recursive: true, force: true });
    if (!r.ok) process.exitCode = 1;
  }
} finally {
  await browser.close();
}
