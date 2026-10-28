'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Runs src/background.js with a stand-in for fetch (answers queued by the test) and a clock the test moves.
function background() {
  let now = 0;
  const answers = [];
  let onMessage = null;
  const ctx = vm.createContext({
    Date: { now: () => now },
    setTimeout, clearTimeout, AbortController,
    fetch: async () => {
      const next = answers.shift();
      if (!next) throw new Error('no answer queued');
      if (next instanceof Error) throw next;
      return { ok: next.ok !== false, json: async () => next.body };
    },
    chrome: {
      runtime: { onMessage: { addListener: (fn) => { onMessage = fn; } } },
      tabs: { onRemoved: { addListener() {} } },
    },
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../src/background.js'), 'utf8'), ctx, { filename: 'background.js' });
  return {
    answer: (a) => answers.push(a),
    at: (ms) => { now = ms; },
    // As the message reaches the content script: a copy (and an object of this realm, for deepStrictEqual).
    status: () => new Promise((resolve) => onMessage({ type: 'getStatus' }, {}, (res) => resolve(structuredClone(res)))),
  };
}

const app = { app: 'HomePodCast', streaming: true, device: 'HomePod', videoDelayMs: 141 };
const timeout = () => new DOMException('The operation was aborted.', 'AbortError');
const refused = () => new TypeError('Failed to fetch');

test('a slow answer keeps the app\'s last status for 2.5 s; a refused connection turns it off at once', async () => {
  const b = background();
  b.answer({ body: app }); b.at(0);
  assert.equal((await b.status()).videoDelayMs, 141);
  b.answer(timeout()); b.at(600); // a stalled app: the picture stays delayed
  assert.deepEqual(await b.status(), { ...app, running: true });
  b.answer(timeout()); b.at(1800);
  assert.equal((await b.status()).streaming, true);
  b.answer(timeout()); b.at(2600); // still no answer 2.5 s after the last one
  assert.deepEqual(await b.status(), { running: false });

  b.answer({ body: app }); b.at(3200);
  assert.equal((await b.status()).running, true);
  b.answer(refused()); b.at(3800); // the app quit
  assert.deepEqual(await b.status(), { running: false });
  b.answer(timeout()); b.at(4400);
  assert.deepEqual(await b.status(), { running: false });

  b.answer({ body: app }); b.at(5000);
  assert.equal((await b.status()).running, true);
  b.answer({ ok: false }); b.at(5600); // something else on the port
  assert.deepEqual(await b.status(), { running: false });
  b.answer(timeout()); b.at(6200);
  assert.deepEqual(await b.status(), { running: false });
});

test('answers are cached for 500 ms', async () => {
  const b = background();
  b.answer({ body: app }); b.at(0);
  await b.status();
  b.at(400);
  assert.equal((await b.status()).running, true); // nothing queued: a fetch would have thrown
  b.answer({ body: { app: 'Other' } }); b.at(600);
  assert.deepEqual(await b.status(), { running: false });
});
