'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('popup explains ambiguous and native-local mixer routes without applying another destination delay', () => {
  const source = fs.readFileSync(path.join(__dirname, '../src/popup.js'), 'utf8');
  const render = source.slice(source.indexOf('function renderStatus()'), source.indexOf('function renderSite()'));
  for (const status of [
    { running: true, streaming: false, destinationRequired: true, message: '无法确定此程序的声音去处，同步暂不可用。' },
    { running: true, streaming: false, local: true, message: '此程序在本机播放，不使用音箱画面延迟。' },
  ]) {
    const el = { statusText: { textContent: '' }, statusCard: { dataset: {} } };
    const ctx = vm.createContext({ state: { status }, el, t: key => key });
    vm.runInContext(render + '\nrenderStatus();', ctx);
    assert.equal(el.statusText.textContent, status.message);
    assert.equal(el.statusCard.dataset.state, status.destinationRequired ? 'unknown' : 'idle');
    assert.equal(el.statusText.innerHTML, undefined);
  }
});
