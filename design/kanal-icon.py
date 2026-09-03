#!/usr/bin/env python3
"""Kanal brand assets — single source of truth.

Geometry lives here, not in a binary. Everything shipped (SVG, .icns, .ico,
favicon) is generated from the tables below, so the icon can never drift
between platforms. Re-run after editing:

    python3 design/kanal-icon.py

That includes the logos, transparent splash mark, and favicon inlined into
web/index.html and docs/index.html — this script rewrites both pages in place,
so there is nothing to paste by hand. Every artefact is emitted on every
platform. Modern ICNS chunks contain ordinary PNG data, so the macOS container
is deterministic too and needs no platform-specific iconutil pass.

No third-party dependencies: the mark is built from round-ended vector strokes,
so it is cheaper and more reproducible to rasterise those directly than to take
on an SVG or image-generation toolchain.

The mark: three voices converge on one room, then leave as three readable
language streams. It takes only the flow idea from the original visual brief:
flat geometry, no copied arrows, dimensional effects, or lettering. Ink and
paper keep the brand distinct from rust/ochre/pine, whose one job in the product
is speaker identity (.impeccable.md).
"""

import math
import os
import re
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# --- palette ---------------------------------------------------------------
# Warm paper, not the interface's cool #FCFCFD: in a Dock full of colour a
# blue-white tile reads as cold and unfinished.
PAPER = (0xF5, 0xF0, 0xE6)
INK = (0x11, 0x1A, 0x21)

# --- geometry, in a 512 design grid ----------------------------------------
# A three-to-one-to-three route: many voices share one room, then every reader
# gets one language. Each tuple is (x1, y1, x2, y2, width, colour). Rounded
# strokes survive 16 px without the little arrowheads and crossings in the
# reference collapsing into noise.
FULL = [
    (82, 168, 168, 168, 30, INK),
    (82, 256, 246, 256, 30, INK),
    (82, 344, 168, 344, 30, INK),
    (168, 168, 246, 256, 30, INK),
    (168, 344, 246, 256, 30, INK),
    (246, 256, 266, 256, 30, INK),
    (266, 256, 344, 168, 30, INK),
    (266, 256, 344, 344, 30, INK),
    (344, 168, 430, 168, 30, INK),
    (266, 256, 430, 256, 30, INK),
    (344, 344, 430, 344, 30, INK),
]

