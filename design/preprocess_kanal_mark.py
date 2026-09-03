#!/usr/bin/env python3
"""Extract the flat Kanal mark from the supplied photographed reference.

The reference contains paper texture and a wordmark.  This script deliberately
looks only in the upper artwork region, classifies the four printed inks, drops
small texture components, crops to the surviving artwork and centres it on a
transparent square canvas.  Output RGB values are exact palette colours.
"""

from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image, ImageFilter


PALETTE = (
    (12, 20, 25, 255),    # ink
    (31, 106, 86, 255),   # pine
    (160, 113, 22, 255),  # ochre
    (176, 59, 44, 255),   # rust
)
CANVAS_SIZE = 512
PADDING = 48
MIN_COMPONENT_PIXELS = 180


def classify(pixel: tuple[int, int, int]) -> int | None:
    red, green, blue = pixel
    high = max(pixel)
    low = min(pixel)
    saturation = 0.0 if high == 0 else (high - low) / high

    # Neutral dark ink, followed by the three strongly chromatic print inks.
    if high < 178 and saturation < 0.58:
        return 0
    if green > red * 1.45 and green > blue * 1.08 and green < 155:
        return 1
    if red > 105 and green > 68 and red > green * 1.18 and green > blue * 1.75:
        return 2
    if red > 105 and red > green * 1.72 and red > blue * 1.72:
        return 3
    return None


def keep_large_components(labels: list[int], width: int, height: int) -> list[int]:
    kept = [-1] * len(labels)
    visited = bytearray(len(labels))

    for start, colour in enumerate(labels):
        if colour < 0 or visited[start]:
            continue

        visited[start] = 1
        queue = deque([start])
        component: list[int] = []
        while queue:
            index = queue.popleft()
            component.append(index)
            x = index % width
            y = index // width
            for neighbour in (
                index - 1 if x else -1,
                index + 1 if x + 1 < width else -1,
                index - width if y else -1,
                index + width if y + 1 < height else -1,
            ):
                if (
                    neighbour >= 0
                    and not visited[neighbour]
                    and labels[neighbour] == colour
                ):
                    visited[neighbour] = 1
                    queue.append(neighbour)

        if len(component) >= MIN_COMPONENT_PIXELS:
            for index in component:
                kept[index] = colour

    return kept


def extract(source: Image.Image) -> Image.Image:
    rgb = source.convert("RGB")
    # The lower 45% contains only the wordmark and slogan.
    artwork = rgb.crop((0, 0, rgb.width, round(rgb.height * 0.55)))
    labels = [classify(pixel) for pixel in artwork.get_flattened_data()]
    numeric_labels = [-1 if label is None else label for label in labels]
    labels = keep_large_components(numeric_labels, artwork.width, artwork.height)

    # Black ink in the photographed sample carries visible paper grain. Close
    # those small voids after classification so the exported mark is flat.
    ink_mask = Image.new("L", artwork.size, 0)
    ink_mask.putdata([255 if colour == 0 else 0 for colour in labels])
    ink_mask = ink_mask.filter(ImageFilter.MaxFilter(9)).filter(ImageFilter.MinFilter(9))
    closed_ink = ink_mask.get_flattened_data()
    labels = [
        0 if closed_ink[index] else colour
        for index, colour in enumerate(labels)
    ]

    points = [
        (index % artwork.width, index // artwork.width)
        for index, colour in enumerate(labels)
        if colour >= 0
    ]
    if not points:
        raise ValueError("no mark pixels found in reference")

    left = min(point[0] for point in points)
    top = min(point[1] for point in points)
    right = max(point[0] for point in points) + 1
    bottom = max(point[1] for point in points) + 1

    mark = Image.new("RGBA", (right - left, bottom - top), (0, 0, 0, 0))
    mark_pixels = mark.load()
    for index, colour in enumerate(labels):
        if colour >= 0:
            x = index % artwork.width
            y = index // artwork.width
            if left <= x < right and top <= y < bottom:
                mark_pixels[x - left, y - top] = PALETTE[colour]

    available = CANVAS_SIZE - 2 * PADDING
    scale = min(available / mark.width, available / mark.height, 1.0)
    if scale < 1.0:
        mark = mark.resize(
            (round(mark.width * scale), round(mark.height * scale)),
            Image.Resampling.NEAREST,
        )

    canvas = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    canvas.alpha_composite(
        mark,
        ((CANVAS_SIZE - mark.width) // 2, (CANVAS_SIZE - mark.height) // 2),
    )
    return canvas


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    with Image.open(args.input) as source:
        result = extract(source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result.save(args.output, optimize=True)


if __name__ == "__main__":
    main()
