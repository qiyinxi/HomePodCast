// Generates the test video for the browser tests with ffmpeg (VP8 WebM, which every Chromium build plays):
// 640×360, 30 fps, 2 minutes. Each frame carries its frame number as a 12-bit code (frame-code.mjs reads it back from any
// copy of the picture, e.g. the extension's delayed canvas), plus the number in large digits for people looking at it.
//
//   node make-video.mjs [out.webm]      (default media/test.webm; kept when it is already there)
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CODE } from './frame-code.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const { W, H, FPS, SECONDS, BITS, SQ, PITCH, X0, Y0 } = CODE;
// drawtext's font: one of these, or fontconfig's default
const FONTS = ['C:\\Windows\\Fonts\\arialbd.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'];

function filters(withText) {
  const box = (x, y, w, h, color, enable) =>
    `drawbox=x=${x}:y=${y}:w=${w}:h=${h}:color=${color}:t=fill${enable ? `:enable='${enable}'` : ''}`;
  const slots = BITS + 2;
  const chain = [
    'format=yuv420p',
    box(X0 - 12, Y0 - 12, slots * PITCH - (PITCH - SQ) + 24, SQ + 24, 'black'),
    box(X0, Y0, SQ, SQ, 'white'), // registration squares at both ends
    box(X0 + (slots - 1) * PITCH, Y0, SQ, SQ, 'white'),
  ];
  for (let k = 0; k < BITS; k++) { // bit k in slot BITS-k: most significant bit on the left
    chain.push(box(X0 + (BITS - k) * PITCH, Y0, SQ, SQ, 'white', `mod(floor(n/${2 ** k}),2)`));
  }
  if (withText) {
    const font = FONTS.find((f) => fs.existsSync(f));
    const fontfile = font ? `fontfile='${font.replace(/\\/g, '/').replace(':', '\\:')}':` : '';
    chain.push(`drawtext=${fontfile}text='%{eif\\:n\\:d\\:4}':fontsize=96:fontcolor=white:x=(w-text_w)/2:y=30`);
  }
  return chain.join(',');
}

// Named after what it contains, so a changed layout makes a new file.
export const VIDEO_FILE = path.join(HERE, 'media',
  `test-${createHash('sha1').update(JSON.stringify(CODE) + filters(true)).digest('hex').slice(0, 10)}.webm`);

export function makeVideo(out = VIDEO_FILE) {
  if (fs.existsSync(out) && fs.statSync(out).size > 10000) return out;
  fs.mkdirSync(path.dirname(out), { recursive: true });
  const tmp = `${out}.part.webm`;
  for (const withText of [true, false]) { // drawtext needs a font; the code alone is enough for the tests
    const args = ['-y', '-hide_banner', '-loglevel', 'error',
      '-f', 'lavfi', '-i', `color=c=0x202830:s=${W}x${H}:r=${FPS}:d=${SECONDS}`,
      '-vf', filters(withText),
      '-c:v', 'libvpx', '-b:v', '1500k', '-crf', '8', '-g', String(FPS), '-deadline', 'good', '-cpu-used', '5',
      '-an', '-t', String(SECONDS), tmp];
    const r = spawnSync('ffmpeg', args, { encoding: 'utf8' });
    if (r.error) throw new Error(`ffmpeg not found (${r.error.message}): install ffmpeg to generate the test video`);
    if (r.status === 0) {
      fs.renameSync(tmp, out);
      return out;
    }
    if (!withText) throw new Error(`ffmpeg failed: ${r.stderr}`);
    console.warn(`ffmpeg drawtext failed, making the video without digits: ${String(r.stderr).trim().split('\n').pop()}`);
  }
  return out;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(`wrote ${makeVideo(process.argv[2] || VIDEO_FILE)}`);
}
