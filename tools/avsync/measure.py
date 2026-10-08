"""Measure how far the delayed copy lags the original in a screen recording of testpage.html.

Each test-video frame carries its frame number as a 16-bit code row (see gen_testvideo.py).
The recording shows the original <video> (left) and the engine's delayed canvas (right); in every
captured frame both code rows are decoded, and delay = left number - right number (in video frames,
1 frame = 1000/60 ms).

Sub-frame resolution: a true delay of e.g. 8.46 frames shows up as a difference that toggles between
8 and 9 from one captured frame to the next, so the MEAN over many captured frames estimates the
delay to a fraction of a frame (as long as the capture instants are not phase-locked to the video).
The median is always a whole frame.

usage:
  python measure.py capture.mp4 [--left x,y,w,h --right x,y,w,h] [--debug-png rows.png]
                    [--fps 60] [--tol 3] [--json out.json]

The code rows are found automatically (any capture scale/position). --left/--right are a fallback:
the bounding box, in capture pixels, of each row of 18 squares (outer edges of the two
registration squares, top and bottom of the squares).
Exit code: 0 = measured, 2 = no code rows / no valid frames, 3 = the original did not advance at
~60 frames/s (frozen/stale capture or paused video), 1 = other error.
Misreads are filtered (registration contrast, ambiguous bits, numbers >= 3600, isolated jumps);
real engine behaviour such as freezes and catch-up jumps is kept in the statistics.
"""

import argparse
import json
import subprocess
import sys

import numpy as np

# Code-row geometry in test-video pixels (mirrors gen_testvideo.py).
SQ, GAP, NBITS, BAND = 48, 8, 16, 32
PITCH = SQ + GAP
SLOTS = NBITS + 2
SPAN = (SLOTS - 1) * PITCH              # 952: centre distance between the registration squares
ROW_W = SLOTS * SQ + (SLOTS - 1) * GAP  # 1000
VIDEO_W = 1280                          # the delayed copy should sit one video width to the right
LOOP = 3600                             # frames in the (looping) test video
THR = 128                               # white/black threshold used only to locate the rows
N_REG, N_BITS = 2, NBITS                # sample layout: [reg L, reg R, 16 bits MSB first, band...]
WEIGHTS = 1 << np.arange(NBITS - 1, -1, -1)
REASONS = ["ok", "registration missing", "ambiguous bit", "number out of range", "number jump"]


def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        sys.exit(f"{args[0]} failed: {r.stderr.decode(errors='replace')[-800:]}")
    return r.stdout


def probe(path):
    j = json.loads(run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                        "stream=width,height,avg_frame_rate:packet=pts_time", "-of", "json", path]))
    st = j["streams"][0]
    pts = sorted(float(p["pts_time"]) for p in j.get("packets", [])
                 if p.get("pts_time") not in (None, "N/A"))
    num, den = (float(x) for x in st.get("avg_frame_rate", "0/1").split("/"))
    return st["width"], st["height"], np.array(pts), (num / den if den else 0.0)


