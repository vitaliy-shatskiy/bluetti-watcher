// Renders explainer.html frame by frame (English and Ukrainian) at device scale 2 and writes explainer.webp/.mp4 (English)
// and explainer-uk.webp/.mp4 beside it, 1640 px wide for the README's width="820".
// Run from docs/media: npm install && node explainer/render.mjs (needs Chrome and ffmpeg on PATH). No service, no live data.
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';
import { encode, lossless } from '../gif.mjs';

const HERE = import.meta.dirname;
const FPS = 15;
const OUT = { en: 'explainer', uk: 'explainer-uk' }; // README.md shows the first, README.uk.md the second
const browser = await chromium.launch({ channel: 'chrome' });
try {
  for (const lang of ['en', 'uk']) {
    const dir = mkdtempSync(join(tmpdir(), 'bw-explainer-'));
    const page = await browser.newPage({ viewport: { width: 1100, height: 620 }, deviceScaleFactor: 2 });
    await page.goto(`${pathToFileURL(join(HERE, 'explainer.html')).href}?lang=${lang}`);
    const total = await page.evaluate(() => window.TOTAL);
    const frames = Math.ceil(total * FPS);
    for (let f = 0; f < frames; f++) {
      await page.evaluate((t) => window.render(t), f / FPS);
      await page.locator('#stage').screenshot({ path: join(dir, `${String(f).padStart(4, '0')}.png`) });
    }
    await page.close();
    const src = lossless(['-framerate', String(FPS), '-i', join(dir, '%04d.png')], join(dir, 'frames.mkv'));
    const r = encode(src, { out: join(HERE, OUT[lang]), width: 1640, fps: FPS, budget: 5 * 1024 * 1024 });
    rmSync(dir, { recursive: true, force: true });
    if (!r.ok) process.exitCode = 1;
  }
} finally {
  await browser.close();
}
