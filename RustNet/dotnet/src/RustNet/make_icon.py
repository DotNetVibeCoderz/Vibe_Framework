"""Draw the RustNet NuGet package icon (icon.png, 256x256).

C# on a chip, powered by Rust: a microcontroller die on .NET purple, its pins
in rust orange, a C# sharp sign on the die. Drawn at 4x and downsampled so the
edges are antialiased; the shapes are chosen to stay legible at the 32 px the
nuget.org gallery list uses. Regenerate with `python make_icon.py`.
"""
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # working size; downsampled to 256
OUT = 256

PURPLE_TOP = (104, 58, 232)
PURPLE_BOTTOM = (38, 14, 110)
DIE = (22, 14, 44)
DIE_EDGE = (70, 44, 140)
RUST = (247, 106, 31)
RUST_DARK = (196, 66, 20)
WHITE = (255, 255, 255)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def main():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Rounded-square tile with a vertical gradient.
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(PURPLE_TOP, PURPLE_BOTTOM, y / S) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=210, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # Pins: four per side, rust orange, drawn under the die.
    die0, die1 = 262, 762
    pin_w, pin_len = 58, 120
    centres = [die0 + (die1 - die0) * (i + 0.5) / 4 for i in range(4)]
    for c in centres:
        c = int(c)
        for box in (
            [c - pin_w // 2, die0 - pin_len, c + pin_w // 2, die0 + 20],   # top
            [c - pin_w // 2, die1 - 20, c + pin_w // 2, die1 + pin_len],   # bottom
            [die0 - pin_len, c - pin_w // 2, die0 + 20, c + pin_w // 2],   # left
            [die1 - 20, c - pin_w // 2, die1 + pin_len, c + pin_w // 2],   # right
        ):
            d.rounded_rectangle(box, radius=14, fill=RUST)

    # Soft orange glow behind the die, so it reads as powered.
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).rounded_rectangle([die0 - 30, die0 - 30, die1 + 30, die1 + 30], radius=110,
                                           fill=RUST + (110,))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(40)))
    d = ImageDraw.Draw(img)

    # The die.
    d.rounded_rectangle([die0, die0, die1, die1], radius=80, fill=DIE, outline=DIE_EDGE, width=14)
    # Pin-1 marker.
    d.ellipse([die0 + 44, die0 + 44, die0 + 92, die0 + 92], fill=RUST_DARK)

    # The sharp sign: two slanted verticals, two horizontals.
    cx, cy = (die0 + die1) // 2, (die0 + die1) // 2 + 6
    bar = 54
    half = 150
    slant = 34
    for dx in (-62, 62):
        d.polygon([
            (cx + dx - bar // 2 + slant, cy - half),
            (cx + dx + bar // 2 + slant, cy - half),
            (cx + dx + bar // 2 - slant, cy + half),
            (cx + dx - bar // 2 - slant, cy + half),
        ], fill=WHITE)
    for dy in (-58, 58):
        d.rounded_rectangle([cx - half, cy + dy - bar // 2, cx + half, cy + dy + bar // 2], radius=10, fill=WHITE)

    img.resize((OUT, OUT), Image.LANCZOS).save("icon.png", optimize=True)


if __name__ == "__main__":
    main()
