# Rustzor.UI

`Rustzor.UI` adalah component kit milik Rustzor sendiri: app shell, card, data grid, chart SVG, form, modal, tab, dan toast, dalam tema terang dan gelap. Stylesheet-nya otomatis di-link saat Anda me-reference package ini.

```xml
<PackageReference Include="Rustzor.UI" Version="0.1.0-preview.1" />
```

```razor
@using Rustzor.UI
```

## Component

| Component | Parameter utama |
| --- | --- |
| `AppShell` | `Brand`, `BrandIcon`, `Title`, `Items` (`List<NavItem>`), `TopBar`, `SidebarFooter`, child content |
| `PageHeader` | `Title`, `Eyebrow`, `Text`, `Actions` |
| `Card` | `Title`, `Subtitle`, `Class`, `Actions`, child content |
| `StatCard` | `Label`, `Value`, `Delta`, `Hint`, `Trend` (sebuah sparkline) |
| `DataGrid<TItem>` | `Items`, `Columns` (`GridColumn<TItem>`), `PageSize`, `Searchable`, `SearchPlaceholder`, `EmptyTitle`, `EmptyText`, `Toolbar`, `OnRowClick` |
| `BarChart`, `LineChart` | `Labels`, `Series` (`ChartSeries`), `Height`, `ValueFormat` (line), `AriaLabel` |
| `DonutChart` | `Labels`, `Values`, `Center`, `CenterLabel`, `ValueFormat`, `Size` |
| `Sparkline` | `Values`, `Color`, `Width`, `Height` |
| `Modal` | `Open` / `OpenChanged` (`@bind-Open`), `Title`, child content, `Footer` |
| `Tabs` | `Titles`, `Active` / `ActiveChanged` |
| `Field` | `Label`, `Help`, child content (sebuah input) |
| `Badge` | `Kind` (`good`, `warn`, `bad`, `accent`), `Text` |
| `Avatar` | `Name`, `Size` |
| `Progress` | `Percent`, `Color` |
| `EmptyState` | `Icon`, `Title`, `Text` |
| `Icon` | `Name`, `Size`, `Class` |
| `ThemeToggle`, `Toaster` | tidak ada |

Helper static: `Toasts.Success/Info/Warning/Error(text)` (ditampilkan oleh `Toaster` di halaman) dan `Theme.Current` / `Theme.Set("dark")` / `Theme.Toggle()`.

## App shell

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

`AppShell` sudah menyertakan `Toaster` dan `ThemeToggle`, serta melipat sidebar menjadi drawer di layar sempit.

## Data grid

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

## Chart

```razor
<BarChart Labels="months" Height="220"
          Series="@(new List<ChartSeries> { new("Revenue", revenue) { Color = "var(--rz-chart-1)" } })" />
<DonutChart Labels="sources" Values="amounts" Center="Rp 1.2 M" CenterLabel="won" />
```

Chart-chart ini berupa SVG yang digambar di C#: tidak ada library chart yang diunduh. Untuk jenis chart lainnya, gunakan [integrasi](integrations.md).

## Theming

Setiap warna, font, dan radius adalah CSS custom property di `:root` dengan prefix `--rz-`: `--rz-bg`, `--rz-surface`, `--rz-surface-2`, `--rz-line`, `--rz-fg`, `--rz-muted`, `--rz-accent`, `--rz-accent-fg`, `--rz-accent-soft`, `--rz-good`, `--rz-warn`, `--rz-bad`, `--rz-sidebar*`, `--rz-chart-1…5`, `--rz-font-display`, `--rz-font-body`, `--rz-font-mono`, `--rz-radius`. Override property tersebut di stylesheet Anda sendiri (yang di-link setelah stylesheet kit) untuk tema terang, untuk `prefers-color-scheme: dark`, dan untuk `[data-theme="dark"]`, yang di-set oleh `ThemeToggle`. Setiap sample app melakukan hal ini agar punya identitasnya sendiri; `samples/Crm/wwwroot/crm.css` adalah contoh yang ringkas.

Ikon berupa kumpulan ikon garis bawaan yang dipanggil berdasarkan nama: `home`, `chart`, `trend`, `users`, `user`, `cart`, `box`, `settings`, `search`, `plus`, `minus`, `trash`, `edit`, `check`, `x`, `bell`, `chat`, `video`, `mic`, `map`, `pin`, `layers`, `cpu`, `sparkles`, `sun`, `moon`, `download`, `upload`, `filter`, `menu`, `arrow-up`, `arrow-down`, `arrow-right`, `calendar`, `clock`, `mail`, `phone`, `file`, `image`, `brush`, `shapes`, `cube`, `gamepad`, `factory`, `wallet`, `receipt`, `send`, `zap`, `refresh`, `eye`, `star`, `tag`, `building`, `thermometer`, `gauge`, `alert`, `info`, `logout`, `rust`.
