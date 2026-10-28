// Draws the README's SVGs in a light and a dark variant: node docs/media/draw.mjs
// Palette = the dashboard's own (src/BluettiWatcher/Web/station.html :root). No dependencies.
// The images carry no words to translate (the product name, numbers and icons only), so README.md and
// README.uk.md show the same files. Sizes: viewBox 960 wide, shown at about 880 px, every text 14 px or more.
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT = dirname(fileURLToPath(import.meta.url));
const SANS = 'system-ui, -apple-system, "Segoe UI", sans-serif';
const MONO = 'ui-monospace, SFMono-Regular, Consolas, monospace';

const THEMES = {
  dark: { bg: '#111214', card: '#1a1b1e', raised: '#26282c', line: '#2c2e33', text: '#e4e6ea', dim: '#8b8e95', ok: '#5fad65', warn: '#e0a53a', bad: '#db5c5c', accent: '#548af7' },
  light: { bg: '#f4f5f7', card: '#ffffff', raised: '#eceef1', line: '#d9dce1', text: '#1d1f23', dim: '#5e636b', ok: '#3d8b44', warn: '#b7801c', bad: '#c2413f', accent: '#2f6be0' },
};

const STILL = '@media (prefers-reduced-motion: reduce) { * { animation: none !important; } }';

// Icons, centred on (x, y), about 22 px tall.
const bolt = (x, y, col) => `<path transform="translate(${x - 7} ${y - 11}) scale(.35)" d="M26 0 L4 36 H20 L14 64 L40 24 H24 Z" fill="${col}"/>`;
const boltOff = (x, y, col, c) => bolt(x, y, col) +
  `<line x1="${x - 11}" y1="${y - 11}" x2="${x + 11}" y2="${y + 11}" stroke="${c.card}" stroke-width="5" stroke-linecap="round"/>` +
  `<line x1="${x - 11}" y1="${y - 11}" x2="${x + 11}" y2="${y + 11}" stroke="${c.bad}" stroke-width="2.5" stroke-linecap="round"/>`;
const leaf = (x, y, col) => `<g transform="translate(${x - 11} ${y - 11})"><path d="M1 21 C1 7 9 1 22 1 C22 14 15 21 1 21 Z" fill="${col}"/>` +
  `<path d="M3 19 L15 7" stroke="${'#00000055'}" stroke-width="1.6" stroke-linecap="round"/></g>`;
const moon = (x, y, col) => `<path transform="translate(${x - 9} ${y - 10})" d="M8 0 A10 10 0 1 0 18 14 A8 8 0 0 1 8 0 Z" fill="${col}"/>`;

