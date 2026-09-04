"""One-off script to generate the MDreader app icon (rounded-square Fluent
tile + "M-down" markdown mark) at master resolution, then derive every size
needed for the .ico and the unpackaged app's tile PNGs. Not part of the build;
run manually whenever the icon needs regenerating."""

import math
from PIL import Image, ImageDraw, ImageFont

SIZE = 1024
CORNER_RADIUS = round(SIZE * 0.22)

TOP_COLOR = (58, 150, 221)      # #3A96DD - lighter Windows 11 accent blue
BOTTOM_COLOR = (11, 73, 145)    # #0B4991 - deep accent blue
GLYPH_COLOR = (255, 255, 255)
HIGHLIGHT_COLOR = (255, 255, 255, 40)


def rounded_square_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def vertical_gradient(size, top, bottom):
    grad = Image.new("RGB", (1, size), color=0)
    for y in range(size):
        t = y / (size - 1)
        r = round(top[0] + (bottom[0] - top[0]) * t)
        g = round(top[1] + (bottom[1] - top[1]) * t)
        b = round(top[2] + (bottom[2] - top[2]) * t)
        grad.putpixel((0, y), (r, g, b))
    return grad.resize((size, size))


def load_bold_font(px_size):
    candidates = [
        r"C:\Windows\Fonts\segoeuib.ttf",
        r"C:\Windows\Fonts\arialbd.ttf",
    ]
    for path in candidates:
        try:
            return ImageFont.truetype(path, px_size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_icon():
    base = vertical_gradient(SIZE, TOP_COLOR, BOTTOM_COLOR).convert("RGBA")
    mask = rounded_square_mask(SIZE, CORNER_RADIUS)

    tile = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    tile.paste(base, (0, 0), mask)

    draw = ImageDraw.Draw(tile)

    # Subtle top sheen for depth (Fluent-style soft highlight).
    sheen = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sheen_draw = ImageDraw.Draw(sheen)
    sheen_draw.rounded_rectangle(
        [SIZE * 0.06, SIZE * 0.05, SIZE * 0.94, SIZE * 0.5],
        radius=CORNER_RADIUS,
        fill=(255, 255, 255, 28),
    )
    sheen_mask = rounded_square_mask(SIZE, CORNER_RADIUS)
    tile = Image.alpha_composite(tile, Image.composite(sheen, Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0)), sheen_mask))
    draw = ImageDraw.Draw(tile)

    # "M" glyph, left of center.
    m_font = load_bold_font(round(SIZE * 0.46))
    m_text = "M"
    m_bbox = draw.textbbox((0, 0), m_text, font=m_font)
    m_w, m_h = m_bbox[2] - m_bbox[0], m_bbox[3] - m_bbox[1]

    # Down-chevron glyph, to the right of the M (the "down" in Markdown).
    chevron_h = SIZE * 0.16
    chevron_w = SIZE * 0.22
    gap = SIZE * 0.04

    total_w = m_w + gap + chevron_w
    start_x = (SIZE - total_w) / 2
    center_y = SIZE * 0.52

    m_x = start_x - m_bbox[0]
    m_y = center_y - m_h / 2 - m_bbox[1]
    draw.text((m_x, m_y), m_text, font=m_font, fill=GLYPH_COLOR)

    chevron_cx = start_x + m_w + gap + chevron_w / 2
    chevron_cy = center_y
    stroke = round(SIZE * 0.052)
    half_w = chevron_w / 2
    half_h = chevron_h / 2
    points_left = [
        (chevron_cx - half_w, chevron_cy - half_h),
        (chevron_cx, chevron_cy + half_h),
    ]
    points_right = [
        (chevron_cx, chevron_cy + half_h),
        (chevron_cx + half_w, chevron_cy - half_h),
    ]
    draw.line(points_left, fill=GLYPH_COLOR, width=stroke, joint="curve")
    draw.line(points_right, fill=GLYPH_COLOR, width=stroke, joint="curve")
    cap_r = stroke / 2
    for x, y in points_left + points_right:
        draw.ellipse([x - cap_r, y - cap_r, x + cap_r, y + cap_r], fill=GLYPH_COLOR)

    return tile


if __name__ == "__main__":
    icon = draw_icon()
    icon.save("icon_master_1024.png")
    print("Saved icon_master_1024.png")
