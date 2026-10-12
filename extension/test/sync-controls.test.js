'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const EXT = path.join(__dirname, '..');
const read = (file) => fs.readFileSync(path.join(EXT, 'src', file), 'utf8');
const messages = JSON.parse(fs.readFileSync(path.join(EXT, '_locales/en/messages.json'), 'utf8'));

class Events {
  constructor() { this.listeners = new Map(); }
  addEventListener(type, callback) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(callback);
  }
  removeEventListener(type, callback) { this.listeners.get(type)?.delete(callback); }
  dispatch(type, event = {}) {
    event.target = event.target || this;
    event.type = type;
    for (const callback of [...(this.listeners.get(type) || [])]) callback(event);
  }
}
class Style {
  constructor() { this.values = new Map(); }
  setProperty(key, value, priority = '') { this.values.set(key, [value, priority]); }
  getPropertyValue(key) { return this.values.get(key)?.[0] || ''; }
  getPropertyPriority(key) { return this.values.get(key)?.[1] || ''; }
  removeProperty(key) { this.values.delete(key); }
}
class Element extends Events {
  constructor(tag) {
    super(); this.tagName = tag.toUpperCase(); this.nodeType = 1; this.children = [];
    this.attributes = new Map(); this.style = new Style(); this.isConnected = true;
    this.rect = { left: 0, top: 0, width: 640, height: 360, right: 640, bottom: 360 };
    this.offsetWidth = 640; this.offsetHeight = 360; this.paused = false;
    this.readyState = 2; this.videoWidth = 640; this.videoHeight = 360;
  }
  appendChild(child) {
    child.remove(); this.children.push(child); child.parentNode = this; child.parentElement = this;
    return child;
  }
  insertBefore(child, sibling) {
    child.remove(); const index = this.children.indexOf(sibling);
    this.children.splice(index < 0 ? this.children.length : index, 0, child);
    child.parentNode = this; child.parentElement = this; return child;
  }
  removeChild(child) {
    const index = this.children.indexOf(child); if (index >= 0) this.children.splice(index, 1);
    child.parentNode = null; child.parentElement = null;
  }
  remove() { this.parentNode?.removeChild(this); }
  get previousSibling() { return this.parentNode?.children[this.parentNode.children.indexOf(this) - 1] || null; }
  get nextSibling() { return this.parentNode?.children[this.parentNode.children.indexOf(this) + 1] || null; }
  contains(node) { return node === this || this.children.some((child) => child.contains(node)); }
  getBoundingClientRect() { return this.rect; }
  setAttribute(key, value) { this.attributes.set(key, value); }
  getAttribute(key) { return this.attributes.has(key) ? this.attributes.get(key) : null; }
  removeAttribute(key) { this.attributes.delete(key); }
  attachShadow(init) { this.shadowInit = init; this.shadow = new Element('shadow-root'); return this.shadow; }
  getContext() { return { clearRect() {}, fillRect() {}, drawImage() {} }; }
}
class Video extends Element {
  requestVideoFrameCallback() { return 1; }
  cancelVideoFrameCallback() {}
}

