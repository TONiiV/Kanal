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

No third-party dependencies: the reference-led mark is stored as cubic vector
routes and filled arrowheads, then rasterised by the same pure-Python geometry.
The five incoming streams and three outgoing arrows are redrawn rather than
auto-traced, so paper texture and antialiasing noise from the reference can
never enter a shipped asset.

The display wordmark is an outlined, spacing-adjusted extraction of the closest
installed match, Century Gothic Bold. Only the five glyph outlines are stored;
the proprietary font file is neither bundled nor needed to regenerate assets.
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
GREEN = (0x1C, 0x6B, 0x58)
OCHRE = (0x9A, 0x6B, 0x10)
RUST = (0xB2, 0x3A, 0x2E)

# --- geometry, in a 512 design grid ----------------------------------------
# Each route is (colour, width, start, commands, optional arrow polygon).
# L = line endpoint; C = cubic control1, control2, endpoint. The route order is
# the paint order: ochre crossings first, then red/black, and the dominant green
# pass last, matching the reference's visible over/under hierarchy.
FULL = [
    (OCHRE, 28, (82, 238), [("L", (154, 238)), ("C", (207, 238, 224, 275, 280, 275))], None),
    (OCHRE, 28, (82, 388), [("L", (159, 388)), ("C", (232, 388, 232, 256, 306, 256))], None),
    (RUST, 28, (82, 315), [("L", (151, 315)), ("C", (205, 315, 218, 246, 273, 246)),
                           ("C", (309, 246, 326, 323, 377, 323))],
     [(377, 291), (433, 323), (377, 355)]),
    (INK, 28, (82, 92), [("L", (165, 92)), ("C", (218, 92, 214, 169, 279, 169)),
                          ("C", (309, 169, 321, 145, 372, 145))],
     [(372, 113), (428, 145), (372, 177)]),
    (GREEN, 28, (82, 164), [("L", (157, 164)), ("C", (213, 164, 218, 246, 294, 252)),
                             ("L", (391, 252))],
     [(391, 208), (455, 252), (391, 296)]),
]

