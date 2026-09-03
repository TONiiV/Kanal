"""Contract tests for the reproducible Kanal brand-asset generator."""

from __future__ import annotations

import importlib.util
import struct
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path


SCRIPT = Path(__file__).with_name("kanal-icon.py")
sys.dont_write_bytecode = True
MARK_COLOURS = {"#0C1419", "#1F6A56", "#A07116", "#B03B2C"}


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
            (root / "design" / "kanal-icon.svg").write_bytes(
                SCRIPT.with_name("kanal-icon.svg").read_bytes()
            )

            generator.ROOT = str(root)
            generator.main()

            svg_paths = [
                root / "design" / "kanal-icon.svg",
                root / "design" / "kanal-icon-compact.svg",
            ]
            for path in svg_paths:
                svg = ET.parse(path).getroot()
                source = path.read_text(encoding="utf-8")
                self.assertNotIn("<text", source)
                self.assertNotIn("kanal", source.lower())
                self.assertEqual("0 0 512 512", svg.attrib["viewBox"])

            icon_source = svg_paths[0].read_text(encoding="utf-8")
            for colour in MARK_COLOURS:
                self.assertIn(colour, icon_source.upper())
            self.assertIn("<rect", icon_source, "png2svg should emit flat vector rectangles")
            self.assertNotIn("#F5F0E6", icon_source.upper(), "the paper texture must be transparent")

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

            self.assertTrue((root / "src" / "Kanal.Host" / "Assets" / "kanal.ico").is_file())
            self.assertTrue((root / "design" / "kanal-icon-1024.png").is_file())
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