def read_frames(path, vf, w, h, budget=48_000_000):
    """Yield (n, h, w) uint8 gray chunks of the filtered video."""
    size = w * h
    chunk = max(1, budget // size)
    p = subprocess.Popen(["ffmpeg", "-v", "error", "-i", path, "-an", "-vf", vf,
                          "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"],
                         stdout=subprocess.PIPE)
    try:
        while True:
            buf = p.stdout.read(size * chunk)
            n = len(buf) // size
            if n == 0:
                break
            yield np.frombuffer(buf, np.uint8, n * size).reshape(n, h, w)
    finally:
        p.stdout.close()
        p.wait()


class Row:
    """One code row: sample points derived from the two registration-square centres."""

    def __init__(self, left, right, sy, squares=()):
        self.left, self.right = np.asarray(left, float), np.asarray(right, float)
        self.sy = float(sy)
        self.squares = tuple(squares)
        self.score = 1.0
        u = (self.right - self.left) / SPAN                 # capture px per test-video px
        self.sx = float(np.hypot(*u)) * SQ
        down = np.array([-u[1], u[0]]) / np.hypot(*u)       # unit vector perpendicular to the row
        centres = self.left + np.outer(np.arange(SLOTS) * PITCH, u)
        off = down * self.sy * (SQ / 2 + BAND / 2) / SQ     # middle of the black band
        mid = centres[1:-1]
        self.points = np.vstack([centres[[0, -1]], mid, mid - off, mid + off])
        self.rx = max(0, int(0.18 * self.sx))
        self.ry = max(0, int(0.18 * self.sy))

    @classmethod
    def from_box(cls, x, y, w, h):
        s = w * SQ / ROW_W
        return cls((x + s / 2, y + h / 2), (x + w - s / 2, y + h / 2), h)

    def box(self):
        return (self.left[0] - self.sx / 2, self.left[1] - self.sy / 2,
                self.right[0] - self.left[0] + self.sx, self.sy)

    def extent(self):
        p = self.points
        return (p[:, 0].min() - self.rx, p[:, 1].min() - self.ry,
                p[:, 0].max() + self.rx, p[:, 1].max() + self.ry)

    def indices(self, ox=0, oy=0):
        pts = np.rint(self.points - [ox, oy]).astype(int)
        dy, dx = np.mgrid[-self.ry:self.ry + 1, -self.rx:self.rx + 1]
        return pts[:, 1, None] + dy.ravel(), pts[:, 0, None] + dx.ravel()

    def inside(self, w, h, ox=0, oy=0):
        x0, y0, x1, y1 = self.extent()
        return x0 - ox >= 0 and y0 - oy >= 0 and x1 - ox < w - 0.5 and y1 - oy < h - 0.5

    def describe(self):
        x, y, w, h = self.box()
        return (f"box {x:.0f},{y:.0f},{w:.0f},{h:.0f}  (square {self.sx:.1f}x{self.sy:.1f} px, "
                f"tilt {np.degrees(np.arctan2(*(self.right - self.left)[::-1])):+.2f} deg)")


def sample(frames, idx):
    ys, xs = idx
    return frames[:, ys, xs].mean(-1)       # (n, points)


def decode(vals):
    """vals (n, points) -> (number, status) per frame; status indexes REASONS."""
    reg = vals[:, :N_REG]
    bits = vals[:, N_REG:N_REG + N_BITS]
    band = vals[:, N_REG + N_BITS:]
    black = np.median(band, axis=1)
    con = reg.mean(1) - black
    reg_bad = ((con < 40) | (reg.min(1) - black < 0.7 * con)
               | (band.max(1) - black > 0.35 * con))
    v = (bits - black[:, None]) / np.maximum(con, 1)[:, None]
    amb = ((v > 0.25) & (v < 0.75)).any(1)
    num = ((v >= 0.5) * WEIGHTS).sum(1)
    status = np.where(reg_bad, 1, np.where(amb, 2, np.where(num >= LOOP, 3, 0)))
    return num, status


def components(mask):
    """4-connected components of a boolean image -> list of (x0, y0, x1, y1, area), x1/y1 exclusive."""
    h, w = mask.shape
    m = np.zeros((h, w + 2), np.int8)
    m[:, 1:-1] = mask
    d = np.diff(m, axis=1)
    ry, rs = np.nonzero(d == 1)
    _, re = np.nonzero(d == -1)
    n = len(ry)
    parent = list(range(n))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    rowstart = np.searchsorted(ry, np.arange(h + 1))
    for y in range(1, h):
        i, i1 = rowstart[y - 1], rowstart[y]
        j, j1 = rowstart[y], rowstart[y + 1]
        while i < i1 and j < j1:
            if rs[i] < re[j] and rs[j] < re[i]:
                a, b = find(i), find(j)
                if a != b:
                    parent[b] = a
            if re[i] < re[j]:
                i += 1
            else:
                j += 1
    comps = {}
    for k in range(n):
        r = find(k)
        c = comps.get(r)
        if c is None:
            comps[r] = [rs[k], ry[k], re[k], ry[k] + 1, re[k] - rs[k]]
        else:
            c[0] = min(c[0], rs[k]); c[2] = max(c[2], re[k])
            c[1] = min(c[1], ry[k]); c[3] = max(c[3], ry[k] + 1)
            c[4] += re[k] - rs[k]
    return [tuple(int(v) for v in c) for c in comps.values()]


def locate(path, w, h, nframes, log):
    """Find the code rows: always-white squares paired 17 pitches apart, verified by decoding."""
    stride = max(1, nframes // 60) | 1          # odd stride mixes the low bits of the samples
    count = np.zeros((h, w), np.uint16)
    kept, n = [], 0
    for chunk in read_frames(path, f"select='not(mod(n,{stride}))',format=gray", w, h):
        count += (chunk > THR).sum(0, dtype=np.uint16)
        for f in chunk:
            if n % 4 == 0 and len(kept) < 16:
                kept.append(f.copy())
            n += 1
    if n == 0:
        sys.exit("no frames decoded from the capture")
    kept = np.stack(kept)
    frac = count / n
    squares = []
    for x0, y0, x1, y1, area in components(frac >= 0.8):
        bw, bh = x1 - x0, y1 - y0
        if min(bw, bh) >= 5 and 0.75 <= bw / bh <= 1.33 and area >= 0.8 * bw * bh:
            squares.append(((x0 + x1 - 1) / 2, (y0 + y1 - 1) / 2, bw, bh))
    log(f"locate: {n} sampled frames, {len(squares)} always-white square candidates")

    cands = []
    for i, a in enumerate(squares):
        for j, b in enumerate(squares):
            dx, dy = b[0] - a[0], b[1] - a[1]
            if dx <= 0:
                continue
            dist = np.hypot(dx, dy)
            s_exp = dist * SQ / SPAN
            if abs(dy) > 0.35 * s_exp or s_exp < 5:
                continue
            if not all(abs(q[2] - s_exp) <= max(1.5, 0.3 * s_exp) and
                       abs(q[3] - s_exp) <= max(1.5, 0.4 * s_exp) for q in (a, b)):
                continue
            row = Row(a[:2], b[:2], (a[3] + b[3]) / 2, (i, j))
            if not row.inside(w, h):
                continue
            _, status = decode(sample(kept, row.indices()))
            row.score = float(np.mean(status == 0))
            if row.score >= 0.5:
                cands.append(row)
    rows, used = [], set()
    for r in sorted(cands, key=lambda r: -r.score):
        if not used & set(r.squares):
            rows.append(r)
            used |= set(r.squares)
    for r in rows:
        log(f"  code row: {r.describe()}, decodes {r.score:.0%} of sampled frames")
    if len(rows) < 2:
        return rows
    best = None
    for a in rows:
        for b in rows:
            if b.left[0] <= a.left[0] or abs(b.sx / a.sx - 1) > 0.1:
                continue
            off = (b.left[0] - a.left[0]) / (a.sx / SQ)          # in test-video pixels
            cost = abs(off - VIDEO_W) / VIDEO_W + abs(b.left[1] - a.left[1]) / a.sy \
                + (2 - a.score - b.score)
            if best is None or cost < best[0]:
                best = (cost, a, b, off)
    if best is None:
        return rows[:1]
    _, a, b, off = best
    if abs(off - VIDEO_W) > 0.05 * VIDEO_W:
        log(f"  warning: delayed copy is {off / VIDEO_W:.2f} video widths right of the original "
            f"(expected 1.00)")
    return [a, b]


def jump_filter(num, ok, t, fps, tol, min_run=3):
    """Reject isolated misreads. Clean frames are split into runs wherever the number step disagrees
    with the elapsed time by more than tol frames (modulo the loop); runs shorter than min_run are
    dropped. Real engine behaviour (a freeze, then a catch-up jump) forms long runs and stays in."""
    idx = np.nonzero(ok)[0]
    out = ok.copy()
    if len(idx) < min_run:
        return out
    dt = np.diff(t[idx])
    dev = np.abs((np.diff(num[idx]) - fps * dt + LOOP / 2) % LOOP - LOOP / 2)
    run = np.r_[0, np.cumsum(dev > tol + 0.05 * fps * dt)]
    out[idx[np.bincount(run)[run] < min_run]] = False
    return out


def write_png(path, gray, rows, ox, oy):
    rgb = np.repeat(gray[:, :, None], 3, axis=2).copy()
    colours = [(0, 140, 255)] * N_REG + [(255, 40, 40)] * N_BITS + [(255, 200, 0)] * (2 * N_BITS)
    for row in rows:
        ys, xs = row.indices(ox, oy)
        for k, c in enumerate(colours):
            rgb[ys[k], xs[k]] = c
    h, w = gray.shape
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
                    "-s", f"{w}x{h}", "-i", "-", path], input=rgb.tobytes(), check=True)