function harness({ content = false, realEngine = false, stored = {}, status = { streaming: true, videoDelayMs: 141 } } = {}) {
  const events = new Events();
  const doc = new Events();
  doc.documentElement = new Element('html'); doc.body = new Element('body');
  doc.documentElement.appendChild(doc.body); doc.fullscreenEnabled = true;
  doc.createElement = (tag) => tag === 'video' ? new Video(tag) : new Element(tag);
  const walk = (node) => [node, ...node.children.flatMap(walk)];
  doc.getElementsByTagName = (tag) => walk(doc.documentElement).filter((e) => e.tagName === tag.toUpperCase());
  doc.exitFullscreen = () => { doc.fullscreenElement = null; doc.dispatch('fullscreenchange'); return Promise.resolve(); };
  const timers = new Map(); let nextTimer = 1;
  const frames = []; // requestAnimationFrame callbacks, run by frame()
  const data = structuredClone(stored); const writes = [];
  const ctx = vm.createContext({ document: doc, innerWidth: 1280, innerHeight: 720,
    addEventListener: events.addEventListener.bind(events), removeEventListener: events.removeEventListener.bind(events),
    setTimeout: (fn, delay) => { const id = nextTimer++; timers.set(id, { fn, delay }); return id; },
    clearTimeout: (id) => timers.delete(id), setInterval: (fn, delay) => { const id = nextTimer++; timers.set(id, { fn, delay, interval: true }); return id; },
    clearInterval: (id) => timers.delete(id),
    requestAnimationFrame: (fn) => { frames.push(fn); return frames.length; }, cancelAnimationFrame() {}, performance: { now: () => 0 },
    HTMLVideoElement: Video, HTMLCanvasElement: Element,
    ResizeObserver: class { observe() {} disconnect() {} }, MutationObserver: class { observe() {} disconnect() {} },
    getComputedStyle: (v) => ({
      display: 'block', visibility: 'visible', position: v.style.getPropertyValue('position') || 'static',
      width: `${v.rect.width}px`, height: `${v.rect.height}px`, boxSizing: 'content-box',
      objectFit: 'contain', objectPosition: '50% 50%', opacity: '1', zIndex: 'auto',
      borderLeftWidth: '0', borderRightWidth: '0', borderTopWidth: '0', borderBottomWidth: '0',
      paddingLeft: '0', paddingRight: '0', paddingTop: '0', paddingBottom: '0',
      filter: 'none', borderRadius: '0', clipPath: 'none', transform: 'none', transformOrigin: 'center',
    }),
    location: { hostname: 'example.test', ancestorOrigins: [] },
    chrome: { runtime: { id: 'test-extension', onMessage: { addListener() {} }, sendMessage: (_msg, callback) => callback(status) },
      i18n: { getMessage: (key, subs = []) => {
        const msg = messages[key]; if (!msg) return '';
        return msg.message.replace(/\$([A-Z]+)\$/g, (_, name) => subs[Number(msg.placeholders[name.toLowerCase()].content.slice(1)) - 1]);
      } }, storage: { sync: {
        get: (keys, callback) => callback(Object.fromEntries(keys.filter((key) => key in data).map((key) => [key, structuredClone(data[key])]))),
        set: (items, callback) => { Object.assign(data, structuredClone(items)); writes.push({ set: structuredClone(items) }); callback?.(); },
        remove: (key, callback) => { delete data[key]; writes.push({ remove: key }); callback?.(); },
      }, onChanged: { addListener() {} } } },
  });
  ctx.window = ctx; ctx.top = ctx;
  let activation = false; const requests = [];
  const fullscreenable = (element) => {
    element.requestFullscreen = () => {
      requests.push({ container: element, activation });
      doc.fullscreenElement = element; doc.dispatch('fullscreenchange'); return Promise.resolve();
    };
    return element;
  };
  const addVideo = () => {
    const container = fullscreenable(new Element('div')); const video = new Video('video');
    doc.body.appendChild(container); container.appendChild(video); return { container, video };
  };
  const first = addVideo();
  if (realEngine) vm.runInContext(read('delay-engine.js'), ctx);
  else ctx.HPCDelay = { supported: true, isAttachedElsewhere: () => false, attach: () => {
    let enabled, delayMs;
    return { setEnabled: (value) => { enabled = value; }, setDelay: (value) => { delayMs = value; }, detach() {},
      stats: () => ({ mode: enabled && delayMs ? 'canvas' : 'off', delayMs }) };
  } };
  vm.runInContext(read('sync-controls.js'), ctx);
  if (content) vm.runInContext(read('content.js'), ctx);
  const click = (element, isTrusted = true) => {
    activation = true;
    element.dispatch('click', { isTrusted, preventDefault() {}, stopPropagation() {} });
    activation = false;
  };
  const key = (options = {}) => {
    const event = { key: 'ArrowRight', altKey: true, isTrusted: true, target: first.video, ...options, prevented: false, stopped: false,
      preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } };
    doc.dispatch('keydown', event);
    return event;
  };
  const pointer = (x, y, options = {}) => doc.dispatch('pointermove', { clientX: x, clientY: y, isTrusted: true, ...options });
  const frame = () => { for (const fn of frames.splice(0)) fn(0); };
  const flush = (delay) => {
    for (const [id, timer] of [...timers]) {
      if (timer.delay === delay) { if (!timer.interval) timers.delete(id); timer.fn(); }
    }
  };
  const fullscreen = (element) => { doc.fullscreenElement = element; doc.dispatch('fullscreenchange'); };
  return { ctx, doc, ...first, events, timers, frames, data, writes, requests, click, key, pointer, frame, flush, fullscreen,
    fullscreenable, addVideo };
}
const box = (element, left, top, width, height) => {
  element.rect = { left, top, width, height, right: left + width, bottom: top + height };
  return element;
};
const settled = () => new Promise((resolve) => setImmediate(resolve));
function manager(h, state = { enabled: true, streaming: true, delayMs: 141, offsetMs: 0 }) {
  const deltas = [];
  const ui = h.ctx.HPCSyncControls.create({ getState: () => state, isEligible: (v) => v.isConnected,
    adjustOffset: (delta) => { deltas.push(delta); state.offsetMs += delta; state.delayMs += delta; } });
  ui.add(h.video); return { ui, state, deltas, entry: ui.players.get(h.video) };
}

