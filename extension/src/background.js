/*
 * HomePodCast 视频同步 — 后台 service worker
 *
 * 只有这里访问 HomePodCast 的本地状态接口：该接口不发送 CORS 头，
 * 扩展后台凭 host_permissions 可以绕过 CORS，content script 不行。
 *
 * 消息：
 *   {type:'getStatus', report?:[stats…]}  → 状态 JSON（附加 running:true）或 {running:false}
 *                                           report 为该 frame 中各视频的 stats()，供弹窗显示
 *   {type:'getTabInfo', tabId}            → {videos:[stats…]}（最近 3 秒内各 frame 上报的汇总）
 */
'use strict';

const STATUS_URL = 'http://127.0.0.1:47100/v1/status';
const TIMEOUT_MS = 800;
const CACHE_MS = 500;
const REPORT_TTL_MS = 3000;

let cached = null;
let cachedAt = 0;
let inflight = null;

/** tabId -> Map(frameId -> {at, videos}) */
const reports = new Map();

async function fetchStatus() {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(STATUS_URL, { signal: ctrl.signal, cache: 'no-store', credentials: 'omit' });
    if (!res.ok) return { running: false };
    const json = await res.json();
    if (!json || typeof json !== 'object' || json.app !== 'HomePodCast') return { running: false };
    return { ...json, running: true };
  } catch (_) {
    return { running: false }; // 连接被拒绝 / 超时：HomePodCast 未运行
  } finally {
    clearTimeout(timer);
  }
}

function getStatus() {
  if (cached && Date.now() - cachedAt < CACHE_MS) return Promise.resolve(cached);
  if (!inflight) {
    inflight = fetchStatus().then((s) => {
      cached = s;
      cachedAt = Date.now();
      inflight = null;
      return s;
    });
  }
  return inflight;
}

function record(sender, videos) {
  const tabId = sender.tab && sender.tab.id;
  if (typeof tabId !== 'number') return;
  let frames = reports.get(tabId);
  if (!frames) reports.set(tabId, (frames = new Map()));
  frames.set(sender.frameId || 0, { at: Date.now(), videos: videos.slice(0, 32) });
}

function collect(tabId) {
  const frames = reports.get(tabId);
  const out = [];
  if (!frames) return out;
  const now = Date.now();
  for (const [frameId, r] of frames) {
    if (now - r.at > REPORT_TTL_MS) frames.delete(frameId);
    else out.push(...r.videos);
  }
  return out;
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || typeof msg !== 'object') return undefined;
  if (msg.type === 'getStatus') {
    if (sender.tab && Array.isArray(msg.report)) record(sender, msg.report);
    getStatus().then(sendResponse, () => sendResponse({ running: false }));
    return true; // 异步回复
  }
  if (msg.type === 'getTabInfo') {
    sendResponse({ videos: collect(msg.tabId) });
    return undefined;
  }
  return undefined;
});

chrome.tabs.onRemoved.addListener((tabId) => reports.delete(tabId));
