'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { readyPopup, msg } = require('./popup-harness');

// What the app's local API answers when its per-app outputs give the browser's sound no speaker delay
// (TrayApp.ApiUndelayedStatus). message is in the app's language, which need not be the browser's.
const unknown = { running: true, app: 'HomePodCast', streaming: false, destinationRequired: true,
  reason: 'destination-unknown', message: '无法确定此程序的声音去处，同步暂不可用。', destinations: [] };
const stopped = { running: true, app: 'HomePodCast', streaming: false, destination: 'homepod', local: false,
  destinationRequired: true, reason: 'destination-unknown', message: '无法确定此程序的声音去处，同步暂不可用。' };
const local = { running: true, app: 'HomePodCast', streaming: false, destination: 'local', local: true,
  destinationRequired: false, reason: 'local-output', message: '此程序在本机播放，不使用音箱画面延迟。' };
const withoutReason = ({ reason, ...status }) => status; // as apps before reason answer

test('the popup explains mixer routes without a picture delay in the browser\'s language and no contradicting hint', async () => {
  for (const [status, text, state] of [
    [unknown, msg('statusDestinationUnknown'), 'unknown'],
    [stopped, msg('statusDestinationUnknown'), 'unknown'],
    [local, msg('statusLocalOutput'), 'idle'],
    [withoutReason(unknown), msg('statusDestinationUnknown'), 'unknown'],
    [withoutReason(local), msg('statusLocalOutput'), 'idle'],
    [{ ...unknown, reason: 'something-newer', message: 'Newer reason' }, 'Newer reason', 'unknown'],
  ]) {
    const p = await readyPopup({ status });
    const what = JSON.stringify(status);
    assert.equal(p.view().status, text, what);
    assert.equal(p.view().state, state, what);
    assert.equal(p.view().hint, null, what); // not "HomePodCast is not streaming": other apps may well be
    assert.equal(p.view().delay, '—', what);
    assert.equal(p.nodes.statusText.innerHTML, undefined, what); // the app's message only ever as text
  }
});

test('a plain not-streaming answer still says so', async () => {
  const p = await readyPopup({ status: { running: true, app: 'HomePodCast', streaming: false, videoDelayMs: 0 } });
  assert.equal(p.view().status, msg('statusIdle'));
  assert.equal(p.view().hint, msg('hintNotStreaming'));
});
