/* Localized in-page sync controls. Fullscreen is requested directly from a click,
 * so the browser keeps transient user activation. The existing player DOM stays put. */
(() => {
  'use strict';
  if (globalThis.HPCSyncControls) return;
  const UI_ATTR = 'data-hpc-sync-controls';
  const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String)) || key;
  const fullscreenElement = () => document.fullscreenElement || document.webkitFullscreenElement;
  const signed = (n) => `${n >= 0 ? '+' : '−'}${Math.abs(n)}`;

  function editingTarget(event) {
    const path = typeof event.composedPath === 'function' ? event.composedPath() : [event.target];
    return path.some((n) => n && (n.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(n.tagName) ||
      (n.getAttribute && n.getAttribute('role') === 'textbox')));
  }

  function overrideStyles(element, values) {
    const saved = Object.entries(values).map(([key, value]) => {
      const old = element.style.getPropertyValue(key);
      const priority = element.style.getPropertyPriority(key);
      element.style.setProperty(key, value, 'important');
      return [key, old, priority];
    });
    return () => {
      for (const [key, value, priority] of saved) {
        if (value) element.style.setProperty(key, value, priority);
        else element.style.removeProperty(key);
      }
    };
  }

  class Manager {
    constructor(options) {
      this.options = options;
      this.players = new Map();
      this.session = null;
      this.selected = null;
      this.detached = false;
      this.onFullscreen = this.onFullscreen.bind(this);
      this.onKey = this.onKey.bind(this);
      this.update = this.update.bind(this);
      document.addEventListener('fullscreenchange', this.onFullscreen);
      document.addEventListener('webkitfullscreenchange', this.onFullscreen);
      document.addEventListener('keydown', this.onKey, true);
      globalThis.addEventListener('resize', this.update);
      globalThis.addEventListener('scroll', this.update, true);
    }

    add(video) {
      if (this.detached || this.players.has(video)) return;
      const host = document.createElement('div');
      host.setAttribute(UI_ATTR, '');
      host.setAttribute('data-hpc-delay', 'controls'); // Ignore our own DOM in the video scanner.
      const root = host.attachShadow({ mode: 'open' });
      const style = document.createElement('style');
      style.textContent = `
        :host { all: initial !important; position: fixed !important; z-index: 2147483647 !important;
          left: var(--hpc-left, 0px) !important; top: var(--hpc-top, 0px) !important;
          transform: translateX(-100%) !important; }
        :host([hidden]) { display: none !important; }
        * { box-sizing: border-box; }
        .bar { display: flex; align-items: center; gap: 4px; padding: 4px; border: 1px solid #ffffff40;
          border-radius: 8px; background: #151518e8; color: #fff;
          font: 12px/1.4 system-ui, 'Segoe UI', sans-serif; box-shadow: 0 2px 8px #0005; }
        button { all: unset; box-sizing: border-box; cursor: pointer; padding: 5px 7px; border-radius: 5px;
          color: #fff; font: inherit; white-space: nowrap; }
        button:hover { background: #ffffff28; }
        button:focus-visible { outline: 2px solid #71b7ff; outline-offset: -1px; }
        .adjustments { display: none; gap: 2px; }
        .bar.fullscreen .adjustments { display: flex; }
        .feedback { display: none; position: absolute; top: 100%; right: 0; margin-top: 5px; width: max-content;
          max-width: min(440px, 90vw); padding: 7px 10px; border-radius: 6px; background: #151518e8;
          color: #fff; font: 12px/1.4 system-ui, 'Segoe UI', sans-serif; }
        .feedback.visible { display: block; }
        @media (max-width: 480px) { .bar.fullscreen { flex-wrap: wrap; max-width: 90vw; justify-content: flex-end; } }
      `;
      const bar = document.createElement('div');
      bar.className = 'bar';
      const full = document.createElement('button');
      full.type = 'button';
      full.textContent = t('syncFullscreen');
      full.title = t('syncFullscreenTitle');
      full.addEventListener('click', (event) => {
        event.preventDefault();
        event.stopPropagation();
        this.toggleFullscreen(video);
      });
      const adjustments = document.createElement('div');
      adjustments.className = 'adjustments';
      for (const delta of [-20, -5, 5, 20]) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = `${signed(delta)} ms`;
        button.title = t('syncAdjustmentLabel', signed(delta));
        button.setAttribute('aria-label', button.title);
        button.addEventListener('click', (event) => {
          event.preventDefault();
          event.stopPropagation();
          this.adjust(video, delta);
        });
        adjustments.appendChild(button);
      }
      const feedback = document.createElement('div');
      feedback.className = 'feedback';
      feedback.setAttribute('role', 'status');
      feedback.setAttribute('aria-live', 'polite');
      bar.appendChild(full);
      bar.appendChild(adjustments);
      root.appendChild(style);
      root.appendChild(bar);
      root.appendChild(feedback);
      this.players.set(video, { host, bar, full, feedback, feedbackTimer: 0 });
      this.update();
    }

    remove(video) {
      const entry = this.players.get(video);
      if (!entry) return;
      if (this.session && this.session.video === video) {
        this.restoreSession();
        const fe = fullscreenElement();
        if (fe && fe.contains(video) && typeof document.exitFullscreen === 'function') {
          try { Promise.resolve(document.exitFullscreen()).catch(() => {}); } catch (_) { /* already exiting */ }
        }
      }
      clearTimeout(entry.feedbackTimer);
      entry.host.remove();
      this.players.delete(video);
      if (this.selected === video) this.selected = null;
    }

    activeVideo() {
      const fe = fullscreenElement();
      if (!fe || fe.tagName === 'VIDEO' || fe.tagName === 'BWP-VIDEO') return null;
      if (this.selected && this.players.has(this.selected) && fe.contains(this.selected)) return this.selected;
      const candidates = [...this.players.keys()].filter((v) => v.isConnected && fe.contains(v));
      candidates.sort((a, b) => Number(a.paused) - Number(b.paused) ||
        b.getBoundingClientRect().width * b.getBoundingClientRect().height -
        a.getBoundingClientRect().width * a.getBoundingClientRect().height);
      return candidates[0] || null;
    }

    update() {
      if (this.detached) return;
      const state = this.options.getState();
      const fe = fullscreenElement();
      const active = this.activeVideo();
      for (const [video, entry] of this.players) {
        const rect = video.getBoundingClientRect();
        const eligible = this.options.isEligible(video);
        const available = state.enabled && state.streaming && eligible;
        entry.host.hidden = !available || (!!fe && active !== video) || rect.bottom <= 0 || rect.right <= 0 ||
          rect.top >= globalThis.innerHeight || rect.left >= globalThis.innerWidth;
        const parent = fe && active === video ? fe : document.body || document.documentElement;
        if (entry.host.parentNode !== parent) parent.appendChild(entry.host);
        const right = Math.max(8, Math.min(rect.right - 8, globalThis.innerWidth - 8));
        entry.host.style.setProperty('--hpc-left', `${right}px`);
        entry.host.style.setProperty('--hpc-top', `${Math.max(8, rect.top + 8)}px`);
        entry.bar.className = active === video ? 'bar fullscreen' : 'bar';
        entry.full.textContent = t(active === video ? 'syncExitFullscreen' : 'syncFullscreen');
        entry.full.title = t(active === video ? 'syncShortcuts' : 'syncFullscreenTitle');
      }
    }

    show(video, message) {
      const entry = this.players.get(video);
      if (!entry) return;
      clearTimeout(entry.feedbackTimer);
      entry.feedback.textContent = message;
      entry.feedback.className = 'feedback visible';
      entry.feedbackTimer = setTimeout(() => { entry.feedback.className = 'feedback'; }, 4000);
    }

    toggleFullscreen(video) {
      if (this.detached || !this.players.has(video)) return;
      const fe = fullscreenElement();
      if (fe && fe.contains(video)) {
        const exit = document.exitFullscreen || document.webkitExitFullscreen;
        if (!exit) return;
        try { Promise.resolve(exit.call(document)).catch(() => this.show(video, t('syncFullscreenFailed'))); }
        catch (_) { this.show(video, t('syncFullscreenFailed')); }
        return;
      }
      const container = video.parentElement;
      const request = container && (container.requestFullscreen || container.webkitRequestFullscreen);
      if (!request || document.fullscreenEnabled === false || fe) {
        this.show(video, t('syncFullscreenFailed'));
        return;
      }
      this.restoreSession();
      const restoreContainer = overrideStyles(container, {
        display: 'block', position: 'relative', width: '100%', height: '100%', 'min-width': '0', 'min-height': '0',
        'max-width': 'none', 'max-height': 'none', margin: '0', padding: '0',
        'box-sizing': 'border-box', overflow: 'hidden', background: '#000', transform: 'none',
      });
      const restoreVideo = overrideStyles(video, {
        position: 'absolute', left: '0', top: '0', right: 'auto', bottom: 'auto', width: '100%', height: '100%',
        'min-width': '0', 'min-height': '0', 'max-width': 'none', 'max-height': 'none',
        margin: '0', padding: '0', border: '0', 'box-sizing': 'border-box', 'object-fit': 'contain', transform: 'none',
      });
      const session = { video, container, restore: () => { restoreVideo(); restoreContainer(); } };
      this.session = session;
      this.selected = video;
      // Do not await another API or send a background message before this call: activation is required.
      try {
        Promise.resolve(request.call(container)).then(() => {
          if (this.detached || this.session !== session) return;
          this.update();
          this.show(video, t('syncShortcuts'));
        }).catch(() => {
          if (this.session === session) this.restoreSession();
          this.update();
          this.show(video, t('syncFullscreenFailed'));
        });
      } catch (_) {
        this.restoreSession();
        this.update();
        this.show(video, t('syncFullscreenFailed'));
      }
    }

    restoreSession() {
      if (!this.session) return;
      this.session.restore();
      this.session = null;
    }

    onFullscreen() {
      if (this.session && fullscreenElement() !== this.session.container) this.restoreSession();
      this.update();
    }

    adjust(video, delta) {
      if (video !== this.activeVideo()) return;
      const state = this.options.getState();
      if (!state.enabled || !state.streaming || !this.options.isEligible(video)) return;
      this.options.adjustOffset(delta);
      const next = this.options.getState();
      this.show(video, t('syncDelayFeedback', next.delayMs, signed(next.offsetMs)));
    }

    onKey(event) {
      if (event.defaultPrevented || event.isComposing || editingTarget(event) || !event.altKey || event.ctrlKey || event.metaKey ||
          (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) return;
      const video = this.activeVideo();
      const state = this.options.getState();
      if (!video || !state.enabled || !state.streaming || !this.options.isEligible(video)) return;
      event.preventDefault();
      event.stopPropagation();
      this.adjust(video, (event.key === 'ArrowLeft' ? -1 : 1) * (event.shiftKey ? 20 : 5));
    }

    detach() {
      if (this.detached) return;
      for (const video of [...this.players.keys()]) this.remove(video);
      this.detached = true;
      this.restoreSession();
      document.removeEventListener('fullscreenchange', this.onFullscreen);
      document.removeEventListener('webkitfullscreenchange', this.onFullscreen);
      document.removeEventListener('keydown', this.onKey, true);
      globalThis.removeEventListener('resize', this.update);
      globalThis.removeEventListener('scroll', this.update, true);
    }
  }

  globalThis.HPCSyncControls = Object.freeze({ create: (options) => new Manager(options) });
})();
