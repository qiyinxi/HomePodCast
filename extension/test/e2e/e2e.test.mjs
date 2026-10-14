// Browser tests of the extension (CI job extension-e2e): Chromium with the unpacked extension, local stand-ins for
// a YouTube and a bilibili player and a bare <video>, and a fake HomePodCast API. Covers what docs/
// device-test-checklist.zh-CN.md had people check by hand on the real sites, except what needs those sites or a
// person's eyes. Run: npm ci && npm test (in this folder; ffmpeg must be installed). HPC_E2E_HEADED=1 shows the
// browser; HPC_E2E_API_PORT picks the fake API's port (default 47100, or a free one when HomePodCast has it).
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { after, afterEach, before, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { CODE, framesBehind } from './frame-code.mjs';
import { sleep, startHarness, waitFor } from './harness.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const IDLE_MS = 2500; // sync-controls.js: the controls hide this long after the last activity
const OFFSET_LIMIT = 500;
const MAX_DELAY_MS = 2000;
const BASE_MS = 141; // the fake app's videoDelayMs (「推荐」-like)
const SCREEN_BASE_MS = 1000; // for screenshot checks: far more than a screenshot's own latency
const FRAME_MS = 1000 / CODE.FPS;

let h;
let msg; // chrome.i18n.getMessage with the browser's UI language

function loadMessages(uiLang) {
  const dirs = [uiLang.replace('-', '_'), uiLang.split('-')[0], 'en'];
  const dir = dirs.find((d) => fs.existsSync(path.join(HERE, '..', '..', '_locales', d, 'messages.json')));
  const table = JSON.parse(fs.readFileSync(path.join(HERE, '..', '..', '_locales', dir, 'messages.json'), 'utf8'));
  return (key, ...subs) => {
    const m = table[key];
    assert.ok(m, `message ${key} missing in ${dir}`);
    return m.message.replace(/\$([A-Za-z0-9_]+)\$/g, (_, name) => subs[Number(m.placeholders[name.toLowerCase()].content.slice(1)) - 1]);
  };
}

const signed = (n) => `${n >= 0 ? '+' : '−'}${Math.abs(n)}`;
const near = (a, b, tol = 2) => Math.abs(a - b) <= tol;

// ---------- helpers on a page ----------

const videoBox = (page) => page.evaluate(() => {
  const r = window.__e2e.player().getBoundingClientRect();
  return { left: r.left, top: r.top, width: r.width, height: r.height, x: r.left + r.width / 2, y: r.top + r.height / 2 };
});

async function pointAt(page, where) {
  if (where === 'outside') {
    await page.mouse.move(4, 4, { steps: 2 });
    return;
  }
  const b = await videoBox(page);
  const [fx, fy] = where === 'video' ? [0.5, 0.5] : where;
  await page.mouse.move(b.left + b.width * fx, b.top + b.height * fy, { steps: 4 });
}

const shown = async (page) => (await h.controls(page)).shown;
const waitShown = (page, timeout = 1500) => waitFor(() => shown(page), { timeout, what: 'the controls to show' });
const waitHidden = (page, timeout = 7000) => waitFor(async () => !(await shown(page)), { timeout, what: 'the controls to hide' });

/** Asserts the controls stay hidden for ms. */
async function staysHidden(page, ms, why) {
  const until = Date.now() + ms;
  while (Date.now() < until) {
    assert.equal(await shown(page), false, `the controls showed up ${why}`);
    await sleep(100);
  }
}

async function alt(page, key, { shift = false, times = 1 } = {}) {
  await page.keyboard.down('Alt');
  if (shift) await page.keyboard.down('Shift');
  for (let i = 0; i < times; i++) await page.keyboard.press(key);
  if (shift) await page.keyboard.up('Shift');
  await page.keyboard.up('Alt');
}

async function clickControl(page, text) {
  await pointAt(page, 'video'); // shows the controls
  const c = await waitFor(async () => {
    const s = await h.controls(page);
    return s.shown && s.buttons.find((b) => b.text === text) ? s : null;
  }, { timeout: 2000, what: `the "${text}" button` });
  const b = c.buttons.find((x) => x.text === text);
  await page.mouse.click(b.x, b.y);
}

/** Sets the app's videoDelayMs and waits until the page uses it. */
async function setBase(page, ms) {
  h.app.setStatus({ videoDelayMs: ms });
  await waitFor(async () => (await h.debug(page)).status?.videoDelayMs === ms, { timeout: 5000, what: `videoDelayMs ${ms} to arrive` });
}

const fullscreenElement = (page) => page.evaluate(() => {
  const fe = document.fullscreenElement;
  return fe && { tag: fe.tagName, id: fe.id, cls: typeof fe.className === 'string' ? fe.className : '', isVideoParent: fe === window.__e2e.player().parentElement };
});

/** Waits until the saved site offset, the content script and the engine all agree on offset. */
async function expectOffset(page, offset) {
  const host = (await h.debug(page)).host;
  const base = h.app.status.videoDelayMs;
  const delay = Math.min(MAX_DELAY_MS, Math.max(0, base + offset));
  await waitFor(async () => {
    const d = await h.debug(page);
    const stored = (await h.storage(page))[host];
    const storedOffset = stored ? stored.offsetMs : 0;
    return d.settings.offsetMs === offset && d.delay === delay && d.stats[0].delayMs === delay && storedOffset === offset;
  }, { timeout: 3000, what: `site offset ${offset} ms (delay ${delay} ms) saved and applied` });
  return delay;
}

/** Layout facts that sync fullscreen must give back on exit. */
const layout = (page) => page.evaluate(() => {
  const v = window.__e2e.player();
  const decls = (el) => {
    const out = {};
    for (let i = 0; i < el.style.length; i++) {
      const k = el.style[i];
      out[k] = el.style.getPropertyValue(k) + (el.style.getPropertyPriority(k) ? ' !important' : '');
    }
    return out;
  };
  const rect = (el) => {
    const r = el.getBoundingClientRect();
    return [r.left, r.top, r.width, r.height].map((n) => Math.round(n));
  };
  const chain = [];
  for (let el = v.parentElement; el && el !== document.body; el = el.parentElement) chain.push(`${el.tagName}.${el.className}`);
  const styled = [...document.querySelectorAll('[style]')]
    .filter((e) => e !== v && !e.matches('canvas[data-hpc-delay], [data-hpc-sync-controls]'))
    .map((e) => `${e.tagName}: ${e.getAttribute('style')}`);
  const c = window.__e2e.overlayOf(v);
  return {
    videoStyle: decls(v), video: rect(v), parent: rect(v.parentElement), chain, styled,
    canvasAfterVideo: !!c, canvas: c ? rect(c) : null, scroll: [Math.round(scrollX), Math.round(scrollY)],
  };
});

/** The delayed picture is on screen: a screenshot shows a frame about the delay behind the video. */
async function assertDelayedOnScreen(page, points = []) {
  const base = h.app.status.videoDelayMs;
  assert.ok(base >= 900, 'screen checks need a long delay');
  const s = await h.screen(page, { points: points.map((p) => p.at) });
  assert.notEqual(s.frame, null, 'no picture with a readable frame code where the video is');
  const lag = framesBehind(s.video, s.frame) * FRAME_MS;
  // A screenshot takes some time itself, so the lag reads a bit long; without the delayed canvas it would be that alone.
  assert.ok(lag >= base * 0.6 && lag <= base + 1500, `screen shows frame ${s.frame} while the video is at ${s.video}: ${Math.round(lag)} ms behind, expected about ${base}`);
  points.forEach((p, i) => {
    const [r, g, b] = s.colors[i];
    assert.ok(near(r, p.rgb[0], 40) && near(g, p.rgb[1], 40) && near(b, p.rgb[2], 40),
      `${p.name} not visible at ${p.at} (colour ${s.colors[i]}, expected ${p.rgb})`);
  });
  return s;
}

const centerOf = (page, selector) => page.evaluate((sel) => {
  const r = document.querySelector(sel).getBoundingClientRect();
  return [r.left + r.width / 2, r.top + r.height / 2];
}, selector);

// A point in a label's padding, where only its background colour shows (not its text).
const paddingOf = (page, selector) => page.evaluate((sel) => {
  const r = document.querySelector(sel).getBoundingClientRect();
  return [r.left + 4, r.top + r.height / 2];
}, selector);

async function leaveFullscreen(page) {
  if (await fullscreenElement(page)) await page.evaluate(() => document.exitFullscreen());
  await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving fullscreen' });
}

/** The delayed canvas sits exactly over the video. */
async function assertCanvasOverVideo(page) {
  await waitFor(async () => {
    const l = await layout(page);
    return l.canvas && l.canvas.every((n, i) => near(n, l.video[i], 2)) ? l : null;
  }, { timeout: 3000, what: 'the delayed canvas to cover the video' });
}

/** Overlay frame numbers over ms: all readable, all from at least minFrame on, and moving on. */
async function watchOverlay(page, ms, { minFrame = 0 } = {}) {
  const frames = [];
  const until = Date.now() + ms;
  while (Date.now() < until) {
    const s = await page.evaluate(() => window.__e2e.sample());
    frames.push(s.overlay);
    await sleep(80);
  }
  assert.ok(frames.every((f) => f !== null), `the delayed canvas showed an unreadable picture: ${frames.join(',')}`);
  assert.ok(frames.every((f) => f >= minFrame), `the delayed canvas showed frames from before ${minFrame}: ${frames.join(',')}`);
  return frames;
}

/** Runs fn up to `tries` times: only for checks that time things and can lose to a busy machine. */
async function retry(fn, tries = 2) {
  for (let i = 1; ; i++) {
    try {
      return await fn();
    } catch (e) {
      if (i >= tries) throw e;
      console.log(`# retrying after: ${e.message}`);
    }
  }
}

// ---------- the tests ----------

before(async () => {
  h = await startHarness();
  const worker = await waitFor(async () => (await h.ext.workers())[0], { what: 'the service worker' });
  const uiLang = await worker.evaluate(() => chrome.i18n.getUILanguage());
  console.log(`# ${await h.browser.version()}, UI language ${uiLang}, fake API on port ${h.app.port}`);
  msg = loadMessages(uiLang);
});

after(async () => {
  if (h) await h.close();
});

// A failed test leaves screenshots of the open tabs in artifacts/ (CI uploads them).
afterEach(async (t) => {
  if (t.passed || !h) return;
  const dir = path.join(HERE, 'artifacts');
  fs.mkdirSync(dir, { recursive: true });
  const name = t.name.replace(/[^\w.-]+/g, '_').slice(0, 80);
  for (const [i, p] of (await h.browser.pages()).entries()) {
    await p.screenshot({ path: path.join(dir, `${name}-${i}.png`) }).catch(() => {});
  }
});

describe('YouTube-like player', () => {
  let page;
  let saved;
  after(() => page?.close());

  test('the delayed canvas covers the video and runs the app\'s videoDelayMs behind it', async () => {
    h.app.resetStatus();
    page = await h.open('youtube', '/watch?v=e2e');
    const d = await h.debug(page);
    assert.equal(d.host, 'youtube.localhost');
    assert.equal(d.stats.length, 1);
    assert.equal(d.stats[0].delayMs, BASE_MS);
    assert.match(d.stats[0].mode, /^(videoframe|canvas)$/);
    await assertCanvasOverVideo(page);
    await retry(async () => {
      const lag = await h.lagMs(page);
      assert.ok(lag.median >= BASE_MS - 70 && lag.median <= BASE_MS + 110, `lag ${lag.median} ms at ${BASE_MS} ms`);
    });
    await setBase(page, 400);
    await retry(async () => {
      await sleep(800);
      const lag = await h.lagMs(page);
      assert.ok(lag.median >= 400 - 70 && lag.median <= 400 + 110, `lag ${lag.median} ms at 400 ms`);
    });
    await setBase(page, SCREEN_BASE_MS);
    await sleep(1200);
    await assertDelayedOnScreen(page);
  });

  test('the controls appear when the pointer moves over the video and hide about 2.5 s after it stops', async () => {
    await pointAt(page, 'outside');
    await waitHidden(page);
    await staysHidden(page, 500, 'with the pointer outside the video');
    await retry(async () => {
      await pointAt(page, 'outside');
      await waitHidden(page);
      await pointAt(page, 'video');
      const movedAt = Date.now();
      await waitShown(page);
      const c = await h.controls(page);
      assert.deepEqual(c.buttons.map((b) => b.text), [msg('syncFullscreen')], 'outside fullscreen only 同步全屏 shows');
      assert.equal(c.parentTag, 'BODY');
      await waitHidden(page, IDLE_MS + 3000);
      const after = Date.now() - movedAt;
      assert.ok(after >= IDLE_MS - 600 && after <= IDLE_MS + 1500, `hid ${after} ms after the last pointer movement`);
    });
  });

  test('Alt alone shows the controls, which hide again about 2.5 s later', async () => {
    await waitHidden(page);
    await retry(async () => {
      await page.keyboard.press('Alt');
      const pressedAt = Date.now();
      await waitShown(page);
      await waitHidden(page, IDLE_MS + 3000);
      const after = Date.now() - pressedAt;
      assert.ok(after >= IDLE_MS - 600 && after <= IDLE_MS + 1500, `hid ${after} ms after Alt`);
    });
  });

  test('the site\'s own fullscreen (key f) shows no controls by itself; Alt+→ adjusts while they are hidden', async () => {
    await pointAt(page, 'outside');
    await waitHidden(page);
    await page.keyboard.press('f');
    await waitFor(async () => (await fullscreenElement(page))?.id === 'movie_player', { what: 'the player to go fullscreen' });
    await staysHidden(page, 1500, 'when the site went fullscreen');
    await alt(page, 'ArrowRight');
    const delay = await expectOffset(page, 5);
    const c = await waitFor(async () => {
      const s = await h.controls(page);
      return s.feedback ? s : null;
    }, { what: 'the delay notice' });
    assert.equal(c.feedback, msg('syncDelayFeedback', delay, signed(5)));
    assert.ok(c.inFullscreenElement, 'the controls are inside the fullscreen player');
    assert.deepEqual(c.buttons.map((b) => b.text), [msg('syncExitFullscreen'), '−20 ms', '−5 ms', '+5 ms', '+20 ms']);
    await assertCanvasOverVideo(page);
    await sleep(1000);
    await assertDelayedOnScreen(page, [
      { name: 'the captions', at: await paddingOf(page, '.ytp-caption-segment'), rgb: [255, 204, 0] },
      { name: 'the progress bar', at: await centerOf(page, '.ytp-progress-bar'), rgb: [255, 0, 51] },
    ]);
    await page.keyboard.press('f');
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'the site to leave fullscreen' });
    await h.clearStorage(page);
    await expectOffset(page, 0);
  });

  test('the site\'s fullscreen button: the controls go when it goes fullscreen and come back with the mouse or Alt', async () => {
    const button = await centerOf(page, '.ytp-fullscreen-button');
    await page.mouse.move(button[0], button[1], { steps: 4 }); // over the video: the controls show, as they should
    await page.mouse.click(button[0], button[1]);
    await waitFor(async () => (await fullscreenElement(page))?.id === 'movie_player', { what: 'the player to go fullscreen' });
    await waitHidden(page, 1000);
    await staysHidden(page, 1500, 'after the site went fullscreen with the pointer resting');
    await pointAt(page, [0.4, 0.4]);
    await waitShown(page);
    await waitHidden(page);
    await page.keyboard.press('Alt');
    await waitShown(page);
    await alt(page, 'ArrowLeft');
    await expectOffset(page, -5);
    await page.evaluate(() => document.exitFullscreen());
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving fullscreen' });
    await h.clearStorage(page);
    await expectOffset(page, 0);
  });

  test('同步全屏 makes the player (not the bare video) fullscreen and keeps captions and controls over the delayed picture', async () => {
    await pointAt(page, 'outside');
    await waitHidden(page);
    saved = await layout(page);
    await clickControl(page, msg('syncFullscreen'));
    const fe = await waitFor(() => fullscreenElement(page), { what: 'sync fullscreen' });
    // The outermost element with the video's box: here the wrapper around YouTube's #movie_player.
    assert.equal(fe.id, 'player-container', `fullscreen element ${JSON.stringify(fe)}`);
    assert.ok(await page.evaluate(() => document.fullscreenElement.contains(document.getElementById('movie_player'))));
    const c = await waitFor(async () => {
      const s = await h.controls(page);
      return s.feedback ? s : null;
    }, { what: 'the shortcut notice' });
    assert.equal(c.feedback, msg('syncShortcuts'));
    assert.ok(c.inFullscreenElement);
    const vb = await videoBox(page);
    const vp = await page.evaluate(() => [innerWidth, innerHeight]);
    assert.ok(near(vb.width, vp[0], 2) || near(vb.height, vp[1], 2), `the picture doesn't fill the screen: ${JSON.stringify(vb)} in ${vp}`);
    await assertCanvasOverVideo(page);
    await sleep(800);
    await assertDelayedOnScreen(page, [
      { name: 'the captions', at: await paddingOf(page, '.ytp-caption-segment'), rgb: [255, 204, 0] },
      { name: 'the progress bar', at: await centerOf(page, '.ytp-progress-bar'), rgb: [255, 0, 51] },
    ]);
    await watchOverlay(page, 1000);
  });

  test('±5 / ±20 ms buttons change the delay and the saved site offset', async () => {
    const base = h.app.status.videoDelayMs;
    let offset = 0;
    for (const delta of [20, 5, -5, -20, -20]) {
      await clickControl(page, `${signed(delta)} ms`);
      offset += delta;
      const delay = await expectOffset(page, offset);
      assert.equal(delay, base + offset);
      const c = await h.controls(page);
      assert.equal(c.feedback, msg('syncDelayFeedback', delay, signed(offset)));
    }
    assert.equal((await h.storage(page))['youtube.localhost'].offsetMs, -20);
  });

  test('Alt+←/→ (5 ms) and Alt+Shift+←/→ (20 ms) adjust also while the controls are hidden', async () => {
    await pointAt(page, [0.3, 0.6]); // off the controls, so they can hide (the notice too: 4 s)
    await page.evaluate(() => document.querySelector('#movie_player').focus()); // out of the clicked button
    await waitHidden(page, 8000);
    let offset = -20;
    for (const [key, shift, delta] of [['ArrowRight', false, 5], ['ArrowRight', true, 20], ['ArrowLeft', false, -5], ['ArrowRight', true, 20], ['ArrowLeft', true, -20]]) {
      await alt(page, key, { shift });
      offset += delta;
      const delay = await expectOffset(page, offset);
      assert.equal((await h.controls(page)).feedback, msg('syncDelayFeedback', delay, signed(offset)));
    }
    assert.equal(offset, 0);
    assert.equal((await h.storage(page))['youtube.localhost'], undefined, 'an offset of 0 is not stored');
  });

  test('the site offset stays within ±500 ms and the picture delay within 0…2000 ms', async () => {
    let base = h.app.status.videoDelayMs; // 1000
    await alt(page, 'ArrowRight', { shift: true, times: 30 });
    assert.equal(await expectOffset(page, Math.min(OFFSET_LIMIT, MAX_DELAY_MS - base)), base + OFFSET_LIMIT);
    await alt(page, 'ArrowLeft', { shift: true, times: 60 });
    assert.equal(await expectOffset(page, -OFFSET_LIMIT), base - OFFSET_LIMIT);
    base = BASE_MS;
    await setBase(page, base); // the offset stays −500, the picture delay is 0
    await alt(page, 'ArrowLeft', { shift: true, times: 2 }); // the next step lands where the delay reaches 0
    assert.equal(await expectOffset(page, -base), 0, 'stops where the picture delay reaches 0');
    base = 1800;
    await setBase(page, base);
    await alt(page, 'ArrowRight', { shift: true, times: 40 });
    assert.equal(await expectOffset(page, MAX_DELAY_MS - base), MAX_DELAY_MS, 'stops where the picture delay reaches 2000 ms');
    await h.clearStorage(page);
    await expectOffset(page, 0);
    await setBase(page, SCREEN_BASE_MS);
  });

  test('退出全屏 restores the page layout', async () => {
    await clickControl(page, msg('syncExitFullscreen'));
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving sync fullscreen' });
    await assertCanvasOverVideo(page);
    const now = await waitFor(async () => {
      const l = await layout(page);
      return l.video.every((n, i) => n === saved.video[i]) ? l : null;
    }, { timeout: 3000, what: 'the video back in place' }).catch(() => layout(page));
    assert.deepEqual(now, saved);
    const c = await h.controls(page);
    assert.equal(c.count, 1);
    assert.equal(c.parentTag, 'BODY');
    await waitHidden(page);
  });

  test('pause, play and seeking keep the delayed picture right', async () => {
    await pointAt(page, 'outside');
    await page.keyboard.press('k'); // the site's pause key
    await waitFor(() => page.evaluate(() => document.querySelector('video').paused), { what: 'pause' });
    // Paused: the delayed canvas shows the paused frame itself (the sound has stopped too).
    const paused = await waitFor(async () => {
      const s = await page.evaluate(() => window.__e2e.sample());
      return s.overlay !== null && s.overlay === s.video ? s : null;
    }, { timeout: 2000, what: 'the delayed canvas to show the paused frame' });
    const still = await watchOverlay(page, 600, { minFrame: paused.video });
    assert.ok(still.every((f) => f === paused.video), `the paused picture changed: ${still}`);
    const shot = await h.screen(page);
    assert.equal(shot.frame, paused.video, 'the screen shows the paused frame');

    await page.keyboard.press('k');
    await waitFor(() => page.evaluate(() => !document.querySelector('video').paused), { what: 'play' });
    await sleep(h.app.status.videoDelayMs + 800);
    const playing = await watchOverlay(page, 800, { minFrame: paused.video });
    assert.ok(playing[playing.length - 1] > playing[0], `the delayed picture doesn't move after play: ${playing}`);

    // Seek to 75 % with the site's progress bar.
    const bar = await page.evaluate(() => {
      const r = document.querySelector('.ytp-progress-bar').getBoundingClientRect();
      return { x: r.left + r.width * 0.75, y: r.top + r.height / 2 };
    });
    await page.mouse.click(bar.x, bar.y);
    const target = Math.round(0.75 * CODE.SECONDS * CODE.FPS);
    await waitFor(() => page.evaluate((t) => {
      const v = document.querySelector('video');
      return !v.seeking && v.currentTime > t - 1;
    }, 0.75 * CODE.SECONDS), { what: 'the seek' });
    // Right after the seek the delayed canvas shows the new position, never the frames from before it.
    const afterSeek = await waitFor(async () => {
      const s = await page.evaluate(() => window.__e2e.sample());
      return s.overlay !== null && s.overlay >= target - 3 ? s : null;
    }, { timeout: 2000, what: 'the delayed canvas to show the seek position' });
    assert.ok(afterSeek.overlay <= target + 60, `after the seek the canvas shows ${afterSeek.overlay}`);
    const frames = await watchOverlay(page, h.app.status.videoDelayMs + 1500, { minFrame: target - 3 });
    assert.ok(frames[frames.length - 1] > frames[0], `the delayed picture doesn't move after the seek: ${frames}`);
    await retry(async () => {
      const lag = await h.lagMs(page);
      const base = h.app.status.videoDelayMs;
      assert.ok(lag.median >= base - 100 && lag.median <= base + 150, `lag ${lag.median} ms after the seek at ${base} ms`);
    });
  });

  test('when the app stops streaming the picture is no longer delayed, and the controls are not offered', async () => {
    h.app.setStatus({ streaming: false });
    await waitFor(() => page.evaluate(() => !window.__e2e.overlayOf()), { timeout: 5000, what: 'the delayed canvas to go' });
    const d = await h.debug(page);
    assert.equal(d.stats[0].mode, 'off');
    assert.equal(d.stats[0].reason, 'zero-delay');
    const s = await h.screen(page);
    assert.notEqual(s.frame, null);
    const behind = framesBehind(s.video, s.frame) * FRAME_MS;
    assert.ok(behind < 400, `the screen is still ${Math.round(behind)} ms behind`);
    await pointAt(page, 'video');
    await staysHidden(page, 800, 'while the app is not streaming');
    await page.keyboard.press('Alt');
    await staysHidden(page, 500, 'on Alt while the app is not streaming');

    h.app.setDown(true); // HomePodCast not running at all
    await waitFor(async () => (await h.debug(page)).status?.running === false, { timeout: 5000, what: 'running: false' });
    assert.equal((await h.debug(page)).stats[0].mode, 'off');

    h.app.setDown(false);
    h.app.setStatus({ streaming: true });
    await h.waitOverlay(page);
    await retry(async () => {
      await sleep(h.app.status.videoDelayMs + 500);
      const lag = await h.lagMs(page);
      const base = h.app.status.videoDelayMs;
      assert.ok(lag.median >= base - 100 && lag.median <= base + 150, `lag ${lag.median} ms after streaming resumed`);
    });
  });

  describe('popup', () => {
    let popup;

    const popupText = () => popup.evaluate(() => ({
      status: document.getElementById('statusText').textContent,
      host: document.getElementById('host').textContent,
      delay: document.getElementById('effDelay').textContent,
      offset: document.getElementById('offset').textContent,
      hint: document.getElementById('hint').hidden ? null : document.getElementById('hint').textContent,
      videos: document.getElementById('videos').textContent,
      site: document.getElementById('siteToggle').checked,
    }));
    const waitPopup = (pred, what) => waitFor(async () => {
      const p = await popupText();
      return pred(p) ? p : null;
    }, { timeout: 5000, what });

    test('the toolbar popup shows the app status, the site, the picture delay and the measured lag', async () => {
      await page.bringToFront();
      await setBase(page, BASE_MS);
      await h.ext.triggerAction(page);
      const target = await h.browser.waitForTarget((t) => t.url().endsWith('/src/popup.html'), { timeout: 5000 });
      popup = await target.asPage();
      const p = await waitPopup((x) => x.delay === String(BASE_MS), 'the popup to load');
      assert.equal(p.status, [msg('statusStreaming'), 'E2E HomePod', `${BASE_MS} ms`].join(' · '));
      assert.equal(p.host, 'youtube.localhost');
      assert.equal(p.offset, '+0 ms');
      assert.equal(p.hint, null, `no hint at ${BASE_MS} ms`);
      assert.ok(p.videos.startsWith(`${msg('videosCount', 1)} · ${msg('videosDelaying', 1)}`), p.videos);
    });

    test('above 300 ms the popup suggests the 影视 or 推荐 scene', async () => {
      h.app.setStatus({ videoDelayMs: 300 });
      let p = await waitPopup((x) => x.delay === '300', 'the popup to show 300 ms');
      assert.equal(p.hint, null, 'no hint at exactly 300 ms');
      h.app.setStatus({ videoDelayMs: 538 }); // 「音乐」
      p = await waitPopup((x) => x.delay === '538', 'the popup to show 538 ms');
      assert.equal(p.hint, msg('hintLongDelay'));
      if (msg('htmlLang') === 'zh-CN') assert.equal(p.hint, '延迟较长：看视频建议用「影视」或「推荐」场景。');
      h.app.setStatus({ videoDelayMs: BASE_MS });
      p = await waitPopup((x) => x.delay === String(BASE_MS), 'the popup back at the base delay');
      assert.equal(p.hint, null);
    });

    test('compatibility delays above 2000 ms explain the picture limit', async () => {
      h.app.setStatus({ videoDelayMs: 2250 });
      const p = await waitPopup((x) => x.delay === '2000', 'the popup to clamp a compatibility delay');
      assert.equal(p.hint, msg('hintDelayLimit'));
      h.app.setStatus({ videoDelayMs: BASE_MS });
      await waitPopup((x) => x.delay === String(BASE_MS), 'the popup back at the normal delay');
    });

    test('the popup\'s −/+ change the site offset the page uses', async () => {
      await setBase(page, BASE_MS);
      await popup.click('#plus');
      await popup.click('#plus');
      let p = await waitPopup((x) => x.offset === '+2 ms', 'offset +2');
      assert.equal(p.delay, String(BASE_MS + 2));
      await expectOffset(page, 2);
      await popup.click('#minus');
      await popup.click('#minus');
      await popup.click('#minus');
      p = await waitPopup((x) => x.offset === '−1 ms', 'offset −1');
      await expectOffset(page, -1);
      await popup.click('#reset');
      await waitPopup((x) => x.offset === '+0 ms', 'offset reset');
      await expectOffset(page, 0);
    });

    test('在此网站启用 switches the delayed picture off and back on', async () => {
      await popup.click('#siteToggle');
      let p = await waitPopup((x) => !x.site, 'the site switch off');
      assert.equal(p.hint, msg('hintSiteOff'));
      await waitFor(() => page.evaluate(() => !window.__e2e.overlayOf()), { timeout: 4000, what: 'the delayed canvas to go' });
      assert.equal((await h.debug(page)).stats[0].reason, 'disabled');
      await popup.click('#siteToggle');
      p = await waitPopup((x) => x.site, 'the site switch on');
      await h.waitOverlay(page);
      assert.deepEqual(await h.storage(page), {}, 'back to the defaults: nothing stored');
    });

    test('the popup says when the app isn\'t streaming or isn\'t running', async () => {
      h.app.setStatus({ streaming: false });
      let p = await waitPopup((x) => x.status === msg('statusIdle'), 'not streaming');
      assert.equal(p.delay, '—');
      assert.equal(p.hint, msg('hintNotStreaming'));
      h.app.setDown(true);
      p = await waitPopup((x) => x.status === msg('statusNotRunning'), 'not running');
      assert.equal(p.hint, msg('hintNotRunning'));
      h.app.setDown(false);
      h.app.setStatus({ streaming: true });
      await waitPopup((x) => x.delay === String(BASE_MS), 'streaming again');
      await popup.close();
    });
  });
});

