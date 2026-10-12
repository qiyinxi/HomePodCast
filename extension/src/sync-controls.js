/* Localized in-page sync controls.
 * They show only while the pointer moves over a video or rests on them, or Alt is pressed, and hide IDLE_MS after the
 * last activity; entering or leaving fullscreen never shows them by itself. Alt+←/→ works in fullscreen while they are
 * hidden. "Sync fullscreen" puts the site's player into fullscreen (playerRoot: the outermost element around the video
 * that has the video's box), so the site's own controls, captions and danmaku stay; it is requested directly from the
 * click, so the browser keeps transient user activation. The existing player DOM stays put. */
(() => {
  'use strict';
  if (globalThis.HPCSyncControls) return;
  const UI_ATTR = 'data-hpc-sync-controls';
  const IDLE_MS = 2500; // the controls hide this long after the last pointer activity or Alt
  const FEEDBACK_MS = 4000;
  const ROOT_TOLERANCE_PX = 4;
  const PASSIVE_CAPTURE = { capture: true, passive: true };
  const t = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String)) || key;
  const fullscreenElement = () => document.fullscreenElement || document.webkitFullscreenElement;
  const signed = (n) => `${n >= 0 ? '+' : '−'}${Math.abs(n)}`;
  const inside = (rect, p) => p.x >= rect.left && p.x < rect.right && p.y >= rect.top && p.y < rect.bottom;
  const near = (a, b) => Math.abs(a - b) <= ROOT_TOLERANCE_PX;

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

  // Box r frames the video's box v: the same box, or a letterbox / pillarbox around it (the same on one axis, the video
  // centred on the other).
  function frames(r, v) {
    if (r.left > v.left + ROOT_TOLERANCE_PX || r.top > v.top + ROOT_TOLERANCE_PX ||
        r.right < v.right - ROOT_TOLERANCE_PX || r.bottom < v.bottom - ROOT_TOLERANCE_PX) return false;
    const sameX = near(r.left, v.left) && near(r.right, v.right);
    const sameY = near(r.top, v.top) && near(r.bottom, v.bottom);
    return (sameX && sameY) || (sameX && near(v.top - r.top, r.bottom - v.bottom)) ||
      (sameY && near(v.left - r.left, r.right - v.right));
  }

  // The site's player around the video (YouTube's player, bilibili's video area): the outermost ancestor below <body>
  // that frames it. Wrappers without a box of their own are passed over; the first larger box (the page around the
  // player, or a player whose own bar sits below the picture) ends the search. Null: none frames it.
  function playerRoot(video) {
    const v = video.getBoundingClientRect();
    let root = null;
    for (let el = video.parentElement; el && el !== document.body && el !== document.documentElement; el = el.parentElement) {
      const r = el.getBoundingClientRect();
      if (r.width < 1 || r.height < 1) continue;
      if (!frames(r, v)) break;
      root = el;
    }
    return root;
  }

  class Manager {
    constructor(options) {
      this.options = options;
      this.players = new Map();
      this.session = null;
      this.selected = null;
      this.detached = false;
      this.pointer = null;
      this.frame = 0;
      this.updating = false;
      this.onFullscreen = this.onFullscreen.bind(this);
      this.onKey = this.onKey.bind(this);
      this.onPointer = this.onPointer.bind(this);
      this.onLayout = this.onLayout.bind(this);
      this.onFrame = this.onFrame.bind(this);
      this.update = this.update.bind(this);
      document.addEventListener('fullscreenchange', this.onFullscreen);
      document.addEventListener('webkitfullscreenchange', this.onFullscreen);
      document.addEventListener('keydown', this.onKey, true);
      document.addEventListener('pointermove', this.onPointer, PASSIVE_CAPTURE);
      document.addEventListener('pointerdown', this.onPointer, PASSIVE_CAPTURE);
      globalThis.addEventListener('resize', this.onLayout, { passive: true });
      globalThis.addEventListener('scroll', this.onLayout, PASSIVE_CAPTURE);
    }

    add(video) {
      if (this.detached || this.players.has(video)) return;
      const host = document.createElement('div');
      host.setAttribute(UI_ATTR, '');
      host.setAttribute('data-hpc-delay', 'controls'); // Ignore our own DOM in the video scanner.
      host.hidden = true; // until the pointer moves over the video
      const root = host.attachShadow({ mode: 'closed' }); // page scripts can neither read nor press the controls
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
        if (!event.isTrusted) return;
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
          if (!event.isTrusted) return;
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
      const entry = {
        host, bar, full, feedback, feedbackTimer: 0, hideTimer: 0,
        revealed: false, hovered: false, focused: false, feedbackShown: false,
        view: { hidden: true }, // what was last written to the DOM
      };
      // Resting on the controls or tabbing into them keeps them; the idle time starts again when the pointer or focus leaves.
      host.addEventListener('pointerenter', () => { entry.hovered = true; this.update(); });
      host.addEventListener('pointerleave', () => { entry.hovered = false; this.reveal(entry); this.update(); });
      host.addEventListener('focusin', () => { entry.focused = true; this.update(); });
      host.addEventListener('focusout', () => { entry.focused = false; this.reveal(entry); this.update(); });
      this.players.set(video, entry);
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
      clearTimeout(entry.hideTimer);
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

    // Shows the controls of this player for IDLE_MS more (update() decides whether it may show them at all).
    reveal(entry) {
      clearTimeout(entry.hideTimer);
      entry.revealed = true;
      entry.hideTimer = setTimeout(() => {
        entry.hideTimer = 0;
        entry.revealed = false;
        this.update();
      }, IDLE_MS);
    }

    onPointer(event) {
      if (!event.isTrusted || !this.players.size) return;
      this.pointer = { x: event.clientX, y: event.clientY };
      this.schedule();
    }

    // Scroll and resize: only shown controls have a position to keep up with.
    onLayout() {
      for (const entry of this.players.values()) {
        if (!entry.view.hidden) {
          this.schedule();
          return;
        }
      }
    }

    // At most one update per frame, however many pointer, scroll or resize events come in.
    schedule() {
      if (this.frame || this.detached) return;
      this.frame = requestAnimationFrame(this.onFrame);
    }

    onFrame() {
      this.frame = 0;
      this.update();
    }

    update() {
      if (this.detached) return;
      // Moving focused controls between the page and the fullscreen player makes Chrome fire focusout during the move;
      // an update from there would move them first and make this one's appendChild throw. It runs next frame instead.
      if (this.updating) {
        this.schedule();
        return;
      }
      this.updating = true;
      try {
        this.render();
      } finally {
        this.updating = false;
      }
    }

    render() {
      const pointer = this.pointer;
      this.pointer = null;
      // Every read first, then only the writes that change something: a write between two reads forces a layout.
      const state = this.options.getState();
      const fe = fullscreenElement();
      const active = this.activeVideo();
      const width = globalThis.innerWidth;
      const height = globalThis.innerHeight;
      const rows = [];
      for (const [video, entry] of this.players) {
        rows.push({ video, entry, rect: video.getBoundingClientRect(), eligible: this.options.isEligible(video) });
      }
      const page = document.body || document.documentElement;
      for (const { video, entry, rect, eligible } of rows) {
        const own = active === video;
        const usable = state.enabled && state.streaming && eligible && (!fe || own) &&
          rect.bottom > 0 && rect.right > 0 && rect.top < height && rect.left < width;
        if (usable && pointer && inside(rect, pointer)) this.reveal(entry);
        const shown = usable && (entry.revealed || entry.hovered || entry.focused || entry.feedbackShown);
        const view = entry.view;
        const parent = own ? fe : page;
        if (entry.host.parentNode !== parent) parent.appendChild(entry.host);
        if (view.hidden !== !shown) entry.host.hidden = view.hidden = !shown;
        if (!shown) continue; // the rest only matters once they show
        const left = `${Math.max(8, Math.min(rect.right - 8, width - 8))}px`;
        const top = `${Math.max(8, rect.top + 8)}px`;
        if (view.left !== left) entry.host.style.setProperty('--hpc-left', view.left = left);
        if (view.top !== top) entry.host.style.setProperty('--hpc-top', view.top = top);
        const barClass = own ? 'bar fullscreen' : 'bar';
        if (view.barClass !== barClass) entry.bar.className = view.barClass = barClass;
        const text = t(own ? 'syncExitFullscreen' : 'syncFullscreen');
        if (view.text !== text) entry.full.textContent = view.text = text;
        const title = t(own ? 'syncShortcuts' : 'syncFullscreenTitle');
        if (view.title !== title) entry.full.title = view.title = title;
      }
    }

    show(video, message) {
      const entry = this.players.get(video);
      if (!entry) return;
      clearTimeout(entry.feedbackTimer);
      entry.feedback.textContent = message;
      entry.feedback.className = 'feedback visible';
      entry.feedbackShown = true;
      entry.feedbackTimer = setTimeout(() => this.hideFeedback(entry), FEEDBACK_MS);
      this.update();
    }

    hideFeedback(entry) {
      clearTimeout(entry.feedbackTimer);
      entry.feedbackTimer = 0;
      if (!entry.feedbackShown) return;
      entry.feedbackShown = false;
      entry.feedback.className = 'feedback';
      this.update();
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
      // The site's player keeps its own controls, captions and danmaku; a video no player frames takes its parent along.
      const target = playerRoot(video) || video.parentElement;
      const request = target && (target.requestFullscreen || target.webkitRequestFullscreen);
      if (!request || document.fullscreenEnabled === false || fe) {
        this.show(video, t('syncFullscreenFailed'));
        return;
      }
      this.restoreSession();
      // The picture fills the screen even where the site doesn't resize its video for a fullscreen it didn't ask for. The
      // player's own layers (controls, captions, danmaku) come later in the player or have a z-index, so they stay on top.
      const restore = overrideStyles(video, {
        position: 'fixed', left: '0', top: '0', right: 'auto', bottom: 'auto', width: '100%', height: '100%',
        'min-width': '0', 'min-height': '0', 'max-width': 'none', 'max-height': 'none',
        margin: '0', padding: '0', border: '0', 'box-sizing': 'border-box', 'object-fit': 'contain', transform: 'none',
        background: '#000',
      });
      const session = { video, target, restore };
      this.session = session;
      this.selected = video;
      // Do not await another API or send a background message before this call: activation is required.
      try {
        Promise.resolve(request.call(target)).then(() => {
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
      const fe = fullscreenElement();
      if (this.session && fe !== this.session.target) this.restoreSession();
      // Entering or leaving fullscreen never shows the controls by itself: what was shown before goes (the sync
      // fullscreen's own hint excepted, whichever comes first, it or this event).
      this.pointer = null;
      for (const [video, entry] of this.players) {
        clearTimeout(entry.hideTimer);
        entry.hideTimer = 0;
        entry.revealed = false;
        entry.hovered = false;
        if (!(this.session && this.session.video === video)) this.hideFeedback(entry);
      }
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
      if (!event.isTrusted || event.defaultPrevented || event.isComposing || editingTarget(event)) return;
      if (event.key === 'Alt') {
        // Alt alone shows the controls, as moving the pointer over the video does (for those who don't).
        if (!event.repeat && !event.ctrlKey && !event.metaKey) {
          for (const entry of this.players.values()) this.reveal(entry);
          this.update();
        }
        return;
      }
      if (!event.altKey || event.ctrlKey || event.metaKey || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) return;
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
      if (this.frame) cancelAnimationFrame(this.frame);
      this.frame = 0;
      document.removeEventListener('fullscreenchange', this.onFullscreen);
      document.removeEventListener('webkitfullscreenchange', this.onFullscreen);
      document.removeEventListener('keydown', this.onKey, true);
      document.removeEventListener('pointermove', this.onPointer, true);
      document.removeEventListener('pointerdown', this.onPointer, true);
      globalThis.removeEventListener('resize', this.onLayout);
      globalThis.removeEventListener('scroll', this.onLayout, true);
    }
  }

  globalThis.HPCSyncControls = Object.freeze({ create: (options) => new Manager(options) });
})();
