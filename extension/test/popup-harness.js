'use strict';
// Runs src/popup.js in a VM with stand-ins for its elements and the chrome.* calls it makes. The page's answer and the
// app's status wait until the test gives them (answerPage, answerStatus), so every state the popup passes through on
// its way to the real one can be looked at. Not a *.test.js file: the tests require it.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const EXT = path.join(__dirname, '..');
const messages = JSON.parse(fs.readFileSync(path.join(EXT, '_locales/en/messages.json'), 'utf8'));
const IDS = ['statusCard', 'statusText', 'siteCard', 'host', 'effDelay', 'minus', 'plus', 'offset', 'reset', 'hint',
  'siteToggle', 'globalToggle', 'videos'];

// English text of a message, with its $NAME$ placeholders filled as chrome.i18n does.
function msg(key, ...subs) {
  const m = messages[key];
  if (!m) return '';
  return m.message.replace(/\$([A-Z]+)\$/g, (_, name) => String(subs[Number(m.placeholders[name.toLowerCase()].content.slice(1)) - 1]));
}

class Node {
  constructor(id) {
    this.id = id; this.textContent = ''; this.title = ''; this.hidden = false; this.disabled = false; this.checked = false;
    this.dataset = {}; this.classes = new Set(); this.listeners = new Map();
    this.classList = { toggle: (name, on) => (on ? this.classes.add(name) : this.classes.delete(name)) };
  }
  addEventListener(type, callback) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(callback);
  }
  dispatch(type, event = {}) {
    for (const callback of this.listeners.get(type) || []) callback({ type, target: this, preventDefault() {}, ...event });
  }
  setAttribute(key, value) { this[key] = value; }
  setPointerCapture() {}
}

/**
 * Opens the popup. stored: chrome.storage.sync's contents; getError / setError: storage.sync.get rejects, set and
 * remove fail (chrome.runtime.lastError in a callback, a rejected promise without one), as Chrome does.
 */
function openPopup({ stored = {}, getError = null, setError = null } = {}) {
  const nodes = Object.fromEntries(IDS.map((id) => [id, new Node(id)]));
  const pending = { page: [], status: [] };
  const timers = new Map(); let nextTimer = 1;
  const data = structuredClone(stored); const writes = [];
  const runtime = { id: 'test-extension', unchecked: 0 };
  let lastError; let lastErrorRead = true;
  Object.defineProperty(runtime, 'lastError', { get: () => { lastErrorRead = true; return lastError; } });
  // A callback runs with lastError set; Chrome logs "Unchecked runtime.lastError" when it doesn't read it.
  const callback = (fn, error) => {
    lastError = error; lastErrorRead = !error;
    try { fn(); } finally { if (!lastErrorRead) runtime.unchecked++; lastError = undefined; }
  };
  runtime.sendMessage = (message, cb) => {
    if (message.type === 'getStatus') pending.status.push(cb);
    else if (message.type === 'getTabInfo') cb({ videos: [] });
    else cb(undefined);
  };
  const write = (entry, cb) => {
    if (setError) {
      if (cb) { callback(cb, { message: setError }); return undefined; }
      return Promise.reject(new Error(setError));
    }
    if (entry.set) Object.assign(data, structuredClone(entry.set));
    else for (const key of [].concat(entry.remove)) delete data[key];
    writes.push(entry);
    if (cb) { callback(cb); return undefined; }
    return Promise.resolve();
  };
  const ctx = vm.createContext({
    document: {
      documentElement: { lang: '' },
      getElementById: (id) => nodes[id],
      querySelectorAll: () => [],
      addEventListener() {},
      visibilityState: 'visible',
    },
    addEventListener() {},
    setTimeout: (fn, delay) => { const id = nextTimer++; timers.set(id, { fn, delay }); return id; },
    clearTimeout: (id) => timers.delete(id),
    setInterval: (fn, delay) => { const id = nextTimer++; timers.set(id, { fn, delay, interval: true }); return id; },
    clearInterval: (id) => timers.delete(id),
    chrome: {
      runtime,
      i18n: { getMessage: (key, subs = []) => msg(key, ...subs) },
      tabs: {
        query: async () => [{ id: 7 }],
        sendMessage: (_tabId, _message, _options, cb) => pending.page.push(cb),
      },
      storage: { sync: {
        get: async (keys) => {
          if (getError) throw new Error(getError);
          return Object.fromEntries(keys.filter((key) => key in data).map((key) => [key, structuredClone(data[key])]));
        },
        set: (items, cb) => write({ set: structuredClone(items) }, cb),
        remove: (keys, cb) => write({ remove: keys }, cb),
      } },
    },
  });
  ctx.window = ctx;
  vm.runInContext(fs.readFileSync(path.join(EXT, 'src', 'popup.js'), 'utf8'), ctx, { filename: 'popup.js' });
  return {
    ctx, nodes, timers, data, writes, runtime, pending,
    /** The content script's answer to hpc:pageInfo (undefined: the page has none). */
    answerPage(res) { const cb = pending.page.shift(); if (!cb) throw new Error('nothing asked the page'); cb(res); },
    /** The background's answer to getStatus. */
    answerStatus(res) { const cb = pending.status.shift(); if (!cb) throw new Error('nothing asked for the status'); cb(res); },
    /** Runs the timers with this delay (the debounced save: 250, the refresh: 1000). */
    flush(delay) {
      for (const [id, timer] of [...timers]) {
        if (timer.delay === delay) { if (!timer.interval) timers.delete(id); timer.fn(); }
      }
    },
    view: () => ({
      status: nodes.statusText.textContent,
      state: nodes.statusCard.dataset.state,
      hint: nodes.hint.hidden ? null : nodes.hint.textContent,
      dimmed: nodes.siteCard.classes.has('unavailable'),
      delay: nodes.effDelay.textContent,
      offset: nodes.offset.textContent,
    }),
    // Keyboard presses of −/+ (a click with detail 0), as Enter or Space give.
    press: (id) => nodes[id].dispatch('click', { detail: 0 }),
  };
}

// Every promise continuation the popup has queued has run.
const settled = () => new Promise((resolve) => setImmediate(resolve));

/** Opens the popup on a page with the content script, answers the page and the app, and lets it settle. */
async function readyPopup({ status, page = { host: 'example.test', videos: [] }, ...options } = {}) {
  const p = openPopup(options);
  await settled();
  p.answerPage(page);
  await settled();
  p.answerStatus(status);
  await settled();
  return p;
}

module.exports = { openPopup, readyPopup, settled, msg };
