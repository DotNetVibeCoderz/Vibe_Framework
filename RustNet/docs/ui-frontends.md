# Alternative UI frontends

`RustNet.UI` draws on the device's display. Its layout, hit-testing and input
routing are plain logic, so the same element tree can be presented by other
frontends on a full OS — for simulators, gateway apps, tests and terminals.

## AsciiConsole

`dotnet/frontends/RustNet.UI.AsciiConsole` renders a layout as text. It arranges
the tree with the toolkit's own code and maps each pixel rectangle onto
character cells, one cell per glyph of the device's 8×8 font, so a 160×128
window is 20×16 characters and text lands where it does on the panel.

```bash
rustnet ui preview layout.xml                 # print one frame
rustnet ui preview layout.xml --interactive   # drive it from the keyboard
rustnet ui preview layout.xml --ascii         # 7-bit output for old terminals and CI logs
```

```
┌────────────────────────────┐
│Greenhouse                  │
│[x] Fan                     │
│──────────────────●─────────│
│[█████████░░░░░░░░░░░░░░░░░]│
│ North                      │
│>South                      │
│┌──────────────────────────┐│
││           Save           ││
│└──────────────────────────┘│
└────────────────────────────┘
```

In code:

```csharp
using RustNet.UI;
using RustNet.UI.AsciiConsole;

UiElement root = Ui.LoadXml(File.ReadAllText("layout.xml"));
Console.Write(new AsciiRenderer().Render(root));          // one frame

var host = new AsciiConsoleHost(root);
host.Changed = e => Console.Title = $"{e.Id} changed";
host.Run();                                               // Tab / Enter / arrows / Esc
```

Keyboard activation goes through `Ui.Tap`, so a checkbox, radio group, listbox
or combobox behaves exactly as a tap on the device. Every control kind is
drawn: containers and borders, labels and wrapped text, buttons, text boxes,
check boxes, radios, sliders, progress bars, list and combo boxes, gauges,
charts (block sparklines), data grids, calendars, tab controls, expanders,
tree views, message boxes, scroll viewers (clipped, with a thumb), and the
shape primitives.

## WinForms, GTK and Silk.NET + SkiaSharp

These show the UI **pixel for pixel as the device draws it**. RustNet.UI paints
through `RustNet.UI.Paint`, which forwards to the device `Display` unless a
frontend has installed an `IUiCanvas` in `Paint.Target`. The shared core,
`dotnet/frontends/RustNet.UI.Frontends`, provides `FramebufferCanvas` — an
RGB565 framebuffer that reproduces `rustnet-gfx` exactly (its Bresenham lines,
filled circles, clip semantics and font8x8 glyphs) — and `UiSession`, which
renders the tree into it and routes clicks, wheel and typing through
`Ui.Tap` / `Ui.Scroll`. Each toolkit only shows the frame:

| Project | Toolkit | Shows the frame with |
|---|---|---|
| `RustNet.UI.WinForms` (`net10.0-windows`) | Windows Forms | `UiControl` / `UiForm`, nearest-neighbour `DrawImage` |
| `RustNet.UI.Gtk` | GTK 3 via GtkSharp | `UiView` (a `DrawingArea`) / `UiWindow`, Cairo with `Filter.Nearest` |
| `RustNet.UI.Skia` | Silk.NET window + SkiaSharp | `SkiaUiWindow` on an OpenGL surface; `SkiaUiRenderer` draws into any `SKCanvas` |

Each project also runs as a viewer: `<project> layout.xml [--zoom N]`, and the
Skia one renders headlessly with `--png out.png`. In code:

```csharp
Application.Run(new RustNet.UI.WinForms.UiForm(xml, zoom: 3));        // WinForms

Gtk.Application.Init();                                                // GTK
new RustNet.UI.Gtk.UiWindow(xml, zoom: 3).ShowAll();
Gtk.Application.Run();

RustNet.UI.Skia.SkiaUiWindow.FromXml(xml, zoom: 3).Run();              // Silk.NET + Skia
```

**Verified** by `DesktopFramebufferMatchesTheDevicePixelForPixel`: a layout
with fifteen control kinds is rendered on the virtual device and by
`FramebufferCanvas`, and the two framebuffers must be identical. The Skia
raster output and the WinForms bitmap are checked against the framebuffer in
`tests/Frontends.Tests` and `tests/Frontends.Windows.Tests`. The GTK runtime
needs GTK 3 installed (on Windows GtkSharp fetches it on first run).
