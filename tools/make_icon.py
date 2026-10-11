"""Draw the HomePodCast icon and write every size the app uses.

    python tools/make_icon.py            # src/app.ico + extension/icons/*.png
    python tools/make_icon.py --msix     # packaging/msix/Assets/*.png (the MSIX package's logos)
    python tools/make_icon.py --png out.png --size 512

The masters are docs/icon.svg (24 px and up) and docs/icon-small.svg (16/20 px: one thick white wave, a wider band
under the top). This script draws the same shapes with Pillow, each size on its own (8x supersampled, then
area-averaged down), so no SVG renderer is needed. Keep the numbers below in step with the SVGs.
"""
import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw

BLUE = (0x1A, 0x63, 0xF5, 255)   # tile, and the band under the speaker's top
WHITE = (255, 255, 255, 255)     # speaker body
LIGHT = (0xC2, 0xDA, 0xFC, 255)  # speaker top and the waves
SS = 8                           # supersampling
TILE_RADIUS = 12

ROOT = Path(__file__).resolve().parent.parent
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
EXTENSION_SIZES = [16, 32, 48, 128]

# MSIX logos (packaging/msix/AppxManifest.xml). The icon logos (Square44x44Logo, StoreLogo) are the rounded tile, like
# the exe's icon; the Start tiles (Square150x150Logo, Wide310x150Logo) are blue edge to edge with the icon's shapes in
# a centered square of TILE_GLYPH times the tile's height. Every asset in every scale; the 44x44 logo also in the
# target sizes Windows asks for in lists, the taskbar and Explorer, plated and unplated (the same picture: the icon
# brings its own plate). makepri indexes these qualified names (build.ps1), so the manifest names only the base name.
MSIX_DIR = ROOT / "packaging" / "msix" / "Assets"
MSIX_SCALES = [100, 125, 150, 200, 400]
MSIX_TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]
TILE_GLYPH = 0.66

# Body: from the lower edge of an ellipse at body_top (that edge shows as the band) down to a rounded bottom.
FULL = dict(left=9, right=35, body_top=17.3, bottom=48, ry=4.5, top_cy=16,
            waves=[(43.6, 23.7, 40.3, 13.5, 4.2, LIGHT), (48.8, 18.3, 45.7, 20.9, 4.2, LIGHT)])
SMALL = dict(left=9, right=35, body_top=19, bottom=48, ry=5, top_cy=16,
             waves=[(48.4, 21.5, 42.5, 17, 8, WHITE)])


def _wave(d, u, x, y1, y2, r, width, color):
    """The SVG arc from (x, y1) to (x, y2) with radius r bulging right, as a round-capped stroke."""
    half = (y2 - y1) / 2
    cy = (y1 + y2) / 2
    cx = x - math.sqrt(r * r - half * half)
    angle = math.degrees(math.asin(half / r))
    ro = r + width / 2
    d.arc([u(cx - ro), u(cy - ro), u(cx + ro), u(cy + ro)], -angle, angle, fill=color, width=u(width))
    for y in (y1, y2):
        d.ellipse([u(x - width / 2), u(y - width / 2), u(x + width / 2), u(y + width / 2)], fill=color)


def draw(size, rounded=True):
    """The icon at size x size; rounded=False fills the whole square with the tile's blue (for the MSIX tiles)."""
    px = size * SS
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def u(v):  # 64-unit grid -> supersampled pixels (whole: fractional shapes leave seams)
        return round(v * px / 64)

    g = SMALL if size <= 20 else FULL
    left, right, ry = g["left"], g["right"], g["ry"]
    if rounded:
        d.rounded_rectangle([0, 0, px - 1, px - 1], radius=u(TILE_RADIUS), fill=BLUE)
    else:
        d.rectangle([0, 0, px - 1, px - 1], fill=BLUE)
    d.rectangle([u(left), u(g["body_top"]), u(right), u(g["bottom"])], fill=WHITE)
    d.ellipse([u(left), u(g["bottom"] - ry), u(right), u(g["bottom"] + ry)], fill=WHITE)
    d.ellipse([u(left), u(g["body_top"] - ry), u(right), u(g["body_top"] + ry)], fill=BLUE)  # the body's curved top
    d.ellipse([u(left), u(g["top_cy"] - ry), u(right), u(g["top_cy"] + ry)], fill=LIGHT)
    for w in g["waves"]:
        _wave(d, u, *w)
    return img.resize((size, size), Image.BOX)  # area average: LANCZOS rings into stripes at 16 px


def tile(width, height):
    """A Start tile: blue edge to edge, the icon's shapes centered in a square TILE_GLYPH of the height."""
    img = Image.new("RGBA", (width, height), BLUE)
    side = math.floor(height * TILE_GLYPH + 0.5)
    img.paste(draw(side, rounded=False), ((width - side) // 2, (height - side) // 2))
    return img


def msix_assets(out):
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("*.png"):
        old.unlink()
    for scale in MSIX_SCALES:
        def s(v):  # the size at this scale, rounded half up like Windows (50 at 125 % -> 63)
            return math.floor(v * scale / 100 + 0.5)
        draw(s(44)).save(out / f"Square44x44Logo.scale-{scale}.png")
        draw(s(50)).save(out / f"StoreLogo.scale-{scale}.png")
        tile(s(150), s(150)).save(out / f"Square150x150Logo.scale-{scale}.png")
        tile(s(310), s(150)).save(out / f"Wide310x150Logo.scale-{scale}.png")
    for size in MSIX_TARGET_SIZES:
        icon = draw(size)
        icon.save(out / f"Square44x44Logo.targetsize-{size}.png")
        icon.save(out / f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
    print(f"wrote {out}")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--png")
    p.add_argument("--size", type=int, default=512)
    p.add_argument("--msix", action="store_true", help="write the MSIX package logos to packaging/msix/Assets")
    args = p.parse_args()
    if args.png:
        draw(args.size).save(args.png)
        return
    if args.msix:
        msix_assets(MSIX_DIR)
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
