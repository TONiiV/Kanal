#!/usr/bin/env python3
"""Package the supplied Kanal PNG for every application surface.

``design/kanal-icon.png`` is the source of truth. This dependency-free
packager preserves that artwork, places it on a warm beige rounded tile for app
icons, and produces deterministic square PNG, ICO, ICNS and favicon
derivatives. The splash keeps the standalone transparent mark. No SVG asset
is generated or shipped.
"""

from __future__ import annotations

import base64
import math
import os
import re
import struct
import zlib


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _chunk(tag: bytes, data: bytes) -> bytes:
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def png_bytes(size: int, rgba: bytes) -> bytes:
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride : (y + 1) * stride]
    return (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _chunk(b"IEND", b"")
    )


def _dib_bytes(size: int, rgba: bytes) -> bytes:
    header = struct.pack(
        "<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0
    )
    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        row = y * size * 4
        for x in range(size):
            offset = row + x * 4
            pixels += bytes(
                (rgba[offset + 2], rgba[offset + 1], rgba[offset], rgba[offset + 3])
            )
    mask_stride = ((size + 31) // 32) * 4
    return header + bytes(pixels) + b"\x00" * (mask_stride * size)


def ico_bytes(entries: list[tuple[int, bytes]]) -> bytes:
    blobs = []
    for size, rgba in entries:
        payload = png_bytes(size, rgba) if size >= 64 else _dib_bytes(size, rgba)
        blobs.append((size, payload))
    result = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    for size, payload in blobs:
        result += struct.pack(
            "<BBBBHHII", 0 if size >= 256 else size, 0 if size >= 256 else size,
            0, 0, 1, 32, len(payload), offset,
        )
        offset += len(payload)
    return result + b"".join(payload for _, payload in blobs)


def icns_bytes(entries: list[tuple[int, bytes]]) -> bytes:
    types = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07",
             256: b"ic08", 512: b"ic09", 1024: b"ic10"}
    chunks = []
    for size, rgba in entries:
        payload = png_bytes(size, rgba)
        chunks.append(types[size] + struct.pack(">I", len(payload) + 8) + payload)
    body = b"".join(chunks)
    return b"icns" + struct.pack(">I", len(body) + 8) + body


def _paeth(left: int, up: int, upper_left: int) -> int:
    estimate = left + up - upper_left
    distances = (abs(estimate - left), abs(estimate - up), abs(estimate - upper_left))
    return (left, up, upper_left)[distances.index(min(distances))]


