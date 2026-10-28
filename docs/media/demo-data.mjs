// Demo station for the README media: the /api/now and /api/history answers of one simulated power cut.
// Exports START, CUT_AT, now(minute), history(url, minute), serve(ctx, getMinute). Invented readings only.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// Not the service's port (18940): every request is answered by serve() below, nothing reaches a live service.
export const ORIGIN = 'http://127.0.0.1:18949';
export const PAGE = readFileSync(resolve(import.meta.dirname, '../../src/BluettiWatcher/Web/station.html'), 'utf8');
const CAP = 2048; // demo station, Wh
const EFF = 0.85;

// Demo story: minute 0..30 on mains, then a cut; the battery drains to the stop level over about seven hours.
export const START = Date.parse('2026-10-20T18:00:00+03:00');
export const CUT_AT = 30;
// The charge follows the load: eco draws about 223 W, so the battery loses 223 / (2048 x 0.85) = about 12.8 % an hour.
export const DRAIN = (360 * 0.62) / (CAP * EFF) * 100;
// %/h the station loses at a given output, and the hours the charge lasts at it: one model for every number shown.
const rateAt = (out) => (out / (CAP * EFF)) * 100;
function reading(min) {
  const at = new Date(START + min * 60000);
  const load = 360 + Math.round(40 * Math.sin(min / 7));
  if (min < CUT_AT) return { at, soc: 100, inAc: load, inDc: 0, outAc: load, outDc: 0, volt: 228, hz: 50, mains: true };
  const eco = min >= CUT_AT + 3;
  const out = eco ? Math.round(load * 0.62) : load;
  const soc = Math.max(8, Math.round(100 - ((min - CUT_AT) / 60) * DRAIN));
  return { at, soc, inAc: 0, inDc: 0, outAc: out, outDc: 0, volt: null, hz: null, mains: false };
}
const iso = (d) => d.toISOString().replace('Z', '+00:00');
const row = (r) => ({ ...r, at: iso(r.at) });

export function now(minute) {
  const r = reading(minute);
  const cut = !r.mains;
  const eco = cut && minute >= CUT_AT + 3;
  const level = r.soc <= 10 ? 'stop' : r.soc <= 20 ? 'emergency' : 'ok';
  const rate = cut ? Math.round(rateAt(r.outAc) * 10) / 10 : null;
  const hours = cut ? r.soc / rate : null;
  return {
    type: 'AC200PL', capacityWh: CAP, efficiency: 0.85, mode: cut ? 'inverter output' : 'bypass output (D)', modeCode: cut ? 'INVERTER_OUTPUT' : 'BYPASS_OUTPUT_D',
    eco: eco ? 'eco' : '', ecoNote: null, manual: 'auto', modePending: null, modeError: '', modeErrorNote: null,
    level, cut, hibernateDelayedUntil: null, ecoAt: cut && !eco ? iso(new Date(START + (CUT_AT + 3) * 60000)) : null,
    grid: cut ? 'off' : 'on', silent: false, note: '', notes: [], dryRun: false,
    hoursLeft: hours, runtimeSource: cut ? 'model' : null, ratePerHour: rate,
    hoursLeftLow: hours && hours * 0.85, hoursLeftHigh: hours && hours * 1.12, learnedEfficiency: 0.82,
    thresholds: { emergencyAt: 20, stopAt: 10, warnAt: 7, hibernateAt: 5 },
    last: row(r), wake: { everyMinutes: 0, floorPercent: 10, armed: false, wakeAt: null, disarmOwed: false, error: '' },
    dtekWatcher: null,
  };
}

export function history(url, minute) {
  const q = new URL(url).searchParams;
  const to = Number(q.get('to')), from = Number(q.get('from'));
  const rows = [];
  for (let t = Math.max(from, START - 6 * 3600000); t <= to; t += 300000) {
    const m = Math.floor((t - START) / 60000);
    rows.push(row(m < 0 ? { ...reading(0), at: new Date(t) } : { ...reading(Math.min(m, minute)), at: new Date(t) }));
  }
  return { step: 60, rows };
}

// Answers every request of a Playwright context: the shipped page, and the demo API at the current minute.
export async function serve(ctx, getMinute) {
  const json = (route, body) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await ctx.route(`${ORIGIN}/**`, (route) => {
    const u = route.request().url();
    if (u.includes('/api/now')) return json(route, now(getMinute()));
    if (u.includes('/api/history')) return json(route, history(u, getMinute()));
    if (u.includes('/api/mode')) return json(route, { ok: true });
    return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: PAGE });
  });
}
