# RustNet UI Designer

A WPF desktop editor for embedded UI. Design a screen visually, then save it as a
`RustNet.UI` XML layout the device loads at runtime — no code changes to reshape a
UI. Write the app behind it in the code pane, and **run it on a device from the
same window**. **Jack The Code Bender**, the built-in assistant, can design the
screen for you and write that code — [docs/assistant.md](assistant.md).

```bash
dotnet run --project dotnet/tools/RustNet.Designer          # launch the editor
dotnet run --project dotnet/tools/RustNet.Designer ui.xml   # open a layout
dotnet run --project dotnet/tools/RustNet.Designer app.cs   # open a C# file
```

## Layout

- **Command strip** (top): New / Open / Save / Save as / Close for the active
  document, then Delete / Up / Down for the selection, then the four panel
  toggles.
- **Deploy strip**: the target device, `Detect`, the app id, and Run — see
  [Running on a device](#running-on-a-device).
- **Toolbox** (left, collapsible): every `RustNet.UI` control — containers
  (stack/panel/border/canvas/grid/scrollviewer) and widgets (label/button/
  textbox/checkbox/radio/slider/progress/listbox/image/rect). Click to add one
  into the selected container.
- **Centre**, three tabs:
  - **DESIGN** — a WYSIWYG canvas that renders the tree exactly as the device
    paints it (RGB565 colors, the same two-pass Measure/Arrange layout), inside a
    bezel because that is what it is: a physical panel. Click a control to select
    it; the selection is outlined. **Drag** a control that lives in a `canvas` to
    reposition it — its `x`/`y` update live (clamped to non-negative).
    Layout-managed children (in a stack/grid/…) are placed by their container, so
    they don't drag.
  - **LAYOUT XML** — the same tree as markup, editable. **Apply to canvas**
    parses it and replaces the design; a parse error leaves the design alone and
    tells you why. **Reload from canvas** goes the other way.
  - **CODE** — app code, whether you wrote it, opened it or Jack generated it.
- **Inspector** (right, collapsible): the **element tree** (hierarchy, selection
  synced with the canvas) over the **properties** grid — id, text, position/size,
  colors (RGB565 hex, with a swatch showing the quantised colour the panel will
  really display), scale, slider/progress range, checkbox state, radio group, grid
  columns, container padding/gap/orientation, listbox items. Edits redraw the
  canvas live.
- **Assistant** (far right, collapsible): the chat panel.
- **Output** (bottom, collapsible): the build, deploy and detect log. Opens by
  itself the first time something writes to it.
- **Readout strip** (bottom): the selection, its laid-out position and size, and
  its foreground/background words — the numbers you would otherwise hunt for in
  the property grid — plus the panel size and the last status message.

Every panel hides and comes back with its width intact, so a narrow screen can
show just the canvas, or just the editor and the assistant.

## Two documents, one window

The layout and the code are separate documents, and every file command acts on
whichever tab is in front: **New**, **Open**, **Save**, **Save as** and
**Close**. A tab header carries a `•` while its document has unsaved changes, and
closing or replacing a dirty document asks first.

**Open** picks the pane from the extension — `.xml` loads onto the canvas, `.cs`
into the code pane. Saving a layout writes `Ui.ToXml`, which round-trips losslessly
with `Ui.LoadXml`, including container padding and gap, stack orientation, border
colour and radio groups. When the XML pane holds unapplied edits, those are what
Save and Run use — the text you are looking at is the layout.

## Editing code

Both editor tabs are the same pane, so the layout XML gets a real editor too:

| Action | Where |
|---|---|
| Cut / Copy / Paste / Undo / Redo | toolbar, or the usual gestures |
| Find | `Ctrl+F`, then `F3` / `Shift+F3` for next and previous |
| Replace | `Ctrl+H` — with match case, regular expressions, a live match count, and Replace All |
| Go to line | `Ctrl+G` |
| Format | `Alt+Shift+F` |

`Format` runs C# through Roslyn and XML through `XDocument`; JSON works too.
Text that does not parse comes back untouched with the parser's complaint, so
formatting a half-written file cannot damage it. Roslyn fixes indentation and
spacing while keeping your line breaks, which is what Format Document means in
an IDE.

## Running on a device

**Detect** probes the local virtual device and every serial port and keeps the
ones that answer an RNDP `info`, labelled with the board they reported. The chip
family comes from that answer and is used for signing — signing for the wrong
family produces a perfectly good image the device then refuses.

What **Run** does depends on the tab in front:

- **CODE** → a scratch project referencing every `RustNet.*` library →
  `dotnet build` (Debug, because the interpreter only models Debug-shaped async
  state machines) → RNX → RSA-signed RNSB → flashed over RNDP → started.
- **DESIGN** or **LAYOUT XML** → the layout is pushed to `/data/ui.xml` on the
  device filesystem. No reflash: that is the point of the XML format, and an app
  that reads it with `Ui.LoadXml` picks up the new screen on its next render.

`Stop` halts the running app and `Logs` reads the device log into the output
pane. All of it uses the same libraries as the `rustnet` CLI, in process, so what
the Designer sends is byte-identical to `rustnet flash --start`.

The signing key is `keys/rustnet-signing.key` in the checkout; the device must
already be provisioned with its public half
(`rustnet provision --key keys/rustnet-signing.pub`).

## Keyboard

| | |
|---|---|
| `Ctrl+N` / `Ctrl+O` / `Ctrl+S` / `Ctrl+W` | new, open, save, close the active document |
| `F5` | run on the target |
| `Del` | delete the selected element (not while typing) |
| `Ctrl+1` / `Ctrl+2` / `Ctrl+3` / `Ctrl+J` | toolbox, inspector, output, assistant |

## Verified path

The designer reuses the tested `RustNet.UI` model (`Ui.LoadXml` / `Ui.ToXml`) for
load/save and the same layout engine for the preview. A headless `--selftest`
covers load → render → round-trip → drag-to-move, the formatter, the assistant's
own checks, and the deploy pipeline end to end: it builds a scratch app, compiles
it to RNX, signs it, and — if a device is answering — flashes and starts it. An
end-to-end xunit check confirms a designer-saved XML loads and renders on the
virtual device pixel-for-pixel (the title and slider appear at the right colors
and positions). So: **design in the tool → run it on the chip.**