def decode_png(path: str) -> tuple[int, int, bytes]:
    """Decode the checked-in 8-bit RGBA PNG without third-party packages."""
    with open(path, "rb") as handle:
        data = handle.read()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("brand source is not a PNG")

    offset = 8
    compressed = bytearray()
    width = height = None
    while offset < len(data):
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        tag = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        offset += 12 + length
        if tag == b"IHDR":
            width, height, depth, colour_type, compression, filtering, interlace = struct.unpack(
                ">IIBBBBB", payload
            )
            if (depth, colour_type, compression, filtering, interlace) != (8, 6, 0, 0, 0):
                raise ValueError("brand source must be a non-interlaced 8-bit RGBA PNG")
        elif tag == b"IDAT":
            compressed += payload

    if width is None or height is None:
        raise ValueError("brand source has no IHDR chunk")
    encoded = zlib.decompress(bytes(compressed))
    stride = width * 4
    result = bytearray(width * height * 4)
    source_offset = 0
    previous = bytearray(stride)
    for y in range(height):
        filter_type = encoded[source_offset]
        source_offset += 1
        row = bytearray(encoded[source_offset:source_offset + stride])
        source_offset += stride
        for index in range(stride):
            left = row[index - 4] if index >= 4 else 0
            up = previous[index]
            upper_left = previous[index - 4] if index >= 4 else 0
            if filter_type == 1:
                row[index] = (row[index] + left) & 255
            elif filter_type == 2:
                row[index] = (row[index] + up) & 255
            elif filter_type == 3:
                row[index] = (row[index] + ((left + up) // 2)) & 255
            elif filter_type == 4:
                row[index] = (row[index] + _paeth(left, up, upper_left)) & 255
            elif filter_type != 0:
                raise ValueError(f"unsupported PNG filter {filter_type}")
        result[y * stride:(y + 1) * stride] = row
        previous = row
    return width, height, bytes(result)


def square_rgba(width: int, height: int, source: bytes) -> tuple[int, bytes]:
    size = max(width, height)
    result = bytearray(size * size * 4)
    left = (size - width) // 2
    top = (size - height) // 2
    for y in range(height):
        source_start = y * width * 4
        target_start = ((top + y) * size + left) * 4
        result[target_start:target_start + width * 4] = source[source_start:source_start + width * 4]
    return size, bytes(result)


ICON_TILE_RGB = (0xF5, 0xF0, 0xE6)


def rounded_beige_icon_rgba(mark: bytes, size: int) -> bytes:
    """Composite the unmodified mark over a warm beige rounded app tile."""
    radius = size * 18 // 100
    result = bytearray(size * size * 4)
    for y in range(size):
        py = y + 0.5
        dy = max(radius - py, py - (size - radius), 0.0)
        for x in range(size):
            px = x + 0.5
            dx = max(radius - px, px - (size - radius), 0.0)
            distance = math.hypot(dx, dy)
            tile_alpha = max(0, min(255, round((radius + 0.5 - distance) * 255)))

            offset = (y * size + x) * 4
            mark_alpha = mark[offset + 3]
            remaining = 255 - mark_alpha
            output_alpha = mark_alpha + (tile_alpha * remaining + 127) // 255
            if output_alpha:
                for channel, tile_channel in enumerate(ICON_TILE_RGB):
                    numerator = (
                        mark[offset + channel] * mark_alpha
                        + (tile_channel * tile_alpha * remaining + 127) // 255
                    )
                    result[offset + channel] = min(255, (numerator + output_alpha // 2) // output_alpha)
                result[offset + 3] = output_alpha
    return bytes(result)


def resize_rgba(source: bytes, source_size: int, target_size: int) -> bytes:
    """Nearest-upscale or box-downsample a transparent square RGBA image."""
    if target_size == source_size:
        return source
    if target_size > source_size:
        result = bytearray(target_size * target_size * 4)
        for y in range(target_size):
            sy = min(source_size - 1, y * source_size // target_size)
            for x in range(target_size):
                sx = min(source_size - 1, x * source_size // target_size)
                src = (sy * source_size + sx) * 4
                dst = (y * target_size + x) * 4
                result[dst:dst + 4] = source[src:src + 4]
        return bytes(result)
    result = bytearray(target_size * target_size * 4)
    for y in range(target_size):
        top = y * source_size // target_size
        bottom = max(top + 1, (y + 1) * source_size // target_size)
        for x in range(target_size):
            left = x * source_size // target_size
            right = max(left + 1, (x + 1) * source_size // target_size)
            alpha_sum = red_sum = green_sum = blue_sum = 0
            samples = (right - left) * (bottom - top)
            for sy in range(top, bottom):
                for sx in range(left, right):
                    offset = (sy * source_size + sx) * 4
                    alpha = source[offset + 3]
                    alpha_sum += alpha
                    red_sum += source[offset] * alpha
                    green_sum += source[offset + 1] * alpha
                    blue_sum += source[offset + 2] * alpha
            dst = (y * target_size + x) * 4
            if alpha_sum:
                result[dst] = red_sum // alpha_sum
                result[dst + 1] = green_sum // alpha_sum
                result[dst + 2] = blue_sum // alpha_sum
                result[dst + 3] = alpha_sum // samples
    return bytes(result)


FAVICON_LINK = re.compile(rb'<link rel="icon" href="data:image/[^"]*">')
HTML_PAGES = [("web", "index.html"), ("docs", "index.html")]


def write_favicon_into_pages(uri: str, log=print) -> None:
    link = (f'<link rel="icon" href="{uri}">').encode()
    for parts in HTML_PAGES:
        path = os.path.join(ROOT, *parts)
        with open(path, "rb") as handle:
            before = handle.read()
        after, replacements = FAVICON_LINK.subn(link, before)
        if replacements != 1:
            relative = os.path.relpath(path, ROOT)
            raise SystemExit(f"{relative}: expected one favicon link, found {replacements}")
        if after != before:
            with open(path, "wb") as handle:
                handle.write(after)
            log(f"  {os.path.relpath(path, ROOT):52s} updated")


def main() -> None:
    design = os.path.join(ROOT, "design")
    assets = os.path.join(ROOT, "src", "Kanal.Host", "Assets")
    iconset = os.path.join(design, "Kanal.iconset")
    os.makedirs(iconset, exist_ok=True)

    def write(path: str, data: bytes) -> None:
        with open(path, "wb") as handle:
            handle.write(data)
        print(f"  {os.path.relpath(path, ROOT):52s} {len(data):6d} B")

    source_png = os.path.join(design, "kanal-icon.png")
    width, height, decoded = decode_png(source_png)
    source_size, source_rgba = square_rgba(width, height, decoded)
    icon_rgba = rounded_beige_icon_rgba(source_rgba, source_size)
    print(f"PNG\n  {os.path.relpath(source_png, ROOT):52s} source ({width}x{height})")

    cache: dict[int, bytes] = {}
    def rgba(size: int) -> bytes:
        if size not in cache:
            cache[size] = resize_rgba(icon_rgba, source_size, size)
        return cache[size]

    entries = [(16, "", 16), (16, "@2x", 32), (32, "", 32), (32, "@2x", 64),
               (128, "", 128), (128, "@2x", 256), (256, "", 256),
               (256, "@2x", 512), (512, "", 512), (512, "@2x", 1024)]
    for nominal, suffix, pixels in entries:
        name = f"icon_{nominal}x{nominal}{suffix}.png"
        write(os.path.join(iconset, name), png_bytes(pixels, rgba(pixels)))

    sizes = (16, 32, 64, 128, 256, 512, 1024)
    write(os.path.join(design, "kanal.icns"), icns_bytes([(size, rgba(size)) for size in sizes]))
    write(os.path.join(assets, "kanal.ico"),
          ico_bytes([(size, rgba(size)) for size in (16, 24, 32, 48, 64, 128, 256)]))
    write(os.path.join(design, "kanal-icon-1024.png"), png_bytes(1024, rgba(1024)))
    splash_rgba = resize_rgba(source_rgba, source_size, 512)
    write(os.path.join(assets, "kanal-splash-mark.png"), png_bytes(512, splash_rgba))

    favicon = "data:image/png;base64," + base64.b64encode(png_bytes(32, rgba(32))).decode()
    write(os.path.join(design, "favicon-datauri.txt"), (favicon + "\n").encode())
    write_favicon_into_pages(favicon)


if __name__ == "__main__":
    main()
