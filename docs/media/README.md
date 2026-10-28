# README media

Every image the READMEs show is drawn by a script here. Palette = the dashboard's own (`src/BluettiWatcher/Web/station.html`).

| File | What | Redraw |
|---|---|---|
| `hero-light.svg`, `hero-dark.svg` | Animated hero | `node draw.mjs` |
| `flow-light.svg`, `flow-dark.svg` | Battery through one cut, default thresholds (illustration) | `node draw.mjs` |
| `demo.gif`, `demo.mp4` | The real dashboard page through a simulated cut, demo readings | `npm install && node record-demo.mjs` (Chrome and ffmpeg on PATH) |

The "How it works" diagram is a Mermaid block in the README itself. Redraw an asset only when what it shows changed: git keeps every old GIF.
