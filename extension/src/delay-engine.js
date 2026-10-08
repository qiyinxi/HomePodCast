/*
 * HomePodCast 视频同步 — 画面延迟引擎
 *
 * 可作为扩展 content script 使用，也可在普通网页里用 <script> 直接加载（测量工具使用）。
 *
 *   const ctrl = HPCDelay.attach(video, { placement: 'overlay' | 'beside', delayMs: 141 });
 *   ctrl.setDelay(ms); ctrl.setEnabled(bool); ctrl.detach(); ctrl.stats();
 *
 * 原理：用 requestVideoFrameCallback 抓取每一帧（记录 metadata.expectedDisplayTime），
 * 存进环形缓冲；requestAnimationFrame 循环里把“捕获时间 ≤ 呈现时间 − 延迟”的最新一帧
 * 画到覆盖在 <video> 上方的 canvas 上。优先用 WebCodecs VideoFrame（零拷贝，但同时最多
 * 持有 videoFrameBudget 个，见 DEFAULT_VIDEOFRAME_BUDGET 的说明），其余帧及
 * VideoFrame 不可用（如跨域视频的 SecurityError）时用 canvas 池（drawImage 拷贝，最大 1920 像素宽）。
 *
 * 可选参数（opts）：
 *   placement          'overlay'（默认）| 'beside'（调试：延迟画面放在视频右侧）
 *   delayMs            初始延迟，默认 0
 *   enabled            初始开关，默认 true
 *   forceCanvas        true 时跳过 WebCodecs，直接用 canvas 池
 *   videoFrameBudget   最多同时持有的 VideoFrame 数，默认 3（设为 40 即纯 WebCodecs）
 *   stallFallback      默认 true：已缓冲却出现 waiting（疑似解码器饿死）时改用纯 canvas 模式
 *   activeAtZero       true 时延迟为 0 也保持运行（用于测量 canvas 管线本身的延迟）
 *   presentAheadMs     canvas 从 rAF 绘制到上屏的时间，默认 DEFAULT_PRESENT_AHEAD_MS（至少一个刷新周期）
 *   presentAheadFrames 改用刷新周期数表示上面这个时间（给了就优先于 presentAheadMs）
 *
 * 画布型播放器（bilibili 的 <bwp-video> 等：画面画在元素内部的 <canvas> 上，没有 <video>）：
 *   HPCDelay.attach(bwpVideo, opts) 同样可用，见 CanvasSourceController。额外参数：
 *   sourceCanvas       直接指定画面所在的 canvas（默认自动查找，包括封闭的 shadow root）
 *   sourceShowMs       捕获时间戳到原画面上屏的时间，默认 DEFAULT_SOURCE_SHOW_MS
 *
 * 注意：同一个 <video> 重复 attach 会返回已有的控制器（忽略新的 opts）；先 detach() 再 attach。
 */
