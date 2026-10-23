#!/usr/bin/env python3
"""Generates src/FaceTray/facetray.ico (multi-size) — dark rounded square,
white 'f' glyph, red counter dot. Run: python3 assets/make_icon.py"""
from PIL import Image, ImageDraw, ImageFont
import os

SIZES = [16, 20, 24, 32, 48, 64, 256]
FONT_CANDIDATES = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "C:/Windows/Fonts/segoeui.ttf",
]


def font(size):
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                pass
    return ImageFont.load_default()


def render(size):
    scale = 4  # supersample for smooth corners
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(s * 0.22)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=(16, 16, 16, 255))
    # white f
    f = font(int(s * 0.78))
    d.text((s * 0.30, s * 0.06), "f", font=f, fill=(255, 255, 255, 255))
    # red dot bottom-right
    dot_r = s * 0.16
    cx, cy = s * 0.76, s * 0.76
    d.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=(229, 72, 77, 255))
    return img.resize((size, size), Image.LANCZOS)


def main():
    out = os.path.join(os.path.dirname(__file__), "..", "src", "FaceTray", "facetray.ico")
    images = [render(s) for s in SIZES]
    images[-1].save(out, format="ICO", sizes=[(s, s) for s in SIZES],
                    append_images=images[:-1])
    print("wrote", out)


if __name__ == "__main__":
    main()
