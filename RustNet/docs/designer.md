# RustNet UI Designer

A WPF desktop editor for embedded UI. Design a screen visually, then save
it as a `RustNet.UI` XML layout the device loads at runtime — no code
changes to reshape a UI.

```bash
dotnet run --project dotnet/tools/RustNet.Designer          # launch the editor
dotnet run --project dotnet/tools/RustNet.Designer ui.xml   # open a file
```

## Layout (Visual-Studio-WPF style)

- **Toolbox** (left): every `RustNet.UI` control — containers
  (stack/panel/border/canvas/grid/scrollviewer) and widgets (label/button/
  textbox/checkbox/radio/slider/progress/listbox/image/rect). Click to add
  one into the selected container.
- **Design surface** (center): a WYSIWYG canvas that renders the tree
  exactly as the device paints it (RGB565 colors, the same two-pass
  Measure/Arrange layout). Click a control to select it; the selection is
  outlined. **Drag** a control that lives in a `canvas` to reposition it —
  its `x`/`y` update live (clamped to non-negative). Layout-managed children
  (in a stack/grid/…) are placed by their container, so they don't drag.
- **Element Tree** (top-right): the layout hierarchy; selection is synced
  with the canvas.
- **Properties** (bottom-right): edit the selected control — id, text,
  position/size, colors (RGB565 hex), scale, slider/progress range, checkbox
  state, radio group, grid columns, container padding/gap/orientation,
  listbox items. Edits redraw the canvas live.

## File round-trip

**File → Open** parses an existing `RustNet.UI` XML into the tree;
**Save / Save As** writes it back with `Ui.ToXml`. The format round-trips
losslessly with `Ui.LoadXml`, so a hand-edited layout and a designer-edited
one interoperate. Edit menu: delete, move up/down.

## Verified path

The designer reuses the tested `RustNet.UI` model (`Ui.LoadXml` /
`Ui.ToXml`) for load/save and the same layout engine for the preview. A
headless `--selftest` (load → render → round-trip → drag-to-move) runs in CI; an
end-to-end check confirms a designer-saved XML loads and renders on the
virtual device pixel-for-pixel (the title and slider appear at the right
colors and positions). So: **design in the tool → save XML → runs on the
chip.**