def parse_box(s):
    v = [float(x) for x in s.split(",")]
    if len(v) != 4:
        raise argparse.ArgumentTypeError("expected x,y,w,h")
    return v


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("capture")
    ap.add_argument("--left", type=parse_box, help="x,y,w,h of the original's code row")
    ap.add_argument("--right", type=parse_box, help="x,y,w,h of the delayed copy's code row")
    ap.add_argument("--fps", type=float, default=60.0, help="test video frame rate (default 60)")
    ap.add_argument("--tol", type=float, default=3.0,
                    help="frames a number may differ from its predecessor + elapsed time before it "
                         "counts as a jump (default 3; 1e9 disables the jump check)")
    ap.add_argument("--debug-png", help="write the first frame with the sample points marked")
    ap.add_argument("--json", help="also write the results as JSON")
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args()
    log = (lambda *_: None) if a.quiet else print

    w, h, pts, cap_fps = probe(a.capture)
    log(f"capture: {a.capture}  {w}x{h}, {len(pts)} frames")
    if (a.left is None) != (a.right is None):
        sys.exit("give both --left and --right, or neither")
    if a.left:
        rows = [Row.from_box(*a.left), Row.from_box(*a.right)]
        for name, r in zip(("left", "right"), rows):
            if not r.inside(w, h):
                sys.exit(f"--{name} box (with its band) does not fit inside the {w}x{h} capture")
    else:
        rows = locate(a.capture, w, h, len(pts), log)
        if len(rows) < 2:
            print(f"RESULT none: found {len(rows)} code row(s), need 2 (original + delayed copy). "
                  "Check the capture, or pass --left/--right boxes.")
            return 2
    log(f"original (left): {rows[0].describe()}")
    log(f"delayed (right): {rows[1].describe()}")

    ext = np.array([r.extent() for r in rows])
    cx0, cy0 = (int(max(0, np.floor(v) - 2)) for v in ext[:, :2].min(0))
    cx1, cy1 = int(min(w, np.ceil(ext[:, 2].max()) + 3)), int(min(h, np.ceil(ext[:, 3].max()) + 3))
    cw, ch = cx1 - cx0, cy1 - cy0
    idx = [r.indices(cx0, cy0) for r in rows]
    nums, stats, first = [[], []], [[], []], None
    for chunk in read_frames(a.capture, f"format=gray,crop={cw}:{ch}:{cx0}:{cy0}", cw, ch):
        if first is None:
            first = chunk[0].copy()
        for side in (0, 1):
            nm, st = decode(sample(chunk, idx[side]))
            nums[side].append(nm)
            stats[side].append(st)
    if first is None:
        sys.exit("no frames decoded from the capture")
    nums = [np.concatenate(x) for x in nums]
    stats = [np.concatenate(x) for x in stats]
    n = len(nums[0])
    if a.debug_png:
        write_png(a.debug_png, first, rows, cx0, cy0)
        log(f"debug image: {a.debug_png} (crop at {cx0},{cy0}; blue=registration, red=bits, "
            f"yellow=band)")

    t = pts[:n] if len(pts) >= n else np.arange(n) / (cap_fps or a.fps)
    if len(t) > 1 and t[-1] > t[0]:
        log(f"capture rate: {(n - 1) / (t[-1] - t[0]):.2f} frames/s over {t[-1] - t[0]:.2f} s")
    for side in (0, 1):
        ok = stats[side] == 0
        stats[side][ok & ~jump_filter(nums[side], ok, t, a.fps, a.tol)] = 4
    valid = (stats[0] == 0) & (stats[1] == 0)
    for side, name in ((0, "original"), (1, "delayed ")):
        counts = np.bincount(stats[side], minlength=len(REASONS))
        rej = ", ".join(f"{REASONS[k]} {counts[k]}" for k in range(1, len(REASONS)) if counts[k])
        good = nums[side][stats[side] == 0]
        span = f", frame numbers {good.min()}..{good.max()}" if len(good) else ""
        log(f"{name}: {counts[0]}/{n} frames decode cleanly{span}" + (f"; rejected: {rej}" if rej else ""))
        if counts[4] > 0.2 * (counts[0] + counts[4]):
            print(f"warning: {name.strip()} side jumps erratically ({counts[4]} frames in runs < 3); "
                  f"if that is the engine's real behaviour, rerun with a larger --tol")

    rates = []
    for side in (0, 1):
        i = np.nonzero(stats[side] == 0)[0]
        span = t[i[-1]] - t[i[0]] if len(i) > 1 else 0
        steps = (np.diff(nums[side][i]) + LOOP // 2) % LOOP - LOOP // 2
        rates.append(steps.sum() / span if span > 0 else float("nan"))
    log(f"playback rate seen: original {rates[0]:.1f}, delayed {rates[1]:.1f} video frames/s "
        f"(expected {a.fps:g})")
    stale = not (0.5 * a.fps <= rates[0] <= 1.5 * a.fps)

    if not valid.any():
        print(f"RESULT none: no captured frame decoded cleanly on both sides (0/{n})")
        return 2
    d = ((nums[0][valid] - nums[1][valid] + LOOP // 2) % LOOP - LOOP // 2).astype(float)
    ms = 1000.0 / a.fps
    med, mean, sd = float(np.median(d)), float(d.mean()), float(d.std(ddof=1)) if len(d) > 1 else 0.0
    res = dict(capture=a.capture, frames=n, valid=int(valid.sum()),
               median_frames=med, mean_frames=mean, sd_frames=sd,
               median_ms=med * ms, mean_ms=mean * ms, sd_ms=sd * ms,
               min_frames=float(d.min()), max_frames=float(d.max()),
               histogram={int(k): int(c) for k, c in zip(*np.unique(d, return_counts=True))},
               rows={"left": rows[0].box(), "right": rows[1].box()})
    print(f"valid frames (both sides): {res['valid']}/{n}")
    print(f"delay, frames: median {med:.0f}  mean {mean:.3f}  sd {sd:.3f}  "
          f"(min {d.min():.0f}, max {d.max():.0f})")
    print(f"delay, ms:     median {med * ms:.1f}  mean {mean * ms:.2f}  sd {sd * ms:.2f}  "
          f"(1 frame = {ms:.3f} ms)")
    print("histogram:")
    top = max(res["histogram"].values())
    for k, c in res["histogram"].items():
        print(f"  {k:5d} fr {k * ms:8.1f} ms  {c:6d}  {'#' * max(1, round(40 * c / top))}")
    if len(res["histogram"]) > 1:
        print("  (a difference that toggles between neighbouring values = a delay between them; "
              "the mean resolves it)")
    print(f"RESULT delay mean {mean * ms:.1f} ms ({mean:.2f} frames), median {med * ms:.1f} ms, "
          f"sd {sd * ms:.1f} ms, valid {res['valid']}/{n}")
    res["playback_rate"] = {"left": rates[0], "right": rates[1]}
    res["stale_capture"] = bool(stale)
    if a.json:
        with open(a.json, "w") as f:
            json.dump(res, f, indent=1)
    if stale:
        print(f"warning: the original advanced at {rates[0]:.1f} frames/s instead of ~{a.fps:g}: "
              "the capture looks frozen/stale (or the video was paused); result not trustworthy")
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