test('a direct in-page click requests the player\'s fullscreen with activation and restores exact inline styles on exit', async () => {
  const h = harness({ realEngine: true }); const { ui, entry } = manager(h);
  h.video.style.setProperty('width', '640px', 'important'); h.container.style.setProperty('height', '360px');
  const engine = h.ctx.HPCDelay.attach(h.video, { delayMs: 141, forceCanvas: true });
  assert.equal(engine.stats().mode, 'canvas');
  h.click(entry.full); await settled();
  assert.equal(h.requests.length, 1); assert.equal(h.requests[0].activation, true);
  assert.equal(h.doc.fullscreenElement, h.container); assert.equal(h.video.parentNode, h.container);
  assert.equal(engine.stats().mode, 'canvas'); assert.equal(entry.host.parentNode, h.container);
  assert.equal(entry.full.textContent, 'Exit fullscreen');
  assert.equal(h.video.style.getPropertyValue('width'), '100%');
  h.click(entry.full); await settled();
  assert.equal(h.doc.fullscreenElement, null);
  assert.equal(h.video.style.getPropertyValue('width'), '640px');
  assert.equal(h.video.style.getPropertyPriority('width'), 'important');
  assert.equal(h.video.style.getPropertyValue('position'), '');
  assert.equal(h.container.style.getPropertyValue('height'), '360px');
  assert.equal(entry.host.parentNode, h.doc.body); assert.equal(engine.stats().mode, 'canvas');
  ui.detach(); engine.detach();
});

test('native video fullscreen stays blocked by the engine; exiting and using sync fullscreen resumes delay', async () => {
  const h = harness({ realEngine: true }); const { ui, entry, deltas } = manager(h);
  const engine = h.ctx.HPCDelay.attach(h.video, { delayMs: 141, forceCanvas: true });
  h.fullscreen(h.video);
  assert.equal(engine.stats().reason, 'video-fullscreen'); assert.equal(entry.host.hidden, true);
  assert.equal(h.key().prevented, false); assert.deepEqual(deltas, []);
  h.fullscreen(null); h.click(entry.full); await settled();
  assert.equal(engine.stats().mode, 'canvas'); assert.equal(entry.host.hidden, false);
  ui.detach(); engine.detach();
});

test('fullscreen failure restores styles and displays localized feedback', async () => {
  const h = harness(); const { ui, entry } = manager(h);
  h.container.requestFullscreen = () => Promise.reject(new Error('iframe fullscreen policy'));
  h.click(entry.full); await settled();
  assert.equal(ui.session, null); assert.equal(h.video.style.getPropertyValue('width'), '');
  assert.match(entry.feedback.textContent, /Fullscreen is unavailable/);
  assert.equal(entry.feedback.className, 'feedback visible');
  h.doc.fullscreenEnabled = false; h.click(entry.full);
  assert.equal(h.requests.length, 0); ui.detach();
});

test('fullscreen shortcuts and buttons adjust by 5 or 20 ms and show the effective delay', () => {
  const h = harness(); const { ui, entry, deltas } = manager(h);
  assert.equal(h.key().prevented, false);
  h.fullscreen(h.container);
  assert.equal(h.key().prevented, true); assert.equal(h.key({ key: 'ArrowLeft', shiftKey: true }).prevented, true);
  const buttons = entry.bar.children[1].children;
  h.click(buttons[0]); h.click(buttons[3]);
  assert.deepEqual(deltas, [5, -20, -20, 20]);
  assert.equal(entry.feedback.textContent, 'Picture delay 126 ms · Site offset −15 ms');
  h.flush(4000); assert.equal(entry.feedback.className, 'feedback'); ui.detach();
});

