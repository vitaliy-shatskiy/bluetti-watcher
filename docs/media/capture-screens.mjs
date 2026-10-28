// Captures framed screenshots of the real dashboard on demo data: screen-uk.png (desktop, Ukrainian)
// and screen-mobile.png (phone, English). Run from docs/media: npm install && node capture-screens.mjs (needs Chrome).
import { join } from 'node:path';
import { chromium } from 'playwright-core';
import { ORIGIN, START, CUT_AT, serve } from './demo-data.mjs';

const HERE = import.meta.dirname;
const MINUTE = CUT_AT + 240; // four hours into the cut: eco on, about half the battery left
const SHOTS = [
  { file: 'screen-uk.png', locale: 'uk-UA', viewport: { width: 1280, height: 860 }, scale: 1, frame: 1360 },
  { file: 'screen-mobile.png', locale: 'en-US', viewport: { width: 390, height: 844 }, scale: 2, frame: 470 },
];
// Backdrop and shadow in the dashboard's palette (station.html :root).
const FRAME = (img, w) => `<body style="margin:0;background:#0d0e10"><div id="f" style="width:${w}px;padding:40px;box-sizing:border-box;
background:linear-gradient(135deg,#1d2a44 0%,#16181c 55%,#2a2216 100%)"><img src="data:image/png;base64,${img}"
style="display:block;width:100%;border-radius:14px;box-shadow:0 18px 50px rgba(0,0,0,.55),0 0 0 1px #2c2e33"></div></body>`;

const browser = await chromium.launch({ channel: 'chrome' });
try {
  for (const s of SHOTS) {
    const ctx = await browser.newContext({ viewport: s.viewport, deviceScaleFactor: s.scale, colorScheme: 'dark', locale: s.locale });
    await serve(ctx, () => MINUTE);
    const page = await ctx.newPage();
    await page.clock.setFixedTime(START + MINUTE * 60000);
    await page.goto(`${ORIGIN}/`);
    await page.waitForTimeout(2000);
    const shot = (await page.screenshot()).toString('base64');
    const frame = await browser.newPage({ deviceScaleFactor: 1 });
    await frame.setContent(FRAME(shot, s.frame));
    await frame.locator('#f').screenshot({ path: join(HERE, s.file) });
    await frame.close();
    await ctx.close();
  }
} finally {
  await browser.close();
}
console.log(`capture-screens.mjs: wrote ${SHOTS.map((s) => s.file).join(', ')}`);