(() => {
  'use strict';

  const root = globalThis;
  if (root.HPCDelay && typeof root.HPCDelay.attach === 'function') return;

  const VERSION = '0.2.0';
  const FRAME_MS = 16.7;
  const MAX_FRAMES = 40;
  // 从 <video> 创建的 VideoFrame 直接引用解码器的输出缓冲（零拷贝）。实测 Chrome 154 + D3D11
  // 硬件解码（RTX 4070 SUPER，720p60，延迟 141 ms）：H.264 同时持有约 8 帧、AV1 约 6 帧时
  // 解码器就会饿死——掉帧并触发 waiting（连声音一起卡顿）；持有 4 帧时三种编码都正常。
  // 因此最多同时持有这么多 VideoFrame（留出余量取 3），超出的帧改用 canvas 拷贝；
  // 两种条目可以混在同一个缓冲里。
  const DEFAULT_VIDEOFRAME_BUDGET = 3;
  // rAF 里画到 canvas 的帧要过多久才真正上屏（相对 <video> 自己的帧按 expectedDisplayTime 上屏）。
  // 实测 Chrome + 240 Hz 屏，目标 141 ms、窗口录制按换帧时刻计时：提前 1 个刷新周期（4.2 ms）
  // 时实际 154.2 ms，2 个 150.0，4 个 141.7——每个周期正好 4.2 ms，即上屏约需 17 ms，
  // 和刷新率无关地接近一个 60 Hz 帧。60 Hz 屏上这仍是 1 个周期（原先的假设）。
  const DEFAULT_PRESENT_AHEAD_MS = 16.7;
  // 画布型播放器：captureStream 帧的捕获时间（换算到 performance.now）之后多久，这一帧出现在原画面上。
  // 实测 Chrome 154 + 240 Hz 屏，worker 里画 OffscreenCanvas 的模拟 <bwp-video>（tools/avsync
  // testpage.html?mode=bwp）：取 16.7 时目标 141 ms 实际 141.4（2D）/ 140.0（WebGL），目标 250 实际 249.6。
  const DEFAULT_SOURCE_SHOW_MS = 16.7;
  const SOURCE_TAGS = new Set(['BWP-VIDEO']); // 已知的画布型播放器元素
  const MAX_DELAY_MS = 5000;
  const CANVAS_MODE_MAX_W = 1920;
  const MAX_BACKING_PX = 4096;
  const CHECK_MS = 250;
  const FLUSH_EVENTS = ['seeking', 'seeked', 'pause', 'emptied', 'loadeddata'];
  const OWNER_ATTR = 'data-hpc-attached';
  const CANVAS_ATTR = 'data-hpc-delay';
  const ENGINE_TOKEN = Math.random().toString(36).slice(2, 10);
  const LIVE = Object.freeze({ live: true }); // 哨兵：画面显示的是视频当前（未延迟）帧

  const HAS_RVFC = typeof HTMLVideoElement !== 'undefined' &&
    typeof HTMLVideoElement.prototype.requestVideoFrameCallback === 'function';
  const HAS_VIDEOFRAME = typeof root.VideoFrame === 'function';
  const HAS_CANVAS_CAPTURE = typeof HTMLCanvasElement !== 'undefined' &&
    typeof HTMLCanvasElement.prototype.captureStream === 'function' &&
    typeof root.MediaStreamTrackProcessor === 'function';

  const registry = new WeakMap(); // video -> Controller
  const ctxCache = new WeakMap(); // 池中 canvas -> 2d context

  const num = (v) => {
    const n = parseFloat(v);
    return Number.isFinite(n) ? n : 0;
  };
  const round1 = (v) => Math.round(v * 10) / 10;
  const capacityFor = (delayMs, frameMs = FRAME_MS) => Math.min(MAX_FRAMES, Math.ceil(delayMs / frameMs) + 4);
  const clampDelay = (ms) => {
    const n = Number(ms);
    if (!Number.isFinite(n) || n < 0) return 0;
    return Math.min(MAX_DELAY_MS, n);
  };

  function makeCanvas(w, h) {
    if (typeof OffscreenCanvas === 'function') return new OffscreenCanvas(w, h);
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    return c;
  }

  function scaleOf(node) {
    const el = node && node.nodeType === 11 ? node.host : node; // ShadowRoot -> host
    if (!el || typeof el.getBoundingClientRect !== 'function') return 1;
    const w = el.offsetWidth;
    if (!w) return 1;
    const s = el.getBoundingClientRect().width / w;
    return Number.isFinite(s) && s > 0.01 ? s : 1;
  }

  function posComponent(token, free, scale) {
    if (!token || token === 'center') return free / 2;
    if (token === 'left' || token === 'top') return 0;
    if (token === 'right' || token === 'bottom') return free;
    if (token.endsWith('%')) return (free * num(token)) / 100;
    if (token.endsWith('px')) return num(token) * scale;
    return free / 2; // calc() 等少见写法：居中
  }

  // object-fit / object-position 换算到 canvas 像素坐标，向外取整避免露出底下的原视频边缘。
  function fitRect(iw, ih, cw, ch, fit, position, scale) {
    let w;
    let h;
    if (fit === 'fill') {
      w = cw;
      h = ch;
    } else if (fit === 'cover') {
      const s = Math.max(cw / iw, ch / ih);
      w = iw * s;
      h = ih * s;
    } else if (fit === 'none') {
      w = iw * scale;
      h = ih * scale;
    } else if (fit === 'scale-down') {
      const s = Math.min(cw / iw, ch / ih, scale);
      w = iw * s;
      h = ih * s;
    } else {
      const s = Math.min(cw / iw, ch / ih); // contain（<video> 的默认值）
      w = iw * s;
      h = ih * s;
    }
    const parts = String(position || '50% 50%').trim().split(/\s+/);
    const x = posComponent(parts[0], cw - w, scale);
    const y = posComponent(parts[1], ch - h, scale);
    const x0 = Math.floor(x);
    const y0 = Math.floor(y);
    return { x: x0, y: y0, w: Math.ceil(x + w) - x0, h: Math.ceil(y + h) - y0 };
  }

  function originMinusInset(origin, dx, dy) {
    const p = String(origin || '').split(/\s+/);
    if (p.length < 2 || !p[0].endsWith('px') || !p[1].endsWith('px')) return origin || '';
    return `${num(p[0]) - dx}px ${num(p[1]) - dy}px${p[2] ? ' ' + p[2] : ''}`;
  }

  class Controller {
    constructor(video, opts) {
      this.video = video;
      this.placement = opts.placement === 'beside' ? 'beside' : 'overlay';
      this.delay = clampDelay(opts.delayMs);
      this.enabled = opts.enabled !== false;
      this.activeAtZero = !!opts.activeAtZero;
      this.presentAheadFrames = Number.isFinite(opts.presentAheadFrames) ? opts.presentAheadFrames : null;
      this.presentAheadMs = Number.isFinite(opts.presentAheadMs) ? opts.presentAheadMs : DEFAULT_PRESENT_AHEAD_MS;
      this.captureMode = HAS_VIDEOFRAME && !opts.forceCanvas ? 'videoframe' : 'canvas';
      this.stallFallback = opts.stallFallback !== false;
      this.vfBudget = Number.isFinite(opts.videoFrameBudget)
        ? Math.max(0, Math.floor(opts.videoFrameBudget))
        : DEFAULT_VIDEOFRAME_BUDGET;
      this.vfHeld = 0; // 当前持有（未 close）的 VideoFrame 数量
      this.copies = 0; // canvas 拷贝次数
      this.waits = 0;
      this.fallbackReason = HAS_VIDEOFRAME || opts.forceCanvas ? null : 'no-webcodecs';
      this.drm = !!video.mediaKeys;
      this.fsBlocked = false;
      this.active = false;
      this.detached = false;
      this.reason = '';

      this.canvas = null;
      this.ctx = null;
      this.buf = []; // 待显示的帧（按捕获时间递增）
      this.cur = null; // 当前显示的帧：条目 | LIVE | null
      this.pool = []; // canvas 模式复用的 canvas

      this.captured = 0;
      this.shown = 0;
      this.dropped = 0;
      this.missed = 0;
      this.lastLag = 0;
      this.lastPresented = 0;

      this.vsync = 16.7;
      this.dts = [];
      this.lastRafT = 0;
      this.rvfcId = 0;
      this.rafId = 0;
      this.lastCheck = 0;
      this.needPlace = true;
      this.posL = 0;
      this.posT = 0;
      this.cssW = 0;
      this.fit = 'contain';
      this.objPos = '50% 50%';
      this.styles = Object.create(null);

      this.onFrame = this.onFrame.bind(this);
      this.onRaf = this.onRaf.bind(this);
      this.onMediaEvent = this.onMediaEvent.bind(this);
      this.onPlay = this.onPlay.bind(this);
      this.onEncrypted = this.onEncrypted.bind(this);
      this.onWaiting = this.onWaiting.bind(this);
      this.onFullscreen = this.onFullscreen.bind(this);
      this.onVisibility = this.onVisibility.bind(this);
      this.onLayoutChange = this.onLayoutChange.bind(this);

      for (const t of FLUSH_EVENTS) video.addEventListener(t, this.onMediaEvent);
      video.addEventListener('play', this.onPlay);
      video.addEventListener('playing', this.onPlay);
      video.addEventListener('encrypted', this.onEncrypted);
      video.addEventListener('waiting', this.onWaiting);
      video.addEventListener('resize', this.onLayoutChange); // 视频分辨率变化
      document.addEventListener('fullscreenchange', this.onFullscreen);
      document.addEventListener('webkitfullscreenchange', this.onFullscreen);
      document.addEventListener('visibilitychange', this.onVisibility);
      root.addEventListener('resize', this.onLayoutChange);
      this.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(this.onLayoutChange) : null;
      if (this.ro) this.ro.observe(video);

      try {
        video.setAttribute(OWNER_ATTR, ENGINE_TOKEN);
      } catch (_) { /* ignore */ }

      this.api = Object.freeze({
        setDelay: (ms) => this.setDelay(ms),
        setEnabled: (on) => this.setEnabled(on),
        detach: () => this.detach(),
        stats: () => this.stats(),
      });
    }

    // 构造之后由 attach() 调用（子类的字段那时才已初始化）
    start() {
      this.fsBlocked = this.isVideoFullscreen();
      this.update();
    }

    // ---------- 公开接口 ----------

    setDelay(ms) {
      const d = clampDelay(ms);
      if (d === this.delay) return;
      this.delay = d;
      const cap = this.capacity();
      while (this.buf.length > cap) {
        this.release(this.buf.shift());
        this.dropped++;
      }
      this.update();
    }

    setEnabled(on) {
      const v = !!on;
      if (v === this.enabled) return;
      this.enabled = v;
      this.update();
    }

    detach() {
      if (this.detached) return;
      this.detached = true;
      this.update();
      const v = this.video;
      for (const t of FLUSH_EVENTS) v.removeEventListener(t, this.onMediaEvent);
      v.removeEventListener('play', this.onPlay);
      v.removeEventListener('playing', this.onPlay);
      v.removeEventListener('encrypted', this.onEncrypted);
      v.removeEventListener('waiting', this.onWaiting);
      v.removeEventListener('resize', this.onLayoutChange);
      document.removeEventListener('fullscreenchange', this.onFullscreen);
      document.removeEventListener('webkitfullscreenchange', this.onFullscreen);
      document.removeEventListener('visibilitychange', this.onVisibility);
      root.removeEventListener('resize', this.onLayoutChange);
      if (this.ro) this.ro.disconnect();
      this.pool.length = 0;
      this.canvas = null;
      this.ctx = null;
      try {
        if (v.getAttribute(OWNER_ATTR) === ENGINE_TOKEN) v.removeAttribute(OWNER_ATTR);
      } catch (_) { /* ignore */ }
      if (registry.get(v) === this) registry.delete(v);
    }

    stats() {
      return {
        mode: this.active ? this.captureMode : 'off',
        delayMs: this.delay,
        bufferedFrames: this.buf.length,
        droppedFrames: this.dropped,
        lastShownLagMs: round1(this.lastLag),
        // 以下为附加诊断字段
        placement: this.placement,
        enabled: this.enabled,
        reason: this.reason || null,
        fallback: this.fallbackReason,
        capacity: this.capacity(),
        capturedFrames: this.captured,
        shownFrames: this.shown,
        missedFrames: this.missed,
        heldVideoFrames: this.vfHeld,
        canvasCopies: this.copies,
        waitingEvents: this.waits,
        vsyncMs: round1(this.vsync),
        presentAheadMs: round1(this.aheadMs()),
      };
    }

    // ---------- 状态切换 ----------

    update() {
      let reason = '';
      const unsupported = this.detached ? '' : this.unsupportedReason();
      if (this.detached) reason = 'detached';
      else if (unsupported) reason = unsupported;
      else if (this.drm) reason = 'drm';
      else if (!this.enabled) reason = 'disabled';
      else if (this.delay <= 0 && !this.activeAtZero) reason = 'zero-delay';
      else if (this.fsBlocked) reason = 'video-fullscreen';
      this.reason = reason;
      if (!reason && !this.active) this.activate();
      else if (reason && this.active) this.deactivate();
    }

    activate() {
      this.active = true;
      if (!this.canvas) this.createCanvas();
      this.styles = Object.create(null);
      this.posL = 0;
      this.posT = 0;
      this.setStyle('left', '0px');
      this.setStyle('top', '0px');
      this.lastPresented = 0;
      this.place();
      if (!this.active) return;
      this.drawLive();
      this.startCapture();
      this.kick();
    }

    deactivate() {
      this.active = false;
      this.stopCapture();
      if (this.rafId) {
        cancelAnimationFrame(this.rafId);
        this.rafId = 0;
      }
      this.flush();
      this.releaseCur();
      this.pool.length = 0;
      this.lastLag = 0;
      const c = this.canvas;
      if (c) {
        if (c.parentNode) c.parentNode.removeChild(c);
        c.width = 0; // 释放显存
        c.height = 0;
      }
    }

    kick() {
      if (this.active && !this.rafId) {
        this.lastRafT = 0;
        this.rafId = requestAnimationFrame(this.onRaf);
      }
    }

    // ---------- 画面来源（CanvasSourceController 覆盖这些） ----------

    unsupportedReason() {
      return HAS_RVFC ? '' : 'unsupported';
    }

    startCapture() {
      this.rvfcId = this.video.requestVideoFrameCallback(this.onFrame);
    }

    stopCapture() {
      if (this.rvfcId) {
        try {
          this.video.cancelVideoFrameCallback(this.rvfcId);
        } catch (_) { /* ignore */ }
        this.rvfcId = 0;
      }
    }

    isPaused() {
      return this.video.paused;
    }

    capacity() {
      return capacityFor(this.delay);
    }

    fitStyle(cs) {
      return cs; // object-fit / object-position 从哪里读
    }

    // ---------- 帧捕获 ----------

    onFrame(now, meta) {
      this.rvfcId = 0;
      if (!this.active) return;
      const v = this.video;
      this.rvfcId = v.requestVideoFrameCallback(this.onFrame);
      if (v.mediaKeys) {
        this.drm = true;
        this.update();
        return;
      }
      const pf = meta.presentedFrames;
      if (this.lastPresented && pf > this.lastPresented + 1) this.missed += pf - this.lastPresented - 1;
      this.lastPresented = pf;
      if (v.paused || v.seeking) {
        this.drawLive(); // 暂停/拖动时直接显示当前帧
        return;
      }
      this.capture(meta, now);
      this.kick();
    }

    capture(meta, now) {
      const v = this.video;
      const iw = v.videoWidth;
      const ih = v.videoHeight;
      if (!iw || !ih) return;
      const t = Number.isFinite(meta.expectedDisplayTime) ? meta.expectedDisplayTime : now;
      let e = null;
      if (this.captureMode === 'videoframe' && this.vfHeld < this.vfBudget) {
        try {
          const f = new VideoFrame(v, { timestamp: Math.round((meta.mediaTime || 0) * 1e6) });
          this.vfHeld++;
          e = { time: t, frame: f, src: f, iw, ih };
        } catch (err) {
          if (err && err.name === 'InvalidStateError') return; // 暂时没有可用帧
          this.switchToCanvas((err && err.name) || 'error');
        }
      }
      if (!e) e = this.captureCanvas(t, iw, ih);
      if (e) this.store(e);
    }

    store(e) {
      const t = e.time;
      this.captured++;
      const b = this.buf;
      while (b.length && b[b.length - 1].time >= t) this.release(b.pop()); // 时间倒退：丢弃旧时间线
      b.push(e);
      const cap = this.capacity();
      while (b.length > cap) {
        this.release(b.shift());
        this.dropped++;
      }
    }

    captureCanvas(t, iw, ih, src = this.video) {
      let w = iw;
      let h = ih;
      if (w > CANVAS_MODE_MAX_W) {
        h = Math.max(1, Math.round((h * CANVAS_MODE_MAX_W) / w));
        w = CANVAS_MODE_MAX_W;
      }
      const c = this.pool.pop() || makeCanvas(w, h);
      if (c.width !== w) c.width = w;
      if (c.height !== h) c.height = h;
      let cx = ctxCache.get(c);
      if (!cx) {
        cx = c.getContext('2d', { alpha: false });
        ctxCache.set(c, cx);
      }
      try {
        cx.drawImage(src, 0, 0, w, h);
      } catch (_) {
        this.pool.push(c);
        return null;
      }
      this.copies++;
      return { time: t, canvas: c, src: c, iw, ih };
    }

    switchToCanvas(why) {
      if (this.captureMode === 'canvas') return;
      this.captureMode = 'canvas';
      this.fallbackReason = why;
      this.flush(); // 释放已持有的 VideoFrame
      try {
        console.info('[HomePodCast] 画面延迟改用 canvas 模式：', why);
      } catch (_) { /* ignore */ }
    }

    release(e) {
      if (!e || e === LIVE) return;
      if (e.frame) {
        try {
          e.frame.close();
        } catch (_) { /* ignore */ }
        this.vfHeld = Math.max(0, this.vfHeld - 1);
        e.frame = null;
        e.src = null;
      } else if (e.canvas) {
        if (this.active && this.pool.length < MAX_FRAMES + 2) this.pool.push(e.canvas);
        e.canvas = null;
        e.src = null;
      }
    }

    releaseCur() {
      if (this.cur && this.cur !== LIVE) this.release(this.cur);
      this.cur = null;
    }

    flush() {
      for (const e of this.buf) this.release(e);
      this.buf.length = 0;
      this.lastPresented = 0;
    }

    // ---------- 显示 ----------

    onRaf(t) {
      this.rafId = 0;
      if (!this.active) return;
      const b = this.buf;
      if (this.isPaused() && !b.length) return; // 暂停时停下循环，play 事件会重新启动
      this.rafId = requestAnimationFrame(this.onRaf);
      this.trackVsync(t);
      if (this.needPlace || t - this.lastCheck >= CHECK_MS) {
        this.lastCheck = t;
        this.place();
        if (!this.active) return;
      }
      if (!b.length) return;
      const present = t + this.aheadMs(); // 本次绘制预计上屏的时间
      const target = present - this.delay + this.vsync / 2; // 取最接近目标时刻的刷新周期
      let k = -1;
      for (let i = 0; i < b.length; i++) {
        if (b[i].time <= target) k = i;
        else break;
      }
      if (k < 0) return; // 还没有足够旧的帧：保持当前画面
      for (let i = 0; i < k; i++) {
        this.release(b[i]);
        this.dropped++;
      }
      const e = b[k];
      b.splice(0, k + 1);
      this.releaseCur();
      this.cur = e;
      this.draw(e.src, e.iw, e.ih);
      this.shown++;
      this.lastLag = present - e.time;
    }

    aheadMs() {
      if (this.presentAheadFrames !== null) return this.presentAheadFrames * this.vsync;
      return Math.max(this.vsync, this.presentAheadMs);
    }

    trackVsync(t) {
      if (this.lastRafT) {
        const dt = t - this.lastRafT;
        if (dt > 2 && dt < 100) {
          const d = this.dts;
          d.push(dt);
          if (d.length > 31) d.shift();
          if (d.length >= 5) {
            const s = d.slice().sort((a, b) => a - b);
            this.vsync = s[s.length >> 1];
          }
        }
      }
      this.lastRafT = t;
    }

    drawLive() {
      if (!this.active) return;
      this.releaseCur();
      const v = this.video;
      if (v.readyState >= 2 && v.videoWidth && v.videoHeight) {
        this.cur = LIVE;
        this.draw(v, v.videoWidth, v.videoHeight);
      } else {
        this.clear();
      }
      this.lastLag = 0;
    }

    redraw() {
      const c = this.cur;
      if (c === LIVE) this.draw(this.video, this.video.videoWidth, this.video.videoHeight);
      else if (c && c.src) this.draw(c.src, c.iw, c.ih);
      else this.clear();
    }

    clear() {
      const c = this.canvas;
      const ctx = this.ctx;
      if (!c || !ctx || !c.width || !c.height) return;
      if (this.placement === 'beside') {
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, c.width, c.height);
      } else {
        ctx.clearRect(0, 0, c.width, c.height);
      }
    }

    draw(src, iw, ih) {
      const c = this.canvas;
      const ctx = this.ctx;
      if (!c || !ctx || !src) return;
      this.clear();
      const cw = c.width;
      const ch = c.height;
      if (!iw || !ih || !cw || !ch) return;
      const scale = this.cssW > 0 ? cw / this.cssW : 1;
      const r = fitRect(iw, ih, cw, ch, this.fit, this.objPos, scale);
      try {
        ctx.drawImage(src, r.x, r.y, r.w, r.h);
      } catch (_) { /* 帧已关闭等：跳过 */ }
    }

    // ---------- 布局 ----------

    createCanvas() {
      const c = document.createElement('canvas');
      c.setAttribute(CANVAS_ATTR, this.placement);
      c.setAttribute('aria-hidden', 'true');
      const base = {
        position: 'absolute', left: '0px', top: '0px', right: 'auto', bottom: 'auto',
        margin: '0', padding: '0', border: '0', outline: 'none',
        'min-width': '0', 'min-height': '0', 'max-width': 'none', 'max-height': 'none',
        'box-sizing': 'content-box', 'pointer-events': 'none', 'user-select': 'none',
        float: 'none', transition: 'none', animation: 'none', 'object-fit': 'fill',
        background: this.placement === 'beside' ? '#000' : 'transparent', display: 'block',
      };
      for (const k in base) c.style.setProperty(k, base[k], 'important');
      this.canvas = c;
      this.ctx = c.getContext('2d', { alpha: this.placement !== 'beside' });
    }

    setStyle(k, v) {
      if (this.styles[k] === v) return;
      this.styles[k] = v;
      this.canvas.style.setProperty(k, v, 'important');
    }

    place() {
      this.needPlace = false;
      const v = this.video;
      const c = this.canvas;
      if (!c || !this.active) return;
      if (v.mediaKeys) {
        this.drm = true;
        this.update();
        return;
      }
      const parent = v.parentNode;
      if (!v.isConnected || !parent) {
        this.setStyle('display', 'none');
        return;
      }
      // 必须紧跟在 <video> 之后：保证在视频之上、在网站自己的控件/弹幕层之下
      if (c.parentNode !== parent || c.previousSibling !== v) parent.insertBefore(c, v.nextSibling);

      const cs = getComputedStyle(v);
      const bl = num(cs.borderLeftWidth);
      const br = num(cs.borderRightWidth);
      const bt = num(cs.borderTopWidth);
      const bb = num(cs.borderBottomWidth);
      const pl = num(cs.paddingLeft);
      const pr = num(cs.paddingRight);
      const pt = num(cs.paddingTop);
      const pb = num(cs.paddingBottom);
      let w = num(cs.width);
      let h = num(cs.height);
      if (cs.boxSizing === 'border-box') {
        w -= bl + br + pl + pr;
        h -= bt + bb + pt + pb;
      }
      if (cs.display === 'none' || !(w >= 1 && h >= 1)) {
        this.setStyle('display', 'none');
        return;
      }
      const overlay = this.placement === 'overlay';
      this.setStyle('display', 'block');
      this.setStyle('position', cs.position === 'fixed' ? 'fixed' : 'absolute');
      this.setStyle('width', `${w}px`);
      this.setStyle('height', `${h}px`);
      if (overlay) {
        this.setStyle('z-index', cs.zIndex);
        this.setStyle('visibility', cs.visibility);
        this.setStyle('opacity', cs.opacity);
        this.setStyle('filter', cs.filter);
        this.setStyle('border-radius', cs.borderRadius);
        this.setStyle('clip-path', cs.clipPath);
        this.setStyle('transform', cs.transform);
        this.setStyle('transform-origin', originMinusInset(cs.transformOrigin, bl + pl, bt + pt));
      } else {
        this.setStyle('z-index', '2147483647');
      }

      // 位置：直接比对屏幕坐标并修正，父元素是否 positioned、是否有 transform 都适用
      const vr = v.getBoundingClientRect();
      const cr = c.getBoundingClientRect();
      const sp = scaleOf(parent);
      const sv = v.offsetWidth > 0 ? vr.width / v.offsetWidth : sp;
      const tx = overlay ? vr.left + (bl + pl) * sv : vr.right;
      const ty = overlay ? vr.top + (bt + pt) * sv : vr.top;
      const dx = (tx - cr.left) / sp;
      const dy = (ty - cr.top) / sp;
      if (Math.abs(dx) > 0.05 || Math.abs(dy) > 0.05) {
        this.posL = Math.round((this.posL + dx) * 100) / 100;
        this.posT = Math.round((this.posT + dy) * 100) / 100;
        this.setStyle('left', `${this.posL}px`);
        this.setStyle('top', `${this.posT}px`);
      }

      // 画布像素尺寸
      const dpr = root.devicePixelRatio || 1;
      let bw = Math.max(1, Math.round(w * dpr * sp));
      let bh = Math.max(1, Math.round(h * dpr * sp));
      const m = Math.max(bw, bh);
      if (m > MAX_BACKING_PX) {
        bw = Math.max(1, Math.round((bw * MAX_BACKING_PX) / m));
        bh = Math.max(1, Math.round((bh * MAX_BACKING_PX) / m));
      }
      let redraw = false;
      if (c.width !== bw || c.height !== bh) {
        c.width = bw;
        c.height = bh;
        redraw = true;
      }
      const fs = this.fitStyle(cs);
      if (fs.objectFit !== this.fit || fs.objectPosition !== this.objPos) {
        this.fit = fs.objectFit;
        this.objPos = fs.objectPosition;
        redraw = true;
      }
      this.cssW = w;
      if (redraw) this.redraw();
    }

    onLayoutChange() {
      if (!this.active) return;
      this.needPlace = true;
      this.place(); // ResizeObserver/resize 回调里立即重新定位，避免慢一帧
    }

    isVideoFullscreen() {
      const fe = document.fullscreenElement || document.webkitFullscreenElement;
      return fe === this.video;
    }

    onFullscreen() {
      // 网站对容器全屏：canvas 在容器内，跟随即可；直接对 <video> 全屏：顶层只有视频本身，无法覆盖
      const blocked = this.isVideoFullscreen();
      if (blocked !== this.fsBlocked) {
        this.fsBlocked = blocked;
        this.update();
      }
      if (this.active) this.place();
    }

    // ---------- 媒体事件 ----------

    onMediaEvent(ev) {
      if (!this.active) return;
      this.flush();
      if (ev.type === 'emptied') {
        this.releaseCur();
        this.clear();
        this.lastLag = 0;
        return;
      }
      this.drawLive();
    }

    onPlay() {
      // 继续显示最后一帧，直到缓冲里的帧够“旧”——声音同样晚到，短暂定格是正确的
      this.kick();
    }

    onEncrypted() {
      this.drm = true;
      this.update();
    }

    onWaiting() {
      // 某些硬件解码器的输出帧池很小：持有太多 VideoFrame 会让解码卡住。
      // 若数据其实已缓冲却进入 waiting，就改用 canvas 拷贝模式。
      this.waits++;
      if (!this.active || this.captureMode !== 'videoframe' || !this.stallFallback) return;
      if (this.vfHeld < 2) return;
      const v = this.video;
      const t = v.currentTime;
      let ahead = 0;
      try {
        for (let i = 0; i < v.buffered.length; i++) {
          if (v.buffered.start(i) <= t + 0.05 && v.buffered.end(i) > t) ahead = v.buffered.end(i) - t;
        }
      } catch (_) { /* ignore */ }
      if (ahead > 1) this.switchToCanvas('decoder-stall');
    }

    onVisibility() {
      if (!this.active || document.visibilityState !== 'visible') return;
      this.flush(); // 后台期间的帧已过时
      this.drawLive();
      this.kick();
    }
  }

  // 画布型播放器里显示画面的 canvas。页面脚本里可用播放器自己的 getRenderCanvas()（bilibili 的
  // <bwp-video> 有）；扩展的隔离环境看不到页面 JS 属性，改用 chrome.dom 打开封闭的 shadow root。
  function findSourceCanvas(host, given) {
    if (given && given.tagName === 'CANVAS') return given;
    try {
      if (typeof host.getRenderCanvas === 'function') {
        const c = host.getRenderCanvas();
        if (c && c.tagName === 'CANVAS') return c;
      }
    } catch (_) { /* ignore */ }
    let sr = host.shadowRoot;
    if (!sr) {
      try {
        const d = root.chrome && root.chrome.dom;
        if (d && typeof d.openOrClosedShadowRoot === 'function') sr = d.openOrClosedShadowRoot(host);
      } catch (_) { /* ignore */ }
    }
    let best = null;
    let area = 0;
    for (const scope of sr ? [sr, host] : [host]) {
      const known = scope.querySelector('canvas.-bwp-internal-render-canvas');
      if (known) return known;
      for (const c of scope.querySelectorAll('canvas')) {
        if (c.hasAttribute(CANVAS_ATTR)) continue; // 自己的覆盖层
        const r = c.getBoundingClientRect();
        if (r.width * r.height > area) {
          area = r.width * r.height;
          best = c;
        }
      }
    }
    return best;
  }

  /*
   * 画布型播放器（bilibili 的 <bwp-video>：WASM 解码，在 worker 里画到 transferControlToOffscreen
   * 的 canvas 上，canvas 在封闭的 shadow root 里）。canvas.captureStream() 在画布内容变化时出一帧，
   * MediaStreamTrackProcessor 把它交给我们：立即拷进 canvas 池（VideoFrame 随即 close），按捕获时间戳
   * 排队；显示、布局、清空与 <video> 相同。覆盖层放在播放器元素之后，按元素的位置和大小摆放。
   * 跨域污染的画布 captureStream 会抛 SecurityError：不接管，reason 为 'source-unsupported'。
   * 隔离环境里读不到 paused 等属性：靠元素上派发的 play/pause/seeking… 事件，以及有没有新画面。
   */
  class CanvasSourceController extends Controller {
    constructor(host, opts) {
      super(host, opts);
      this.captureMode = 'canvas';
      this.fallbackReason = null;
      this.givenCanvas = opts.sourceCanvas || null;
      this.showMs = Number.isFinite(opts.sourceShowMs) ? opts.sourceShowMs : DEFAULT_SOURCE_SHOW_MS;
      this.src = null; // 画面所在的 canvas
      this.badSrc = null; // 无法捕获的 canvas
      this.srcIssue = ''; // 'no-capture-api' | 'no-canvas' | 'tainted' | 'capture-failed'
      this.srcPaused = false;
      this.srcSeeking = false;
      this.liveNext = false;
      this.liveAt = 0;
      this.track = null;
      this.reader = null;
      this.gen = 0;
      this.tsOff = Infinity; // performance.now() − 帧时间戳 的下界（≈ 捕获时刻的换算）
      this.lastTs = -1;
      this.lastSrcAt = 0;
      this.ivs = [];
      this.frameMs = FRAME_MS;
      this.runAt = 0;
      this.run = 0;
      this.retryTimer = 0;
    }

    resolveSource() {
      if (this.src && this.src.isConnected) return this.src;
      let c = findSourceCanvas(this.video, this.givenCanvas);
      if (c && !c.isConnected) c = null;
      if (c !== this.src) {
        this.src = c;
        if (c !== this.badSrc) this.srcIssue = '';
      }
      return c;
    }

    unsupportedReason() {
      if (!HAS_CANVAS_CAPTURE) {
        this.srcIssue = 'no-capture-api';
      } else if (!this.resolveSource()) {
        this.srcIssue = 'no-canvas';
        this.scheduleRetry(); // 播放器可能稍后才建好画布
      }
      return this.srcIssue ? 'source-unsupported' : '';
    }

    scheduleRetry() {
      if (this.retryTimer || this.detached) return;
      this.retryTimer = setTimeout(() => {
        this.retryTimer = 0;
        if (!this.detached) this.update();
      }, 1000);
    }

    startCapture() {
      this.stopCapture();
      const c = this.src;
      if (!c) return;
      const gen = this.gen;
      this.tsOff = Infinity;
      this.lastTs = -1;
      this.ivs.length = 0;
      let reader;
      try {
        const track = c.captureStream().getVideoTracks()[0]; // 不给帧率：画布每变化一次出一帧
        if (!track) throw new Error('no video track');
        this.track = track;
        reader = new root.MediaStreamTrackProcessor({ track }).readable.getReader();
      } catch (err) {
        this.stopCapture();
        this.badSrc = c;
        this.srcIssue = err && err.name === 'SecurityError' ? 'tainted' : 'capture-failed';
        try {
          console.info('[HomePodCast] 无法捕获播放器画面：', this.srcIssue, (err && err.message) || '');
        } catch (_) { /* ignore */ }
        this.update(); // → deactivate()
        return;
      }
      this.reader = reader;
      const pump = () => {
        reader.read().then(({ value, done }) => {
          if (gen !== this.gen) {
            if (value) value.close();
            return;
          }
          if (done) return; // 轨道结束（画布被移除等）：place() 会重新查找
          this.onSourceFrame(value);
          pump();
        }, () => { /* 已取消 */ });
      };
      pump();
    }

    stopCapture() {
      this.gen++;
      if (this.reader) {
        this.reader.cancel().catch(() => { /* ignore */ });
        this.reader = null;
      }
      if (this.track) {
        try {
          this.track.stop();
        } catch (_) { /* ignore */ }
        this.track = null;
      }
    }

    onSourceFrame(f) {
      try {
        if (!this.active) return;
        const now = performance.now();
        const ts = f.timestamp / 1000;
        if (ts <= this.lastTs) {
          this.tsOff = Infinity; // 时间戳倒退：重新对时
          this.ivs.length = 0;
        } else if (this.lastTs >= 0) {
          this.trackInterval(ts - this.lastTs);
        }
        this.lastTs = ts;
        // 读取有排队延迟，取 (now − ts) 的下界作为两个时钟的差；缓慢上浮以跟上时钟漂移
        this.tsOff = Math.min(this.tsOff + 0.002, now - ts);
        this.lastSrcAt = now;
        if (this.srcPaused) {
          // 持续出新画面却以为暂停了（漏掉了 play 事件）：按正在播放处理
          if (now - this.runAt > 400) {
            this.runAt = now;
            this.run = 0;
          }
          if (++this.run >= 12) this.srcPaused = false;
        }
        if (this.srcPaused || this.srcSeeking) {
          this.drawLive(f); // 暂停/拖动时直接显示当前画面
          return;
        }
        if (this.liveNext) {
          // 拖动结束：画布比 seeked 事件晚一点才换成新画面。之前捕获的是旧画面，丢掉；
          // 之后的第一帧直接显示（相当于 <video> 在 seeked 时显示的那一帧）
          if (ts + this.tsOff < this.liveAt) return;
          this.liveNext = false;
          this.drawLive(f);
          return;
        }
        const e = this.captureCanvas(ts + this.tsOff + this.showMs, f.displayWidth, f.displayHeight, f);
        if (e) this.store(e);
        this.kick();
      } finally {
        f.close();
      }
    }

    trackInterval(dt) {
      if (!(dt > 2 && dt < 100)) return;
      const d = this.ivs;
      d.push(dt);
      if (d.length > 15) d.shift();
      if (d.length >= 5) {
        const s = d.slice().sort((a, b) => a - b);
        this.frameMs = Math.min(50, Math.max(4, s[s.length >> 1]));
      }
    }

    capacity() {
      return capacityFor(this.delay, this.frameMs);
    }

    isPaused() {
      return this.srcPaused || performance.now() - this.lastSrcAt > 500;
    }

    fitStyle(cs) {
      const c = this.src;
      if (!c || !c.isConnected) return cs;
      try {
        return getComputedStyle(c);
      } catch (_) {
        return cs;
      }
    }

    drawLive(frame) {
      if (!this.active) return;
      this.releaseCur();
      const c = this.src;
      if (frame) {
        this.cur = LIVE;
        this.draw(frame, frame.displayWidth, frame.displayHeight);
      } else if (c && c.width && c.height) {
        this.cur = LIVE;
        this.draw(c, c.width, c.height);
      } else {
        this.clear();
      }
      this.lastLag = 0;
    }

    redraw() {
      if (this.cur === LIVE) this.drawLive();
      else super.redraw();
    }

    place() {
      if (this.active && this.src && !this.src.isConnected) {
        // 播放器换了画布：停用后重新查找、重新捕获
        this.deactivate();
        this.update();
        return;
      }
      super.place();
    }

    onMediaEvent(ev) {
      const t = ev.type;
      if (t === 'pause' || t === 'emptied') this.srcPaused = true;
      if (t === 'seeking') this.srcSeeking = true;
      else if (t === 'seeked' || t === 'emptied' || t === 'loadeddata') this.srcSeeking = false;
      if (t === 'seeked' || t === 'loadeddata') {
        this.liveNext = true;
        this.liveAt = performance.now();
      }
      super.onMediaEvent(ev);
    }

    onPlay() {
      if (this.srcPaused) this.liveNext = false; // 暂停期间的新画面已经直接显示过
      this.srcPaused = false;
      super.onPlay();
    }

    detach() {
      clearTimeout(this.retryTimer);
      this.retryTimer = 0;
      super.detach();
      this.src = null;
    }

    stats() {
      const s = super.stats();
      s.source = 'canvas';
      s.sourceIssue = this.srcIssue || null;
      s.sourceFrameMs = round1(this.frameMs);
      s.sourceShowMs = this.showMs;
      return s;
    }
  }

  function attach(video, opts) {
    const o = opts || {};
    const isVideo = !!video && video.tagName === 'VIDEO';
    if (!video || video.nodeType !== 1 || (!isVideo && !SOURCE_TAGS.has(video.tagName) && !o.sourceCanvas)) {
      throw new TypeError('HPCDelay.attach：需要 <video> 或画布型播放器元素（如 <bwp-video>）');
    }
    const existing = registry.get(video);
    if (existing && !existing.detached) return existing.api;
    const c = isVideo ? new Controller(video, o) : new CanvasSourceController(video, o);
    registry.set(video, c);
    c.start();
    return c.api;
  }

  // 视频是否已被另一个引擎实例（例如页面自己加载的测量脚本）接管
  function isAttachedElsewhere(video) {
    const owner = video && video.getAttribute && video.getAttribute(OWNER_ATTR);
    return !!owner && owner !== ENGINE_TOKEN;
  }

  root.HPCDelay = Object.freeze({
    version: VERSION,
    attach,
    isAttachedElsewhere,
    supported: HAS_RVFC,
    webcodecs: HAS_VIDEOFRAME,
    canvasCapture: HAS_CANVAS_CAPTURE,
  });
})();
