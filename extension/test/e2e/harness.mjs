// Starts everything the browser tests need: the test video, the fake app API, the page server, and Chromium with the
// unpacked extension. Nothing here talks to anything but loopback.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import puppeteer from 'puppeteer';
import { startFakeApp } from './fake-app.mjs';
import { CODE, framesBehind, installReader } from './frame-code.mjs';
import { makeVideo } from './make-video.mjs';
import { startSiteServer } from './site-server.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const EXT_SRC = path.resolve(HERE, '..', '..');
export const APP_PORT = 47100; // what the extension asks (src/background.js, manifest host_permissions)

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Polls fn until it returns something truthy; throws with the last value after timeout ms. */
export async function waitFor(fn, { timeout = 5000, interval = 100, what = 'condition' } = {}) {
  const until = Date.now() + timeout;
  let last;
  for (;;) {
    last = await fn();
    if (last) return last;
    if (Date.now() > until) throw new Error(`timed out after ${timeout} ms waiting for ${what} (last: ${JSON.stringify(last)})`);
    await sleep(interval);
  }
}

// The extension as shipped (manifest.json, src, _locales, icons), copied to a temporary folder. When the fake API
// cannot have port 47100 (HomePodCast itself runs on this computer), the copy asks the fake API's port instead.
function prepareExtension(port) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hpc-e2e-ext-'));
  for (const item of ['manifest.json', 'src', '_locales', 'icons']) {
    fs.cpSync(path.join(EXT_SRC, item), path.join(dir, item), { recursive: true });
  }
  if (port !== APP_PORT) {
    for (const file of ['manifest.json', path.join('src', 'background.js')]) {
      const p = path.join(dir, file);
      const text = fs.readFileSync(p, 'utf8');
      const patched = text.replaceAll(`127.0.0.1:${APP_PORT}`, `127.0.0.1:${port}`);
      if (patched === text) throw new Error(`${file} no longer names 127.0.0.1:${APP_PORT}: update the e2e harness`);
      fs.writeFileSync(p, patched);
    }
  }
  return dir;
}

async function startApp() {
  const wanted = process.env.HPC_E2E_API_PORT ? Number(process.env.HPC_E2E_API_PORT) : APP_PORT;
  try {
    return await startFakeApp({ port: wanted });
  } catch (e) {
    if (e.code !== 'EADDRINUSE' || process.env.HPC_E2E_API_PORT) throw e;
    // HomePodCast owns 47100 here: run against a copy of the extension that asks another port.
    const app = await startFakeApp({ port: 0 });
    console.log(`# port ${APP_PORT} is in use (HomePodCast running?): fake API on ${app.port}, extension copy patched`);
    return app;
  }
}

