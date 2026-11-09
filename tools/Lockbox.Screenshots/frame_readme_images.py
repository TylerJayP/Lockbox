"""Adds a window frame, rounded corners and a soft shadow to the README screenshots.

    dotnet run --project tools/Lockbox.Screenshots -- readme raw-shots
    python tools/Lockbox.Screenshots/frame_readme_images.py raw-shots docs/images

Needs Python 3 with Pillow (pip install pillow) and the Inter font (falls back to the default font).
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ICON = os.path.join(ROOT, "src", "Lockbox.App", "Assets", "lockbox.png")
NAMES = ["vault", "unlock", "card", "wifi", "protected", "checkup", "import"]


def load_font(size):
    for path in ["/usr/share/fonts/opentype/inter/Inter-Medium.otf", "C:/Windows/Fonts/segoeui.ttf"]:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def frame(src, dst, icon, font, radius=12, bar=34, pad=28, ss=2):
    shot = Image.open(src).convert("RGBA")
    w, h = shot.size
    win = Image.new("RGBA", (w, h + bar), (11, 13, 16, 255))
    d = ImageDraw.Draw(win)
    win.alpha_composite(icon, (12, 8))
    d.text((38, 9), "Lockbox", font=font, fill=(200, 205, 214, 255))
    grey = (150, 156, 166, 255)
    x = w - 24
    d.line([(x - 5, 12), (x + 5, 22)], fill=grey); d.line([(x - 5, 22), (x + 5, 12)], fill=grey)
    x -= 46; d.rectangle([x - 5, 12, x + 5, 22], outline=grey)
    x -= 46; d.line([(x - 5, 17), (x + 5, 17)], fill=grey)
    win.paste(shot, (0, bar))

    big = (win.width * ss, win.height * ss)
    mask = Image.new("L", big, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big[0] - 1, big[1] - 1], radius=radius * ss, fill=255)
    win.putalpha(mask.resize(win.size, Image.LANCZOS))
    border = Image.new("RGBA", big, (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle([0, 0, big[0] - 1, big[1] - 1], radius=radius * ss, outline=(52, 58, 70, 255), width=2 * ss)
    win.alpha_composite(border.resize(win.size, Image.LANCZOS))

    canvas = Image.new("RGBA", (win.width + pad * 2, win.height + pad * 2), (0, 0, 0, 0))
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([pad, pad + 6, pad + win.width, pad + win.height + 6], radius=radius, fill=(0, 0, 0, 110))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(14)))
    canvas.alpha_composite(win, (pad, pad))
    canvas.save(dst, optimize=True)


def main():
    src_dir, dst_dir = sys.argv[1], sys.argv[2]
    os.makedirs(dst_dir, exist_ok=True)
    icon = Image.open(ICON).convert("RGBA").resize((18, 18), Image.LANCZOS)
    font = load_font(13)
    for name in NAMES:
        frame(os.path.join(src_dir, name + ".png"), os.path.join(dst_dir, name + ".png"), icon, font)
    Image.open(ICON).convert("RGBA").resize((160, 160), Image.LANCZOS).save(os.path.join(dst_dir, "logo.png"), optimize=True)
    print("framed", len(NAMES), "images into", dst_dir)


if __name__ == "__main__":
    main()
