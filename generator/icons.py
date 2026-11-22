#!/usr/bin/env python3
"""Render the addon's UI icons (white on transparent, 64x64, 32-bit TGA) into addon/AudioVibes/media/.

The shapes follow the design's 24-unit stroke icons (stroke 2, round caps/joins). WoW tints them with
SetVertexColor (body text colour normally, gold on hover), so they are drawn in pure white.

    python icons.py
"""
from __future__ import annotations

import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "addon" / "AudioVibes" / "media"
SIZE = 64
SS = 4                    # supersampling
S = SIZE * SS / 24.0      # design units -> supersampled px
W = round(2 * S)          # stroke width (2 units)


def P(x, y):
    return (x * S, y * S)


def poly(d, pts, closed=False, width=W):
    pts = [P(*p) for p in pts]
    if closed:
        pts = pts + [pts[0], pts[1]]
    d.line(pts, fill=255, width=width, joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - r, y - r, x + r, y + r], fill=255)


def circle(d, cx, cy, r, width=W, fill=False):
    x, y, rr = cx * S, cy * S, r * S
    if fill:
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=255)
    else:
        d.ellipse([x - rr, y - rr, x + rr, y + rr], outline=255, width=width)


def heart(d):
    pts = []
    for i in range(0, 361, 6):
        t = math.radians(i)
        x = 16 * math.sin(t) ** 3
        y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((12 + x * 0.56, 11.6 - y * 0.56))
    poly(d, pts, closed=True)


def gear(d):
    pts = []
    teeth = 8
    for i in range(teeth * 4):
        a = 2 * math.pi * i / (teeth * 4) - math.pi / 2
        r = 9.5 if (i % 4) in (0, 1) else 7.2
        pts.append((12 + r * math.cos(a), 12 + r * math.sin(a)))
    poly(d, pts, closed=True, width=round(1.8 * S))
    circle(d, 12, 12, 3)


def pin(d):
    pts = [(12, 21)]
    for i in range(-40, 221, 10):          # arc over the top of the pin
        a = math.radians(i)
        pts.append((12 + 7 * math.cos(a), 10 - 7 * math.sin(a)))
    poly(d, pts, closed=True)
    circle(d, 12, 10, 2.5)


ICONS = {
    "wave":     lambda d: [poly(d, [(x, 12 - h), (x, 12 + h)]) for x, h in ((4, 2), (8, 6), (12, 9), (16, 5), (20, 2))],
    "note":     lambda d: (poly(d, [(9, 18), (9, 5), (21, 3), (21, 16)]), circle(d, 6, 18, 3), circle(d, 18, 16, 3)),
    "prev":     lambda d: (poly(d, [(19, 20), (9, 12), (19, 4)], closed=True), poly(d, [(5, 19), (5, 5)])),
    "play":     lambda d: poly(d, [(6, 4), (20, 12), (6, 20)], closed=True),
    "next":     lambda d: (poly(d, [(5, 4), (15, 12), (5, 20)], closed=True), poly(d, [(19, 5), (19, 19)])),
    "voldown":  lambda d: (poly(d, [(11, 5), (6, 9), (2, 9), (2, 15), (6, 15), (11, 19)], closed=True), poly(d, [(16, 12), (21, 12)])),
    "volup":    lambda d: (poly(d, [(11, 5), (6, 9), (2, 9), (2, 15), (6, 15), (11, 19)], closed=True),
                           poly(d, [(16, 12), (21, 12)]), poly(d, [(18.5, 9.5), (18.5, 14.5)])),
    "heart":    heart,
    "bolt":     lambda d: poly(d, [(13, 2), (4, 14), (11, 14), (10, 22), (19, 10), (12, 10)], closed=True),
    "gear":     gear,
    "up":       lambda d: poly(d, [(6, 15), (12, 9), (18, 15)]),
    "down":     lambda d: poly(d, [(6, 9), (12, 15), (18, 9)]),
    "close":    lambda d: (poly(d, [(6, 6), (18, 18)]), poly(d, [(18, 6), (6, 18)])),
    "pin":      pin,
    "check":    lambda d: poly(d, [(5, 12), (10, 17), (20, 7)], width=round(2.5 * S)),
    "cross":    lambda d: (poly(d, [(6, 6), (18, 18)], width=round(2.5 * S)), poly(d, [(18, 6), (6, 18)], width=round(2.5 * S))),
    "warn":     lambda d: (circle(d, 12, 12, 9), poly(d, [(12, 8), (12, 13)]), circle(d, 12, 16.3, 1.2, fill=True)),
    "search":   lambda d: (circle(d, 11, 11, 7), poly(d, [(20, 20), (16.5, 16.5)])),
    "dot":      lambda d: circle(d, 12, 12, 10, fill=True),
    "radio":    lambda d: circle(d, 12, 12, 5, fill=True),
}


def write_tga(path: Path, img: Image.Image):
    """32-bit uncompressed TGA, bottom-left origin (what WoW expects)."""
    img = img.convert("RGBA")
    w, h = img.size
    header = struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, w, h, 32, 8)
    rows = []
    px = img.load()
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    path.write_bytes(header + b"".join(rows))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, draw in ICONS.items():
        mask = Image.new("L", (SIZE * SS, SIZE * SS), 0)
        draw(ImageDraw.Draw(mask))
        mask = mask.resize((SIZE, SIZE), Image.LANCZOS)
        icon = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
        icon.putalpha(mask)
        write_tga(OUT / f"{name}.tga", icon)
    # contact sheet for eyeballing (not shipped to WoW; ignored by git)
    sheet = Image.new("RGBA", (len(ICONS) * 72, 72), (20, 17, 13, 255))
    for i, name in enumerate(ICONS):
        mask = Image.new("L", (SIZE * SS, SIZE * SS), 0)
        ICONS[name](ImageDraw.Draw(mask))
        mask = mask.resize((SIZE, SIZE), Image.LANCZOS)
        gold = Image.new("RGBA", (SIZE, SIZE), (255, 209, 0, 255))
        sheet.paste(gold, (i * 72 + 4, 4), mask)
    sheet.save(Path(__file__).resolve().parent / "icons-preview.png")
    print(f"{len(ICONS)} icons -> {OUT}")


if __name__ == "__main__":
    main()
