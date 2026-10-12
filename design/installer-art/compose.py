"""Builds the two WixUI bitmaps from panel-art.png and the app icon tile.

    python design/installer-art/compose.py

Writes installers/windows/Dialog.bmp (493x312) and Banner.bmp (493x58). WixUI draws the
welcome and finish text over the dialog bitmap right of x=164, so that part stays plain white.
panel-art.png keeps its generated indigo/apricot colours; recolor() maps them to the silver,
charcoal and purple of design/icon/kanal-tile-light.png.
"""
import colorsys
from pathlib import Path
from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
TILE = ROOT / "design" / "icon" / "kanal-tile-light.png"
OUT = ROOT / "installers" / "windows"
WHITE = (255, 255, 255, 255)

PURPLE = colorsys.rgb_to_hsv(114 / 255, 103 / 255, 183 / 255)
APRICOT = colorsys.rgb_to_hsv(234 / 255, 166 / 255, 106 / 255)
SILVER_HUE, SILVER_SAT = 240 / 360, 0.025


def recolor(img):
    src = img.load()
    out = img.copy()
    dst = out.load()
    for y in range(img.height):
        for x in range(img.width):
            h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in src[x, y]))
            deg = h * 360
            if 10 <= deg <= 50 and s > 0.2:
                h, s, v = PURPLE[0], min(1, s * PURPLE[1] / APRICOT[1]), v * PURPLE[2] / APRICOT[2]
            elif s < 0.2 and v > 0.8:
                h, s = SILVER_HUE, SILVER_SAT
            elif 200 <= deg <= 260 and s > 0.2:
                s *= 0.08
            dst[x, y] = tuple(round(c * 255) for c in colorsys.hsv_to_rgb(h, s, v))
    return out


def tile(size):
    t = Image.open(TILE).convert("RGBA")
    t.thumbnail((size, size), Image.LANCZOS)
    return t


def cover(img, w, h):
    iw, ih = img.size
    if iw / ih > w / h:
        nw = round(ih * w / h)
        img = img.crop(((iw - nw) // 2, 0, (iw - nw) // 2 + nw, ih))
    else:
        nh = round(iw * h / w)
        img = img.crop((0, (ih - nh) // 2, iw, (ih - nh) // 2 + nh))
    return img.resize((w, h), Image.LANCZOS)


def dialog():
    art = cover(Image.open(HERE / "panel-art.png").convert("RGB"), 164 * 2, 312 * 2)
    panel = recolor(art).resize((164, 312), Image.LANCZOS)
    d = Image.new("RGBA", (493, 312), WHITE)
    d.alpha_composite(panel.convert("RGBA"))
    d.alpha_composite(tile(76), ((164 - 76) // 2, 34))
    return d


def banner():
    b = Image.new("RGBA", (493, 58), WHITE)
    t = tile(44)
    b.alpha_composite(t, (493 - 7 - t.width, (58 - t.height) // 2))
    return b


if __name__ == "__main__":
    dialog().convert("RGB").save(OUT / "Dialog.bmp")
    banner().convert("RGB").save(OUT / "Banner.bmp")
    print("wrote", OUT / "Dialog.bmp", OUT / "Banner.bmp")
