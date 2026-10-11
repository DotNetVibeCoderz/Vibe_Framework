# Rustzor.UI

`Rustzor.UI` is Rustzor's own component kit: an app shell, cards, a data grid, SVG charts, forms, modals, tabs and toasts, in light and dark themes. Its stylesheet is linked automatically when you reference the package.

```xml
<PackageReference Include="Rustzor.UI" Version="0.1.0-preview.2" />
```

```razor
@using Rustzor.UI
```

## Components

| Component | Main parameters |
| --- | --- |
| `AppShell` | `Brand`, `BrandIcon`, `Title`, `Items` (`List<NavItem>`), `TopBar`, `SidebarFooter`, child content |
| `PageHeader` | `Title`, `Eyebrow`, `Text`, `Actions` |
| `Card` | `Title`, `Subtitle`, `Class`, `Actions`, child content |
| `StatCard` | `Label`, `Value`, `Delta`, `Hint`, `Trend` (a sparkline) |
| `DataGrid<TItem>` | `Items`, `Columns` (`GridColumn<TItem>`), `PageSize`, `Searchable`, `SearchPlaceholder`, `EmptyTitle`, `EmptyText`, `Toolbar`, `OnRowClick` |
| `BarChart`, `LineChart` | `Labels`, `Series` (`ChartSeries`), `Height`, `ValueFormat` (line), `AriaLabel` |
| `DonutChart` | `Labels`, `Values`, `Center`, `CenterLabel`, `ValueFormat`, `Size` |
| `Sparkline` | `Values`, `Color`, `Width`, `Height` |
| `Modal` | `Open` / `OpenChanged` (`@bind-Open`), `Title`, child content, `Footer` |
| `Tabs` | `Titles`, `Active` / `ActiveChanged` |
| `Field` | `Label`, `Help`, child content (an input) |
| `Badge` | `Kind` (`good`, `warn`, `bad`, `accent`), `Text` |
| `Avatar` | `Name`, `Size` |
| `Progress` | `Percent`, `Color` |
| `EmptyState` | `Icon`, `Title`, `Text` |
| `Icon` | `Name`, `Size`, `Class` |
| `ThemeToggle`, `Toaster` | none |

Static helpers: `Toasts.Success/Info/Warning/Error(text)` (shown by a `Toaster` on the page) and `Theme.Current` / `Theme.Set("dark")` / `Theme.Toggle()`.

## An app shell

```razor
@inherits LayoutComponentBase

<AppShell Brand="Kedai Senja" BrandIcon="cart" Items="nav">
    <TopBar><span class="muted">@DateTime.Now.ToString("dddd, d MMMM")</span></TopBar>
    <ChildContent>@Body</ChildContent>
</AppShell>

@code {
    private readonly List<NavItem> nav = new()
    {
        NavItem.Link("Overview", "", "home"),
        NavItem.Link("Orders", "orders", "receipt"),
        NavItem.Heading("Back office"),
        NavItem.Link("Menu", "menu", "tag"),
    };
}
```

`AppShell` includes a `Toaster` and a `ThemeToggle`, and collapses the sidebar into a drawer on narrow screens.

## A data grid

```razor
<DataGrid TItem="Contact" Items="contacts" Columns="columns" PageSize="14"
          SearchPlaceholder="Search name, company, email" OnRowClick="Open" />

@code {
    private readonly List<GridColumn<Contact>> columns = new()
    {
        new("Name", c => c.Name),
        new("Company", c => c.Company),
        new("Value", c => c.Value) { Numeric = true, Format = "N0" },
        new("Phone", c => c.Phone) { Sortable = false },
    };
}
```

## Charts

```razor
<BarChart Labels="months" Height="220"
          Series="@(new List<ChartSeries> { new("Revenue", revenue) { Color = "var(--rz-chart-1)" } })" />
<DonutChart Labels="sources" Values="amounts" Center="Rp 1.2 M" CenterLabel="won" />
```

The charts are SVG drawn in C#: no chart library is downloaded. For more chart types use the [integrations](integrations.md).

## Theming

Every colour, font and radius is a CSS custom property on `:root`, prefixed `--rz-`: `--rz-bg`, `--rz-surface`, `--rz-surface-2`, `--rz-line`, `--rz-fg`, `--rz-muted`, `--rz-accent`, `--rz-accent-fg`, `--rz-accent-soft`, `--rz-good`, `--rz-warn`, `--rz-bad`, `--rz-sidebar*`, `--rz-chart-1…5`, `--rz-font-display`, `--rz-font-body`, `--rz-font-mono`, `--rz-radius`. Override them in your own stylesheet (it is linked after the kit's) for light, for `prefers-color-scheme: dark`, and for `[data-theme="dark"]`, which `ThemeToggle` sets. Each sample does this to get its own identity; `samples/Crm/wwwroot/crm.css` is a compact example.

Icons are a built-in set of line icons by name: `home`, `chart`, `trend`, `users`, `user`, `cart`, `box`, `settings`, `search`, `plus`, `minus`, `trash`, `edit`, `check`, `x`, `bell`, `chat`, `video`, `mic`, `map`, `pin`, `layers`, `cpu`, `sparkles`, `sun`, `moon`, `download`, `upload`, `filter`, `menu`, `arrow-up`, `arrow-down`, `arrow-right`, `calendar`, `clock`, `mail`, `phone`, `file`, `image`, `brush`, `shapes`, `cube`, `gamepad`, `factory`, `wallet`, `receipt`, `send`, `zap`, `refresh`, `eye`, `star`, `tag`, `building`, `thermometer`, `gauge`, `alert`, `info`, `logout`, `rust`.
