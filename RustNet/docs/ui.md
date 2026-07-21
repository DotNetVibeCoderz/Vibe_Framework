# UI toolkit (WPF/Glide-style, XML-loadable)

`RustNet.UI` renders an element tree to the device display through
`RustNet.Graphics.Display` — the same model as WPF/NETMF-Glide, sized for
small panels: build the tree in code or load it from XML markup, mutate
elements by id, re-render.

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
| `label` / `textblock` | text | `text`, `fg`, `scale` |
| `button` | bordered box + text | `text`, `fg`, `bg`, `border` |
| `textbox` | bordered input box + text | `text`, `fg`, `border` |
| `checkbox` | box + tick + label | `text`, `checked` |
| `radio` | circle + dot + label | `text`, `checked`, `group` |
| `slider` | track + knob | `min`, `max`, `value`, `fg` |
| `progress` | value bar | `value`, `min`, `max`, `fg` |
| `listbox` | bordered list, selected row highlighted | `items="A;B;C"`, `selected` |
| `image` | bordered image placeholder | `width`, `height`, `bg` |
| `rect` | filled rectangle | `bg`, `width`, `height` |

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