# Below 64 px the full junctions become soft. The compact mark fattens the
# strokes and lets the middle route carry straight through the shared room.
COMPACT = [
    (88, 176, 174, 176, 40, INK),
    (88, 256, 424, 256, 40, INK),
    (88, 336, 174, 336, 40, INK),
    (174, 176, 246, 256, 40, INK),
    (174, 336, 246, 256, 40, INK),
    (266, 256, 338, 176, 40, INK),
    (266, 256, 338, 336, 40, INK),
    (338, 176, 424, 176, 40, INK),
    (338, 336, 424, 336, 40, INK),
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


def _stroke_sd(px, py, x1, y1, x2, y2, width):
    """Signed distance from a point to a round-ended line segment."""
    vx = x2 - x1
    vy = y2 - y1
    length2 = vx * vx + vy * vy
    if length2 == 0:
        return math.hypot(px - x1, py - y1) - width / 2.0
    t = max(0.0, min(1.0, ((px - x1) * vx + (py - y1) * vy) / length2))
    return math.hypot(px - (x1 + t * vx), py - (y1 + t * vy)) - width / 2.0


def render(size, inset=1.0, background=True, shapes=None):
    """Rasterise the icon to a straight-alpha RGBA buffer."""
    shapes = shapes or geometry_for(size)
    tile = size * inset
    off = (size - tile) / 2.0
    scale = tile / GRID
    radius = tile * SQUIRCLE

    buf = bytearray(size * size * 4)

    # Background tile first, then composite each route over it.
    if background:
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

    for gx1, gy1, gx2, gy2, gwidth, colour in shapes:
        x1 = off + gx1 * scale
        y1 = off + gy1 * scale
        x2 = off + gx2 * scale
        y2 = off + gy2 * scale
        width = gwidth * scale
        pad = width / 2.0 + 1
        left = max(0, int(math.floor(min(x1, x2) - pad)))
        top = max(0, int(math.floor(min(y1, y2) - pad)))
        right = min(size, int(math.ceil(max(x1, x2) + pad)))
        bottom = min(size, int(math.ceil(max(y1, y2) + pad)))
        for py in range(top, bottom):
            yc = py + 0.5
            row = py * size * 4
            for px in range(left, right):
                a = _coverage(_stroke_sd(px + 0.5, yc, x1, y1, x2, y2, width))
                if a <= 0.0:
                    continue
                i = row + px * 4
                da = buf[i + 3] / 255.0
                oa = a + da * (1 - a)
                for c in range(3):
                    buf[i + c] = int(
                        (colour[c] * a + buf[i + c] * da * (1 - a)) / oa + 0.5
                    )
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


# --- ICNS ------------------------------------------------------------------


def icns_bytes(entries):
    """Build a modern macOS icon container from (size, RGBA) entries."""
    chunk_types = {
        16: b"icp4",
        32: b"icp5",
        64: b"icp6",
        128: b"ic07",
        256: b"ic08",
        512: b"ic09",
        1024: b"ic10",
    }
    chunks = []
    for size, rgba in entries:
        payload = png_bytes(size, rgba)
        chunks.append(chunk_types[size] + struct.pack(">I", len(payload) + 8) + payload)
    body = b"".join(chunks)
    return b"icns" + struct.pack(">I", len(body) + 8) + body


# --- SVG -------------------------------------------------------------------


def _hex(c):
    return "#%02X%02X%02X" % c


def mark_elements(shapes):
    """SVG elements for the shared route geometry."""
    return "".join(
        '\n  <line x1="%s" y1="%s" x2="%s" y2="%s" stroke="%s" '
        'stroke-width="%s" stroke-linecap="round"/>'
        % (_n(x1), _n(y1), _n(x2), _n(y2), _hex(colour), _n(width))
        for x1, y1, x2, y2, width, colour in shapes
    )


def svg_source(shapes):
    """The app icon: a paper tile and the shared vector mark."""
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" '
        'width="512" height="512" role="img" aria-label="Kanal">',
        '\n  <rect width="512" height="512" rx="%s" fill="%s"/>'
        % (_n(GRID * SQUIRCLE), _hex(PAPER)),
    ]
    parts.append(mark_elements(shapes))
    parts.append("\n</svg>\n")
    return "".join(parts)


WORDMARK_PATH = (
    "M350 82H382V149L450 82H494L414 158L500 238H454L382 168V238H350Z "
    "M515 238L574 82H612L671 238H635L622 199H564L551 238H515Z "
    "M575 167H611L593 112L575 167Z "
    "M690 238V82H725L800 181V82H834V238H800L724 139V238H690Z "
    "M852 238L911 82H949L1008 238H972L959 199H901L888 238H852Z "
    "M912 167H948L930 112L912 167Z "
    "M1027 82H1061V207H1136V238H1027V82Z"
)