// Hero: the name, the reaction steps as icons and levels, and a battery ring that drains while the steps light up.
function hero(c) {
  const W = 960, H = 300, R = 96, CIRC = 2 * Math.PI * R, cx = 800, cy = 150;
  const steps = [
    { w: 64, icon: (x, y) => boltOff(x, y, c.warn, c), col: c.warn },
    { w: 64, icon: (x, y) => leaf(x, y, c.ok), col: c.ok },
    { w: 86, label: '20 %', col: c.warn },
    { w: 86, label: '10 %', col: c.bad },
    { w: 108, label: '5 %', icon: (x, y) => moon(x, y, c.bad), col: c.bad },
    { w: 64, icon: (x, y) => bolt(x, y, c.ok), col: c.ok },
  ];
  let x = 48;
  const chips = steps.map((s, i) => {
    const y = 176, h = 52, mid = x + s.w / 2;
    let inner = '';
    if (s.label && s.icon) inner = `<text x="${x + 20}" y="${y + 33}" font-family='${SANS}' font-size="21" font-weight="700" fill="${c.text}">${s.label}</text>` + s.icon(x + s.w - 26, y + h / 2);
    else if (s.label) inner = `<text x="${mid}" y="${y + 33}" text-anchor="middle" font-family='${SANS}' font-size="21" font-weight="700" fill="${c.text}">${s.label}</text>`;
    else inner = s.icon(mid, y + h / 2);
    const g = `<g class="step s${i}"><rect x="${x}" y="${y}" width="${s.w}" height="${h}" rx="26" fill="${c.raised}" stroke="${s.col}" stroke-width="2"/>${inner}</g>`;
    const arrow = i < steps.length - 1 ? `<path d="M${x + s.w + 5} ${y + 20} l6 6 l-6 6" fill="none" stroke="${c.dim}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>` : '';
    x += s.w + 18;
    return g + arrow;
  }).join('');
  const delays = steps.map((_, i) => `.s${i}{animation-delay:${(i * 1.6).toFixed(1)}s}`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Bluetti Watcher: mains lost, eco, 20 %, 10 %, 5 % hibernate, mains back">
<style>
.ring{stroke-dasharray:${CIRC.toFixed(1)};animation:drain 10s ease-in-out infinite;transform:rotate(-90deg);transform-origin:${cx}px ${cy}px}
@keyframes drain{0%{stroke-dashoffset:0;stroke:${c.ok}}45%{stroke:${c.warn}}75%{stroke-dashoffset:${(CIRC * 0.95).toFixed(1)};stroke:${c.bad}}88%{stroke-dashoffset:${(CIRC * 0.95).toFixed(1)};stroke:${c.bad}}100%{stroke-dashoffset:0;stroke:${c.ok}}}
.step{animation:lit 9.6s infinite backwards}
@keyframes lit{0%,4%{opacity:.7}8%,22%{opacity:1}30%,100%{opacity:.7}}
${delays}
.bolt{animation:blink 10s infinite}
@keyframes blink{0%,10%{opacity:1}14%,86%{opacity:.15}92%,100%{opacity:1}}
${STILL}
</style>
<rect width="${W}" height="${H}" rx="22" fill="${c.bg}"/>
<rect x="12" y="12" width="${W - 24}" height="${H - 24}" rx="18" fill="${c.card}" stroke="${c.line}"/>
<text x="46" y="122" font-family='${SANS}' font-size="58" font-weight="700" fill="${c.text}">Bluetti Watcher</text>
<rect x="48" y="142" width="72" height="5" rx="2.5" fill="${c.accent}"/>
${chips}
<circle cx="${cx}" cy="${cy}" r="${R}" fill="none" stroke="${c.raised}" stroke-width="20"/>
<circle class="ring" cx="${cx}" cy="${cy}" r="${R}" fill="none" stroke="${c.ok}" stroke-width="20" stroke-linecap="round"/>
<g class="bolt" transform="translate(${cx - 26} ${cy - 40}) scale(1.25)"><path d="M26 0 L4 36 H20 L14 64 L40 24 H24 Z" fill="${c.warn}"/></g>
</svg>
`;
}

// How it reacts: the battery level through one cut with the default thresholds (README "How it reacts").
// Static on purpose: an <img> SVG whose animation has not run (offscreen, a theme swap) shows its first frame.
function flow(c) {
  const W = 960, H = 330, L = 72, Rr = 930, T = 58, B = 290;
  const x = (f) => L + f * (Rr - L), y = (p) => B - (p / 100) * (B - T);
  // Illustration of one cut: mains lost, drain to 5 %, hibernated, mains back and charging.
  const pts = [[0, 100], [0.08, 100], [0.18, 90], [0.55, 20], [0.66, 10], [0.76, 5], [0.9, 5], [1, 32]];
  const xy = pts.map(([f, p]) => [x(f), y(p)]);
  const path = xy.map(([px, py], i) => `${i ? 'L' : 'M'}${px.toFixed(1)} ${py.toFixed(1)}`).join(' ');
  // Marker: dot on the curve, a dotted leader up (or down) to the label, a gap of 8 px on each end.
  const mark = (f, p, col, label, icon, below, lift = 0) => {
    const px = x(f), py = y(p);
    const ly = below ? py + 52 : py - 46 - lift; // label centre line
    const top = ly - 14, bottom = ly + 14;
    const [y1, y2] = below ? [py + 8, top - 8] : [py - 8, bottom + 8];
    let body;
    if (label && icon) body = `<text x="${px - 6}" y="${ly + 7}" text-anchor="end" font-family='${SANS}' font-size="20" font-weight="700" fill="${c.text}">${label}</text>` + icon(px + 10, ly);
    else if (label) body = `<text x="${px}" y="${ly + 7}" text-anchor="middle" font-family='${SANS}' font-size="20" font-weight="700" fill="${c.text}">${label}</text>`;
    else body = `<g transform="translate(${px} ${ly}) scale(1.3) translate(${-px} ${-ly})">${icon(px, ly)}</g>`;
    return `<line x1="${px}" y1="${y1}" x2="${px}" y2="${y2}" stroke="${col}" stroke-width="1.5" stroke-dasharray="3 4"/>` +
      `<circle cx="${px}" cy="${py}" r="7" fill="${c.card}" stroke="${col}" stroke-width="3"/>` + body;
  };
  const marks = [
    mark(0.08, 100, c.warn, null, (ix, iy) => boltOff(ix, iy, c.warn, c), true),
    mark(0.13, 95, c.ok, null, (ix, iy) => leaf(ix, iy, c.ok), true, 0),
    mark(0.55, 20, c.warn, '20 %', null, false),
    mark(0.66, 10, c.bad, '10 %', null, false, 26),
    mark(0.76, 5, c.bad, '5 %', (ix, iy) => moon(ix, iy, c.bad), false),
    mark(0.9, 5, c.ok, null, (ix, iy) => bolt(ix, iy, c.ok), false),
  ].join('');
  const grid = [0, 25, 50, 75, 100].map((p) => `<line x1="${L}" x2="${Rr}" y1="${y(p)}" y2="${y(p)}" stroke="${c.line}"/><text x="${L - 12}" y="${y(p) + 5}" text-anchor="end" font-family='${MONO}' font-size="14" fill="${c.dim}">${p}%</text>`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Battery level through one power cut and the default thresholds: mains lost, eco, 20 %, 10 %, 5 % hibernate, mains back">

<rect width="${W}" height="${H}" rx="20" fill="${c.card}" stroke="${c.line}"/>
<rect x="${x(0.08)}" y="${T - 34}" width="${x(0.9) - x(0.08)}" height="${B - T + 34}" fill="${c.warn}" fill-opacity=".08"/>
${grid}
<path d="${path}" fill="none" stroke="${c.accent}" stroke-width="4" stroke-linejoin="round" stroke-linecap="round"/>
${marks}
</svg>
`;
}

for (const [theme, c] of Object.entries(THEMES)) {
  writeFileSync(join(OUT, `hero-${theme}.svg`), hero(c));
  writeFileSync(join(OUT, `flow-${theme}.svg`), flow(c));
}
console.log('draw.mjs: wrote hero-{light,dark}.svg, flow-{light,dark}.svg');
