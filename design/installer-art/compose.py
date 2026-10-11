"""Builds the two WixUI bitmaps from panel-art.png and the K mark.

    python design/installer-art/compose.py

Writes installers/windows/Dialog.bmp (493x312) and Banner.bmp (493x58). WixUI draws the
welcome and finish text over the dialog bitmap right of x=164, so that part stays plain white.
"""
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
MARK = ROOT / "design" / "kanal-icon.png"
OUT = ROOT / "installers" / "windows"
WHITE = (255, 255, 255, 255)
SHEET = (255, 252, 247, 255)
SS = 4


def mark(size):
    k = Image.open(MARK).convert("RGBA")
    k = k.crop(k.getbbox())
    k.thumbnail((size, size), Image.LANCZOS)
    return k


def tile(size):
    big = size * SS
    m = Image.new("L", (big, big), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, big - 1, big - 1], radius=big // 5, fill=255)
    t = Image.new("RGBA", (size, size), SHEET[:3] + (0,))
    t.putalpha(m.resize((size, size), Image.LANCZOS))
    k = mark(int(size * 0.66))
    t.alpha_composite(k, ((size - k.width) // 2, (size - k.height) // 2))
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
    d = Image.new("RGBA", (493, 312), WHITE)
    d.alpha_composite(cover(Image.open(HERE / "panel-art.png").convert("RGBA"), 164, 312))
    d.alpha_composite(tile(76), ((164 - 76) // 2, 34))
    return d


def banner():
    b = Image.new("RGBA", (493, 58), WHITE)
    k = mark(40)
    b.alpha_composite(k, (493 - 9 - k.width, (58 - k.height) // 2))
    return b


if __name__ == "__main__":
    dialog().convert("RGB").save(OUT / "Dialog.bmp")
    banner().convert("RGB").save(OUT / "Banner.bmp")
    print("wrote", OUT / "Dialog.bmp", OUT / "Banner.bmp")
