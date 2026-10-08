"""Draw the HomePodCast icon (a speaker with sound waves on a blue tile) and write every size the app uses.

    python tools/make_icon.py            # src/app.ico + extension/icons/*.png
    python tools/make_icon.py --png out.png --size 512

Each size is drawn on its own (8x supersampled, then downscaled), and 16/20 px use a simpler shape with one
thick wave so the tray icon stays legible. The geometry is on a 64-unit grid. Needs Pillow.
"""
import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw

BLUE = (37, 99, 235, 255)       # #2563EB
WHITE = (255, 255, 255, 255)
WAVE2 = (201, 216, 250, 255)    # white at 75% over the blue
SS = 8                          # supersampling

ROOT = Path(__file__).resolve().parent.parent
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
EXTENSION_SIZES = [16, 32, 48, 128]


def _wave(d, u, cx, cy, r, half_angle, width, color):
    """An arc centred on (cx, cy) with radius r (to the middle of the stroke), opening to the right, round caps."""
    ro = r + width / 2
    d.arc([u(cx - ro), u(cy - ro), u(cx + ro), u(cy + ro)], -half_angle, half_angle, fill=color, width=round(u(width)))
    for a in (-half_angle, half_angle):
        x = cx + r * math.cos(math.radians(a))
        y = cy + r * math.sin(math.radians(a))
        d.ellipse([u(x - width / 2), u(y - width / 2), u(x + width / 2), u(y + width / 2)], fill=color)


def draw(size):
    px = size * SS
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def u(v):  # 64-unit grid -> supersampled pixels (whole: fractional rounded rectangles leave a seam)
        return round(v * px / 64)

    d.rounded_rectangle([0, 0, px - 1, px - 1], radius=u(14), fill=BLUE)
    if size <= 20:
        d.rounded_rectangle([u(9), u(11), u(34), u(53)], radius=u(12.5), fill=WHITE)  # a full pill: no middle strip to seam
        d.ellipse([u(21.5 - 5), u(21 - 5), u(21.5 + 5), u(21 + 5)], fill=BLUE)
        _wave(d, u, 36, 32, 15, 52, 8, WHITE)
    else:
        d.rounded_rectangle([u(12), u(13), u(34), u(51)], radius=u(11), fill=WHITE)
        d.ellipse([u(23 - 3.5), u(21 - 3.5), u(23 + 3.5), u(21 + 3.5)], fill=BLUE)
        _wave(d, u, 35.34, 32, 9, 51, 4, WHITE)
        _wave(d, u, 36.05, 32, 17, 50, 4, WAVE2)
    return img.resize((size, size), Image.BOX)  # area average: LANCZOS rings into stripes at 16 px


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--png")
    p.add_argument("--size", type=int, default=512)
    args = p.parse_args()
    if args.png:
        draw(args.size).save(args.png)
        return

    images = [draw(s) for s in ICO_SIZES]
    ico = ROOT / "src" / "app.ico"
    images[-1].save(ico, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=images[:-1])
    print(f"wrote {ico}")

    out = ROOT / "extension" / "icons"
    out.mkdir(exist_ok=True)
    for s in EXTENSION_SIZES:
        draw(s).save(out / f"icon{s}.png")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
