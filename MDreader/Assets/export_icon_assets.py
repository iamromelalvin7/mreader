"""Derives the .ico and all Package.appxmanifest tile PNGs from
icon_master_1024.png (produced by generate_icon.py). One-off script, not part
of the build."""

from PIL import Image, ImageDraw
from generate_icon import vertical_gradient, draw_icon, TOP_COLOR, BOTTOM_COLOR, GLYPH_COLOR

MASTER = Image.open("icon_master_1024.png")


def square_asset(size, path):
    MASTER.resize((size, size), Image.LANCZOS).save(path)
    print(f"Saved {path} ({size}x{size})")


def wide_asset(width, height, path, with_wordmark=False):
    canvas = vertical_gradient(height, TOP_COLOR, BOTTOM_COLOR).convert("RGBA").resize((width, height))
    glyph_h = round(height * 0.4)
    glyph = draw_icon_glyph_only(glyph_h)
    gap = round(width * 0.035)

    if with_wordmark:
        from PIL import ImageFont
        font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", round(height * 0.18))
        draw = ImageDraw.Draw(canvas)
        text = "MDreader"
        bbox = draw.textbbox((0, 0), text, font=font)
        text_w, text_h = bbox[2] - bbox[0], bbox[3] - bbox[1]

        total_w = glyph.width + gap + text_w
        gx = (width - total_w) // 2
        gy = (height - glyph.height) // 2
        canvas.paste(glyph, (gx, gy), glyph)

        tx = gx + glyph.width + gap - bbox[0]
        ty = (height - text_h) / 2 - bbox[1]
        draw.text((tx, ty), text, font=font, fill=GLYPH_COLOR)
    else:
        gx = (width - glyph.width) // 2
        gy = (height - glyph.height) // 2
        canvas.paste(glyph, (gx, gy), glyph)

    canvas.save(path)
    print(f"Saved {path} ({width}x{height})")


def draw_icon_glyph_only(target_h):
    # Crop the glyph region out of the master tile (no rounded background) by
    # rendering directly at the requested glyph height via the shared drawer,
    # then trimming the transparent/background padding around the glyph.
    scale = target_h / 1024
    big = draw_icon()
    # Replace the tile background with full transparency, keep only the glyph
    # by masking out near-background-color pixels is fragile; instead redraw
    # glyph-only at the right size using the same proportions as generate_icon.
    from generate_icon import load_bold_font
    size = round(1024 * scale / 0.62)  # back out an equivalent "SIZE" so glyph proportions match
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    m_font = load_bold_font(round(size * 0.46))
    m_text = "M"
    m_bbox = draw.textbbox((0, 0), m_text, font=m_font)
    m_w, m_h = m_bbox[2] - m_bbox[0], m_bbox[3] - m_bbox[1]

    chevron_h = size * 0.16
    chevron_w = size * 0.22
    gap = size * 0.04

    total_w = m_w + gap + chevron_w
    start_x = (size - total_w) / 2
    center_y = size * 0.52

    m_x = start_x - m_bbox[0]
    m_y = center_y - m_h / 2 - m_bbox[1]
    draw.text((m_x, m_y), m_text, font=m_font, fill=GLYPH_COLOR)

    chevron_cx = start_x + m_w + gap + chevron_w / 2
    chevron_cy = center_y
    stroke = round(size * 0.052)
    half_w = chevron_w / 2
    half_h = chevron_h / 2
    points_left = [(chevron_cx - half_w, chevron_cy - half_h), (chevron_cx, chevron_cy + half_h)]
    points_right = [(chevron_cx, chevron_cy + half_h), (chevron_cx + half_w, chevron_cy - half_h)]
    draw.line(points_left, fill=GLYPH_COLOR, width=stroke, joint="curve")
    draw.line(points_right, fill=GLYPH_COLOR, width=stroke, joint="curve")
    cap_r = stroke / 2
    for x, y in points_left + points_right:
        draw.ellipse([x - cap_r, y - cap_r, x + cap_r, y + cap_r], fill=GLYPH_COLOR)

    bbox = canvas.getbbox()
    cropped = canvas.crop(bbox)
    return cropped.resize((round(cropped.width * target_h / cropped.height), target_h), Image.LANCZOS)


if __name__ == "__main__":
    # Win32 .exe icon (ApplicationIcon) - the one that actually matters for
    # this unpackaged app's taskbar/title-bar/Explorer appearance.
    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    MASTER.save(
        "AppIcon.ico",
        sizes=[(s, s) for s in ico_sizes],
    )
    print("Saved AppIcon.ico")

    # Package.appxmanifest tile assets (only consumed if/when this app is
    # ever packaged as MSIX; kept in sync for completeness).
    square_asset(50, "StoreLogo.png")
    square_asset(88, "Square44x44Logo.scale-200.png")
    square_asset(24, "Square44x44Logo.targetsize-24_altform-unplated.png")
    square_asset(300, "Square150x150Logo.scale-200.png")
    square_asset(48, "LockScreenLogo.scale-200.png")
    wide_asset(620, 300, "Wide310x150Logo.scale-200.png", with_wordmark=True)
    wide_asset(1240, 600, "SplashScreen.scale-200.png", with_wordmark=True)
