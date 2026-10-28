#!/usr/bin/env node
// Turns a screen recording into a README animation (animated WebP or GIF) plus a sharp MP4, shrinking the animation
// until it fits the budget. Capture at device scale 2 and pass --width = twice the README's display width (the <img>
// width attribute), so retina screens get one pixel per device pixel and text stays crisp.
// Self-contained (Node 20+, ffmpeg on PATH, no packages): copy it anywhere. Exports encode(); run as a CLI:
//   node gif.mjs <video> [--out <path without extension>] [--format webp|gif] [--width 1640] [--fps 12] [--budget-mb 5]
// Writes <out>.webp (or .gif) and <out>.mp4 (default <out> = the video's path without extension; the MP4 is skipped
// when it would overwrite the source). Give it a lossless source (PNG frames muxed with lossless(), or FFV1): an
// H.264 yuv420p source has already blurred coloured text. Up to 4 tries, each lower in quality, fps or width.
// Exit 0 = within budget, 1 = still over budget after the tries, 2 = could not run.
import { existsSync, mkdirSync, statSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';

const MB = 1024 * 1024;
const mb = (n) => `${(n / MB).toFixed(2)} MB`;
const even = (n) => Math.max(2, Math.round(n / 2) * 2);

// Returns null when ffmpeg runs, else the reason it does not.
export function ffmpegMissing() {
  try {
    execFileSync('ffmpeg', ['-version'], { stdio: 'pipe', windowsHide: true, timeout: 20000 });
    return null;
  } catch (e) {
    return e.code === 'ENOENT' ? 'ffmpeg is not on PATH (winget install Gyan.FFmpeg)' : `ffmpeg -version failed: ${e.message.split('\n')[0]}`;
  }
}

// Runs ffmpeg quietly; throws with ffmpeg's own stderr on failure.
export function ff(args, timeout = 1800000) {
  try {
    execFileSync('ffmpeg', ['-y', '-v', 'error', ...args], { stdio: 'pipe', windowsHide: true, timeout });
  } catch (e) {
    const err = (e.stderr && e.stderr.toString().trim()) || e.message;
    throw new Error(`ffmpeg failed: ${err.split('\n').slice(-3).join(' | ')}`);
  }
}

// Muxes ffmpeg input args (a PNG pattern or a concat list) into a lossless FFV1 .mkv, the source encode() wants.
export function lossless(inputArgs, mkv) {
  ff([...inputArgs, '-c:v', 'ffv1', '-pix_fmt', 'bgr0', mkv]);
  return mkv;
}

// The tries: the asked settings first, then lower quality (WebP) or fps (GIF), then less width.
function plan(format, fps, width) {
  const steps = format === 'webp'
    ? [
      { fps, width, quality: 90 },
      { fps, width, quality: 80 },
      { fps: Math.min(fps, 10), width, quality: 70 },
      { fps: Math.min(fps, 8), width: even(width * 0.8), quality: 70 },
    ]
    : [
      { fps, width, colors: 256 },
      { fps: Math.min(fps, 10), width, colors: 192 },
      { fps: Math.min(fps, 8), width, colors: 128 },
      { fps: Math.min(fps, 6), width: even(width * 0.8), colors: 128 },
    ];
  return steps.filter((s, i) => i === 0 || JSON.stringify(s) !== JSON.stringify(steps[i - 1]));
}

function animate(video, file, format, s) {
  const scale = `fps=${s.fps},scale=${s.width}:-1:flags=lanczos`;
  if (format === 'webp') {
    ff(['-i', video, '-vf', scale, '-c:v', 'libwebp_anim', '-lossless', '0', '-q:v', String(s.quality),
      '-compression_level', '6', '-preset', 'text', '-loop', '0', file]);
  } else {
    ff(['-i', video, '-vf', `${scale},split[a][b];[a]palettegen=max_colors=${s.colors}:stats_mode=diff[p];[b][p]paletteuse=dither=none:diff_mode=rectangle`, file]);
  }
}

// Encodes video -> <out>.<format> within budget (bytes) and <out>.mp4; returns { ok, file, size, used }. Logs each step.
export function encode(video, { out, format = 'webp', width = 1640, fps = 12, budget = 5 * MB, log = console.log } = {}) {
  const base = resolve(out ? out.replace(/\.(gif|webp|mp4)$/i, '') : video.replace(/\.[^.\\/]+$/, ''));
  const file = `${base}.${format}`, mp4 = `${base}.mp4`;
  mkdirSync(dirname(base), { recursive: true });
  if (resolve(mp4).toLowerCase() === resolve(video).toLowerCase()) log(`gif: mp4 skipped, ${mp4} is the source itself`);
  else {
    ff(['-i', video, '-vf', `scale=${even(width)}:-2:flags=lanczos`, '-c:v', 'libx264', '-crf', '18', '-tune', 'stillimage', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', mp4]);
    log(`gif: ${mp4} ${mb(statSync(mp4).size)}`);
  }
  let size = 0, used;
  const tries = plan(format, fps, width);
  for (const [i, s] of tries.entries()) {
    used = s;
    animate(video, file, format, s);
    size = statSync(file).size;
    log(`gif: try ${i + 1}/${tries.length} ${JSON.stringify(s)} -> ${mb(size)}`);
    if (size <= budget) break;
  }
  const ok = size <= budget;
  log(`gif: ${file} ${mb(size)} ${JSON.stringify(used)} ${ok ? 'within' : 'OVER'} the ${mb(budget)} budget`);
  return { ok, file, size, used };
}

export function cli(argv) {
  const o = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('--')) { o._.push(a); continue; }
    if (!['--out', '--format', '--width', '--fps', '--budget-mb'].includes(a) || i + 1 >= argv.length) { console.error(`gif: bad option ${a}`); return 2; }
    o[a.slice(2)] = argv[++i];
  }
  const video = o._[0];
  if (!video || !existsSync(video)) { console.error('gif: give an existing video file (<video> [--out <name>])'); return 2; }
  const format = o.format || 'webp';
  if (!['webp', 'gif'].includes(format)) { console.error('gif: --format is webp or gif'); return 2; }
  const width = Number(o.width || 1640), fps = Number(o.fps || 12), budget = Number(o['budget-mb'] || 5) * MB;
  if (!(width > 0 && fps > 0 && budget > 0)) { console.error('gif: --width, --fps and --budget-mb must be positive numbers'); return 2; }
  const missing = ffmpegMissing();
  if (missing) { console.error(`gif: ${missing}`); return 2; }
  try { return encode(video, { out: o.out, format, width, fps, budget }).ok ? 0 : 1; } catch (e) { console.error(`gif: ${e.message}`); return 2; }
}

if (process.argv[1] && resolve(process.argv[1]) === import.meta.filename) process.exitCode = cli(process.argv.slice(2));
