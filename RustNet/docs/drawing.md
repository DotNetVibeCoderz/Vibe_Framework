# Image decoders & drawing (`RustNet.Drawing`)

A `System.Drawing`-shaped API for embedded images: decode BMP, GIF, PNG and
JPEG into a display-ready `Bitmap` (RGB565 surface) and blit it to the
display in one call. BMP/GIF are pure managed code (run on any chip); PNG and
JPEG decode in the runtime (the interpreter would be far slower for inflate /
DCT). Either way the blit uses an efficient `Display.DrawImage` intrinsic
(one buffer copy, not one intrinsic per pixel).

## Bitmap

```csharp
Bitmap bmp = Bitmap.Decode(imageBytes);   // sniffs BMP/GIF/PNG/JPEG from the header
int w = bmp.Width, h = bmp.Height;
ushort px = bmp.GetPixel(0, 0);           // RGB565
byte[] buf = bmp.ToRgb565Bytes();         // little-endian, for DrawImage
ushort c = Bitmap.Rgb565(255, 128, 0);    // pack 8-8-8 → RGB565

bool masked = bmp.HasAlpha;               // does it carry coverage?
byte a = bmp.GetAlpha(0, 0);              // 0 transparent, 255 opaque
bmp.SetAlpha(0, 0, 128);                  // creates the channel on first use
bmp.SetPixel(0, 0, c, 128);               // colour and coverage together
byte[] mask = bmp.ToAlphaBytes();         // empty when the image is opaque
```

## Transparency

Coverage lives in a **separate 8-bit channel beside the RGB565 pixels**, not
inside them. The framebuffer is 16 bits per pixel with no spare bit, and
widening every pixel to carry one would double a 150 KB frame to buy something
only some images use. A parallel byte per pixel costs 50% more for the images
that need it and nothing at all for the ones that do not — which is why the
channel is *absent*, not zeroed, until something sets it.

Three behaviours are worth knowing, because each is a judgement rather than a
mechanism:

- **The channel starts opaque.** Making one pixel transparent must not erase
  every other one, so the first `SetAlpha` fills the channel with 255 and then
  writes the pixel you asked for.
- **A 32-bit BMP whose alpha is zero everywhere is read as having none.**
  Plenty of writers leave that byte at zero meaning "unused", and honouring it
  literally turns the whole image invisible. Treating it as absent is the
  interpretation a caller can recover from.
- **GIF transparency comes from the Graphic Control Extension**, and only when
  its flag says the index means anything — otherwise index 0 would read as
  transparent in every GIF that does not use it. The transparent pixel keeps
  its palette colour, so an application that flattens the image later gets the
  author's background rather than black.

## Decoders

| Format | Support |
|---|---|
| BMP | uncompressed 24-bit and 32-bit `BITMAPINFOHEADER`, top-down or bottom-up, **32-bit alpha honoured** (managed) |
| GIF | 87a/89a, global/local colour table, **LZW** decompression, interlaced frames de-interlaced (first frame), **transparent index honoured** (managed) |
| PNG | full decoder via the runtime (`RustNet.Drawing.Native::DecodeRgb565`, backed by the Rust `image` crate → inflate + unfilter + de-palette) |
| JPEG | baseline + progressive via the runtime (same native intrinsic) |

`Bitmap.Decode` sniffs the header: BMP (`BM`) and GIF (`GIF`) decode in
managed IL; PNG (`89 50 4E 47`) and JPEG (`FF D8 FF`) are handed to the
native `DecodeRgb565` intrinsic, which returns
`[width:u16 LE][height:u16 LE][rgb565 LE …]`. On a target whose firmware is
`no_std` (no `image` crate) the native call is unavailable — use BMP/GIF
there.

## Drawing to the display

```csharp
using RustNet.Drawing;
using RustNet.Graphics;

Bitmap logo = Bitmap.Decode(bytes);
Display.Init(logo.Width, logo.Height);
Display.DrawImage(0, 0, logo.Width, logo.Height, logo.ToRgb565Bytes());
Display.Present();
```

`Display.DrawImage(x, y, w, h, rgb565)` blits a decoded image (RGB565
little-endian, row-major), clipped to the panel, in a single call.

For an image with coverage, `Display.DrawImageAlpha` takes the mask alongside
it and blends per pixel:

```csharp
if (logo.HasAlpha)
{
    Display.DrawImageAlpha(0, 0, logo.Width, logo.Height,
        logo.ToRgb565Bytes(), logo.ToAlphaBytes());
}
else
{
    Display.DrawImage(0, 0, logo.Width, logo.Height, logo.ToRgb565Bytes());
}
```

Two calls rather than one because `RustNet.Drawing` stays independent of
`RustNet.Graphics`: an image is data, and a decoder that reaches for the
display drags the display into every program that only wanted to read a file.
`HasAlpha` is the whole of the decision.

A mask shorter than the image is opaque past where it runs out — an image that
loses its mask should appear, not vanish. `Display.BlendImage` is the other
tool and a different one: it applies a **single** alpha to a whole image, for
fades and uniform overlays, and needs no channel.

## Panel configuration, rotation & clipping

```csharp
// Select the panel driver, physical size and clockwise rotation.
Display.Configure(PanelDriver.St7735, 160, 128, 90);
int w = Display.Width(), h = Display.Height();   // rotation-aware logical size

Display.SetClip(10, 10, 40, 40);                 // mask drawing to a rectangle
Display.DrawImage(0, 0, bmp.Width, bmp.Height, bmp.ToRgb565Bytes());
Display.ClearClip();
```

`Configure` picks the driver (`Ssd1306`/`St7735`/`Ili9341`/`Generic`; advisory
on the virtual device) and rotates the surface 0/90/180/270° — `Width()`/
`Height()` then report the logical size (swapped for 90/270). `SetClip`
constrains every primitive (lines, rects, text, blits, gradients, blends) to
a rectangle until `ClearClip`; reads are never clipped. Rotation and clip are
applied in the framebuffer, so apps draw in logical coordinates and the
runtime maps them to the physical panel.

## Verification

BMP and GIF decode is unit-tested pixel-exact against ground-truth vectors
(a 12-colour image encoded by PIL, expected RGB565 computed from each
format's own output — so GIF's LZW and palette handling are checked
end to end). An on-device test decodes a GIF on the interpreter, blits it,
and the captured framebuffer matches the expected pixels exactly. PNG and
JPEG are verified on-device too: an app embeds a 16×12 PNG and JPEG (left
half red, right half green), decodes both via the native intrinsic, blits
them, and the captured framebuffer shows the expected RGB565 colours for
each.