# At favicon size the two ochre feeder routes disappear; three coloured output
# routes remain, with heavier strokes and arrowheads large enough to rasterise.
COMPACT = [
    (INK, 42, (78, 134), [("L", (176, 134)), ("C", (230, 134, 229, 190, 302, 190)),
                           ("L", (365, 190))], [(365, 151), (434, 190), (365, 229)]),
    (GREEN, 42, (78, 256), [("L", (374, 256))], [(374, 212), (448, 256), (374, 300)]),
    (RUST, 42, (78, 378), [("L", (176, 378)), ("C", (230, 378, 229, 322, 302, 322)),
                            ("L", (365, 322))], [(365, 283), (434, 322), (365, 361)]),
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


def _route_segments(route):
    """Flatten one line/cubic route into short segments for the rasteriser."""
    _, width, start, commands, _ = route
    current = start
    segments = []
    for kind, values in commands:
        if kind == "L":
            target = values
            segments.append((*current, *target, width))
            current = target
            continue
        if kind != "C":
            raise ValueError("unknown route command %s" % kind)
        x0, y0 = current
        x1, y1, x2, y2, x3, y3 = values
        previous = current
        for step in range(1, 25):
            t = step / 24.0
            u = 1.0 - t
            target = (
                u ** 3 * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t ** 3 * x3,
                u ** 3 * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t ** 3 * y3,
            )
            segments.append((*previous, *target, width))
            previous = target
        current = (x3, y3)
    return segments


def _inside_polygon(px, py, points):
    inside = False
    previous = points[-1]
    for current in points:
        x1, y1 = previous
        x2, y2 = current
        if (y1 > py) != (y2 > py):
            crossing = (x2 - x1) * (py - y1) / (y2 - y1) + x1
            if px < crossing:
                inside = not inside
        previous = current
    return inside


def _polygon_coverage(px, py, points):
    samples = ((-0.25, -0.25), (0.25, -0.25), (-0.25, 0.25), (0.25, 0.25))
    return sum(_inside_polygon(px + dx, py + dy, points) for dx, dy in samples) / 4.0


def _composite(buf, index, colour, alpha):
    if alpha <= 0.0:
        return
    destination_alpha = buf[index + 3] / 255.0
    output_alpha = alpha + destination_alpha * (1 - alpha)
    for channel in range(3):
        buf[index + channel] = int(
            (colour[channel] * alpha + buf[index + channel] * destination_alpha * (1 - alpha))
            / output_alpha
            + 0.5
        )
    buf[index + 3] = int(output_alpha * 255 + 0.5)


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

    for route in shapes:
        colour, gwidth, start, _, arrow = route
        width = gwidth * scale
        for segment_index, (gx1, gy1, gx2, gy2, _) in enumerate(_route_segments(route)):
            if segment_index == 0:
                length = math.hypot(gx2 - gx1, gy2 - gy1)
                gx1 += (gx2 - gx1) / length * gwidth / 2.0
                gy1 += (gy2 - gy1) / length * gwidth / 2.0
            x1 = off + gx1 * scale
            y1 = off + gy1 * scale
            x2 = off + gx2 * scale
            y2 = off + gy2 * scale
            pad = width / 2.0 + 1
            left = max(0, int(math.floor(min(x1, x2) - pad)))
            top = max(0, int(math.floor(min(y1, y2) - pad)))
            right = min(size, int(math.ceil(max(x1, x2) + pad)))
            bottom = min(size, int(math.ceil(max(y1, y2) + pad)))
            for py in range(top, bottom):
                yc = py + 0.5
                row = py * size * 4
                for px in range(left, right):
                    alpha = _coverage(_stroke_sd(px + 0.5, yc, x1, y1, x2, y2, width))
                    _composite(buf, row + px * 4, colour, alpha)

        # The reference has squared input ends; cover the first round cap with
        # a small rectangle, while joints stay round and clean.
        sx, sy = start
        cap = [(sx, sy - gwidth / 2), (sx + gwidth / 2, sy - gwidth / 2),
               (sx + gwidth / 2, sy + gwidth / 2), (sx, sy + gwidth / 2)]
        polygons = [cap] + ([arrow] if arrow else [])
        for polygon in polygons:
            points = [(off + x * scale, off + y * scale) for x, y in polygon]
            left = max(0, int(math.floor(min(x for x, _ in points) - 1)))
            top = max(0, int(math.floor(min(y for _, y in points) - 1)))
            right = min(size, int(math.ceil(max(x for x, _ in points) + 1)))
            bottom = min(size, int(math.ceil(max(y for _, y in points) + 1)))
            for py in range(top, bottom):
                row = py * size * 4
                for px in range(left, right):
                    alpha = _polygon_coverage(px + 0.5, py + 0.5, points)
                    _composite(buf, row + px * 4, colour, alpha)

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
    """SVG elements for the reference-led route geometry."""
    elements = []
    for colour, width, start, commands, arrow in shapes:
        data = "M%s %s" % (_n(start[0]), _n(start[1]))
        for kind, values in commands:
            data += " %s%s" % (kind, " ".join(_n(value) for value in values))
        elements.append(
            '\n  <path d="%s" fill="none" stroke="%s" stroke-width="%s" '
            'stroke-linecap="butt" stroke-linejoin="round"/>'
            % (data, _hex(colour), _n(width))
        )
        if arrow:
            arrow_data = "M%s Z" % " L".join("%s %s" % (_n(x), _n(y)) for x, y in arrow)
            elements.append('\n  <path d="%s" fill="%s"/>' % (arrow_data, _hex(colour)))
    return "".join(elements)


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
    "M140 1508H413V639L815 1088H1157L689 566L1214 0H875L413 501V0H140Z"
    "M2130 1088H2402V0H2130V115Q2050 39 1969.5 5.5Q1889 -28 1795 -28Q1584 -28 1430 135.5Q1276 299 1276 542Q1276 794 1425 955Q1574 1116 1787 1116Q1885 1116 1971 1079Q2057 1042 2130 968ZM1843 864Q1716 864 1632 774.5Q1548 685 1548 545Q1548 404 1633.5 313Q1719 222 1844 222Q1973 222 2058 311.5Q2143 401 2143 546Q2143 688 2058 776Q1973 864 1843 864Z"
    "M2678 1088H2950V977Q3043 1055 3118.5 1085.5Q3194 1116 3273 1116Q3435 1116 3548 1003Q3643 907 3643 719V0H3373V477Q3373 672 3355.5 736Q3338 800 3294.5 833.5Q3251 867 3187 867Q3104 867 3044.5 811.5Q2985 756 2962 658Q2950 607 2950 437V0H2678Z"
    "M4711 1088H4983V0H4711V115Q4631 39 4550.5 5.5Q4470 -28 4376 -28Q4165 -28 4011 135.5Q3857 299 3857 542Q3857 794 4006 955Q4155 1116 4368 1116Q4466 1116 4552 1079Q4638 1042 4711 968ZM4424 864Q4297 864 4213 774.5Q4129 685 4129 545Q4129 404 4214.5 313Q4300 222 4425 222Q4554 222 4639 311.5Q4724 401 4724 546Q4724 688 4639 776Q4554 864 4424 864Z"
    "M5230 1508H5503V0H5230Z"
)

