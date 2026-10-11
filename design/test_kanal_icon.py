"""Contract tests for the reproducible Kanal brand-asset generator."""

from __future__ import annotations

import base64
import importlib.util
import struct
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("kanal-icon.py")
sys.dont_write_bytecode = True


SHEETS = {
    "light": (bytes.fromhex("F5F0E6"), bytes.fromhex("FFFCF7")),
    "dark": (bytes.fromhex("222838"), bytes.fromhex("181C28")),
}


def contrast(first: bytes, second: bytes) -> float:
    def luminance(rgb: bytes) -> float:
        linear = [
            c / 255 / 12.92 if c / 255 <= 0.04045 else ((c / 255 + 0.055) / 1.055) ** 2.4
            for c in rgb
        ]
        return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2]

    high, low = sorted((luminance(first), luminance(second)), reverse=True)
    return (high + 0.05) / (low + 0.05)


def load_generator():
    spec = importlib.util.spec_from_file_location("kanal_icon", SCRIPT)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class BrandAssetGeneratorTests(unittest.TestCase):
    def test_generator_emits_the_reference_led_brand_suite(self):
        generator = load_generator()

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "design").mkdir()
            (root / "src" / "Kanal.Host" / "Assets").mkdir(parents=True)
            for page in (root / "web" / "index.html", root / "docs" / "index.html"):
                page.parent.mkdir()
                page.write_text(
                    '<!doctype html><link rel="icon" href="data:image/svg+xml,old">'
                    '<img class="brand-logo" src="data:image/png;base64,old" alt="">',
                    encoding="utf-8",
                )
            (root / "design" / "kanal-icon.png").write_bytes(
                SCRIPT.with_name("kanal-icon.png").read_bytes()
            )
            (root / "design" / "icon-silver").mkdir()
            for name in generator.SILVER_SOURCES.values():
                (root / "design" / "icon-silver" / name).write_bytes(
                    SCRIPT.with_name("icon-silver").joinpath(name).read_bytes()
                )

            generator.ROOT = str(root)
            generator.main()

            source = root / "design" / "kanal-icon.png"
            source_data = source.read_bytes()
            self.assertEqual(b"\x89PNG\r\n\x1a\n", source_data[:8])
            width, height, bit_depth, colour_type = struct.unpack(">IIBB", source_data[16:26])
            self.assertEqual((8, 6), (bit_depth, colour_type))
            self.assertGreaterEqual(min(width, height), 1024)
            self.assertEqual(SCRIPT.with_name("kanal-icon.png").read_bytes(), source_data)
            self.assertFalse(list((root / "design").glob("*.svg")))

            assets = root / "src" / "Kanal.Host" / "Assets"
            source_width, source_height, source_rgba = generator.decode_png(str(source))
            source_size, square_source = generator.square_rgba(source_width, source_height, source_rgba)
            mark_1024 = generator.resize_rgba(square_source, source_size, 1024)
            brightness = {}
            for variant, name in generator.SPLASH_MARK_NAMES.items():
                data = (assets / name).read_bytes()
                width, height, bit_depth, colour_type = struct.unpack(">IIBB", data[16:26])
                self.assertEqual((512, 512, 8, 6), (width, height, bit_depth, colour_type))
                _, _, tile = generator.decode_png(
                    str(root / "design" / "icon-silver" / generator.SILVER_SOURCES[variant])
                )
                expected = generator.png_bytes(
                    512,
                    generator.resize_rgba(
                        generator.silver_mark_rgba(mark_1024, tile, 1024), 1024, 512
                    ),
                )
                self.assertEqual(expected, data, "the splash must be the silver mark on transparency")

                _, _, pixels = generator.decode_png(str(assets / name))
                self.assertEqual(0, pixels[3], "the transparent canvas must have an empty top-left pixel")
                opaque = [pixels[i : i + 4] for i in range(0, len(pixels), 4) if pixels[i + 3] >= 250]
                self.assertTrue(opaque)
                self.assertTrue(
                    all(max(p[:3]) - min(p[:3]) <= 12 for p in opaque),
                    "the silver mark must carry no brand colour",
                )
                brightness[variant] = sum(p[0] for p in opaque) / len(opaque)
                tones = {bytes(p[:3]) for p in opaque}
                for tone in tones:
                    for background in SHEETS[variant]:
                        self.assertGreaterEqual(
                            contrast(tone, background), 3.0,
                            f"{name} tone {tuple(tone)} on #{background.hex().upper()}",
                        )
            self.assertGreater(brightness["dark"], brightness["light"] + 60)

            for variant, name in generator.HOST_ICON_NAMES.items():
                ico = (assets / name).read_bytes()
                self.assertEqual((0, 1, 7), struct.unpack("<HHH", ico[:6]))
                sizes = [ico[6 + 16 * i] or 256 for i in range(7)]
                self.assertEqual([16, 24, 32, 48, 64, 128, 256], sizes)
            self.assertNotEqual((assets / "kanal.ico").read_bytes(), (assets / "kanal-dark.ico").read_bytes())
            self.assertFalse((root / "design" / "kanal-icon-1024.png").exists())

            favicon = (root / "design" / "favicon-datauri.txt").read_text()
            self.assertTrue(favicon.startswith("data:image/png;base64,"))
            favicon_png = generator.png_bytes(
                32,
                generator.resize_rgba(
                    generator.rounded_beige_icon_rgba(square_source, source_size), source_size, 32
                ),
            )
            self.assertIn(base64.b64encode(favicon_png).decode(), favicon, "the favicon keeps the coloured mark")
            page_text = (root / "web" / "index.html").read_text()
            self.assertNotIn('src="data:image/png;base64,old"', page_text)
            self.assertIn('<img class="brand-logo" src="data:image/png;base64,', page_text)
            icns = (root / "design" / "kanal.icns").read_bytes()
            self.assertEqual(b"icns", icns[:4])
            self.assertEqual(len(icns), struct.unpack(">I", icns[4:8])[0])
            self.assertEqual(
                (root / "web" / "index.html").read_bytes(),
                (root / "docs" / "index.html").read_bytes(),
            )

            generated = {
                path.relative_to(root): path.read_bytes()
                for path in root.rglob("*")
                if path.is_file()
            }
            generator.main()
            regenerated = {
                path.relative_to(root): path.read_bytes()
                for path in root.rglob("*")
                if path.is_file()
            }
            self.assertEqual(generated, regenerated, "a second run must be byte-for-byte idempotent")


if __name__ == "__main__":
    unittest.main()