export async function startHarness() {
  const videoFile = makeVideo();
  const app = await startApp();
  const site = await startSiteServer({ videoFile });
  const extDir = prepareExtension(app.port);
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'hpc-e2e-profile-'));
  const headless = process.env.HPC_E2E_HEADED ? false : true;
  const lang = process.env.HPC_E2E_LANG || 'zh-CN';
  const browser = await puppeteer.launch({
    headless,
    enableExtensions: [extDir],
    userDataDir,
    defaultViewport: null,
    env: { ...process.env, LANGUAGE: lang.replace('-', '_'), LANG: `${lang.replace('-', '_')}.UTF-8` },
    args: [
      `--lang=${lang}`,
      '--window-size=1280,800',
      '--screen-info={1280x720}', // headless: a 16:9 screen for fullscreen (else 800×600)
      '--autoplay-policy=no-user-gesture-required',
      // Loopback only: every other host name fails to resolve.
      '--host-resolver-rules=MAP *.localhost 127.0.0.1, MAP * ~NOTFOUND, EXCLUDE 127.0.0.1, EXCLUDE localhost',
      '--disable-background-networking',
      '--disable-component-update',
      '--no-first-run',
      '--no-default-browser-check',
    ],
  });
  const ext = await waitFor(async () => {
    for (const e of (await browser.extensions()).values()) if (path.resolve(e.path) === path.resolve(extDir)) return e;
    return null;
  }, { what: 'the extension to load' });

  const h = {
    app, site, browser, ext, extDir, userDataDir, CODE,
    pageErrors: [], // uncaught errors in the pages, the extension's content scripts included

    /** Opens a test page in a new tab and waits until the extension has taken over its video. */
    async open(siteName, pathname, { waitOverlay = true } = {}) {
      const page = await browser.newPage();
      page.on('pageerror', (e) => {
        console.log(`# page error (${siteName}): ${e.message}`);
        h.pageErrors.push(`${siteName}: ${e.message}`);
      });
      await page.goto(site.url(siteName, pathname), { waitUntil: 'load' });
      await page.evaluate(installReader, CODE);
      await waitFor(() => page.evaluate(() => {
        const v = document.querySelector('video');
        return v && v.readyState >= 2 && !v.paused && v.currentTime > 0.3;
      }), { timeout: 15000, what: `the video on ${siteName} to play` });
      if (waitOverlay) await h.waitOverlay(page);
      return page;
    },

    /** The extension's content-script world in the page's main frame (chrome.*, chrome.dom, __hpcContentDebug). */
    async realm(page) {
      return waitFor(async () => {
        for (const r of page.extensionRealms()) {
          if (r.origin === `chrome-extension://${ext.id}`) return r;
        }
        return null;
      }, { what: 'the content script realm' });
    },

    async debug(page) {
      const r = await h.realm(page);
      return r.evaluate(() => {
        const d = globalThis.__hpcContentDebug;
        return d && { host: d.host, settings: d.settings, status: d.status, delay: d.desiredDelay(), stats: d.stats() };
      });
    },

    async storage(page) {
      const r = await h.realm(page);
      return r.evaluate(() => chrome.storage.sync.get(null));
    },

    async clearStorage(page) {
      const r = await h.realm(page);
      await r.evaluate(() => chrome.storage.sync.clear());
    },

    /** The in-page controls (in their closed shadow root): visibility, texts and button boxes. */
    async controls(page) {
      const r = await h.realm(page);
      return r.evaluate(() => {
        const hosts = [...document.querySelectorAll('[data-hpc-sync-controls]')];
        if (!hosts.length) return { count: 0 };
        const host = hosts[0];
        const root = chrome.dom.openOrClosedShadowRoot(host);
        const box = (el) => {
          const b = el.getBoundingClientRect();
          return { x: b.left + b.width / 2, y: b.top + b.height / 2, width: b.width, height: b.height };
        };
        const fe = document.fullscreenElement;
        const buttons = [...root.querySelectorAll('button')].map((b) => ({ text: b.textContent, title: b.title, ...box(b) }));
        const feedback = root.querySelector('.feedback');
        return {
          count: hosts.length,
          shown: !host.hidden && host.getBoundingClientRect().width > 0,
          inFullscreenElement: !!fe && fe.contains(host),
          parentTag: host.parentElement && host.parentElement.tagName,
          buttons: buttons.filter((b) => b.width > 0),
          feedback: feedback.classList.contains('visible') ? feedback.textContent : null,
        };
      });
    },

    async waitOverlay(page, timeout = 8000) {
      await waitFor(() => page.evaluate(() => {
        const v = document.querySelector('video');
        const c = window.__e2e.overlayOf(v);
        return !!c && c.width > 0 && getComputedStyle(c).display !== 'none' && window.__e2e.readOverlay(v) !== null;
      }), { timeout, what: 'the delayed canvas' });
    },

    /** Frame lag of the delayed canvas behind the video, in ms (median of n samples taken in animation frames). */
    async lagMs(page, n = 15) {
      const lags = [];
      const seen = [];
      for (let i = 0; i < n * 3 && lags.length < n; i++) {
        const s = await page.evaluate(() => window.__e2e.sample(document.querySelector('video')));
        if (s.video !== null && s.overlay !== null && !s.paused) {
          lags.push((framesBehind(s.video, s.overlay) * 1000) / CODE.FPS);
          seen.push(s.overlay);
        }
        await sleep(60);
      }
      if (lags.length < n / 2) throw new Error(`could not read the frame numbers (${lags.length} samples)`);
      lags.sort((a, b) => a - b);
      return { median: lags[lags.length >> 1], min: lags[0], max: lags[lags.length - 1], overlayFrames: seen };
    },

    /** Screenshot of the viewport, decoded: the frame number shown where the video is, and colours at points. */
    async screen(page, { points = [] } = {}) {
      const data = `data:image/png;base64,${await page.screenshot({ encoding: 'base64' })}`;
      return page.evaluate(async (dataUrl, pts) => {
        const v = document.querySelector('video');
        const r = v.getBoundingClientRect();
        const rect = { left: r.left, top: r.top, width: r.width, height: r.height };
        return {
          frame: await window.__e2e.readScreenshot(dataUrl, rect, v.videoWidth, v.videoHeight),
          video: window.__e2e.readVideo(v),
          colors: await window.__e2e.screenshotPixel(dataUrl, pts),
        };
      }, data, points);
    },

    async close() {
      await browser.close().catch(() => {});
      await Promise.all([app.close(), site.close()]);
      for (const d of [extDir, userDataDir]) fs.rmSync(d, { recursive: true, force: true });
    },
  };
  return h;
}
