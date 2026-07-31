#!/usr/bin/env python3
"""Kanal app icon — single source of truth.

Geometry lives here, not in a binary. Everything shipped (SVG, .icns, .ico,
favicon) is generated from the tables below, so the icon can never drift
between platforms. Re-run after editing:

    python3 design/kanal-icon.py

No third-party dependencies: this box has no rsvg/inkscape/ImageMagick/PIL,
and the icon is nothing but capsules, so it is cheaper to rasterise them
directly than to take on a toolchain.

The mark: a level meter over three lines of translation. Sound in, three
languages out — the thing the tool actually does. Ink is the machine's own
voice; rust/ochre/pine are the three languages, matching the speaker accents
already shared by host and mobile client (.impeccable.md).
"""

import math
import os
import struct
import subprocess
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# --- palette ---------------------------------------------------------------
# Warm paper, not the interface's cool #FCFCFD: in a Dock full of colour a
# blue-white tile reads as cold and unfinished.
PAPER = (0xF5, 0xF0, 0xE6)
INK = (0x11, 0x1A, 0x21)
RUST = (0xB2, 0x3A, 0x2E)
OCHRE = (0x9A, 0x6B, 0x10)
PINE = (0x1C, 0x6B, 0x58)

# --- geometry, in a 512 design grid ----------------------------------------
# (x, y, w, h, colour); every element is a capsule — radius is min(w,h)/2.
FULL = [
    (131, 126, 34, 70, INK),
    (185, 102, 34, 118, INK),
    (239, 85, 34, 152, INK),
    (293, 108, 34, 106, INK),
    (347, 130, 34, 62, INK),
    (146, 276, 220, 38, RUST),
    (172, 334, 168, 38, OCHRE),
    (158, 392, 196, 38, PINE),
]

# Below ~48 px the five meter bars collapse into one grey smear. Three fatter
# bars over three fatter lines keeps the 3-against-3 reading intact.
COMPACT = [
    (148, 106, 52, 96, INK),
    (230, 70, 52, 168, INK),
    (312, 94, 52, 120, INK),
    # Thinner lines than the meter bars, bought back as gap: at 16 px the paper
    # between the three lines is what carries "three languages", and it needs
    # every fraction of a pixel it can get.
    (132, 276, 248, 38, RUST),
    (166, 340, 180, 38, OCHRE),
    (148, 404, 216, 38, PINE),
]

GRID = 512.0
COMPACT_BELOW = 64  # sizes strictly under this use COMPACT
SQUIRCLE = 0.2237  # corner radius as a fraction of the tile — squircle approx.

# macOS ships the tile inset in its canvas (824 pt of 1024) so every Dock icon
# shares an optical size. Windows and the web want it full-bleed.
MACOS_INSET = 824.0 / 1024.0


def geometry_for(size):
    return COMPACT if size < COMPACT_BELOW else FULL


# --- rasteriser ------------------------------------------------------------


def _capsule_sd(px, py, x, y, w, h, r):
    """Signed distance from a pixel centre to a rounded rect. Negative inside."""
    dx = abs(px - (x + w / 2)) - (w / 2 - r)
    dy = abs(py - (y + h / 2)) - (h / 2 - r)
    outside = math.hypot(max(dx, 0.0), max(dy, 0.0))
    return outside + min(max(dx, dy), 0.0) - r


def _coverage(sd):
    """1 px analytic antialiasing band around the edge."""
    if sd <= -0.5:
        return 1.0
    if sd >= 0.5:
        return 0.0
    return 0.5 - sd


