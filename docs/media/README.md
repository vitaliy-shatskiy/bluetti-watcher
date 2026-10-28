# README media

Every image the READMEs show is made by a script here, on invented demo data. Palette = the dashboard's own (`src/BluettiWatcher/Web/station.html`). The drawn SVGs carry no words to translate (the name, numbers and icons), so `README.md` and `README.uk.md` show the same files; the explainer has words and is rendered once per language.

| File | What | Redraw |
|---|---|---|
| `hero-light.svg`, `hero-dark.svg` | Animated hero | `node draw.mjs` |
| `flow-light.svg`, `flow-dark.svg` | Battery through one cut, default thresholds (illustration) | `node draw.mjs` |
| `explainer/explainer.webp`, `.mp4` (English), `explainer-uk.webp`, `.mp4` | What happens in a power cut, six steps (illustration, `explainer/explainer.html`) | `npm install && node explainer/render.mjs` (Chrome and ffmpeg on PATH) |
| `demo.webp`, `demo.mp4` | The real dashboard page through a simulated cut | `npm install && node record-demo.mjs` (Chrome and ffmpeg on PATH) |
| `screen-uk.png`, `screen-mobile.png` | The real dashboard, framed: Ukrainian desktop, English phone | `npm install && node capture-screens.mjs` (Chrome on PATH) |
| `../demos/cli.webp`, `../demos/cli.mp4` | The command line: `--help`, `power-mode status` on a demo flag, `--version` | from the repo root: `vhs docs/demos/cli.tape && node docs/media/gif.mjs docs/demos/cli-source.gif --out docs/demos/cli --fps 25` (vhs, ttyd, ffmpeg on PATH, the exe installed) |

`demo-data.mjs` holds the demo station both dashboard recorders serve: every request is answered in the browser, on a port that is not the service's, so nothing reaches a running service. `gif.mjs` turns a lossless video into an animated WebP (or `--format gif`) plus an MP4 within a 5 MB budget. The "How it works" diagram is a Mermaid block in the README itself. Redraw an asset only when what it shows changed: git keeps every old one.

Sharp on every screen:

- Capture at device scale 2 and encode at twice the README's display width (`width="820"` -> 1640 px): a retina screen gets one image pixel per device pixel.
- The smallest text stays about 11-12 px on screen: capture a page at a viewport near the display width, never a wide page shrunk. The dashboard runs at 904 x 890, its narrowest desktop layout, cut to the header, tiles and chart.
- `gif.mjs` takes PNG frames or FFV1 (`lossless()`), never H.264: its 4:2:0 chroma blurs coloured text.
- The first frame is a complete picture.