def logo_source(with_tagline=False):
    """Horizontal logo with an outlined geometric wordmark and optional slogan."""
    height = 390 if with_tagline else 320
    title = "Kanal — One room. Every language." if with_tagline else "Kanal"
    parts = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 %d" '
        'width="1200" height="%d" role="img" aria-label="%s">' % (height, height, title),
        '\n  <rect x="24" y="32" width="256" height="256" rx="57.267" fill="%s"/>' % _hex(PAPER),
        '\n  <g transform="translate(24 32) scale(.5)">%s\n  </g>' % mark_elements(FULL),
        '\n  <path d="%s" fill="%s" fill-rule="evenodd"/>' % (WORDMARK_PATH, _hex(INK)),
    ]
    if with_tagline:
        parts.append(
            '\n  <text x="350" y="302" fill="%s" font-family="Helvetica Neue,Helvetica,Arial,sans-serif" '
            'font-size="26" font-weight="500" letter-spacing="1.2">One room. Every language.</text>'
            % _hex(INK)
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
    for x1, y1, x2, y2, width, colour in shapes:
        svg += "<line x1='%s' y1='%s' x2='%s' y2='%s' stroke='%s' stroke-width='%s' stroke-linecap='round'/>" % (
            _n(x1),
            _n(y1),
            _n(x2),
            _n(y2),
            _hex(colour),
            _n(width),
        )
    svg += "</svg>"
    encoded = svg.replace("%", "%25").replace("<", "%3C").replace(">", "%3E")
    encoded = encoded.replace("#", "%23").replace('"', "%22")
    return "data:image/svg+xml," + encoded


# --- HTML ------------------------------------------------------------------

# Matched and rewritten in place, on bytes: the two pages are checked out with
# CRLF on Windows but stored with LF, and they must stay byte-identical to each
# other. Touching only the characters inside the line leaves every terminator —
# and every other byte of the file — exactly as it was found.
FAVICON_LINK = re.compile(rb'<link rel="icon" href="data:image/svg\+xml,[^"]*">')

# The pages the favicon must be kept in step with. docs/ is the GitHub Pages
# copy of web/ and CI asserts the two are byte-identical, so both are rewritten
# from the same string in the same run rather than copied by hand.
HTML_PAGES = [("web", "index.html"), ("docs", "index.html")]


def write_favicon_into_pages(uri, log):
    """Replace the inlined favicon in every page. Raises if one has drifted."""
    link = ('<link rel="icon" href="%s">' % uri).encode()
    for parts in HTML_PAGES:
        path = os.path.join(ROOT, *parts)
        with open(path, "rb") as fh:
            before = fh.read()
        after, n = FAVICON_LINK.subn(link, before)
        if n != 1:
            # Never leave one page stale while the other is current: a silent
            # skip here is the exact failure CI's byte-identity check cannot see,
            # because it compares the two pages to each other, not to this file.
            raise SystemExit(
                "%s: expected exactly 1 <link rel=\"icon\"> to rewrite, found %d"
                % (os.path.relpath(path, ROOT), n)
            )
        if after == before:
            log("  %-52s unchanged" % os.path.relpath(path, ROOT))
            continue
        with open(path, "wb") as fh:
            fh.write(after)
        log("  %-52s updated" % os.path.relpath(path, ROOT))


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
    write(os.path.join(design, "kanal-logo-horizontal.svg"), logo_source().encode())
    write(os.path.join(design, "kanal-logo-lockup.svg"), logo_source(with_tagline=True).encode())

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
    # Keep the conventional iconset as an inspectable intermediate, while the
    # checked-in ICNS is assembled directly from the same cached pixels.
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

    icns = os.path.join(design, "kanal.icns")
    write(
        icns,
        icns_bytes(
            [(size, rgba(size, MACOS_INSET)) for size in (16, 32, 64, 128, 256, 512, 1024)]
        ),
    )

    print("\n.ico + PNG")
    ico = ico_bytes([(s, rgba(s)) for s in (16, 24, 32, 48, 64, 128, 256)])
    write(os.path.join(assets, "kanal.ico"), ico)
    write(os.path.join(design, "kanal-icon-1024.png"), png_bytes(1024, rgba(1024)))
    write(
        os.path.join(assets, "kanal-splash-mark.png"),
        png_bytes(512, render(512, background=False, shapes=FULL)),
    )

    # The mobile page must stay a single self-contained file, so its favicon is
    # inlined rather than fetched (.impeccable.md: no external assets at load).
    # COMPACT, not FULL: a tab favicon is only ever rasterised at 16–32 px, where
    # heavier strokes preserve the three-to-one-to-three reading.
    print("\ninline favicon")
    uri = favicon_data_uri(COMPACT)
    write(os.path.join(design, "favicon-datauri.txt"), (uri + "\n").encode())
    write_favicon_into_pages(uri, print)


if __name__ == "__main__":
    main()
