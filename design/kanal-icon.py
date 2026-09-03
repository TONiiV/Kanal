#!/usr/bin/env python3
"""Package the png2svg-traced Kanal mark for every application surface.

``design/kanal-icon.svg`` is the source of truth. The preprocessing script
removes paper texture and text from the photographed reference before
xyproto/png2svg converts the flat raster to SVG rectangles. This dependency-
free packager produces deterministic PNG, ICO, ICNS and favicon derivatives.
"""

from __future__ import annotations

import base64
import os
import re
import struct
import xml.etree.ElementTree as ET
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


def _parse_colour(value: str) -> tuple[int, int, int, int]:
    value = value.lstrip("#")
    if len(value) == 3:
        value = "".join(channel * 2 for channel in value)
    if len(value) != 6:
        raise ValueError(f"unsupported SVG colour: #{value}")
    return tuple(int(value[index:index + 2], 16) for index in (0, 2, 4)) + (255,)


def traced_svg_rgba(path: str) -> tuple[int, bytes]:
    """Rasterise the rectangle-only SVG emitted by xyproto/png2svg."""
    root = ET.parse(path).getroot()
    view_box = [int(float(value)) for value in root.attrib["viewBox"].split()]
    if view_box[:2] != [0, 0] or view_box[2] != view_box[3]:
        raise ValueError("the traced icon must use a square, origin-zero viewBox")
    size = view_box[2]
    pixels = bytearray(size * size * 4)
    for group in root:
        if group.tag.rsplit("}", 1)[-1] != "g":
            continue
        colour = _parse_colour(group.attrib["fill"])
        for rect in group:
            if rect.tag.rsplit("}", 1)[-1] != "rect":
                raise ValueError("the traced SVG may contain only groups and rectangles")
            left = int(float(rect.attrib.get("x", 0)))
            top = int(float(rect.attrib.get("y", 0)))
            width = int(float(rect.attrib.get("width", 1)))
            height = int(float(rect.attrib.get("height", 1)))
            for y in range(top, top + height):
                row = y * size * 4
                for x in range(left, left + width):
                    offset = row + x * 4
                    pixels[offset:offset + 4] = colour
    return size, bytes(pixels)


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

    source_svg = os.path.join(design, "kanal-icon.svg")
    with open(source_svg, "rb") as handle:
        svg = handle.read()
    source_size, source_rgba = traced_svg_rgba(source_svg)
    print(f"SVG\n  {os.path.relpath(source_svg, ROOT):52s} source ({len(svg)} B)")
    write(os.path.join(design, "kanal-icon-compact.svg"), svg)

    cache: dict[int, bytes] = {}
    def rgba(size: int) -> bytes:
        if size not in cache:
            cache[size] = resize_rgba(source_rgba, source_size, size)
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
    write(os.path.join(assets, "kanal-splash-mark.png"), png_bytes(512, rgba(512)))

    favicon = "data:image/png;base64," + base64.b64encode(png_bytes(32, rgba(32))).decode()
    write(os.path.join(design, "favicon-datauri.txt"), (favicon + "\n").encode())
    write_favicon_into_pages(favicon)


if __name__ == "__main__":
    main()
