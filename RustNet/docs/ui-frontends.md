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

## Other frontends

WinForms, GTK and Silk.NET + SkiaSharp frontends are planned on the same
principle — present the arranged tree, route input through `Ui.Tap` — and are
not implemented yet.
