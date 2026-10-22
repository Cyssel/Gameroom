# Generates Gameroom's visual identity (app icons, tray icons, theme logos and splash screens).
# The mark is a doorway (the "room") with a geometric "G" inside it.
#
#   python build\branding\generate_branding.py            writes the images into source\
#   python build\branding\generate_branding.py --preview <dir>  also writes preview sheets into <dir>
#
# Requires Pillow. Only file contents change: names and paths stay the ones Playnite themes expect.

import io
import math
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(ROOT, "source")
DESKTOP_IMAGES = os.path.join(SOURCE, "Playnite.DesktopApp", "Themes", "Desktop", "Default", "Images")
FULLSCREEN_IMAGES = os.path.join(SOURCE, "Playnite.FullscreenApp", "Themes", "Fullscreen", "Default", "Images")

# Brand palette: violet to blue, diagonal from the top left corner.
BRAND_START = (139, 92, 246)
BRAND_END = (37, 99, 235)
GRAY_START = (74, 74, 78)
GRAY_END = (168, 168, 172)
SPLASH_START = (13, 15, 30)
SPLASH_END = (35, 42, 84)

APP_ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
TRAY_ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SUPERSAMPLE = 8


def mark_geometry(size):
    """Mark geometry in target pixels. Small sizes are snapped to the pixel grid so they stay sharp."""
    n = float(size)
    snap = round if size <= 64 else (lambda v: v)
    x0 = snap(n * 0.09)
    y0 = snap(n * 0.03)
    door = (x0, y0, n - x0, n - y0)
    # The G is the same doorway shape, inset by an even margin and drawn as an outline.
    inset = snap(n * 0.13)
    letter = (door[0] + inset, door[1] + inset, door[2] - inset, door[3] - inset)
    stroke = max(2, snap(n * 0.125))
    return {
        "door": door,
        "door_corner": n * 0.08,
        "letter": letter,
        "letter_corner": stroke * 1.1,
        "stroke": stroke,
    }


def draw_arch(draw, box, corner, fill):
    """Doorway shape: half circle on top, straight sides, slightly rounded bottom corners."""
    x0, y0, x1, y1 = box
    r = (x1 - x0) / 2
    draw.ellipse((x0, y0, x1, y0 + 2 * r), fill=fill)
    if corner > 0:
        draw.rounded_rectangle((x0, y0 + r, x1, y1), radius=corner, fill=fill)
        draw.rectangle((x0, y0 + r, x1, y1 - corner), fill=fill)
    else:
        draw.rectangle((x0, y0 + r, x1, y1), fill=fill)


def door_mask(size, geo, ss):
    mask = Image.new("L", (size * ss, size * ss), 0)
    draw_arch(ImageDraw.Draw(mask), [v * ss for v in geo["door"]], geo["door_corner"] * ss, 255)
    return mask


def g_mask(size, geo, ss):
    x0, y0, x1, y1 = [v * ss for v in geo["letter"]]
    stroke = geo["stroke"] * ss
    corner = geo["letter_corner"] * ss
    radius = (x1 - x0) / 2
    cx, cy = x0 + radius, y0 + radius
    mask = Image.new("L", (size * ss, size * ss), 0)
    draw = ImageDraw.Draw(mask)
    draw_arch(draw, (x0, y0, x1, y1), corner, 255)
    draw_arch(draw, (x0 + stroke, y0 + stroke, x1 - stroke, y1 - stroke), max(0, corner - stroke), 0)
    # Opening of the G on the upper right; PIL angles go clockwise from 3 o'clock.
    reach = radius * 1.5
    draw.pieslice((cx - reach, cy - reach, cx + reach, cy + reach), start=-50, end=0, fill=0)
    # Crossbar from the middle to the right side, its top edge on the arch's center line.
    draw.rectangle((cx, cy, x1, cy + stroke), fill=255)
    return mask


def diagonal_gradient(width, height, start, end):
    """Linear gradient from the top left to the bottom right corner."""
    small = 64
    grad = Image.new("RGB", (small, small))
    pixels = grad.load()
    for y in range(small):
        for x in range(small):
            t = (x + y) / (2.0 * (small - 1))
            pixels[x, y] = tuple(int(round(s + (e - s) * t)) for s, e in zip(start, end))
    return grad.resize((width, height), Image.BILINEAR)


def horizontal_gradient(width, height, start, end):
    line = Image.new("RGB", (width, 1))
    pixels = line.load()
    for x in range(width):
        t = x / float(width - 1)
        pixels[x, 0] = tuple(int(round(s + (e - s) * t)) for s, e in zip(start, end))
    return line.resize((width, height), Image.NEAREST)


def render_mark(size, style="brand"):
    """style: brand (colored door, white G), gray (default game icon), white / black (tray silhouettes)."""
    ss = SUPERSAMPLE if size <= 128 else 4
    big = size * ss
    geo = mark_geometry(size)
    door = door_mask(size, geo, ss)
    letter = g_mask(size, geo, ss)

    if style in ("white", "black"):
        color = (255, 255, 255) if style == "white" else (0, 0, 0)
        alpha = Image.composite(Image.new("L", door.size, 0), door, letter)
        image = Image.new("RGBA", (big, big), color + (0,))
        image.putalpha(alpha)
    else:
        start, end = (BRAND_START, BRAND_END) if style == "brand" else (GRAY_START, GRAY_END)
        fill = diagonal_gradient(big, big, start, end)
        fill = Image.composite(Image.new("RGB", (big, big), (255, 255, 255)), fill, letter)
        image = fill.convert("RGBA")
        image.putalpha(door)

    return downsample(image, (size, size))