# Outlined at the same source font, so the lockup is portable and deterministic.
# The text remains in aria-label/metadata for accessibility and exact-copy tests.
TAGLINE_PATH = (
    "M863 1508Q1175 1508 1399.5 1282Q1624 1056 1624 731Q1624 409 1402.5 186Q1181 -37 865 -37Q534 -37 315 192Q96 421 96 736Q96 947 198 1124Q300 1301 478.5 1404.5Q657 1508 863 1508ZM860 1234Q656 1234 517 1092Q378 950 378 731Q378 487 553 345Q689 234 865 234Q1064 234 1204 378Q1344 522 1344 733Q1344 943 1203 1088.5Q1062 1234 860 1234Z"
    "M1858 1088H2130V977Q2223 1055 2298.5 1085.5Q2374 1116 2453 1116Q2615 1116 2728 1003Q2823 907 2823 719V0H2553V477Q2553 672 2535.5 736Q2518 800 2474.5 833.5Q2431 867 2367 867Q2284 867 2224.5 811.5Q2165 756 2142 658Q2130 607 2130 437V0H1858Z"
    "M4177 465H3300Q3319 349 3401.5 280.5Q3484 212 3612 212Q3765 212 3875 319L4105 211Q4019 89 3899 30.5Q3779 -28 3614 -28Q3358 -28 3197 133.5Q3036 295 3036 538Q3036 787 3196.5 951.5Q3357 1116 3599 1116Q3856 1116 4017 951.5Q4178 787 4178 517ZM3903 680Q3876 771 3796.5 828Q3717 885 3612 885Q3498 885 3412 821Q3358 781 3312 680Z"
    "M4907 1088H5141V951Q5179 1032 5242 1074Q5305 1116 5380 1116Q5433 1116 5491 1088L5406 853Q5358 877 5327 877Q5264 877 5220.5 799Q5177 721 5177 493L5178 440V0H4907Z"
    "M6137 1116Q6291 1116 6426.5 1039Q6562 962 6638 830Q6714 698 6714 545Q6714 391 6637.5 257Q6561 123 6429 47.5Q6297 -28 6138 -28Q5904 -28 5738.5 138.5Q5573 305 5573 543Q5573 798 5760 968Q5924 1116 6137 1116ZM6141 859Q6014 859 5929.5 770.5Q5845 682 5845 544Q5845 402 5928.5 314Q6012 226 6140 226Q6268 226 6353 315Q6438 404 6438 544Q6438 684 6354.5 771.5Q6271 859 6141 859Z"
    "M7448 1116Q7602 1116 7737.5 1039Q7873 962 7949 830Q8025 698 8025 545Q8025 391 7948.5 257Q7872 123 7740 47.5Q7608 -28 7449 -28Q7215 -28 7049.5 138.5Q6884 305 6884 543Q6884 798 7071 968Q7235 1116 7448 1116ZM7452 859Q7325 859 7240.5 770.5Q7156 682 7156 544Q7156 402 7239.5 314Q7323 226 7451 226Q7579 226 7664 315Q7749 404 7749 544Q7749 684 7665.5 771.5Q7582 859 7452 859Z"
    "M8246 1088H8520V963Q8590 1040 8675.5 1078Q8761 1116 8862 1116Q8964 1116 9046 1066Q9128 1016 9178 920Q9243 1016 9337.5 1066Q9432 1116 9544 1116Q9660 1116 9748 1062Q9836 1008 9874.5 921Q9913 834 9913 638V0H9638V552Q9638 737 9592 802.5Q9546 868 9454 868Q9384 868 9328.5 828Q9273 788 9246 717.5Q9219 647 9219 491V0H8944V527Q8944 673 8922.5 738.5Q8901 804 8858 836Q8815 868 8754 868Q8686 868 8630.5 827.5Q8575 787 8547.5 714Q8520 641 8520 484V0H8246Z"
    "M10322 285Q10387 285 10433 239.5Q10479 194 10479 129Q10479 64 10433 18Q10387 -28 10322 -28Q10257 -28 10211 18Q10165 64 10165 129Q10165 194 10211 239.5Q10257 285 10322 285Z"
    "M11350 1471H12153V1197H11628V931H12153V662H11628V275H12153V0H11350Z"
    "M12267 1088H12545L12820 446L13094 1088H13371L12908 0H12731Z"
    "M14621 465H13744Q13763 349 13845.5 280.5Q13928 212 14056 212Q14209 212 14319 319L14549 211Q14463 89 14343 30.5Q14223 -28 14058 -28Q13802 -28 13641 133.5Q13480 295 13480 538Q13480 787 13640.5 951.5Q13801 1116 14043 1116Q14300 1116 14461 951.5Q14622 787 14622 517ZM14347 680Q14320 771 14240.5 828Q14161 885 14056 885Q13942 885 13856 821Q13802 781 13756 680Z"
    "M14778 1088H15012V951Q15050 1032 15113 1074Q15176 1116 15251 1116Q15304 1116 15362 1088L15277 853Q15229 877 15198 877Q15135 877 15091.5 799Q15048 721 15048 493L15049 440V0H14778Z"
    "M15376 1088H15655L15938 405L16250 1088H16530L15848 -398H15566L15790 81Z"
    "M17229 1508H17502V0H17229Z"
    "M18553 1088H18825V0H18553V115Q18473 39 18392.5 5.5Q18312 -28 18218 -28Q18007 -28 17853 135.5Q17699 299 17699 542Q17699 794 17848 955Q17997 1116 18210 1116Q18308 1116 18394 1079Q18480 1042 18553 968ZM18266 864Q18139 864 18055 774.5Q17971 685 17971 545Q17971 404 18056.5 313Q18142 222 18267 222Q18396 222 18481 311.5Q18566 401 18566 546Q18566 688 18481 776Q18396 864 18266 864Z"
    "M19101 1088H19373V977Q19466 1055 19541.5 1085.5Q19617 1116 19696 1116Q19858 1116 19971 1003Q20066 907 20066 719V0H19796V477Q19796 672 19778.5 736Q19761 800 19717.5 833.5Q19674 867 19610 867Q19527 867 19467.5 811.5Q19408 756 19385 658Q19373 607 19373 437V0H19101Z"
    "M21134 1088H21406V156Q21406 -120 21295 -250Q21146 -426 20846 -426Q20686 -426 20577 -386Q20468 -346 20393 -268.5Q20318 -191 20282 -80H20583Q20623 -126 20686 -149.5Q20749 -173 20835 -173Q20945 -173 21012 -139Q21079 -105 21106.5 -51Q21134 3 21134 135Q21062 63 20983 31.5Q20904 0 20804 0Q20585 0 20434 158Q20283 316 20283 558Q20283 817 20443 974Q20588 1116 20789 1116Q20883 1116 20966.5 1081.5Q21050 1047 21134 968ZM20851 861Q20722 861 20638 774.5Q20554 688 20554 557Q20554 421 20640 334Q20726 247 20856 247Q20983 247 21065.5 332Q21148 417 21148 555Q21148 691 21065 776Q20982 861 20851 861Z"
    "M21681 1088H21957V564Q21957 411 21978 351.5Q21999 292 22045.5 259Q22092 226 22160 226Q22228 226 22275.5 258.5Q22323 291 22346 354Q22363 401 22363 555V1088H22637V627Q22637 342 22592 237Q22537 109 22430 40.5Q22323 -28 22158 -28Q21979 -28 21868.5 52Q21758 132 21713 275Q21681 374 21681 635Z"
    "M23715 1088H23987V0H23715V115Q23635 39 23554.5 5.5Q23474 -28 23380 -28Q23169 -28 23015 135.5Q22861 299 22861 542Q22861 794 23010 955Q23159 1116 23372 1116Q23470 1116 23556 1079Q23642 1042 23715 968ZM23428 864Q23301 864 23217 774.5Q23133 685 23133 545Q23133 404 23218.5 313Q23304 222 23429 222Q23558 222 23643 311.5Q23728 401 23728 546Q23728 688 23643 776Q23558 864 23428 864Z"
    "M25067 1088H25339V156Q25339 -120 25228 -250Q25079 -426 24779 -426Q24619 -426 24510 -386Q24401 -346 24326 -268.5Q24251 -191 24215 -80H24516Q24556 -126 24619 -149.5Q24682 -173 24768 -173Q24878 -173 24945 -139Q25012 -105 25039.5 -51Q25067 3 25067 135Q24995 63 24916 31.5Q24837 0 24737 0Q24518 0 24367 158Q24216 316 24216 558Q24216 817 24376 974Q24521 1116 24722 1116Q24816 1116 24899.5 1081.5Q24983 1047 25067 968ZM24784 861Q24655 861 24571 774.5Q24487 688 24487 557Q24487 421 24573 334Q24659 247 24789 247Q24916 247 24998.5 332Q25081 417 25081 555Q25081 691 24998 776Q24915 861 24784 861Z"
    "M26705 465H25828Q25847 349 25929.5 280.5Q26012 212 26140 212Q26293 212 26403 319L26633 211Q26547 89 26427 30.5Q26307 -28 26142 -28Q25886 -28 25725 133.5Q25564 295 25564 538Q25564 787 25724.5 951.5Q25885 1116 26127 1116Q26384 1116 26545 951.5Q26706 787 26706 517ZM26431 680Q26404 771 26324.5 828Q26245 885 26140 885Q26026 885 25940 821Q25886 781 25840 680Z"
    "M27075 285Q27140 285 27186 239.5Q27232 194 27232 129Q27232 64 27186 18Q27140 -28 27075 -28Q27010 -28 26964 18Q26918 64 26918 129Q26918 194 26964 239.5Q27010 285 27075 285Z"
)


