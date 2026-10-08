"""Generate testvideo.mp4 for the screen-capture delay harness.

1280x720, 60 fps, 60 s (3600 frames), H.264 yuv420p + AAC.
  * Frame number n as a 16-bit binary code: 16 squares (48x48, 8 px gaps, white = 1), MSB left,
    on a black band, with an always-white registration square at each end (18 slots, 1000 px wide).
  * Large frame counter (drawtext) and the time.
  * Every 90 frames (1.5 s): white top-left corner flash for 2 frames + a 1 kHz 30 ms beep.

usage: python gen_testvideo.py [out.mp4]
The geometry constants are mirrored in measure.py.
"""

import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
OUT = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "testvideo.mp4"

W, H, FPS, DUR = 1280, 720, 60, 60
SQ, GAP, NBITS = 48, 8, 16
PITCH = SQ + GAP
SLOTS = NBITS + 2                         # registration + 16 bits + registration
ROW_W = SLOTS * SQ + (SLOTS - 1) * GAP    # 1000
X0 = (W - ROW_W) // 2                     # 140: left edge of the left registration square
Y0 = 608                                  # top of the squares
BAND = 32                                 # black margin around the row
BG = "0x1c2833"
FONT = "C\\:/Windows/Fonts/consolab.ttf"
FLASH_EVERY, FLASH_FRAMES, FLASH_SIZE = 90, 2, 160


def box(x, y, w, h, color, enable=None):
    f = f"drawbox=x={x}:y={y}:w={w}:h={h}:color={color}:t=fill"
    if enable:
        f += f":enable='{enable}'"
    return f


chain = [
    "format=yuv420p",
    box(X0 - BAND, Y0 - BAND, ROW_W + 2 * BAND, SQ + 2 * BAND, "black"),
    box(X0, Y0, SQ, SQ, "white"),                              # left registration
    box(X0 + (SLOTS - 1) * PITCH, Y0, SQ, SQ, "white"),        # right registration
]
for k in range(NBITS):                    # bit k sits in slot 16-k (MSB in slot 1)
    slot = NBITS - k
    chain.append(box(X0 + slot * PITCH, Y0, SQ, SQ, "white", f"mod(floor(n/{2 ** k}),2)"))
chain += [
    f"drawtext=fontfile='{FONT}':text='%{{eif\\:n\\:d\\:4}}':fontsize=260:fontcolor=white"
    ":x=(w-text_w)/2:y=150",
    f"drawtext=fontfile='{FONT}':text='%{{pts\\:hms}}':fontsize=40:fontcolor=0xa0b0c0"
    ":x=(w-text_w)/2:y=440",
    box(0, 0, FLASH_SIZE, FLASH_SIZE, "white", f"lt(mod(n,{FLASH_EVERY}),{FLASH_FRAMES})"),
]

period = FLASH_EVERY / FPS
cmd = [
    "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
    "-f", "lavfi", "-i", f"color=c={BG}:s={W}x{H}:r={FPS}:d={DUR}",
    "-f", "lavfi", "-i", f"aevalsrc=exprs='0.5*sin(2*PI*1000*t)*lt(mod(t,{period}),0.03)'"
                         f":s=48000:d={DUR}",
    "-vf", ",".join(chain),
    "-c:v", "libx264", "-preset", "slow", "-crf", "16", "-pix_fmt", "yuv420p", "-g", "60",
    "-c:a", "aac", "-b:a", "128k", "-t", str(DUR), "-movflags", "+faststart", str(OUT),
]
subprocess.run(cmd, check=True)
print(f"wrote {OUT}")