test('shortcuts leave text editors, IME, other modifiers, normal arrows, and disabled sync untouched', () => {
  const h = harness(); const { ui, state, deltas } = manager(h); h.fullscreen(h.container);
  const input = new Element('input'); const editable = new Element('div'); editable.isContentEditable = true;
  const textbox = new Element('div'); textbox.setAttribute('role', 'textbox');
  const skipped = [
    { target: input }, { target: new Element('textarea') }, { target: new Element('select') },
    { target: editable }, { target: textbox }, { composedPath: () => [input, new Element('custom-editor')] },
    { isComposing: true }, { ctrlKey: true }, { metaKey: true }, { altKey: false },
    { key: 'ArrowUp' }, { defaultPrevented: true },
  ];
  for (const options of skipped) assert.equal(h.key(options).prevented, false, JSON.stringify(options));
  state.enabled = false; assert.equal(h.key().prevented, false);
  state.enabled = true; state.streaming = false; assert.equal(h.key().prevented, false);
  assert.deepEqual(deltas, []); ui.detach();
});

test('multiple players get one fullscreen shortcut owner and removal cleans listeners and temporary styles', () => {
  const h = harness(); const second = h.addVideo(); const { ui, deltas, entry } = manager(h); ui.add(second.video);
  const secondEntry = ui.players.get(second.video);
  h.fullscreen(second.container); h.key();
  assert.deepEqual(deltas, [5]); assert.equal(entry.host.hidden, true); assert.equal(secondEntry.host.hidden, false);
  assert.equal(secondEntry.feedback.textContent, 'Picture delay 146 ms · Site offset +5 ms');
  h.fullscreen(null); h.click(entry.full); ui.remove(h.video);
  assert.equal(h.doc.fullscreenElement, null); assert.equal(h.video.style.getPropertyValue('width'), '');
  assert.equal(entry.host.parentNode, null);
  ui.detach(); assert.equal(h.doc.listeners.get('keydown').size, 0);
});

test('content controls clamp offsets, apply to every controller, debounce persistence, and survive reload', async () => {
  const h = harness({ content: true, stored: { 'example.test': { enabled: true, offsetMs: 498 } } }); await settled();
  const second = h.addVideo(); h.flush(1000); await settled();
  h.fullscreen(h.container); h.key();
  assert.equal(h.ctx.__hpcContentDebug.settings.offsetMs, 500);
  assert.deepEqual(Array.from(h.ctx.__hpcContentDebug.stats(), (s) => s.delayMs), [641, 641]);
  h.key({ shiftKey: true }); h.flush(250);
  assert.equal(h.writes.length, 1); assert.equal(h.data['example.test'].offsetMs, 500);
  h.key({ key: 'ArrowLeft', shiftKey: true }); h.events.dispatch('pagehide');
  assert.equal(h.data['example.test'].offsetMs, 480);
  const reloaded = harness({ content: true, stored: h.data }); await settled();
  assert.equal(reloaded.ctx.__hpcContentDebug.settings.offsetMs, 480);
  assert.equal(second.video.isConnected, true);
});

test('site offset persistence removes defaults, stops where the delay stops changing and respects disabled site settings', async () => {
  const h = harness({ content: true, stored: { 'example.test': { offsetMs: 5 } } }); await settled();
  h.fullscreen(h.container); h.key({ key: 'ArrowLeft' }); h.flush(250);
  assert.equal(h.data['example.test'], undefined); assert.equal(h.writes[0].remove, 'example.test');
  for (let i = 0; i < 30; i++) h.key({ key: 'ArrowLeft', shiftKey: true });
  assert.equal(h.ctx.__hpcContentDebug.settings.offsetMs, -141); // the app's 141 ms: any lower changes nothing
  assert.equal(h.ctx.__hpcContentDebug.desiredDelay(), 0);
  h.flush(250); assert.equal(h.data['example.test'].offsetMs, -141);
  h.key(); assert.equal(h.ctx.__hpcContentDebug.desiredDelay(), 5); // so the first step back counts at once
  const disabled = harness({ content: true, stored: { 'example.test': { enabled: false, offsetMs: 10 } } }); await settled();
  disabled.fullscreen(disabled.container); assert.equal(disabled.key().prevented, false);
  disabled.flush(250); assert.equal(disabled.writes.length, 0);
});

