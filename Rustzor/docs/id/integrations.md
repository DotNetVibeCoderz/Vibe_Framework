# Integrasi

Library UI dan chart JavaScript yang sudah mapan, tersedia sebagai komponen Razor dengan state di C#. Setiap integrasi memuat library-nya dari versi CDN yang di-pin saat pertama kali sebuah komponen membutuhkannya; set `window.RustzorCdn` sebelum `start()` untuk menyajikannya dari host Anda sendiri (untuk aplikasi offline atau CSP yang ketat).

```html
<script>window.RustzorCdn = { echarts: "/vendor/echarts.min.js" };</script>
```

## UI kit

### Bootstrap 5.3: `Rustzor.UI.Bootstrap`

`BsButton`, `BsAlert`, `BsBadge`, `BsCard`, `BsModal`, `BsNavbar`, `BsTabs`/`BsTab`, `BsAccordion`/`BsAccordionItem`, `BsDropdown`, `BsInput`, `BsSwitch`, `BsProgress`, `BsSpinner`, `BsTable`, `BsPagination`, `BsToastHost` dengan `BsToasts.Show`.

```razor
<BsAlert Color="@BsColor.Success" Heading="Hello" Dismissible="true">Bootstrap from C#.</BsAlert>
<BsButton Color="@BsColor.Primary" OnClick="Save">Save</BsButton>
```

![Komponen Bootstrap](../images/int-bootstrap.png)

### Tailwind 3.4: `Rustzor.UI.Tailwind`

Compiler Tailwind berjalan di browser, jadi utility class di file `.razor` Anda langsung berfungsi. Komponen: `TwButton`, `TwCard`, `TwBadge`, `TwInput`, `TwToggle`, `TwAlert`, `TwModal`, `TwTabs`, `TwStat`, `TwTable`; helper `Tw.Css(...)` (class kondisional) dan `Tw.SetTheme(...)`.

```razor
<div class="grid grid-cols-3 gap-4">
    <TwStat Label="Revenue" Value="Rp 12.4 jt" Delta="8%" Caption="vs last week" />
</div>
```

![Komponen Tailwind](../images/int-tailwind.png)

### Material UI 5: `Rustzor.UI.Mui`

Komponen React dari MUI yang di-render sebagai island dan dikendalikan dari C#: `MuiButton`, `MuiTextField`, `MuiSelect`, `MuiSwitch`, `MuiSlider`, `MuiTabs`, `MuiChip`, `MuiAlert`, `MuiCard`, `MuiAppBar`, `MuiProgress`, `MuiRating`, `MuiToggleGroup`, serta `MuiIsland` dengan `Mui.El(...)` untuk komponen lain apa pun di library tersebut.

```razor
<MuiTextField Label="Name" @bind-Value="name" StartIcon="person" />
```

![Komponen Material UI](../images/int-mui.png)

## Chart

### Apache ECharts 5: `Rustzor.Charts.ECharts`

`EChart` menerima option ECharts apa pun, dalam bentuk anonymous object, `JsonValue`, atau builder `EChartOption` (`Line`, `Bar`, `Pie`, `Gauge`, `Heatmap`, `AddLine`, `AddBar`, `Title`, `Set`). Option baru akan di-merge ke chart; chart mengikuti tema terang atau gelap halaman serta warna `--rz-chart-*`.

```razor
<EChart Option="option" Height="280" OnClick="c => picked = c.Name" />

@code {
    private object option = EChartOption.Line(new[] { "Mon", "Tue", "Wed" })
        .AddLine("Visits", new double[] { 120, 200, 150 }, area: true);
}
```

[Dashboard Arus](samples.md#arus-grid-dashboard) memakai ECharts untuk stacked area, donut, dan heatmap yang diperbarui setiap dua detik.

### Chart.js 4: `Rustzor.Charts.ChartJs`

```razor
<ChartJs Type="bar" Labels="labels" Datasets="sets" Height="220" OnClick="c => picked = c.Label" />
```

### D3 7: `Rustzor.Charts.D3`

`D3Chart` menggambar `bar`, `line`, `pie`, `force` (graf force-directed), dan `treemap` dari data; `D3Script` menjalankan kode D3 Anda sendiri terhadap sebuah elemen.

```razor
<D3Chart Kind="bar" Data="data" Height="220" OnSelect="s => picked = s" />
```

![Chart.js, ECharts, dan D3](../images/int-charts.png)

Jika Anda hanya butuh chart bar, line, donut, dan sparkline, `Rustzor.UI` menggambarnya dalam SVG dari C# tanpa perlu mengunduh library chart.