def logo_source(with_tagline=False):
    """Portable outline logos; the lockup follows the reference's vertical hierarchy."""
    title = "Kanal — One room. Every language." if with_tagline else "Kanal"
    if with_tagline:
        parts = [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 720" '
            'width="900" height="720" role="img" aria-label="%s">' % title,
            '\n  <metadata>Mark redrawn from the supplied reference; wordmark and slogan are '
            'outlined from the closest match, Century Gothic Bold.</metadata>',
            '\n  <g transform="translate(245 -20) scale(.8)">%s\n  </g>' % mark_elements(FULL),
            '\n  <path d="%s" transform="translate(100 545) scale(.125 -.125)" fill="%s"/>'
            % (WORDMARK_PATH, _hex(INK)),
            '\n  <path d="%s" transform="translate(150 650) scale(.022 -.022)" fill="%s"/>'
            % (TAGLINE_PATH, _hex(INK)),
        ]
    else:
        parts = [
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 320" '
            'width="1200" height="320" role="img" aria-label="%s">' % title,
            '\n  <metadata>Mark redrawn from the supplied reference; wordmark is outlined '
            'from the closest match, Century Gothic Bold.</metadata>',
            '\n  <g transform="translate(-20 -15) scale(.72)">%s\n  </g>' % mark_elements(FULL),
            '\n  <path d="%s" transform="translate(350 252) scale(.139 -.139)" fill="%s"/>'
            % (WORDMARK_PATH, _hex(INK)),
        ]
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
        "<rect width='512' height='512' rx='%s' fill='%s'/>%s</svg>"
        % (_n(GRID * SQUIRCLE), _hex(PAPER), mark_elements(shapes))
    ).replace("\n", "").replace('"', "'")
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