test('a large desktop delay plus site adjustment remains limited to 2000 ms, and so does the offset', async () => {
  const h = harness({ content: true, status: { streaming: true, videoDelayMs: 1999 } }); await settled();
  h.fullscreen(h.container); h.key({ shiftKey: true });
  assert.equal(h.ctx.__hpcContentDebug.settings.offsetMs, 1);
  assert.equal(h.ctx.__hpcContentDebug.desiredDelay(), 2000);
});

test('the controls stay hidden until the pointer moves over the video and hide again 2.5 s after the last move', () => {
  const h = harness(); const { ui, entry } = manager(h);
  assert.equal(entry.host.hidden, true);
  h.pointer(900, 100); h.frame(); assert.equal(entry.host.hidden, true); // beside the video
  h.pointer(320, 180); assert.equal(entry.host.hidden, true); // handled with the next frame
  h.frame(); assert.equal(entry.host.hidden, false);
  assert.equal(entry.full.textContent, 'Sync fullscreen'); assert.equal(entry.bar.className, 'bar');
  h.flush(2500); assert.equal(entry.host.hidden, true);
  h.pointer(320, 180, { isTrusted: false }); assert.equal(h.frames.length, 0); // a page's synthetic event does nothing
  ui.detach();
});

test('focus leaving the controls while they move out of the fullscreen player (Chrome fires focusout during the move) throws nothing', async () => {
  const h = harness(); const { ui, entry } = manager(h);
  h.click(entry.full); await settled(); // the controls now live in the fullscreen player
  assert.equal(entry.host.parentNode, h.container);
  entry.host.dispatch('focusin'); // the clicked button keeps the focus
  const remove = entry.host.remove.bind(entry.host);
  let focused = true;
  entry.host.remove = function () { // like Chrome: removing the focused element first fires focusout, synchronously
    const from = this.parentNode;
    if (focused) { focused = false; this.dispatch('focusout'); }
    if (this.parentNode !== from) throw new Error('NotFoundError: The node to be removed is no longer a child of this node.');
    remove();
  };
  h.fullscreen(null); // leaving fullscreen moves them back to the page
  assert.equal(entry.host.parentNode, h.doc.body);
  assert.equal(h.doc.body.children.filter((c) => c === entry.host).length, 1);
  h.frame(); assert.equal(entry.host.hidden, false); // focus just left them: the idle time starts again
  h.flush(2500); assert.equal(entry.host.hidden, true);
  ui.detach();
});

test('resting on the controls or focusing them keeps them; leaving starts the idle time again', () => {
  const h = harness(); const { ui, entry } = manager(h);
  h.pointer(320, 180); h.frame(); entry.host.dispatch('pointerenter');
  h.flush(2500); assert.equal(entry.host.hidden, false);
  entry.host.dispatch('pointerleave'); assert.equal(entry.host.hidden, false);
  h.flush(2500); assert.equal(entry.host.hidden, true);
  h.key({ key: 'Alt', altKey: true }); entry.host.dispatch('focusin');
  h.flush(2500); assert.equal(entry.host.hidden, false);
  entry.host.dispatch('focusout'); h.flush(2500); assert.equal(entry.host.hidden, true);
  ui.detach();
});

test('site-native fullscreen never shows the controls by itself; the shortcuts work while they are hidden', () => {
  const h = harness(); const { ui, entry, deltas } = manager(h);
  h.pointer(320, 180); h.frame(); assert.equal(entry.host.hidden, false);
  h.fullscreen(h.container); // the site's own fullscreen button
  assert.equal(entry.host.hidden, true);
  assert.equal(h.key().prevented, true); assert.deepEqual(deltas, [5]);
  assert.equal(entry.host.hidden, false); // the result is shown, briefly
  assert.equal(entry.feedback.textContent, 'Picture delay 146 ms · Site offset +5 ms');
  h.flush(4000); assert.equal(entry.host.hidden, true);
  h.key({ key: 'Alt', altKey: true }); assert.equal(entry.host.hidden, false);
  assert.equal(entry.bar.className, 'bar fullscreen'); assert.equal(entry.full.textContent, 'Exit fullscreen');
  h.flush(2500); assert.equal(entry.host.hidden, true);
  h.pointer(10, 10); h.frame(); assert.equal(entry.host.hidden, false);
  h.fullscreen(null); assert.equal(entry.host.hidden, true); // leaving it doesn't show them either
  ui.detach();
});

