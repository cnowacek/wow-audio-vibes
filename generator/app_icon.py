"""Draws the companion's app/installer icon: companion/src/AudioVibes.App/app.ico (16-256 px).

    python app_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[1] / "companion" / "src" / "AudioVibes.App" / "app.ico"


def draw(size: int = 256) -> Image.Image:
    s = size / 256
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # rounded dark tile with a gold rim
    d.rounded_rectangle([8 * s, 8 * s, 248 * s, 248 * s], radius=48 * s, fill=(24, 20, 34, 255),
                        outline=(214, 170, 72, 255), width=max(1, round(10 * s)))
    gold = (240, 196, 90, 255)
    # beamed eighth notes
    d.ellipse([58 * s, 158 * s, 112 * s, 204 * s], fill=gold)
    d.ellipse([146 * s, 138 * s, 200 * s, 184 * s], fill=gold)
    d.rectangle([98 * s, 66 * s, 112 * s, 182 * s], fill=gold)
    d.rectangle([186 * s, 46 * s, 200 * s, 162 * s], fill=gold)
    d.polygon([(98 * s, 66 * s), (200 * s, 46 * s), (200 * s, 76 * s), (98 * s, 96 * s)], fill=gold)
    # a small "signal" arc top-left
    teal = (96, 214, 200, 255)
    w = max(1, round(9 * s))
    d.arc([30 * s, 30 * s, 90 * s, 90 * s], 180, 270, fill=teal, width=w)
    d.arc([48 * s, 48 * s, 90 * s, 90 * s], 180, 270, fill=teal, width=w)
    return img


if __name__ == "__main__":
    draw().save(OUT, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("wrote", OUT)
