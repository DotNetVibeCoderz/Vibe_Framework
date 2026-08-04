# UI toolkit (WPF/Glide-style, XML-loadable)

`RustNet.UI` renders an element tree to the device display through
`RustNet.Graphics.Display` — the same model as WPF/NETMF-Glide, sized for
small panels: build the tree in code or load it from XML markup, mutate
elements by id, re-render.

A layout drawn by the device itself, captured over RNDP:

![A RustNet.UI screen running on an M5Stack Tough](images/device-mqtt-dashboard.png)

## Elements

One concrete node type (`UiElement`) carries every kind:

| Kind | Renders | Notable properties |
|---|---|---|
| `window` | root container | `width`, `height`, `bg`, `pad`, `gap` |
| `stack` | vertical/horizontal stack | `orient="horizontal"`, `pad`, `gap` |
| `panel` / `border` | filled/bordered container | `bg`, `border`, `pad` |
| `canvas` | absolute positioning | children use `x`, `y` |
| `grid` | N-column grid | `columns`, `gap` |
| `scrollviewer` | clipped, vertically scrollable viewport | `height` (viewport), `scroll` (offset) |
| `dockpanel` | children take edges in declaration order | child `dock="left/top/right/bottom"` |
| `groupbox` | frame with a title notched into it | `text`, `border` |
| `expander` | header that shows or hides its children | `text`, `checked` (expanded) |
| `tabcontrol` | tab strip + one visible page | `selected`; children are `tabitem` |
| `tabitem` | one page of a `tabcontrol` | `text` (the tab's label) |
| `label` / `textblock` | text | `text`, `fg`, `scale` |
| `button` | bordered box + text | `text`, `fg`, `bg`, `border` |
| `textbox` | bordered input box + text | `text`, `fg`, `border` |
| `checkbox` | box + tick + label | `text`, `checked` |
| `radio` | circle + dot + label | `text`, `checked`, `group` |
| `slider` | track + knob | `min`, `max`, `value`, `fg` |
| `progress` | value bar | `value`, `min`, `max`, `fg` |
| `listbox` | bordered list, selected row highlighted | `items="A;B;C"`, `selected` |
| `combobox` | closed box showing the selection; a tap advances it | `items="A;B;C"`, `selected` |
| `textflow` | paragraph text, wrapped to the width it is given | `text`, `scale` |
| `gauge` | 240-degree panel meter with a needle | `min`, `max`, `value`, `fg` |
| `chart` | line trace, or bars with `orient="horizontal"` | `series="3,9,4,7"`, `fg` |
| `datagrid` | rows of cells; row 0 is the header | `items="A\|B;1\|2"`, `columns`, `selected` |
| `treeview` | indented rows; a node's `checked` expands it | children are the nodes |
| `calendar` | month grid, tap to pick a day | `year`, `month`, `value` (day) |
| `messagebox` | centred panel over what it covers | `text`, `bg` |
| `image` | bordered image placeholder | `width`, `height`, `bg` |
| `rect` | filled rectangle | `bg`, `width`, `height` |
| `ellipse` | filled and/or stroked ellipse | `bg`, `border`, `width`, `height` |
| `line` | one straight segment | `x2`, `y2`, `fg` |
| `polygon` | closed outline through its vertices | `points="24,0,48,40,0,40"`, `fg` |

The set mirrors [TinyCLR's UI controls](https://www.ghielectronics.com/docs/tinyclr/feature/user-interface),
which is the closest thing this space has to a reference vocabulary, with the
differences a 320x240 panel forces:

- **`combobox` never opens.** There is one touch point and no popup layer, so a
  dropdown would have to cover the setting it is changing. A tap advances the
  selection and the caret says there is more than one.
- **`messagebox` does not dim what it covers.** Dimming needs to read the
  pixels underneath and write them back darker, and the toolkit draws forward
  only — it never reads the framebuffer. (`RustNet.Drawing` does carry an
  alpha channel now, and `Display.BlendImage` applies a uniform one, but both
  compose an *image* over the panel rather than tint what is already there.)
  The box gets a shadow and a bright edge instead.
- **`chart` scales to its own series**, not to `min`/`max`. A temperature
  moving between 21 and 23 degrees is a flat line against a 0..100 axis and a
  legible curve against its own range.
- **`gauge` is drawn as sixteen straight segments.** The device has no arc
  primitive, and a per-pixel curve in managed code would cost more than the
  rest of the screen — a host call is around 220 microseconds on a K210.
- **`calendar` needs no clock.** The weekday of the first is computed with
  Zeller's congruence, so a month grid draws on a board whose RTC has never
  been set.

Colors are RGB565 hex (`fg="F800"` = red). Layout is two-pass:
`Measure` computes a desired height, `Arrange` assigns absolute bounds
(`LayoutX/Y/W/H`) used by rendering **and hit-testing**.

## Input & events

After `Ui.Render`, route pointer/touch taps into the tree:

```csharp
UiElement hit = Ui.Tap(root, touchX, touchY);   // updates control state, returns it
if (hit != null && hit.Id == "apply") { /* button pressed */ }
Ui.Render(root);                                 // redraw with new state
```

`Ui.Tap` toggles a `checkbox`, selects a `radio` (clearing its `group`),
moves a `slider`, and picks a `listbox` row — then returns the hit element
so the app can react. `root.HitTest(x, y)` exposes plain hit-testing.

A `scrollviewer` gives a fixed-height viewport over taller content:
`Ui.Scroll(root, "id", delta)` moves it (positive = down), the offset is
clamped to the content on the next layout, content is clipped to the
viewport (so it never overdraws neighbours), and a scrollbar thumb appears
when content overflows. Taps outside the viewport don't reach scrolled-away
children.

## Round-trip XML (`Ui.ToXml`)

`Ui.ToXml(root)` serializes a tree back to the markup format, round-tripping
with `Ui.LoadXml` — the save path for the desktop UI Designer.

## XML markup

```xml
<window width="160" height="128" bg="0000" pad="4" gap="4">
  <label id="title" text="Boiler" scale="2" fg="07FF"/>
  <label id="reading" text="--" fg="FFFF"/>
  <progress id="bar" value="0" max="3300" fg="07E0"/>
  <button text="RESET" bg="4208" fg="FFFF"/>
</window>
```

```csharp
UiElement screen = Ui.LoadXml(FileSystem.ReadAllText("/data/ui.xml"));
UiElement bar = screen.FindById("bar");
while (true)
{
    bar.Value = Adc.ReadMillivolts(0);
    Ui.Render(screen);                     // clear + layout + draw + present
    RustNet.Threading.Sleep.Ms(500);
}
```

Because the layout is a file on the device VFS, it can be changed with
`rustnet data push` without reflashing the app.

## Seeing the screen

- `rustnet display capture -o screen.ppm`
- VSCode **RustNet: Open Simulator Panel** — live framebuffer view
- Workbench display tab

Template: `rustnet new ui-dashboard <name>`.