describe('bilibili-like player', () => {
  let page;
  let saved;
  after(() => page?.close());

  test('同步全屏 takes the video area, with danmaku, subtitles and controls over the delayed picture', async () => {
    h.app.resetStatus();
    h.app.setStatus({ videoDelayMs: SCREEN_BASE_MS });
    page = await h.open('bilibili', '/video/BV1e2e');
    assert.equal((await h.debug(page)).host, 'bilibili.localhost');
    await assertCanvasOverVideo(page);
    saved = await layout(page);
    await clickControl(page, msg('syncFullscreen'));
    const fe = await waitFor(() => fullscreenElement(page), { what: 'sync fullscreen' });
    assert.equal(fe.cls, 'bpx-player-primary-area', `fullscreen element ${JSON.stringify(fe)}`);
    await assertCanvasOverVideo(page);
    await sleep(1200);
    await assertDelayedOnScreen(page, [
      { name: 'the danmaku', at: await paddingOf(page, '.bili-danmaku-x-dm'), rgb: [255, 102, 204] },
      { name: 'the subtitles', at: await paddingOf(page, '.bpx-player-subtitle-wrap span'), rgb: [255, 204, 0] },
      { name: 'the progress bar', at: await centerOf(page, '.bpx-player-progress'), rgb: [0, 174, 236] },
    ]);
    await clickControl(page, '+20 ms');
    await expectOffset(page, 20);
    await clickControl(page, msg('syncExitFullscreen'));
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving sync fullscreen' });
    await assertCanvasOverVideo(page);
    assert.deepEqual(await layout(page), saved);
    await h.clearStorage(page);
    await expectOffset(page, 0);
  });

  test('the shortcuts leave a focused input alone (the danmaku box in the site\'s fullscreen)', async () => {
    await leaveFullscreen(page);
    await pointAt(page, 'outside');
    await page.click('.bpx-player-ctrl-full');
    await waitFor(async () => (await fullscreenElement(page))?.cls === 'bpx-player-container', { what: 'the site\'s fullscreen' });
    await page.click('.bpx-player-dm-input');
    await page.keyboard.type('abc');
    await page.evaluate(() => { window.__keys.length = 0; });
    await alt(page, 'ArrowRight');
    await alt(page, 'ArrowRight', { shift: true });
    await sleep(600);
    const keys = await page.evaluate(() => window.__keys.filter((k) => k.key === 'ArrowRight'));
    assert.equal(keys.length, 2, 'the input got both key presses');
    assert.ok(keys.every((k) => k.alt && !k.prevented), `the extension intercepted a key in the input: ${JSON.stringify(keys)}`);
    assert.equal((await h.debug(page)).settings.offsetMs, 0, 'no adjustment while typing');
    assert.equal(await page.evaluate(() => document.querySelector('.bpx-player-dm-input').value), 'abc');
    // Out of the input, the same keys adjust.
    await page.evaluate(() => document.activeElement.blur());
    await alt(page, 'ArrowRight');
    await expectOffset(page, 5);
    await page.click('.bpx-player-ctrl-full');
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving fullscreen' });
    await h.clearStorage(page);
    await expectOffset(page, 0);
  });

  test('网页全屏 (web fullscreen) and pausing keep the delayed picture over the video', async () => {
    await leaveFullscreen(page);
    await pointAt(page, 'outside');
    await page.click('.bpx-player-ctrl-web');
    await waitFor(() => page.evaluate(() => document.querySelector('.bpx-player-container').dataset.screen === 'web'), { what: 'web fullscreen' });
    const vb = await videoBox(page);
    const vp = await page.evaluate(() => innerWidth);
    assert.ok(near(vb.width, vp, 2), `web fullscreen video ${vb.width} wide in a ${vp} px viewport`);
    await assertCanvasOverVideo(page);
    await sleep(800);
    await assertDelayedOnScreen(page, [
      { name: 'the danmaku', at: await paddingOf(page, '.bili-danmaku-x-dm'), rgb: [255, 102, 204] },
    ]);
    await watchOverlay(page, 600);
    await page.click('.bpx-player-ctrl-play');
    const paused = await waitFor(async () => {
      const s = await page.evaluate(() => window.__e2e.sample());
      return s.paused && s.overlay !== null && s.overlay === s.video ? s : null;
    }, { timeout: 2500, what: 'the paused frame on the delayed canvas' });
    assert.equal((await h.screen(page)).frame, paused.video);
    await page.click('.bpx-player-ctrl-play');
    await page.click('.bpx-player-ctrl-web');
    await waitFor(() => page.evaluate(() => document.querySelector('.bpx-player-container').dataset.screen === 'normal'), { what: 'leaving web fullscreen' });
    await assertCanvasOverVideo(page);
    const l = await layout(page);
    assert.deepEqual(l.video, saved.video);
    await sleep(h.app.status.videoDelayMs + 800);
    await watchOverlay(page, 600);
  });
});

