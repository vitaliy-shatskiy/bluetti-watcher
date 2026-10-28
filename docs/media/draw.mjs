// Draws the README's SVGs in a light and a dark variant: node docs/media/draw.mjs
// Palette = the dashboard's own (src/BluettiWatcher/Web/station.html :root). No dependencies.
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
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');

// Hero: name, one-line pitch, and a battery ring that drains through the default levels while the steps light up.
function hero(c) {
  const R = 118, C = 2 * Math.PI * R, cx = 1010, cy = 200;
  const steps = [
    ['Mains lost', c.warn], ['Eco', c.accent], ['20 % emergency', c.warn], ['10 % stop', c.bad], ['5 % hibernate', c.bad], ['Mains back', c.ok],
  ];
  const chips = steps.map(([label, col], i) => {
    const x = 64 + (i % 3) * 196, y = 268 + Math.floor(i / 3) * 48;
    return `<g class="step s${i}"><rect x="${x}" y="${y}" width="184" height="36" rx="18" fill="${c.raised}" stroke="${col}" stroke-opacity=".7"/>` +
      `<circle cx="${x + 20}" cy="${y + 18}" r="5" fill="${col}"/>` +
      `<text x="${x + 34}" y="${y + 23}" font-family='${SANS}' font-size="15" fill="${c.text}">${esc(label)}</text></g>`;
  }).join('');
  const delays = steps.map((_, i) => `.s${i}{animation-delay:${(i * 1.6).toFixed(1)}s}`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="400" viewBox="0 0 1280 400" role="img" aria-label="Bluetti Watcher: keeps a Windows PC running through power cuts on a BLUETTI station">
<style>
.ring{stroke-dasharray:${C.toFixed(1)};animation:drain 10s ease-in-out infinite;transform:rotate(-90deg);transform-origin:${cx}px ${cy}px}
@keyframes drain{0%{stroke-dashoffset:0;stroke:${c.ok}}45%{stroke:${c.warn}}75%{stroke-dashoffset:${(C * 0.95).toFixed(1)};stroke:${c.bad}}88%{stroke-dashoffset:${(C * 0.95).toFixed(1)};stroke:${c.bad}}100%{stroke-dashoffset:0;stroke:${c.ok}}}
.step{opacity:.35;animation:lit 9.6s infinite}
@keyframes lit{0%,4%{opacity:.35}8%,22%{opacity:1}30%,100%{opacity:.35}}
${delays}
.bolt{animation:blink 10s infinite}
@keyframes blink{0%,10%{opacity:1}14%,86%{opacity:.15}92%,100%{opacity:1}}
${STILL}
</style>
<rect width="1280" height="400" rx="24" fill="${c.bg}"/>
<rect x="16" y="16" width="1248" height="368" rx="20" fill="${c.card}" stroke="${c.line}"/>
<text x="64" y="118" font-family='${SANS}' font-size="60" font-weight="700" fill="${c.text}">Bluetti Watcher</text>
<text x="64" y="168" font-family='${SANS}' font-size="23" fill="${c.dim}">Keeps your Windows PC running through power cuts on a</text>
<text x="64" y="200" font-family='${SANS}' font-size="23" fill="${c.dim}">BLUETTI station - and hibernates it before the battery is empty.</text>
<text x="64" y="244" font-family='${MONO}' font-size="14" fill="${c.accent}">Bluetooth LE · read only · one NativeAOT exe · dashboard on 127.0.0.1</text>
${chips}
<circle cx="${cx}" cy="${cy}" r="${R}" fill="none" stroke="${c.raised}" stroke-width="22"/>
<circle class="ring" cx="${cx}" cy="${cy}" r="${R}" fill="none" stroke="${c.ok}" stroke-width="22" stroke-linecap="round"/>
<g class="bolt" transform="translate(${cx - 22} ${cy - 62})"><path d="M26 0 L4 36 H20 L14 64 L40 24 H24 Z" fill="${c.warn}"/></g>
<text x="${cx}" y="${cy + 38}" text-anchor="middle" font-family='${SANS}' font-size="18" fill="${c.dim}">battery</text>
<text x="${cx}" y="${cy + 64}" text-anchor="middle" font-family='${MONO}' font-size="15" fill="${c.dim}">read every minute</text>
</svg>
`;
}

// How it reacts: the battery level through one cut with the default thresholds (README "How it reacts").
function flow(c) {
  const W = 1280, H = 360, L = 90, Rr = 1220, T = 60, B = 290;
  const x = (f) => L + f * (Rr - L), y = (p) => B - (p / 100) * (B - T);
  // Illustration of one cut: mains lost at 8 %, drain to 5 %, hibernated, mains back at 92 %.
  const pts = [[0, 100], [0.08, 100], [0.18, 92], [0.45, 52], [0.62, 20], [0.7, 10], [0.73, 7], [0.76, 5], [0.92, 5], [1, 30]];
  const path = pts.map(([f, p], i) => `${i ? 'L' : 'M'}${x(f).toFixed(1)} ${y(p).toFixed(1)}`).join(' ');
  const marks = [
    [0.08, 100, 'Mains lost', '3 min: eco', c.warn],
    [0.62, 20, '20 %', 'emergency', c.warn],
    [0.7, 10, '10 %', 'stop', c.bad],
    [0.76, 5, '5 %', 'hibernate', c.bad],
    [0.92, 5, 'Mains back', '3 min: normal', c.ok],
  ].map(([f, p, a, b, col], i) => {
    const up = i % 2 === 0 ? -1 : 1;
    const ty = p > 50 ? y(p) + 44 : y(p) - 52 + (up > 0 ? 0 : -20);
    return `<line x1="${x(f)}" y1="${y(p)}" x2="${x(f)}" y2="${ty - 16}" stroke="${col}" stroke-dasharray="3 4"/>` +
      `<circle cx="${x(f)}" cy="${y(p)}" r="7" fill="${c.card}" stroke="${col}" stroke-width="3"/>` +
      `<text x="${x(f)}" y="${ty}" text-anchor="middle" font-family='${SANS}' font-size="17" font-weight="600" fill="${c.text}">${a}</text>` +
      `<text x="${x(f)}" y="${ty + 20}" text-anchor="middle" font-family='${SANS}' font-size="14" fill="${c.dim}">${b}</text>`;
  }).join('');
  const grid = [0, 25, 50, 75, 100].map((p) => `<line x1="${L}" x2="${Rr}" y1="${y(p)}" y2="${y(p)}" stroke="${c.line}"/><text x="${L - 14}" y="${y(p) + 5}" text-anchor="end" font-family='${MONO}' font-size="13" fill="${c.dim}">${p}%</text>`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Battery level through one power cut and what Bluetti Watcher does at each default threshold">
<style>
.curve{stroke-dasharray:1;stroke-dashoffset:1;animation:draw 3s ease-out forwards}
@keyframes draw{to{stroke-dashoffset:0}}
${STILL}
@media (prefers-reduced-motion: reduce) { .curve { stroke-dashoffset: 0; } }
</style>
<rect width="${W}" height="${H}" rx="20" fill="${c.card}" stroke="${c.line}"/>
<rect x="${x(0.08)}" y="${T - 20}" width="${x(0.92) - x(0.08)}" height="${B - T + 20}" fill="${c.warn}" fill-opacity=".08"/>
<text x="${x(0.5)}" y="${T - 28}" text-anchor="middle" font-family='${SANS}' font-size="14" fill="${c.dim}">power cut</text>
${grid}
<path class="curve" pathLength="1" d="${path}" fill="none" stroke="${c.accent}" stroke-width="4" stroke-linejoin="round" stroke-linecap="round"/>
${marks}
<text x="${Rr}" y="${H - 18}" text-anchor="end" font-family='${SANS}' font-size="13" fill="${c.dim}">illustration, default thresholds · each level acts after two agreeing readings</text>
</svg>
`;
}

for (const [theme, c] of Object.entries(THEMES)) {
  writeFileSync(join(OUT, `hero-${theme}.svg`), hero(c));
  writeFileSync(join(OUT, `flow-${theme}.svg`), flow(c));
}
console.log('draw.mjs: wrote hero-{light,dark}.svg, flow-{light,dark}.svg');
