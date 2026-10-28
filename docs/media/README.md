# README media

Every image the READMEs show is made by a script here, on invented demo data. Palette = the dashboard's own (`src/BluettiWatcher/Web/station.html`). The drawn SVGs carry no words to translate (the name, numbers and icons), so `README.md` and `README.uk.md` show the same files; the explainer has words and is rendered once per language.

| File | What | Redraw |
|---|---|---|
| `hero-light.svg`, `hero-dark.svg` | Animated hero | `node draw.mjs` |
| `flow-light.svg`, `flow-dark.svg` | Battery through one cut, default thresholds (illustration) | `node draw.mjs` |
| `explainer/explainer.gif`, `.mp4` (English), `explainer-uk.gif`, `.mp4` | What happens in a power cut, six steps (illustration, `explainer/explainer.html`) | `npm install && node explainer/render.mjs` (Chrome and ffmpeg on PATH) |
| `demo.gif`, `demo.mp4` | The real dashboard page through a simulated cut | `npm install && node record-demo.mjs` (Chrome and ffmpeg on PATH) |
| `screen-uk.png`, `screen-mobile.png` | The real dashboard, framed: Ukrainian desktop, English phone | `npm install && node capture-screens.mjs` (Chrome on PATH) |
| `../demos/cli.gif`, `../demos/cli.mp4` | The command line: `--help`, `power-mode status` on a demo flag, `--version` | from the repo root: `vhs docs/demos/cli.tape` (vhs, ttyd, ffmpeg on PATH, the exe installed) |

`demo-data.mjs` holds the demo station both dashboard recorders serve: every request is answered in the browser, on a port that is not the service's, so nothing reaches a running service. `gif.mjs` turns a video into the GIF plus MP4 within the 2 MB budget. The "How it works" diagram is a Mermaid block in the README itself. Redraw an asset only when what it shows changed: git keeps every old GIF.
