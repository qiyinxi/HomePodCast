"""Measure audio-vs-video offset from a phone recording of the sync test (slider at 0).

Flash onsets come from frame brightness, click onsets from the 1 kHz band of the audio track.
Each flash is paired with the first click after it; offset = click - flash.

usage: python measure_av.py video.mp4 [distance_m]
"""

import json
import subprocess
import sys

import numpy as np

video = sys.argv[1]
distance_m = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
SR = 48000


def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        sys.exit(f"{args[0]} failed: {r.stderr.decode(errors='replace')[-800:]}")
    return r.stdout


# --- video: per-frame timestamps and brightness of the brightest region
probe = json.loads(run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                        "frame=pts_time", "-of", "json", video]))
pts = np.array([float(f["pts_time"]) for f in probe["frames"]])
W, H = 192, 108
raw = run(["ffmpeg", "-v", "error", "-i", video, "-vf", f"scale={W}:{H},format=gray",
           "-fps_mode", "passthrough", "-f", "rawvideo", "-"])
frames = np.frombuffer(raw, np.uint8).reshape(-1, H, W).astype(np.float32)
n = min(len(frames), len(pts))
frames, pts = frames[:n], pts[:n]
frame_dt = float(np.median(np.diff(pts)))

# 3x3 blur to tame noise and small shake.
pad = np.pad(frames, ((0, 0), (1, 1), (1, 1)), mode="edge")
blur = sum(pad[:, dy:dy + H, dx:dx + W] for dy in range(3) for dx in range(3)) / 9.0

# A flash is a short (~90 ms) brightening relative to the picture ~150 ms before and after.
# Slow changes (auto exposure) and random shake cancel out in this comparison.
gap = max(1, int(round(0.15 / frame_dt)))
pulse = np.zeros_like(blur)
pulse[gap:-gap] = blur[gap:-gap] - np.maximum(blur[:-2 * gap], blur[2 * gap:])
score = np.percentile(pulse, 99, axis=0)
mask = score >= np.quantile(score, 0.997)           # the circle's pixels
ys, xs = np.nonzero(mask)
series = blur[:, mask].mean(axis=1)

# Normalise against a running "dark" baseline so exposure drift doesn't matter.
base = np.array([np.median(series[max(0, i - 2 * gap):i + 2 * gap + 1]) for i in range(n)])
rise = series - base
lit_level = np.percentile(rise, 99)
level = np.clip(rise / max(lit_level, 1e-6), 0, 1.5)
on = level > 0.5
flashes = []
last = -1.0
for i in range(1, n):
    if on[i] and not on[i - 1] and pts[i] - last > 0.8:
        # Partially lit onset frame => the flash began part-way through that frame interval.
        frac = float(np.clip(level[i], 0, 1))
        flashes.append(pts[i] - frac * frame_dt)
        last = pts[i]
print(f"circle region: x {xs.min()}-{xs.max()}, y {ys.min()}-{ys.max()} (of {W}x{H}), "
      f"{mask.sum()} px")

# --- audio: 1 kHz band envelope
pcm = np.frombuffer(run(["ffmpeg", "-v", "error", "-i", video, "-vn", "-ac", "1", "-ar", str(SR),
                         "-f", "f32le", "-"]), np.float32)
t = np.arange(len(pcm)) / SR
# quadrature demodulation at 1 kHz, then 4 ms moving average
lo_ = pcm * np.exp(-2j * np.pi * 1000 * t)
k = int(0.004 * SR)
env = np.abs(np.convolve(lo_, np.ones(k) / k, mode="same"))
noise = np.median(env)
peak = np.percentile(env, 99.9)
thr = noise + 0.3 * (peak - noise)
above = env > thr
clicks = []
i = 1
while i < len(env):
    if above[i] and not above[i - 1]:
        # walk back to where the envelope left the noise floor
        j = i
        floor = noise + 0.1 * (peak - noise)
        while j > 0 and env[j] > floor:
            j -= 1
        clicks.append(j / SR + k / 2 / SR)  # compensate the moving-average group delay
        i += int(0.3 * SR)                  # ignore the rest of this click
    else:
        i += 1

# --- pair and report
sound_delay = distance_m / 343.0
offsets = []
for f in flashes:
    later = [c for c in clicks if f - 0.05 <= c <= f + 1.0]
    if later:
        offsets.append((later[0] - f - sound_delay) * 1000)

print(f"video: {n} frames, ~{1 / frame_dt:.1f} fps; flashes detected: {len(flashes)}")
print(f"audio: clicks detected: {len(clicks)} (noise {noise:.4f}, peak {peak:.4f})")
print("flash times:", " ".join(f"{x:.3f}" for x in flashes))
print("click times:", " ".join(f"{x:.3f}" for x in clicks))
if offsets:
    o = np.array(offsets)
    print("offsets ms:", " ".join(f"{x:.0f}" for x in o))
    print(f"RESULT: sound lags picture by median {np.median(o):.0f} ms, mean {o.mean():.0f} ms, "
          f"sd {o.std():.0f} ms over {len(o)} pairs (sound travel {sound_delay * 1000:.1f} ms removed)")
