"""Generates the 'dodaj_tacku' (add point) ribbon icon set for TcmInzenjering.Plugin.

Style: simple flat monoline icon (matches the CGS Labs / AutoCAD ribbon reference
the user pointed at) - a point/crosshair symbol with a green "add" badge, plus a
dashed alignment baseline with existing-point markers to make the "point being
added onto a road alignment" meaning clear at larger sizes. The 16px variant
drops the baseline detail (too fine to read) and keeps only the crosshair + badge.
"""

from PIL import Image, ImageDraw

BLUE = (41, 128, 185, 255)     # #2980B9 - main point/crosshair color
GRAY = (149, 165, 166, 255)    # #95A5A6 - baseline / existing points
GREEN = (39, 174, 96, 255)     # #27AE60 - "add" badge
WHITE = (255, 255, 255, 255)

SS = 8  # supersample factor for anti-aliasing


def draw_full(size):
    canvas = size * SS
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = canvas / 100.0  # unit scale

    def pt(x, y):
        return (x * u, y * u)

    # dashed alignment baseline
    y_base = 80
    dash, gap = 6, 4
    x = 8
    while x < 92:
        x2 = min(x + dash, 92)
        d.line([pt(x, y_base), pt(x2, y_base)], fill=GRAY, width=round(3.2 * u))
        x += dash + gap

    # existing point markers on the baseline
    for mx in (22, 46):
        s = 5
        d.rectangle([pt(mx - s, y_base - s), pt(mx + s, y_base + s)], fill=GRAY)

    # crosshair "new point" symbol
    cx, cy, r = 56, 40, 15
    ring_w = round(3.4 * u)
    d.ellipse([pt(cx - r, cy - r), pt(cx + r, cy + r)], outline=BLUE, width=ring_w)
    dot_r = 4.5
    d.ellipse([pt(cx - dot_r, cy - dot_r), pt(cx + dot_r, cy + dot_r)], fill=BLUE)
    tick = 7
    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        x1, y1 = cx + dx * r, cy + dy * r
        x2, y2 = cx + dx * (r + tick), cy + dy * (r + tick)
        d.line([pt(x1, y1), pt(x2, y2)], fill=BLUE, width=ring_w)

    # green "add" badge, bottom-right of the crosshair
    bx, by, br = 74, 57, 16
    d.ellipse([pt(bx - br, by - br), pt(bx + br, by + br)], fill=GREEN)
    plus_half, plus_w = 8, round(3.6 * u)
    d.line([pt(bx - plus_half, by), pt(bx + plus_half, by)], fill=WHITE, width=plus_w)
    d.line([pt(bx, by - plus_half), pt(bx, by + plus_half)], fill=WHITE, width=plus_w)

    return img.resize((size, size), Image.LANCZOS)


def draw_simple(size):
    """16px variant: crosshair + badge only, enlarged for legibility."""
    canvas = size * SS
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = canvas / 100.0

    def pt(x, y):
        return (x * u, y * u)

    cx, cy, r = 42, 42, 24
    ring_w = round(6.0 * u)
    d.ellipse([pt(cx - r, cy - r), pt(cx + r, cy + r)], outline=BLUE, width=ring_w)
    dot_r = 7
    d.ellipse([pt(cx - dot_r, cy - dot_r), pt(cx + dot_r, cy + dot_r)], fill=BLUE)
    tick = 10
    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        x1, y1 = cx + dx * r, cy + dy * r
        x2, y2 = cx + dx * (r + tick), cy + dy * (r + tick)
        d.line([pt(x1, y1), pt(x2, y2)], fill=BLUE, width=ring_w)

    bx, by, br = 72, 72, 24
    d.ellipse([pt(bx - br, by - br), pt(bx + br, by + br)], fill=GREEN)
    plus_half, plus_w = 12, round(6.4 * u)
    d.line([pt(bx - plus_half, by), pt(bx + plus_half, by)], fill=WHITE, width=plus_w)
    d.line([pt(bx, by - plus_half), pt(bx, by + plus_half)], fill=WHITE, width=plus_w)

    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    plugin_icons = r"C:\Users\User\Desktop\AUTOCAD PROGRAMS\TcmInzenjering.Plugin\Icons"
    ref_icons = r"C:\Users\User\Desktop\AUTOCAD PROGRAMS\ICONS"

    master = draw_full(1024)
    master.save(f"{ref_icons}\\dodaj_tacku_master.png")

    for size in (64, 48, 32):
        draw_full(size).save(f"{plugin_icons}\\dodaj_tacku_{size}.png")

    draw_simple(16).save(f"{plugin_icons}\\dodaj_tacku_16.png")

    # "name.png" alias == 64px version, matching existing convention (teren.png, situacija.png, ...)
    draw_full(64).save(f"{plugin_icons}\\dodaj_tacku.png")

    print("done")
