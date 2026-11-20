# README media

Every image the READMEs show is made by a script here, on invented demo data, in the dashboard's palette
(`src/BluettiWatcher/Web/station.html`). The SVGs carry no words, so `README.md` and `README.uk.md` share them; the
explainer has words and is rendered once per language. The "How it works" diagram is a Mermaid block in the README.
Redraw an asset only when what it shows changed.

## Regenerate

From `docs/media`, once: `npm ci` (installs `playwright-core` from the lockfile). The browser scripts drive the
installed Google Chrome; the video scripts need `ffmpeg` on PATH. Every script writes beside itself.

| Asset | Shows | Command |
|---|---|---|
| `hero-light.svg`, `hero-dark.svg`, `flow-light.svg`, `flow-dark.svg` | Animated hero; battery through one cut at the default thresholds | `node draw.mjs` (or `npm run draw`; no packages) |
| `demo.webp`, `demo.mp4` | The real dashboard through a simulated cut | `node record-demo.mjs` (or `npm run demo`) |
| `screen-uk.png`, `screen-mobile.png` | The real dashboard, framed: Ukrainian desktop, English phone | `node capture-screens.mjs` |
| `explainer/explainer.webp`, `.mp4`, `explainer/explainer-uk.webp`, `.mp4` | A power cut in six steps (`explainer/explainer.html`) | `node explainer/render.mjs` |
| `../demos/cli.webp`, `../demos/cli.mp4` | `--help`, `power-mode status` on a demo flag, `--version` | From the repo root: `vhs docs/demos/cli.tape; node docs/media/gif.mjs docs/demos/cli-source.gif --out docs/demos/cli --fps 25` (vhs, ttyd and ffmpeg on PATH, the exe installed) |

- `demo-data.mjs` is the demo station both dashboard scripts serve: every `/api/*` request is answered inside the
  browser, on a port that is not the service's, so nothing reaches a running service.
- `gif.mjs` turns a lossless video into an animated WebP (or `--format gif`) plus an MP4 within a 5 MB budget
  (`node gif.mjs <video> [--out <path>] [--format webp|gif] [--width 1640] [--fps 12] [--budget-mb 5]`; Node 20+,
  ffmpeg). Exit 0 within budget, 1 still over it, 2 could not run.

## Sharp on every screen

- Capture at device scale 2 and encode at twice the README's display width (`width="820"` -> 1640 px).
- Keep the smallest text about 11-12 px on screen: capture at a viewport near the display width, never a wide page
  shrunk. The dashboard runs at 904 x 890, its narrowest desktop layout, cut to the header, tiles and chart.
- Feed `gif.mjs` PNG frames or FFV1, never H.264: its 4:2:0 chroma blurs coloured text.
- The first frame is a complete picture.
