"""Synthesise a fake screen capture of the test page, for validating measure.py without a browser.

The test video is scaled to 640x360 and hstacked with a copy that lags by --delay frames
(multiples of 0.5 allowed: the video is first doubled to 120 fps), scaled to --width, padded
with a dark border and resampled to --rate frames/s (use a rate that is not 60, e.g. 50, to see
a fractional delay toggle between floor and ceil).

usage: python synth_capture.py out.mp4 --delay 9 [--width 1280] [--rate 60] [--start 50] [--dur 15]
"""

import argparse
import pathlib
import subprocess

HERE = pathlib.Path(__file__).resolve().parent

ap = argparse.ArgumentParser()
ap.add_argument("out")
ap.add_argument("--delay", type=float, required=True, help="lag of the right copy, frames (x.0 or x.5)")
ap.add_argument("--width", type=int, default=1280, help="width of the side-by-side pair")
ap.add_argument("--rate", type=float, default=60, help="capture frame rate")
ap.add_argument("--start", type=float, default=50, help="start time in the test video (50 => wraps at 60)")
ap.add_argument("--dur", type=float, default=15)
ap.add_argument("--video", default=str(HERE / "testvideo.mp4"))
a = ap.parse_args()

half = round(a.delay * 2)
w = a.width // 2 * 2
# Trim inside the graph: -ss with -stream_loop would loop back to the seek point, not to frame 0.
graph = (f"[0:v]trim=start={a.start}:duration={a.dur + 1},setpts=PTS-STARTPTS,"
         f"scale=640:360,fps=120,split[a][b];"
         f"[a]trim=start_frame={half},setpts=PTS-STARTPTS[l];[b]setpts=PTS-STARTPTS[r];"
         f"[l][r]hstack=shortest=1,scale={w}:-2,pad=iw+120:ih+220:60:80:color=0x111111,"
         f"fps={a.rate},format=yuv420p[v]")
loops = int((a.start + a.dur + 1) // 60)
subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-stream_loop", str(loops),
                "-i", a.video, "-filter_complex", graph,
                "-map", "[v]", "-t", str(a.dur), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                a.out], check=True)
print(f"wrote {a.out}: delay {half / 2} frames ({half / 2 * 1000 / 60:.1f} ms), width {w}, rate {a.rate}")