describe('bilibili-like WASM player (<bwp-video>: a worker draws the picture on a canvas in a closed shadow root)', () => {
  let page;
  after(() => page?.close());

  test('the canvas player is delayed like a video', async () => {
    h.app.resetStatus();
    page = await h.open('bilibili', '/video/BV1e2e?player=bwp');
    const d = await h.debug(page);
    assert.equal(d.stats.length, 1, 'only the <bwp-video> is taken over (not its 2×2 px source)');
    assert.equal(d.stats[0].source, 'canvas');
    assert.equal(d.stats[0].sourceIssue, null);
    assert.equal(d.stats[0].mode, 'canvas');
    await assertCanvasOverVideo(page);
    await retry(async () => {
      await sleep(500);
      const lag = await h.lagMs(page);
      // measured against the source video: the worker draws each frame a little after the video shows it
      assert.ok(lag.median >= BASE_MS - 70 && lag.median <= BASE_MS + 170, `lag ${lag.median} ms at ${BASE_MS} ms`);
    });
    await setBase(page, SCREEN_BASE_MS);
    await sleep(1500);
    await assertDelayedOnScreen(page, [
      { name: 'the danmaku', at: await paddingOf(page, '.bili-danmaku-x-dm'), rgb: [255, 102, 204] },
    ]);
  });

  test('pausing shows the paused frame, and 同步全屏 takes the video area', async () => {
    await pointAt(page, 'outside');
    await page.click('.bpx-player-ctrl-play');
    await waitFor(async () => {
      const s = await page.evaluate(() => window.__e2e.sample());
      return s.paused && s.overlay !== null && Math.abs(s.overlay - s.video) <= 1 ? s : null;
    }, { timeout: 3000, what: 'the paused frame on the delayed canvas' });
    await page.click('.bpx-player-ctrl-play');
    await sleep(h.app.status.videoDelayMs + 800);
    const frames = await watchOverlay(page, 800);
    assert.ok(frames[frames.length - 1] > frames[0], `the delayed picture doesn't move after play: ${frames}`);
    const saved = await layout(page);
    await clickControl(page, msg('syncFullscreen'));
    const fe = await waitFor(() => fullscreenElement(page), { what: 'sync fullscreen' });
    assert.equal(fe.cls, 'bpx-player-primary-area', `fullscreen element ${JSON.stringify(fe)}`);
    await assertCanvasOverVideo(page);
    await sleep(800);
    await assertDelayedOnScreen(page, [
      { name: 'the subtitles', at: await paddingOf(page, '.bpx-player-subtitle-wrap span'), rgb: [255, 204, 0] },
    ]);
    await clickControl(page, msg('syncExitFullscreen'));
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving sync fullscreen' });
    await assertCanvasOverVideo(page);
    assert.deepEqual(await layout(page), saved);
  });
});

