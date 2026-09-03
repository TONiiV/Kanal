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
INK = "#111A21"
PAPER = "#F5F0E6"
FLOW_COLOURS = {"#B23A2E", "#9A6B10", "#1C6B58"}


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

            generator.ROOT = str(root)
            generator.main()

            svg_paths = [
                root / "design" / "kanal-icon.svg",
                root / "design" / "kanal-icon-compact.svg",
                root / "design" / "kanal-logo-horizontal.svg",
                root / "design" / "kanal-logo-lockup.svg",
            ]
            for path in svg_paths:
                ET.parse(path)
                source = path.read_text(encoding="utf-8")
                self.assertIn(INK, source, path.name)

            icon_source = svg_paths[0].read_text(encoding="utf-8")
            self.assertIn(PAPER, icon_source)
            for colour in FLOW_COLOURS:
                self.assertIn(colour, icon_source.upper())
            self.assertIn("<path", icon_source, "the mark should be real vector curves and arrowheads")

            lockup = svg_paths[-1].read_text(encoding="utf-8")
            self.assertIn("One room. Every language.", lockup)
            self.assertIn("Century Gothic Bold", lockup)
            self.assertNotIn("<text", lockup, "the committed logo must not depend on an installed font")

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
