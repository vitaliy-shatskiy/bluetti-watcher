# README media

Every image the READMEs show is made by a script here, on invented demo data. Palette = the dashboard's own (`src/BluettiWatcher/Web/station.html`). The drawn images carry no words to translate (the name, numbers and icons), so `README.md` and `README.uk.md` show the same files.

| File | What | Redraw |
|---|---|---|
| `hero-light.svg`, `hero-dark.svg` | Animated hero | `node draw.mjs` |
| `flow-light.svg`, `flow-dark.svg` | Battery through one cut, default thresholds (illustration) | `node draw.mjs` |
| `demo.gif`, `demo.mp4` | The real dashboard page through a simulated cut | `npm install && node record-demo.mjs` (Chrome and ffmpeg on PATH) |
| `screen-uk.png`, `screen-mobile.png` | The real dashboard, framed: Ukrainian desktop, English phone | `npm install && node capture-screens.mjs` (Chrome on PATH) |

`demo-data.mjs` holds the demo station both recorders serve. The "How it works" diagram is a Mermaid block in the README itself. Redraw an asset only when what it shows changed: git keeps every old GIF.
