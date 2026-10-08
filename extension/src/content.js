/*
 * HomePodCast 视频同步 — content script
 *
 * 在每个 frame 里找到可见的 <video> 和画布型播放器 <bwp-video>（≥ 200×112），用 HPCDelay 接管；
 * 每秒向后台询问一次 HomePodCast 状态，设置延迟 = 推流中 ? videoDelayMs + 网站偏移 : 0。
 */
(() => {
  'use strict';

  if (globalThis.__hpcContentLoaded) return;
  globalThis.__hpcContentLoaded = true;

  const Engine = globalThis.HPCDelay;
  if (!Engine || !Engine.supported) return;

  const MIN_W = 200;
  const MIN_H = 112;
  const TICK_MS = 1000;
  const SCAN_DEBOUNCE_MS = 250;
  const GLOBAL_KEY = '__global__';
  const OFFSET_LIMIT = 500;
  const MAX_DELAY_MS = 2000;
  const STATUS_FAILS_BEFORE_OFF = 3;
  const PLAYER_TAGS = ['video', 'bwp-video']; // bwp-video：bilibili 的 WASM 播放器，画面在内部 canvas 上

  const host = topHostname();
  const controllers = new Map(); // video -> controller
  let settings = { globalEnabled: true, siteEnabled: true, offsetMs: 0 };
  let status = null;
  let failures = 0;
  let dead = false;
  let timer = 0;
  let scanTimer = 0;
  let quickTick = 0;
  let observer = null;

  // 设置按“顶层页面”的主机名保存：嵌入在别的网站里的播放器 iframe 跟随所在网站的设置
  function topHostname() {
    try {
      const ao = location.ancestorOrigins;
      if (ao && ao.length) {
        const h = new URL(ao[ao.length - 1]).hostname;
        if (h) return h;
      }
    } catch (_) { /* 不透明来源等 */ }
    try {
      return window.top.location.hostname || '(本地文件)';
    } catch (_) { /* 跨域且无 ancestorOrigins */ }
    return location.hostname || '(本地文件)';
  }

  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

  function readSettings(items) {
    const g = (items && items[GLOBAL_KEY]) || {};
    const s = (items && items[host]) || {};
    settings = {
      globalEnabled: g.enabled !== false,
      siteEnabled: s.enabled !== false,
      offsetMs: clamp(Math.round(Number(s.offsetMs) || 0), -OFFSET_LIMIT, OFFSET_LIMIT),
    };
  }

  function loadSettings() {
    try {
      chrome.storage.sync.get([GLOBAL_KEY, host], (items) => {
        if (chrome.runtime.lastError || dead) return;
        readSettings(items);
        applyAll();
      });
    } catch (e) {
      handleContextError(e);
    }
  }

  function desiredDelay() {
    if (!status || status.running === false || !status.streaming) return 0;
    const base = Number(status.videoDelayMs);
    if (!Number.isFinite(base)) return 0;
    return clamp(Math.round(base + settings.offsetMs), 0, MAX_DELAY_MS);
  }

  function isEligible(v) {
    if (!v.isConnected) return false;
    const r = v.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H) return false;
    const cs = getComputedStyle(v);
    return cs.display !== 'none' && cs.visibility !== 'hidden';
  }

  function applyOne(v, c, eligible) {
    c.setEnabled(eligible && settings.globalEnabled && settings.siteEnabled);
    c.setDelay(desiredDelay());
  }

  function applyAll() {
    if (dead) return;
    for (const [v, c] of controllers) applyOne(v, c, isEligible(v));
  }

  function consider(v) {
    if (dead || controllers.has(v)) return;
    if (Engine.isAttachedElsewhere(v)) return; // 页面自己已经在用延迟引擎（测量工具）
    if (!isEligible(v)) return;
    const c = Engine.attach(v, { placement: 'overlay', delayMs: 0, enabled: false });
    controllers.set(v, c);
    applyOne(v, c, true);
    if (!status && !quickTick) quickTick = setTimeout(() => { quickTick = 0; tick(); }, 0);
  }

  function scan() {
    if (dead) return;
    for (const tag of PLAYER_TAGS) {
      const vids = document.getElementsByTagName(tag);
      for (let i = 0; i < vids.length; i++) consider(vids[i]);
    }
    for (const [v, c] of controllers) {
      if (!v.isConnected) {
        c.detach();
        controllers.delete(v);
      }
    }
  }

  function scheduleScan() {
    if (scanTimer || dead) return;
    scanTimer = setTimeout(() => {
      scanTimer = 0;
      scan();
    }, SCAN_DEBOUNCE_MS);
  }

  function summary() {
    const out = [];
    for (const c of controllers.values()) {
      const s = c.stats();
      out.push({
        mode: s.mode,
        delayMs: s.delayMs,
        lastShownLagMs: s.lastShownLagMs,
        droppedFrames: s.droppedFrames,
        reason: s.reason,
        fallback: s.fallback,
      });
    }
    return out;
  }

  function requestStatus() {
    return new Promise((resolve, reject) => {
      try {
        chrome.runtime.sendMessage({ type: 'getStatus', report: summary() }, (res) => {
          const err = chrome.runtime.lastError;
          if (err) reject(new Error(err.message));
          else resolve(res);
        });
      } catch (e) {
        reject(e);
      }
    });
  }

  async function tick() {
    if (dead) return;
    scan();
    if (!controllers.size) return; // 没有视频就不打扰后台
    try {
      const s = await requestStatus();
      failures = 0;
      status = s && typeof s === 'object' ? s : { running: false };
    } catch (e) {
      if (handleContextError(e)) return;
      if (++failures < STATUS_FAILS_BEFORE_OFF) return; // 后台偶尔重启：先保持现状
      status = { running: false };
    }
    applyAll();
  }

  // 扩展被重新加载/更新后，旧 content script 的 chrome.* 失效：清理并退出
  function handleContextError(e) {
    let alive = false;
    try {
      alive = !!(chrome.runtime && chrome.runtime.id);
    } catch (_) { /* ignore */ }
    if (alive && !/context invalidated/i.test(String(e && e.message))) return false;
    shutdown();
    return true;
  }

  function shutdown() {
    if (dead) return;
    dead = true;
    clearInterval(timer);
    clearTimeout(scanTimer);
    clearTimeout(quickTick);
    if (observer) observer.disconnect();
    document.removeEventListener('play', onPlayCapture, true);
    for (const c of controllers.values()) c.detach();
    controllers.clear();
  }

  function onPlayCapture(ev) {
    const t = ev.target;
    if (t && (t.tagName === 'VIDEO' || t.tagName === 'BWP-VIDEO')) consider(t);
  }

  // ---------- 启动 ----------

  loadSettings();

  try {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (dead || area !== 'sync') return;
      if (GLOBAL_KEY in changes || host in changes) loadSettings();
    });
    chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      if (dead || !msg || msg.type !== 'hpc:pageInfo' || window.top !== window) return undefined;
      sendResponse({ host, videos: summary() });
      return undefined;
    });
  } catch (e) {
    handleContextError(e);
  }

  observer = new MutationObserver((muts) => {
    for (const m of muts) {
      for (const n of m.addedNodes) {
        if (n.nodeType === 1 && n.getAttribute('data-hpc-delay') === null) {
          scheduleScan();
          return;
        }
      }
    }
  });
  observer.observe(document.documentElement || document, { childList: true, subtree: true });
  document.addEventListener('play', onPlayCapture, true); // 媒体事件不冒泡，用捕获阶段

  scan();
  timer = setInterval(tick, TICK_MS);
  tick();

  // 仅用于调试（隔离环境中，页面脚本看不到）
  globalThis.__hpcContentDebug = {
    host,
    get settings() { return { ...settings }; },
    get status() { return status; },
    desiredDelay,
    stats: () => [...controllers.values()].map((c) => c.stats()),
  };
})();
