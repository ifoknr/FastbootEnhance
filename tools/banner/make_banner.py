"""Draws the Fastboot Studio banner (docs/banner.png, 1280x640, the size GitHub uses for a
repository's social preview): the icon, name and tagline in English and Arabic, the
sections of the app as chips, and a screenshot of the app in a window frame.

Run from the repository root after the screenshots exist:  python tools/banner/make_banner.py
Arabic is shaped by Pillow's raqm layout engine (libraqm must be available).
"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont, features

W, H = 1280, 640
BG_TOP = (14, 17, 22)
BG_BOTTOM = (16, 30, 36)
TEXT = (232, 236, 242)
DIM = (154, 166, 182)
MINT = (176, 236, 224)
ACCENT = (61, 214, 196)
CHIP_BG = (24, 34, 42)
CHIP_LINE = (47, 110, 102)

FONTS = "Fonts/"
SCREENSHOT = "docs/screenshots/02-payload-partitions.png"
SCREENSHOT_BACK = "docs/screenshots/21-super-imported.png"


def font(name, size):
    return ImageFont.truetype(FONTS + name, size)


def arabic(draw, xy, text, size, fill, anchor="ra"):
    f = font("NotoKufiArabic-SemiBold.ttf", size)
    if features.check("raqm"):
        draw.text(xy, text, font=f, fill=fill, anchor=anchor, direction="rtl", language="ar",
                  layout_engine=ImageFont.Layout.RAQM)
    else:
        raise SystemExit("Pillow was built without raqm; Arabic would not be shaped")


def background():
    img = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(img)
    for y in range(H):
        t = y / (H - 1)
        d.line([(0, y), (W, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)))

    # A faint dot grid, and a soft teal glow behind the screenshot.
    grid = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    g = ImageDraw.Draw(grid)
    for x in range(24, W, 32):
        for y in range(24, H, 32):
            g.ellipse([x - 1, y - 1, x + 1, y + 1], fill=(255, 255, 255, 14))
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse([700, 60, 1340, 620], fill=(61, 214, 196, 46))
    glow = glow.filter(ImageFilter.GaussianBlur(120))
    img = Image.alpha_composite(img.convert("RGBA"), grid)
    return Image.alpha_composite(img, glow)


def window(shot_path, width):
    """A screenshot with rounded corners, a hairline border and a drop shadow."""
    shot = Image.open(shot_path).convert("RGBA")
    height = round(shot.height * width / shot.width)
    shot = shot.resize((width, height), Image.LANCZOS)
    mask = Image.new("L", shot.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, width - 1, height - 1], 14, fill=255)
    framed = Image.new("RGBA", shot.size, (0, 0, 0, 0))
    framed.paste(shot, (0, 0), mask)
    ImageDraw.Draw(framed).rounded_rectangle([0, 0, width - 1, height - 1], 14, outline=(255, 255, 255, 40), width=1)

    pad = 40
    shadow = Image.new("RGBA", (width + pad * 2, height + pad * 2), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([pad, pad + 10, pad + width, pad + height + 10], 16, fill=(0, 0, 0, 150))
    shadow = shadow.filter(ImageFilter.GaussianBlur(18))
    shadow.alpha_composite(framed, (pad, pad))
    return shadow, pad


def chip(d, x, y, text, f):
    w = d.textlength(text, font=f)
    d.rounded_rectangle([x, y, x + w + 28, y + 34], 17, fill=CHIP_BG, outline=CHIP_LINE, width=1)
    d.text((x + 14, y + 17), text, font=f, fill=MINT, anchor="lm")
    return x + w + 28 + 10


def main():
    img = background()

    # Two app windows on the right, the back one offset and dimmed.
    back, pad = window(SCREENSHOT_BACK, 560)
    dim = Image.new("RGBA", back.size, (14, 17, 22, 0))
    back = Image.alpha_composite(back, dim)
    back.putalpha(back.getchannel("A").point(lambda a: a * 0.55))
    img.alpha_composite(back, (742 - pad, 70 - pad))
    front, pad = window(SCREENSHOT, 600)
    img.alpha_composite(front, (650 - pad, 190 - pad))

    # Fade the screenshots into the background towards the left, behind the text.
    fade = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    fd = ImageDraw.Draw(fade)
    for x in range(560, 760):
        a = round(255 * (1 - (x - 560) / 200) ** 2)
        fd.line([(x, 0), (x, H)], fill=BG_TOP + (a,))
    fd.rectangle([0, 0, 560, H], fill=BG_TOP + (255,))
    # Keep the background's own gradient on the left.
    left = background().crop((0, 0, 760, H))
    mask = fade.getchannel("A").crop((0, 0, 760, H))
    img.paste(left, (0, 0), mask)

    d = ImageDraw.Draw(img)

    icon = Image.open("big_icon.png").convert("RGBA").resize((104, 104), Image.LANCZOS)
    img.alpha_composite(icon, (64, 70))
    d.text((186, 96), "Fastboot Studio", font=font("Roboto-Bold.ttf", 54), fill=TEXT, anchor="lm")
    d.text((188, 146), "v2.0  ·  Windows  ·  by IFOKNR", font=font("Roboto-Medium.ttf", 20), fill=DIM, anchor="lm")

    d.text((66, 222), "The Android toolbox for Windows", font=font("Roboto-SemiBold.ttf", 31), fill=TEXT, anchor="la")
    d.text((66, 266), "Fastboot, payload dumper, super images and backups,", font=font("Roboto-Regular.ttf", 20), fill=DIM, anchor="la")
    d.text((66, 294), "in one fast and careful app.", font=font("Roboto-Regular.ttf", 20), fill=DIM, anchor="la")

    arabic(d, (566, 336), "أداة أندرويد المتكاملة لويندوز", 27, MINT)

    f = font("Roboto-Medium.ttf", 16)
    x, y = 66, 420
    for label in ["Fastboot", "Flash OTA", "Payload Dumper", "Image Tools"]:
        x = chip(d, x, y, label, f)
    x, y = 66, 464
    for label in ["Build Super", "Backup", "العربية / English"]:
        if label.startswith("العربية"):
            w = 150
            d.rounded_rectangle([x, y, x + w, y + 34], 17, fill=CHIP_BG, outline=CHIP_LINE, width=1)
            arabic(d, (x + w - 14, y + 18), "العربية / English", 15, MINT, anchor="rm")
            x += w + 10
        else:
            x = chip(d, x, y, label, f)

    d.text((66, 560), "github.com/ifoknr  ·  t.me/IFOKNR1", font=font("Roboto-Regular.ttf", 17), fill=DIM, anchor="la")
    d.line([(66, 540), (120, 540)], fill=ACCENT, width=3)

    img.convert("RGB").save("docs/banner.png", optimize=True)
    print("wrote docs/banner.png")


if __name__ == "__main__":
    main()