def downsample(image, size):
    # Resize with premultiplied alpha so transparent pixels don't bleed dark fringes.
    return image.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def bmp_icon_entry(image):
    """32-bit BGRA DIB with an AND mask, the classic format every icon consumer understands."""
    width, height = image.size
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    data = image.tobytes("raw", "BGRA")
    stride = width * 4
    for y in range(height - 1, -1, -1):
        rows.append(data[y * stride:(y + 1) * stride])
    mask_stride = ((width + 31) // 32) * 4
    alpha = image.getchannel("A").load()
    mask_rows = []
    for y in range(height - 1, -1, -1):
        row = bytearray(mask_stride)
        for x in range(width):
            if alpha[x, y] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask_rows.append(bytes(row))
    return header + b"".join(rows) + b"".join(mask_rows)


def png_bytes(image):
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path, images):
    entries = []
    for image in sorted(images, key=lambda i: i.size[0]):
        size = image.size[0]
        data = png_bytes(image) if size >= 256 else bmp_icon_entry(image)
        entries.append((size, data))
    offset = 6 + 16 * len(entries)
    header = struct.pack("<HHH", 0, 1, len(entries))
    directory = b""
    payload = b""
    for size, data in entries:
        dim = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        payload += data
        offset += len(data)
    with open(path, "wb") as file:
        file.write(header + directory + payload)


def find_font(names, size):
    fonts_dir = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
    for name in names:
        path = os.path.join(fonts_dir, name)
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise RuntimeError("None of these fonts were found: " + ", ".join(names))


def render_splash(width=684, height=354):
    scale = 4
    w, h = width * scale, height * scale
    background = horizontal_gradient(w, h, SPLASH_START, SPLASH_END).convert("RGBA")
    shape = Image.new("L", (w, h), 0)
    ImageDraw.Draw(shape).rounded_rectangle((0, 0, w - 1, h - 1), radius=16 * scale, fill=255)
    background.putalpha(shape)

    border = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle((0, 0, w - 1, h - 1), radius=16 * scale,
                                             outline=(255, 255, 255, 28), width=scale)
    background.alpha_composite(border)

    mark_size = 128
    font = find_font(["seguisb.ttf", "segoeuib.ttf", "segoeui.ttf"], 76 * scale)
    text = "Gameroom"
    left, top, right, bottom = font.getbbox(text)
    text_width = right - left
    gap = 30 * scale
    total = mark_size * scale + gap + text_width
    x = (w - total) // 2
    mark = render_mark(mark_size * scale)
    background.alpha_composite(mark, (x, (h - mark_size * scale) // 2))

    draw = ImageDraw.Draw(background)
    # Center the text on the cap height so "Gameroom" sits level with the mark.
    cap_top, cap_bottom = font.getbbox("G")[1], font.getbbox("G")[3]
    text_y = h // 2 - (cap_top + cap_bottom) // 2
    draw.text((x + mark_size * scale + gap - left, text_y), text, font=font, fill=(255, 255, 255, 255))

    return downsample(background, width) if width == height else \
        background.convert("RGBa").resize((width, height), Image.LANCZOS).convert("RGBA")


def preview(outputs, folder):
    os.makedirs(folder, exist_ok=True)
    sheet = Image.new("RGBA", (880, 300), (40, 42, 48, 255))
    light = Image.new("RGBA", (440, 150), (238, 238, 240, 255))
    sheet.alpha_composite(light, (440, 0))
    x = 10
    for size in APP_ICON_SIZES[:-2]:
        icon = render_mark(size)
        sheet.alpha_composite(icon, (x, 10))
        sheet.alpha_composite(icon, (x + 440, 10))
        x += size + 8
    x = 10
    for style in ("white", "black", "gray"):
        for size in (16, 24, 32):
            sheet.alpha_composite(render_mark(size, style), (x, 170))
            sheet.alpha_composite(render_mark(size, style), (x, 230))
            x += size + 8
        x += 20
    for size in (16, 24, 32):
        zoom = render_mark(size).resize((size * 8, size * 8), Image.NEAREST)
        outputs.append(("preview_%d.png" % size, zoom))
    sheet.save(os.path.join(folder, "sheet.png"))
    for name, image in outputs:
        image.save(os.path.join(folder, name))


def main():
    brand = {size: render_mark(size) for size in APP_ICON_SIZES}
    logo = brand[256]
    gray = render_mark(256, "gray")
    splash = render_splash()

    for images_dir in (DESKTOP_IMAGES, FULLSCREEN_IMAGES):
        write_ico(os.path.join(images_dir, "applogo.ico"), list(brand.values()))
        logo.save(os.path.join(images_dir, "applogo.png"), optimize=True)

    gray.save(os.path.join(DESKTOP_IMAGES, "applogo_dark.png"), optimize=True)
    write_ico(os.path.join(DESKTOP_IMAGES, "applogo_white.ico"),
              [render_mark(size, "white") for size in TRAY_ICON_SIZES])
    write_ico(os.path.join(DESKTOP_IMAGES, "applogo_black.ico"),
              [render_mark(size, "black") for size in TRAY_ICON_SIZES])
    logo.save(os.path.join(FULLSCREEN_IMAGES, "applogo_clean.png"), optimize=True)
    logo.save(os.path.join(FULLSCREEN_IMAGES, "applogo_full.png"), optimize=True)

    for app in ("Playnite.DesktopApp", "Playnite.FullscreenApp"):
        splash.save(os.path.join(SOURCE, app, "SplashScreen.png"), optimize=True)

    if "--preview" in sys.argv:
        folder = sys.argv[sys.argv.index("--preview") + 1]
        preview([("splash.png", splash), ("logo.png", logo), ("gray.png", gray)], folder)


if __name__ == "__main__":
    main()
