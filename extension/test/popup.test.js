'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { openPopup, readyPopup, settled, msg } = require('./popup-harness');

const streaming = (videoDelayMs) => ({ running: true, streaming: true, device: 'HomePod', videoDelayMs });

test('the popup says nothing about the page or the app before they have answered', async () => {
  const p = openPopup();
  await settled(); // asking the page
  const waiting = { status: msg('statusConnecting'), state: 'unknown', hint: null, dimmed: false, delay: '—',
    offset: '+0 ms', videos: '—' };
  assert.deepEqual(p.view(), waiting);
  p.answerPage({ host: 'example.test', videos: [] });
  await settled(); // settings read, asking the app
  assert.deepEqual(p.view(), { ...waiting, videos: msg('videosNone') });
  p.answerStatus({ running: false });
  await settled();
  assert.deepEqual(p.view(), { ...waiting, status: msg('statusNotRunning'), state: 'off', hint: msg('hintNotRunning'),
    videos: msg('videosNone') });

  const idle = await readyPopup({ status: { running: true, streaming: false } });
  assert.equal(idle.view().hint, msg('hintNotStreaming'));
  const live = await readyPopup({ status: streaming(141) });
  assert.equal(live.view().hint, null); assert.equal(live.view().delay, '141');
});

test('a page without the content script gets the reload hint once it has been asked, whatever the app says', async () => {
  const p = openPopup();
  await settled();
  p.answerPage(undefined);
  await settled();
  assert.equal(p.view().hint, msg('hintUnavailable')); assert.equal(p.view().dimmed, true);
  assert.equal(p.view().videos, '');
  p.answerStatus(streaming(141));
  await settled();
  assert.equal(p.view().hint, msg('hintUnavailable')); assert.equal(p.view().delay, '—');
});

test('switched off on all sites or this one, the popup says so before the app answers', async () => {
  const p = openPopup({ stored: { __global__: { enabled: false } } });
  await settled();
  p.answerPage({ host: 'example.test', videos: [] });
  await settled();
  assert.equal(p.view().hint, msg('hintGlobalOff')); assert.equal(p.view().delay, '0');
  const site = await readyPopup({ status: streaming(141), stored: { 'example.test': { enabled: false } } });
  assert.equal(site.view().hint, msg('hintSiteOff'));
});

// Collects the unhandled rejections while fn runs (and the turns after it).
async function rejections(fn) {
  const seen = [];
  const listener = (reason) => seen.push(reason);
  process.on('unhandledRejection', listener);
  try {
    await fn();
    await settled(); await settled();
  } finally {
    process.off('unhandledRejection', listener);
  }
  return seen;
}

test('unreadable settings keep the defaults and the popup still follows the app', async () => {
  let p;
  const seen = await rejections(async () => {
    p = openPopup({ getError: 'storage unavailable' });
    await settled();
    p.answerPage({ host: 'example.test', videos: [] });
    await settled();
    p.answerStatus(streaming(141));
  });
  assert.deepEqual(seen, []);
  assert.equal(p.view().delay, '141'); assert.equal(p.view().offset, '+0 ms');
  assert.ok([...p.timers.values()].some((timer) => timer.interval && timer.delay === 1000), 'the refresh runs');
  p.flush(1000); assert.equal(p.pending.status.length, 1); // and asks the app again
});

test('a failed save is checked (no unhandled rejection, no unchecked lastError)', async () => {
  const p = await readyPopup({ status: streaming(141), setError: 'MAX_WRITE_OPERATIONS_PER_MINUTE quota exceeded' });
  const seen = await rejections(async () => {
    p.press('plus'); p.flush(250);
    p.nodes.globalToggle.checked = false; p.nodes.globalToggle.dispatch('change'); p.flush(250);
  });
  assert.deepEqual(seen, []);
  assert.equal(p.runtime.unchecked, 0);
  assert.deepEqual(p.writes, []);

  const ok = await readyPopup({ status: streaming(141) });
  ok.press('plus'); ok.flush(250);
  assert.deepEqual(ok.data, { 'example.test': { enabled: true, offsetMs: 1 } });
  ok.press('minus'); ok.flush(250);
  assert.deepEqual(ok.data, {}); // back at the default: removed
});
