"""Draws the Fastboot Studio icon: a deep teal tile with a phone and a lightning bolt drawn
in soft mint lines, with a faint glow. Calm colours that sit well with the app's dark theme
and still stand out on a light desktop.

Run from the repository root:  python tools/icon/make_icon.py
Writes icon.ico (16 to 256 px) and big_icon.png (512 px). Each size is drawn on its own,
at 8x and scaled down, so small sizes stay crisp; at 32 px and below the phone outline is
dropped and only the bolt remains, which is all that reads at that size.
"""
from PIL import Image, ImageDraw, ImageFilter

TILE_TOP = (40, 92, 96)
TILE_BOTTOM = (20, 52, 58)
GLYPH = (176, 236, 224)          # soft mint, a quieter cousin of the theme's Accent
SHEEN = 24                       # alpha of the highlight at the top of the tile
BORDER = (255, 255, 255, 30)     # a hairline edge so the tile holds on dark taskbars

# The navigation bar's bolt (IconFlash), in a 24-unit box.
BOLT = [(13, 2), (4, 14), (11, 14), (10, 22), (19, 10), (12, 10)]


def tile(size, scale=8):
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Rounded tile with a vertical gradient.
    gradient = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(gradient)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE_BOTTOM)) + (255,))
    margin = round(s * 0.04)
    radius = round(s * 0.23)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin, s - margin], radius, fill=255)
    img.paste(gradient, (0, 0), mask)

    # A soft sheen that fades out towards the middle.
    sheen = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sheen)
    fade = int(s * 0.55)
    for y in range(fade):
        sd.line([(0, y), (s, y)], fill=(255, 255, 255, round(SHEEN * (1 - y / fade))))
    img = Image.alpha_composite(img, Image.composite(sheen, Image.new("RGBA", (s, s)), mask))
    ImageDraw.Draw(img).rounded_rectangle([margin, margin, s - margin, s - margin], radius,
                                          outline=BORDER, width=max(1, round(s * 0.006)))

    # The drawing goes on its own layer so it can cast a glow.
    art = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(art)
    small = size <= 32
    if small:
        # Bolt alone, filling most of the tile.
        box = (s * 0.20, s * 0.16, s * 0.60, s * 0.68)
    else:
        # Phone outline in a light stroke.
        w = s * 0.38
        h = s * 0.62
        left = (s - w) / 2
        top = (s - h) / 2
        stroke = max(1, round(s * 0.034))
        d.rounded_rectangle([left, top, left + w, top + h], round(s * 0.075), outline=GLYPH, width=stroke)
        # Earpiece.
        ear = s * 0.07
        d.rounded_rectangle([s / 2 - ear / 2, top + stroke * 2.0, s / 2 + ear / 2, top + stroke * 2.7],
                            round(stroke / 2), fill=GLYPH + (200,))
        # Bolt on the screen.
        box = (left + w * 0.26, top + h * 0.24, w * 0.48, h * 0.56)

    x0, y0, bw, bh = box
    k = min(bw / 15.0, bh / 20.0)  # the bolt spans x 4..19 and y 2..22
    ox = x0 + (bw - 15 * k) / 2 - 4 * k
    oy = y0 + (bh - 20 * k) / 2 - 2 * k
    points = [(ox + x * k, oy + y * k) for x, y in BOLT]
    d.polygon(points, fill=GLYPH)
    # Trace the outline with round joins to soften the bolt's corners.
    d.line(points + [points[0]], fill=GLYPH, width=max(1, round(k * 1.1)), joint="curve")

    if not small:
        glow = art.filter(ImageFilter.GaussianBlur(s * 0.025))
        glow.putalpha(glow.getchannel("A").point(lambda a: a * 0.55))
        img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, art)

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = [tile(n) for n in sizes]
    images[-1].save("icon.ico", format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    tile(512, scale=4).save("big_icon.png")


if __name__ == "__main__":
    main()
