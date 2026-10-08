/*
 * HomePodCast 视频同步 — 弹窗
 * 设置保存在 chrome.storage.sync：
 *   '__global__' → {enabled}
 *   <主机名>     → {enabled, offsetMs}
 */
'use strict';

const GLOBAL_KEY = '__global__';
const OFFSET_LIMIT = 500;
const MAX_DELAY_MS = 2000;
const SAVE_DEBOUNCE_MS = 250;

const $ = (id) => document.getElementById(id);
const el = {
  statusCard: $('statusCard'),
  statusText: $('statusText'),
  siteCard: $('siteCard'),
  host: $('host'),
  effDelay: $('effDelay'),
  minus: $('minus'),
  plus: $('plus'),
  offset: $('offset'),
  reset: $('reset'),
  hint: $('hint'),
  siteToggle: $('siteToggle'),
  globalToggle: $('globalToggle'),
  videos: $('videos'),
};

const state = {
  tabId: null,
  host: null,
  status: null,
  videos: null,
  offset: 0,
  siteEnabled: true,
  globalEnabled: true,
};

let saveTimer = 0;
let pendingSave = null;

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function send(msg) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(msg, (res) => {
        void chrome.runtime.lastError;
        resolve(res);
      });
    } catch (_) {
      resolve(undefined);
    }
  });
}

function askPage(tabId) {
  return new Promise((resolve) => {
    try {
      chrome.tabs.sendMessage(tabId, { type: 'hpc:pageInfo' }, { frameId: 0 }, (res) => {
        void chrome.runtime.lastError; // 页面没有 content script（浏览器内部页面或扩展安装前打开的页面）
        resolve(res);
      });
    } catch (_) {
      resolve(undefined);
    }
  });
}

// ---------- 保存 ----------

function queueSave(key, value) {
  pendingSave = Object.assign(pendingSave || {}, { [key]: value });
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, SAVE_DEBOUNCE_MS);
}

function flushSave() {
  clearTimeout(saveTimer);
  saveTimer = 0;
  const p = pendingSave;
  pendingSave = null;
  if (!p) return;
  const set = {};
  const remove = [];
  for (const [k, v] of Object.entries(p)) {
    // 与默认值相同就删除，避免占用 storage.sync 的条目配额
    const isDefault = k === GLOBAL_KEY ? v.enabled !== false : v.enabled !== false && !v.offsetMs;
    if (isDefault) remove.push(k);
    else set[k] = v;
  }
  if (remove.length) chrome.storage.sync.remove(remove);
  if (Object.keys(set).length) chrome.storage.sync.set(set);
}

function saveSite() {
  if (!state.host) return;
  queueSave(state.host, { enabled: state.siteEnabled, offsetMs: state.offset });
}

// ---------- 渲染 ----------

function effectiveDelay() {
  const s = state.status;
  if (!s || s.running === false || !s.streaming) return null;
  const base = Number(s.videoDelayMs);
  if (!Number.isFinite(base)) return null;
  return clamp(Math.round(base + state.offset), 0, MAX_DELAY_MS);
}

function renderStatus() {
  const s = state.status;
  let text;
  let st;
  if (!s) {
    text = '正在连接…';
    st = 'unknown';
  } else if (s.running === false) {
    text = '未运行';
    st = 'off';
  } else if (!s.streaming) {
    text = '未推流';
    st = 'idle';
  } else {
    const parts = ['推流中'];
    if (s.device) parts.push(String(s.device));
    if (Number.isFinite(Number(s.videoDelayMs))) parts.push(`${Math.round(Number(s.videoDelayMs))} ms`);
    text = parts.join(' · ');
    st = 'streaming';
  }
  el.statusText.textContent = text;
  el.statusCard.dataset.state = st;
}

function renderSite() {
  const available = !!state.host;
  el.siteCard.classList.toggle('unavailable', !available);
  el.host.textContent = available ? state.host : '—';
  el.host.title = available ? state.host : '';
  for (const b of [el.minus, el.plus, el.reset, el.siteToggle]) b.disabled = !available;
  el.siteToggle.checked = state.siteEnabled;
  el.globalToggle.checked = state.globalEnabled;

  const o = state.offset;
  el.offset.textContent = `${o >= 0 ? '+' : '−'}${Math.abs(o)} ms`;
  el.reset.disabled = !available || o === 0;

  let hint = '';
  const eff = effectiveDelay();
  if (!available) {
    el.effDelay.textContent = '—';
    hint = '此页面不支持，或页面在安装扩展之前打开，请刷新页面后再试。';
  } else if (!state.globalEnabled) {
    el.effDelay.textContent = '0';
    hint = '已全局关闭，画面不延迟。';
  } else if (!state.siteEnabled) {
    el.effDelay.textContent = '0';
    hint = '已在此网站关闭，画面不延迟。';
  } else if (eff === null) {
    el.effDelay.textContent = '—';
    hint = state.status && state.status.running !== false
      ? 'HomePodCast 未推流，画面不延迟。'
      : 'HomePodCast 未运行，画面不延迟。';
  } else {
    el.effDelay.textContent = String(eff);
  }
  el.hint.textContent = hint;
  el.hint.hidden = !hint;
}