describe('page whose video no player frames', () => {
  let page;
  after(() => page?.close());

  test('同步全屏 takes the video\'s parent, fills the screen and restores the page on exit', async () => {
    h.app.resetStatus();
    h.app.setStatus({ videoDelayMs: SCREEN_BASE_MS });
    page = await h.open('plain', '/article');
    await assertCanvasOverVideo(page);
    const saved = await layout(page);
    await clickControl(page, msg('syncFullscreen'));
    const fe = await waitFor(() => fullscreenElement(page), { what: 'sync fullscreen' });
    assert.equal(fe.tag, 'ARTICLE');
    assert.ok(fe.isVideoParent);
    await waitFor(async () => {
      const vb = await videoBox(page);
      const vp = await page.evaluate(() => [innerWidth, innerHeight]);
      return near(vb.width, vp[0], 1) && near(vb.height, vp[1], 1);
    }, { timeout: 3000, what: 'the video to fill the screen' });
    await assertCanvasOverVideo(page);
    await sleep(1200);
    await assertDelayedOnScreen(page);
    await clickControl(page, msg('syncExitFullscreen'));
    await waitFor(async () => !(await fullscreenElement(page)), { what: 'leaving sync fullscreen' });
    await assertCanvasOverVideo(page);
    assert.deepEqual(await layout(page), saved);
    assert.equal(await page.evaluate(() => document.querySelector('video').style.length), 0, 'no inline style left on the video');
  });
});

test('no uncaught errors in the pages or the extension\'s content scripts', () => {
  assert.deepEqual(h.pageErrors, []);
});

test('the extension only ever asks GET /v1/status, from its service worker', () => {
  const reqs = h.app.requests;
  assert.ok(reqs.length > 10, `only ${reqs.length} status requests`);
  for (const r of reqs) {
    assert.equal(r.method, 'GET');
    assert.equal(r.path, '/v1/status');
    assert.equal(r.host, `127.0.0.1:${h.app.port}`);
    assert.ok(r.origin === null || r.origin === `chrome-extension://${h.ext.id}`, `request from ${r.origin}`);
  }
  // The service worker caches the status for 0.5 s, however many frames ask once a second.
  const span = (reqs[reqs.length - 1].at - reqs[0].at) / 1000;
  assert.ok(reqs.length <= span * 2.5 + 5, `${reqs.length} requests in ${span.toFixed(0)} s`);
});
