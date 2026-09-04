"""Contract tests for the reproducible Kanal brand-asset generator."""

from __future__ import annotations

import importlib.util
import struct
import sys
import tempfile
import unittest
import zlib
from pathlib import Path


SCRIPT = Path(__file__).with_name("kanal-icon.py")
sys.dont_write_bytecode = True


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
                    '<!doctype html><link rel="icon" href="data:image/svg+xml,old">',
                    encoding="utf-8",
                )
            (root / "design" / "kanal-icon.png").write_bytes(
                SCRIPT.with_name("kanal-icon.png").read_bytes()
            )

            generator.ROOT = str(root)
            generator.main()

            source = root / "design" / "kanal-icon.png"
            source_data = source.read_bytes()
            self.assertEqual(b"\x89PNG\r\n\x1a\n", source_data[:8])
            width, height, bit_depth, colour_type = struct.unpack(">IIBB", source_data[16:26])
            self.assertEqual((1536, 1024, 8, 6), (width, height, bit_depth, colour_type))
            self.assertFalse(list((root / "design").glob("*.svg")))

            splash = root / "src" / "Kanal.Host" / "Assets" / "kanal-splash-mark.png"
            data = splash.read_bytes()
            self.assertEqual(b"\x89PNG\r\n\x1a\n", data[:8])
            width, height, bit_depth, colour_type = struct.unpack(">IIBB", data[16:26])
            self.assertEqual((512, 512, 8, 6), (width, height, bit_depth, colour_type))
            chunks = []
            offset = 8
            while offset < len(data):
                length = struct.unpack(">I", data[offset : offset + 4])[0]
                tag = data[offset + 4 : offset + 8]
                if tag == b"IDAT":
                    chunks.append(data[offset + 8 : offset + 8 + length])
                offset += 12 + length
            first_scanline = zlib.decompress(b"".join(chunks))[: 1 + width * 4]
            self.assertEqual(0, first_scanline[0], "the generator writes unfiltered scanlines")
            self.assertEqual(0, first_scanline[4], "the transparent canvas must have an empty top-left pixel")
            source_width, source_height, source_rgba = generator.decode_png(str(source))
            source_size, square_source = generator.square_rgba(source_width, source_height, source_rgba)
            expected_splash = generator.png_bytes(
                512, generator.resize_rgba(square_source, source_size, 512)
            )
            self.assertEqual(expected_splash, data, "the splash must retain the standalone mark")

            app_icon = root / "design" / "kanal-icon-1024.png"
            icon_data = app_icon.read_bytes()
            chunks = []
            offset = 8
            while offset < len(icon_data):
                length = struct.unpack(">I", icon_data[offset : offset + 4])[0]
                tag = icon_data[offset + 4 : offset + 8]
                if tag == b"IDAT":
                    chunks.append(icon_data[offset + 8 : offset + 8 + length])
                offset += 12 + length
            icon_pixels = zlib.decompress(b"".join(chunks))
            row_stride = 1 + 1024 * 4

            def icon_pixel(x: int, y: int) -> bytes:
                start = y * row_stride + 1 + x * 4
                return icon_pixels[start : start + 4]

            for corner in ((0, 0), (1023, 0), (0, 1023), (1023, 1023)):
                self.assertEqual(0, icon_pixel(*corner)[3], "all outer corners must be transparent")
            beige = bytes((0xF5, 0xF0, 0xE6, 255))
            for edge_centre in ((512, 0), (512, 1023), (0, 512), (1023, 512)):
                self.assertEqual(beige, icon_pixel(*edge_centre), "the rounded tile must reach each edge")

            self.assertTrue((root / "src" / "Kanal.Host" / "Assets" / "kanal.ico").is_file())
            self.assertTrue(app_icon.is_file())
            favicon = (root / "design" / "favicon-datauri.txt").read_text()
            self.assertTrue(favicon.startswith("data:image/png;base64,"))
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