def render(size, inset=1.0):
    """Rasterise the icon to a straight-alpha RGBA buffer."""
    shapes = geometry_for(size)
    tile = size * inset
    off = (size - tile) / 2.0
    scale = tile / GRID
    radius = tile * SQUIRCLE

    buf = bytearray(size * size * 4)

    # Background tile first, then composite each capsule over it.
    for py in range(size):
        yc = py + 0.5
        row = py * size * 4
        for px in range(size):
            sd = _capsule_sd(px + 0.5, yc, off, off, tile, tile, radius)
            a = _coverage(sd)
            if a <= 0.0:
                continue
            i = row + px * 4
            buf[i] = PAPER[0]
            buf[i + 1] = PAPER[1]
            buf[i + 2] = PAPER[2]
            buf[i + 3] = int(a * 255 + 0.5)

    for gx, gy, gw, gh, colour in shapes:
        x = off + gx * scale
        y = off + gy * scale
        w = gw * scale
        h = gh * scale
        r = min(w, h) / 2.0
        x0 = max(0, int(math.floor(x - 1)))
        y0 = max(0, int(math.floor(y - 1)))
        x1 = min(size, int(math.ceil(x + w + 1)))
        y1 = min(size, int(math.ceil(y + h + 1)))
        for py in range(y0, y1):
            yc = py + 0.5
            row = py * size * 4
            for px in range(x0, x1):
                a = _coverage(_capsule_sd(px + 0.5, yc, x, y, w, h, r))
                if a <= 0.0:
                    continue
                i = row + px * 4
                da = buf[i + 3] / 255.0
                oa = a + da * (1 - a)
                if oa <= 0.0:
                    continue
                for c in range(3):
                    src = colour[c]
                    dst = buf[i + c]
                    buf[i + c] = int((src * a + dst * da * (1 - a)) / oa + 0.5)
                buf[i + 3] = int(oa * 255 + 0.5)

    return bytes(buf)


# --- PNG -------------------------------------------------------------------


def _chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def png_bytes(size, rgba):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride : (y + 1) * stride]
    out = b"\x89PNG\r\n\x1a\n"
    out += _chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    out += _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    out += _chunk(b"IEND", b"")
    return out


# --- ICO -------------------------------------------------------------------


def _dib_bytes(size, rgba):
    """BITMAPINFOHEADER + bottom-up BGRA + AND mask, for <=48 px entries.

    Windows shells older than Vista cannot read PNG-compressed ICO entries at
    small sizes, and those are exactly the sizes the taskbar uses.
    """
    header = struct.pack(
        "<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0
    )
    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        row = y * size * 4
        for x in range(size):
            i = row + x * 4
            pixels += bytes((rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]))
    mask_stride = ((size + 31) // 32) * 4
    return header + bytes(pixels) + b"\x00" * (mask_stride * size)


def ico_bytes(entries):
    """entries: list of (size, rgba). >=64 px are stored as PNG, rest as DIB."""
    blobs = []
    for size, rgba in entries:
        payload = png_bytes(size, rgba) if size >= 64 else _dib_bytes(size, rgba)
        blobs.append((size, payload))

    out = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    for size, payload in blobs:
        out += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0,
            0,
            1,
            32,
            len(payload),
            offset,
        )
        offset += len(payload)
    return out + b"".join(p for _, p in blobs)


# --- SVG -------------------------------------------------------------------


def _hex(c):
    return "#%02X%02X%02X" % c


def svg_source(shapes):
    """The design drawing: full-bleed, one rect per element, ready to open."""
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" '
        'width="512" height="512" role="img" aria-label="Kanal">',
        '\n  <rect width="512" height="512" rx="%s" fill="%s"/>'
        % (_n(GRID * SQUIRCLE), _hex(PAPER)),
    ]
    for gx, gy, gw, gh, colour in shapes:
        parts.append(
            '\n  <rect x="%s" y="%s" width="%s" height="%s" rx="%s" fill="%s"/>'
            % (_n(gx), _n(gy), _n(gw), _n(gh), _n(min(gw, gh) / 2), _hex(colour))
        )
    parts.append("\n</svg>\n")
    return "".join(parts)


def _n(v):
    """Trim trailing zeros — keeps the inlined data URI short."""
    return ("%.3f" % v).rstrip("0").rstrip(".")


