// The frame-number code of the test video (make-video.mjs draws it) and the in-page reader that decodes it from the
// <video>, from the extension's delayed canvas, or from a screenshot.

export const CODE = Object.freeze({
  W: 640, H: 360, FPS: 30, SECONDS: 120,
  BITS: 12, // frame numbers 0…4095
  SQ: 32, // square size
  PITCH: 40, // square + gap
  X0: 44, // left edge of the left registration square
  Y0: 164, // top of the squares: mid-picture, clear of the test pages' captions, danmaku and control bars
});

/** How many frames `shown` is behind `current`, also across the loop back to frame 0. */
export function framesBehind(current, shown) {
  const n = CODE.FPS * CODE.SECONDS;
  const d = (((current - shown) % n) + n) % n;
  return d > n / 2 ? d - n : d;
}

// Runs in the page (main world): installs window.__e2e. Self-contained, since puppeteer serializes it.
export function installReader(code) {
  const { W, H, BITS, SQ, PITCH, X0, Y0 } = code;
  const scratch = document.createElement('canvas');
  scratch.width = W;
  scratch.height = H;
  const ctx = scratch.getContext('2d', { willReadFrequently: true });

  // Frame number in the scratch canvas, or null when the code isn't there (blank, garbled or half-drawn picture).
  function decode() {
    const px = ctx.getImageData(0, 0, W, H).data;
    const luma = (cx, cy, r) => {
      let sum = 0;
      let n = 0;
      for (let y = Math.round(cy - r); y < cy + r; y++) {
        for (let x = Math.round(cx - r); x < cx + r; x++) {
          const i = (y * W + x) * 4;
          sum += 0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2];
          n++;
        }
      }
      return sum / n;
    };
    const cy = Y0 + SQ / 2;
    const slot = (s) => luma(X0 + s * PITCH + SQ / 2, cy, 6);
    if (slot(0) < 150 || slot(BITS + 1) < 150 || luma(X0 - 6, cy, 3) > 100) return null;
    let n = 0;
    for (let k = 0; k < BITS; k++) {
      const v = slot(BITS - k);
      if (v > 70 && v < 150) return null; // between black and white: a blend of two frames
      if (v >= 150) n |= 1 << k;
    }
    return n;
  }

  // The picture inside a box of w×h source pixels drawn with object-fit: contain (centred).
  function readContained(source, w, h, iw, ih) {
    const s = Math.min(w / iw, h / ih);
    const dw = iw * s;
    const dh = ih * s;
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, W, H);
    ctx.drawImage(source, (w - dw) / 2, (h - dh) / 2, dw, dh, 0, 0, W, H);
    return decode();
  }

  const overlayOf = (video) => {
    const c = video.nextElementSibling;
    return c && c.tagName === 'CANVAS' && c.getAttribute('data-hpc-delay') === 'overlay' ? c : null;
  };

  window.__e2e = {
    overlayOf,
    readVideo(video) {
      if (!video.videoWidth) return null;
      ctx.drawImage(video, 0, 0, W, H);
      return decode();
    },
    readOverlay(video) {
      const c = overlayOf(video);
      if (!c || !c.width || !video.videoWidth) return null;
      return readContained(c, c.width, c.height, video.videoWidth, video.videoHeight);
    },
    // A screenshot (data URL) of the viewport; rect: the video's content box in CSS pixels.
    async readScreenshot(dataUrl, rect, iw, ih) {
      const img = new Image();
      img.src = dataUrl;
      await img.decode();
      const sx = img.naturalWidth / window.innerWidth;
      const s = Math.min(rect.width / iw, rect.height / ih);
      const dw = iw * s;
      const dh = ih * s;
      ctx.fillStyle = '#000';
      ctx.fillRect(0, 0, W, H);
      ctx.drawImage(img, (rect.left + (rect.width - dw) / 2) * sx, (rect.top + (rect.height - dh) / 2) * sx,
        dw * sx, dh * sx, 0, 0, W, H);
      return decode();
    },
    // Colour of a screenshot pixel at CSS coordinates (x, y).
    async screenshotPixel(dataUrl, points) {
      const img = new Image();
      img.src = dataUrl;
      await img.decode();
      const sx = img.naturalWidth / window.innerWidth;
      const c = document.createElement('canvas');
      c.width = img.naturalWidth;
      c.height = img.naturalHeight;
      const g = c.getContext('2d', { willReadFrequently: true });
      g.drawImage(img, 0, 0);
      return points.map(([x, y]) => [...g.getImageData(Math.round(x * sx), Math.round(y * sx), 1, 1).data.slice(0, 3)]);
    },
    // Frame numbers of the video and of the delayed canvas, read in the same animation frame.
    sample(video) {
      return new Promise((resolve) => requestAnimationFrame(() => {
        resolve({
          at: performance.now(),
          video: this.readVideo(video),
          overlay: this.readOverlay(video),
          currentTime: video.currentTime,
          paused: video.paused,
        });
      }));
    },
  };
}
