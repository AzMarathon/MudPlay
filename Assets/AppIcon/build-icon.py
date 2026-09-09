#!/usr/bin/env python3
"""Regenerate mudplay.ico from the SVG masters. Run from this folder.

Needs rsvg-convert and Pillow. The icon is committed, so this only needs
running when a master changes.

Two masters exist because the artwork doesn't survive linear downscaling:
below 32px the CP437 double rule collapses into one muddy band and the M's
centre vertex fills in, so 16/24 render from mudplay-small.svg (single
heavier rule, wider M, deeper vertex) and 32-and-up from mudplay.svg.

Every entry is written PNG-compressed, which Windows has understood since
Vista and which keeps the whole file under 20 KB.
"""
import pathlib
import subprocess

from PIL import Image

HERE = pathlib.Path(__file__).parent
PLAN = [(16, "mudplay-small.svg"), (24, "mudplay-small.svg"),
        (32, "mudplay.svg"), (48, "mudplay.svg"), (64, "mudplay.svg"),
        (128, "mudplay.svg"), (256, "mudplay.svg")]


def render(size: int, master: str) -> Image.Image:
    png = HERE / f"_{size}.png"
    subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size),
                    str(HERE / master), "-o", str(png)], check=True)
    try:
        return Image.open(png).convert("RGBA")
    finally:
        png.unlink()


frames = [render(size, master) for size, master in PLAN]

# Pillow silently drops any requested size larger than the image it is
# saving, so the biggest frame has to be the one save() is called on.
frames[-1].save(HERE / "mudplay.ico",
                sizes=[(s, s) for s, _ in PLAN],
                append_images=frames[:-1])

with Image.open(HERE / "mudplay.ico") as ico:
    print("entries:", sorted(ico.info["sizes"]), "bytes:",
          (HERE / "mudplay.ico").stat().st_size)