test('Alt shows the controls only where sync applies and never from a text field', () => {
  const h = harness(); const { ui, state, entry } = manager(h);
  h.key({ key: 'Alt', altKey: true, target: new Element('input') }); assert.equal(entry.host.hidden, true);
  state.streaming = false; h.key({ key: 'Alt', altKey: true }); h.pointer(320, 180); h.frame();
  assert.equal(entry.host.hidden, true);
  state.streaming = true; h.key({ key: 'Alt', altKey: true }); assert.equal(entry.host.hidden, false);
  h.video.rect = { left: 0, top: 800, width: 640, height: 360, right: 640, bottom: 1160 }; // scrolled out of view
  h.key({ key: 'Alt', altKey: true }); assert.equal(entry.host.hidden, true);
  ui.detach();
});

test('untrusted clicks and keys do nothing, and the controls live in a closed shadow root', () => {
  const h = harness(); const { ui, entry, deltas } = manager(h);
  assert.equal(entry.host.shadowInit.mode, 'closed');
  h.click(entry.full, false); assert.equal(h.requests.length, 0);
  h.fullscreen(h.container);
  assert.equal(h.key({ isTrusted: false }).prevented, false);
  h.click(entry.bar.children[1].children[3], false);
  assert.deepEqual(deltas, []);
  ui.detach();
});

test('scrolling moves shown controls once per frame and writes only what changed; hidden ones cost nothing', () => {
  const h = harness(); const { ui, entry } = manager(h);
  h.events.dispatch('scroll'); assert.equal(h.frames.length, 0); // hidden: nothing to keep in place
  h.pointer(320, 180); h.frame();
  let writes = 0; const set = entry.host.style.setProperty.bind(entry.host.style);
  entry.host.style.setProperty = (...args) => { writes++; set(...args); };
  for (let i = 0; i < 10; i++) h.events.dispatch('scroll');
  h.events.dispatch('resize');
  assert.equal(h.frames.length, 1);
  h.frame(); assert.equal(writes, 0); // nothing moved
  h.video.rect = { left: 0, top: 50, width: 640, height: 360, right: 640, bottom: 410 };
  h.events.dispatch('scroll'); h.frame();
  assert.equal(writes, 1); assert.equal(entry.host.style.getPropertyValue('--hpc-top'), '58px');
  ui.detach(); h.events.dispatch('scroll'); assert.equal(h.frames.length, 0);
});

test('sync fullscreen goes to the player around the video, past wrappers without a box, up to the page around it', async () => {
  const h = harness(); const { ui } = manager(h);
  const page = box(h.fullscreenable(new Element('main')), 0, 0, 1000, 900); // the player plus the title below it
  const player = box(h.fullscreenable(new Element('div')), 0, 0, 800, 360); // pillarboxed: controls, captions, danmaku
  const wrapper = box(h.fullscreenable(new Element('div')), 0, 0, 0, 0); // only positions the video
  const area = box(h.fullscreenable(new Element('div')), 80, 0, 640, 360);
  const video = box(new Video('video'), 80, 0, 640, 360);
  h.doc.body.appendChild(page); page.appendChild(player); player.appendChild(wrapper); wrapper.appendChild(area); area.appendChild(video);
  ui.add(video); const entry = ui.players.get(video);
  h.click(entry.full); await settled();
  assert.equal(h.requests.at(-1).container, player); assert.equal(h.requests.at(-1).activation, true);
  assert.equal(video.style.getPropertyValue('position'), 'fixed'); // fills the screen whatever the site does
  assert.equal(entry.host.parentNode, player);
  h.click(entry.full); await settled();
  assert.equal(h.doc.fullscreenElement, null); assert.equal(video.style.getPropertyValue('position'), '');

  // A video no box frames (a bare one in an article): its parent goes along.
  const article = box(h.fullscreenable(new Element('article')), 0, 400, 640, 900);
  const bare = box(new Video('video'), 0, 400, 640, 360);
  h.doc.body.appendChild(article); article.appendChild(bare); ui.add(bare);
  h.click(ui.players.get(bare).full); await settled();
  assert.equal(h.requests.at(-1).container, article);
  ui.detach();
});