def favicon_data_uri(shapes):
    """A percent-encoded SVG data URI, for inlining into the mobile page.

    Single quotes throughout so the attribute needs no escaping, and only the
    handful of characters that actually must be encoded are — base64 would cost
    a third more for no benefit.
    """
    svg = (
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 512 512'>"
        "<rect width='512' height='512' rx='%s' fill='%s'/>"
        % (_n(GRID * SQUIRCLE), _hex(PAPER))
    )
    for gx, gy, gw, gh, colour in shapes:
        svg += "<rect x='%s' y='%s' width='%s' height='%s' rx='%s' fill='%s'/>" % (
            _n(gx),
            _n(gy),
            _n(gw),
            _n(gh),
            _n(min(gw, gh) / 2),
            _hex(colour),
        )
    svg += "</svg>"
    encoded = svg.replace("%", "%25").replace("<", "%3C").replace(">", "%3E")
    encoded = encoded.replace("#", "%23").replace('"', "%22")
    return "data:image/svg+xml," + encoded


# --- outputs ---------------------------------------------------------------


def main():
    design = os.path.join(ROOT, "design")
    assets = os.path.join(ROOT, "src", "Kanal.Host", "Assets")
    iconset = os.path.join(design, "Kanal.iconset")
    os.makedirs(iconset, exist_ok=True)

    def write(path, data):
        with open(path, "wb") as fh:
            fh.write(data)
        print("  %-52s %6d B" % (os.path.relpath(path, ROOT), len(data)))

    print("SVG")
    write(os.path.join(design, "kanal-icon.svg"), svg_source(FULL).encode())
    write(os.path.join(design, "kanal-icon-compact.svg"), svg_source(COMPACT).encode())

    # Rasterise once per size; both packagers draw from this.
    print("\nraster")
    cache = {}

    def rgba(size, inset=1.0):
        key = (size, inset)
        if key not in cache:
            cache[key] = render(size, inset)
            print("  %d px%s" % (size, "  (macOS inset)" if inset != 1.0 else ""))
        return cache[key]

    print("\n.icns")
    # iconutil wants both @1x and @2x of each nominal size.
    for nominal, suffix, px in [
        (16, "", 16),
        (16, "@2x", 32),
        (32, "", 32),
        (32, "@2x", 64),
        (128, "", 128),
        (128, "@2x", 256),
        (256, "", 256),
        (256, "@2x", 512),
        (512, "", 512),
        (512, "@2x", 1024),
    ]:
        name = "icon_%dx%d%s.png" % (nominal, nominal, suffix)
        write(os.path.join(iconset, name), png_bytes(px, rgba(px, MACOS_INSET)))

    # iconutil is the only supported way to build an .icns. The bundle is a
    # packaging artefact, so it stays in design/ — Assets/ is embedded into the
    # binary by <AvaloniaResource>, and Avalonia only ever reads the .ico.
    icns = os.path.join(design, "kanal.icns")
    if subprocess.call(["iconutil", "-c", "icns", iconset, "-o", icns]) == 0:
        print("  %-52s %6d B" % (os.path.relpath(icns, ROOT), os.path.getsize(icns)))
    else:
        print("  iconutil failed — .icns not rebuilt (macOS only)")

    print("\n.ico + PNG")
    ico = ico_bytes([(s, rgba(s)) for s in (16, 24, 32, 48, 64, 128, 256)])
    write(os.path.join(assets, "kanal.ico"), ico)
    write(os.path.join(design, "kanal-icon-1024.png"), png_bytes(1024, rgba(1024)))

    # The mobile page must stay a single self-contained file, so its favicon is
    # inlined rather than fetched (.impeccable.md: no external assets at load).
    # COMPACT, not FULL: a tab favicon is only ever rasterised at 16–32 px, and
    # the five-bar meter smears at that size even as vector art.
    print("\ninline favicon (paste into web/index.html and docs/index.html)")
    uri = favicon_data_uri(COMPACT)
    write(os.path.join(design, "favicon-datauri.txt"), (uri + "\n").encode())


if __name__ == "__main__":
    main()
