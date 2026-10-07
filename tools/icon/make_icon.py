"""Draws the Fastboot Enhance icon: a teal tile holding a phone with a lightning bolt.

Run from the repository root:  python tools/icon/make_icon.py
Writes icon.ico (16 to 256 px) and big_icon.png (512 px). Each size is drawn on its own,
at 8x and scaled down, so small sizes stay crisp; at 32 px and below the phone outline is
dropped and only the bolt remains, which is all that reads at that size.
"""
from PIL import Image, ImageDraw

TEAL_TOP = (72, 228, 208)      # lighter than the theme's Accent, for the gradient's top
TEAL_BOTTOM = (25, 160, 146)
INK = (10, 13, 18)             # the theme's Rail colour
HIGHLIGHT = (255, 255, 255, 46)

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
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TEAL_TOP, TEAL_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    margin = round(s * 0.04)
    radius = round(s * 0.23)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin, s - margin], radius, fill=255)
    img.paste(gradient, (0, 0), mask)

    # A soft sheen that fades out towards the middle.
    sheen = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sheen)
    fade = int(s * 0.6)
    for y in range(fade):
        sd.line([(0, y), (s, y)], fill=(255, 255, 255, round(HIGHLIGHT[3] * (1 - y / fade))))
    sheen_mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(sheen_mask).rounded_rectangle([margin, margin, s - margin, s - margin], radius, fill=255)
    img = Image.alpha_composite(img, Image.composite(sheen, Image.new("RGBA", (s, s)), sheen_mask))

    d = ImageDraw.Draw(img)
    if size <= 32:
        # Bolt alone, filling most of the tile.
        box = (s * 0.18, s * 0.14, s * 0.64, s * 0.72)
    else:
        # Phone outline.
        w = s * 0.40
        h = s * 0.66
        left = (s - w) / 2
        top = (s - h) / 2
        stroke = max(1, round(s * 0.055))
        d.rounded_rectangle([left, top, left + w, top + h], round(s * 0.085), outline=INK, width=stroke)
        # Earpiece.
        ear = s * 0.08
        d.rounded_rectangle([s / 2 - ear / 2, top + stroke * 1.6, s / 2 + ear / 2, top + stroke * 2.3],
                            round(stroke / 2), fill=INK)
        # Bolt on the screen.
        box = (left + w * 0.22, top + h * 0.18, w * 0.56, h * 0.66)

    x0, y0, bw, bh = box
    k = min(bw / 15.0, bh / 20.0)  # the bolt spans x 4..19 and y 2..22
    ox = x0 + (bw - 15 * k) / 2 - 4 * k
    oy = y0 + (bh - 20 * k) / 2 - 2 * k
    d.polygon([(ox + x * k, oy + y * k) for x, y in BOLT], fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = [tile(n) for n in sizes]
    images[-1].save("icon.ico", format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    tile(512, scale=4).save("big_icon.png")


if __name__ == "__main__":
    main()
