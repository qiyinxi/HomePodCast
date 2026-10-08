// node --test extension/test/extension.test.js   (CI runs every extension/test/*.test.js: .github/workflows/build.yml)
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const EXT = path.join(__dirname, '..');
const read = (...p) => fs.readFileSync(path.join(EXT, ...p), 'utf8');
const manifest = JSON.parse(read('manifest.json'));
const locales = fs.readdirSync(path.join(EXT, '_locales')).sort();
const messages = Object.fromEntries(locales.map((l) => [l, JSON.parse(read('_locales', l, 'messages.json'))]));
const en = messages.en;

// Loads delay-engine.js the way a page does (a classic script that sets globalThis.HPCDelay).
function loadEngine() {
  const ctx = vm.createContext({});
  vm.runInContext(read('src', 'delay-engine.js'), ctx, { filename: 'delay-engine.js' });
  return ctx.HPCDelay;
}

test('manifest version matches the engine version', () => {
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
  assert.equal(loadEngine().version, manifest.version);
});

test('capacity: delay / 16.7 ms frames plus 4, at most MAX_FRAMES', () => {
  const { capacityFor, MAX_FRAMES } = loadEngine().internals;
  assert.equal(capacityFor(141), 13);
  assert.equal(capacityFor(238), 19);
  assert.equal(capacityFor(538), 37);
  assert.equal(capacityFor(600), MAX_FRAMES);
  assert.equal(capacityFor(2000), MAX_FRAMES);
  assert.equal(capacityFor(238, 33.3), 12); // 30 fps canvas player
});

test('store stride: every frame while the buffer covers the delay, thinned beyond that', () => {
  const { capacityFor, storeStride } = loadEngine().internals;
  const f60 = 1000 / 60;
  for (const d of [0, 141, 238, 250, 538, 600, 629]) assert.equal(storeStride(d, f60, capacityFor(d)), 1, `${d} ms`);
  assert.equal(storeStride(630, f60, capacityFor(630)), 2); // 40 frames of 60 fps no longer cover it
  assert.equal(storeStride(700, f60, capacityFor(700)), 2); // Music scene + a site offset: 30 fps instead of a frozen picture
  assert.equal(storeStride(2000, f60, capacityFor(2000)), 4);
  assert.equal(storeStride(1000, 1000 / 30, capacityFor(1000)), 1); // 30 fps fits 1 s in 40 frames
  assert.equal(storeStride(238, 1000 / 120, capacityFor(238)), 2); // 120 fps video, capacity sized for 60 fps
  assert.equal(storeStride(500, 0, 40), 1); // frame interval unknown
});

test('store stride: 60 fps never thins up to 620 ms, whatever the measured interval jitter', () => {
  // regression: a measured 16.66 ms against a capacity sized for 16.7 ms thinned every other frame at 250 ms
  const { capacityFor, storeStride } = loadEngine().internals;
  for (let d = 0; d <= 620; d++) {
    for (const f of [16.6, 16.66, 16.667, 16.7, 16.75]) { // the averaged interval stays within ±0.1 ms
      assert.equal(storeStride(d, f, capacityFor(d)), 1, `${d} ms at ${f} ms per frame`);
    }
  }
  for (let d = 0; d <= 600; d++) {
    for (const f of [16.4, 16.5, 16.9, 17]) assert.equal(storeStride(d, f, capacityFor(d)), 1, `${d} ms at ${f} ms per frame`);
  }
});

test('store stride: the thinned buffer always spans the delay, with the smallest stride that does', () => {
  const { capacityFor, storeStride } = loadEngine().internals;
  for (const fps of [24, 25, 30, 50, 60, 90, 120, 144]) {
    const f = 1000 / fps;
    for (let d = 0; d <= 5000; d += 7) {
      const cap = capacityFor(d);
      const s = storeStride(d, f, cap);
      const span = (cap - 1) * s * f; // oldest to newest of a full buffer
      assert.ok(span >= d + 2 * s * f || span >= d * 1.03, `${fps} fps, ${d} ms: stride ${s} spans ${span.toFixed(1)} ms`);
      if (s > 1) assert.ok((cap - 3) * (s - 1) * f < 0.98 * d, `${fps} fps, ${d} ms: stride ${s - 1} would do`);
    }
  }
});

test('frame time: presentationTime, else expectedDisplayTime minus one 60 Hz frame, else the callback time', () => {
  const { frameTime } = loadEngine().internals;
  // Chrome 154: expectedDisplayTime = presentationTime + one frame (MP4) or one refresh period (WebM)
  assert.equal(frameTime({ presentationTime: 1000, expectedDisplayTime: 1016.6 }, 1002, 4.17), 1000);
  assert.equal(frameTime({ presentationTime: 1000, expectedDisplayTime: 1004.1 }, 1002, 4.17), 1000);
  assert.ok(Math.abs(frameTime({ expectedDisplayTime: 1016.7 }, 1002, 4.17) - 1000) < 1e-9);
  assert.ok(Math.abs(frameTime({ expectedDisplayTime: 1020 }, 1002, 20) - 1000) < 1e-9); // 50 Hz: one refresh period
  assert.equal(frameTime({}, 1002, 4.17), 1002);
  assert.equal(frameTime({ presentationTime: 0, expectedDisplayTime: NaN }, 1002), 1002);
});

test('default locale exists', () => {
  assert.ok(locales.includes(manifest.default_locale));
});

test('every locale has exactly the keys of en', () => {
  const keys = Object.keys(en).sort();
  for (const l of locales) assert.deepEqual(Object.keys(messages[l]).sort(), keys, `_locales/${l}`);
});

test('messages are non-empty and use only their declared placeholders', () => {
  for (const l of locales) {
    for (const [k, v] of Object.entries(messages[l])) {
      assert.equal(typeof v.message, 'string', `${l}.${k}`);
      assert.ok(v.message.trim(), `${l}.${k} is empty`);
      const used = [...v.message.matchAll(/\$([A-Za-z0-9_]+)\$/g)].map((m) => m[1].toLowerCase()).sort();
      const declared = Object.keys(v.placeholders || {}).map((p) => p.toLowerCase()).sort();
      const enDeclared = Object.keys(en[k].placeholders || {}).map((p) => p.toLowerCase()).sort();
      assert.deepEqual([...new Set(used)], declared, `${l}.${k}: placeholders in the text vs declared`);
      assert.deepEqual(declared, enDeclared, `${l}.${k}: placeholders differ from en`);
    }
  }
});

test('the popup only uses message keys that exist', () => {
  const html = read('src', 'popup.html');
  const js = read('src', 'popup.js');
  const used = new Set([
    ...[...html.matchAll(/data-i18n(?:-title|-aria-label)?="([^"]+)"/g)].map((m) => m[1]),
    ...[...js.matchAll(/\bt\('([A-Za-z0-9_]+)'/g)].map((m) => m[1]),
  ]);
  assert.ok(used.has('hintLongDelay'));
  for (const k of used) assert.ok(k in en, `popup uses missing key ${k}`);
});

test('manifest __MSG_ names exist', () => {
  const names = [...JSON.stringify(manifest).matchAll(/__MSG_([A-Za-z0-9_]+)__/g)].map((m) => m[1]);
  assert.ok(names.length);
  for (const n of names) assert.ok(n in en, n);
});