function renderVideos() {
  const vids = state.videos;
  if (!state.host) {
    el.videos.textContent = '';
    return;
  }
  if (!vids) {
    el.videos.textContent = '—';
    return;
  }
  if (!vids.length) {
    el.videos.textContent = '本页未检测到视频（≥ 200×112）。';
    return;
  }
  const active = vids.filter((v) => v.mode !== 'off');
  const parts = [`本页 ${vids.length} 个视频`];
  if (active.length) {
    parts.push(`${active.length} 个正在延迟`);
    const modes = new Set(active.map((v) => (v.mode === 'videoframe' ? 'WebCodecs' : 'Canvas')));
    parts.push([...modes].join('/'));
    const lags = active.map((v) => Number(v.lastShownLagMs)).filter((n) => Number.isFinite(n) && n > 0);
    if (lags.length) parts.push(`实测 ${Math.round(lags.reduce((a, b) => a + b, 0) / lags.length)} ms`);
  } else if (vids.some((v) => v.reason === 'drm')) {
    parts.push('受版权保护（DRM），无法延迟');
  } else if (vids.some((v) => v.reason === 'video-fullscreen')) {
    parts.push('视频元素单独全屏时无法延迟');
  } else if (vids.some((v) => v.reason === 'source-unsupported')) {
    parts.push('此页面的播放器不支持画面延迟');
  } else {
    parts.push('未延迟');
  }
  el.videos.textContent = parts.join(' · ');
}

function render() {
  renderStatus();
  renderSite();
  renderVideos();
}

// ---------- 交互 ----------

function step(delta) {
  if (!state.host) return;
  const next = clamp(state.offset + delta, -OFFSET_LIMIT, OFFSET_LIMIT);
  if (next === state.offset) return;
  state.offset = next;
  saveSite();
  renderSite();
}

// 按住 −/+ 连续调整
function bindStepper(button, delta) {
  let delayTimer = 0;
  let repeatTimer = 0;
  const stop = () => {
    clearTimeout(delayTimer);
    clearInterval(repeatTimer);
    delayTimer = 0;
    repeatTimer = 0;
  };
  button.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || button.disabled) return;
    e.preventDefault();
    button.setPointerCapture(e.pointerId);
    step(delta);
    stop();
    delayTimer = setTimeout(() => {
      repeatTimer = setInterval(() => step(delta), 60);
    }, 400);
  });
  for (const t of ['pointerup', 'pointercancel', 'lostpointercapture']) button.addEventListener(t, stop);
  button.addEventListener('click', (e) => {
    if (e.detail === 0) step(delta); // 键盘（Enter/空格）触发
  });
}

bindStepper(el.minus, -1);
bindStepper(el.plus, +1);

el.reset.addEventListener('click', () => {
  state.offset = 0;
  saveSite();
  renderSite();
});

el.siteToggle.addEventListener('change', () => {
  state.siteEnabled = el.siteToggle.checked;
  saveSite();
  renderSite();
});

el.globalToggle.addEventListener('change', () => {
  state.globalEnabled = el.globalToggle.checked;
  queueSave(GLOBAL_KEY, { enabled: state.globalEnabled });
  renderSite();
});

// 弹窗关闭时把尚未写入的设置立即保存
window.addEventListener('pagehide', flushSave);
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') flushSave();
});

// ---------- 刷新 ----------

async function refresh() {
  const [status, info] = await Promise.all([
    send({ type: 'getStatus' }),
    state.tabId != null ? send({ type: 'getTabInfo', tabId: state.tabId }) : Promise.resolve(null),
  ]);
  state.status = status && typeof status === 'object' ? status : { running: false };
  if (info && Array.isArray(info.videos)) state.videos = info.videos;
  render();
}

async function init() {
  render();
  let tab = null;
  try {
    [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  } catch (_) { /* ignore */ }
  state.tabId = tab && typeof tab.id === 'number' ? tab.id : null;

  if (state.tabId != null) {
    const page = await askPage(state.tabId);
    if (page && typeof page.host === 'string') {
      state.host = page.host;
      if (Array.isArray(page.videos)) state.videos = page.videos;
    }
  }

  const keys = [GLOBAL_KEY];
  if (state.host) keys.push(state.host);
  const items = await chrome.storage.sync.get(keys);
  const g = items[GLOBAL_KEY] || {};
  const s = (state.host && items[state.host]) || {};
  state.globalEnabled = g.enabled !== false;
  state.siteEnabled = s.enabled !== false;
  state.offset = clamp(Math.round(Number(s.offsetMs) || 0), -OFFSET_LIMIT, OFFSET_LIMIT);

  render();
  await refresh();
  setInterval(refresh, 1000);
}

init();
