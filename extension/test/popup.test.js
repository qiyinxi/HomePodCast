'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { readyPopup } = require('./popup-harness');

const streaming = (videoDelayMs) => ({ running: true, streaming: true, device: 'HomePod', videoDelayMs });

test('the popup\'s −/+ never move a saved offset outside the bounds against the button pressed', async () => {
  // A compatibility delay above 2000 ms: the picture is pinned at 2000 ms, so only offsets of −250 and lower count.
  const p = await readyPopup({ status: streaming(2250) });
  assert.equal(p.view().delay, '2000');
  p.press('plus');
  assert.equal(p.view().offset, '+0 ms');
  p.flush(250); assert.deepEqual(p.writes, []);
  p.press('minus'); assert.equal(p.view().offset, '−250 ms'); // the first step that changes the picture
  p.press('minus'); assert.equal(p.view().offset, '−251 ms');
  assert.equal(p.view().delay, '1999');

  // −400 saved in another scene, now at 141 ms: − keeps it, + moves it up to where the delay changes again.
  const scene = await readyPopup({ status: streaming(141), stored: { 'example.test': { offsetMs: -400 } } });
  scene.press('minus');
  assert.equal(scene.view().offset, '−400 ms');
  scene.flush(250); assert.deepEqual(scene.writes, []);
  scene.press('plus'); assert.equal(scene.view().offset, '−141 ms');
  scene.flush(250); assert.equal(scene.data['example.test'].offsetMs, -141);
});
