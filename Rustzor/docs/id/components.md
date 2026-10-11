# Component, routing, dan form

`Rustzor.Components` adalah model component yang kompatibel dengan Blazor. Jika Anda sudah mengenal Blazor WebAssembly, Anda sudah mengenal hampir semuanya: sintaks `.razor` yang sama, base class yang sama, dan lifecycle yang sama. Halaman ini mencantumkan apa saja yang tersedia dan, sama rincinya, apa yang belum.

## Component

- `ComponentBase` dengan `OnInitialized(Async)`, `OnParametersSet(Async)`, `OnAfterRender(Async)`, `ShouldRender`, `StateHasChanged`, `InvokeAsync`, `SetParametersAsync`.
- `[Parameter]`, `[CascadingParameter]`, `[Parameter(CaptureUnmatchedValues = true)]`, `[EditorRequired]`, `[Inject]`.
- `RenderFragment` dan `RenderFragment<T>` (child content dan template), `@key`, `@ref` (`ElementReference` dan reference ke component).
- Event: `@onclick`, `@oninput`, `@onchange`, `@onkeydown`, `@onsubmit`, drag and drop, event pointer dan focus, dengan `:preventDefault` dan `:stopPropagation`. `EventCallback` dan `EventCallback<T>`.
- `@bind`, `@bind-Value`, `@bind:event`, dan `@bind:format` untuk `string`, `bool`, `int`, `long`, `double`, `decimal`, `DateTime`, bentuk nullable-nya, serta type lain melalui converter generik.
- `LayoutComponentBase` dan `@layout`, `CascadingValue<T>`, `DynamicComponent`, `ErrorBoundary`.
- `MarkupString` untuk HTML yang tepercaya.
- `ElementReference.FocusAsync()`.

```razor
@page "/todo"

<input @bind="draft" @bind:event="oninput" @onkeydown="Key" placeholder="What needs doing?" />
<ul>
    @foreach (var item in items)
    {
        <li @key="item.Id" class="@(item.Done ? "done" : "")" @onclick="() => item.Done = !item.Done">@item.Text</li>
    }
</ul>

@code {
    private sealed class Item { public int Id; public string Text = ""; public bool Done; }
    private readonly List<Item> items = new();
    private string draft = "";

    private void Key(KeyboardEventArgs e)
    {
        if (e.Key != "Enter" || draft.Trim().Length == 0) return;
        items.Add(new Item { Id = items.Count + 1, Text = draft.Trim() });
        draft = "";
    }
}
```

## Hosting dan dependency injection

```csharp
var builder = RustzorHostBuilder.CreateDefault(args);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddScoped<ICart, Cart>();
builder.Services.AddTransient(sp => new Clock(sp.GetRequiredService<Catalog>()));
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
```

`AddSingleton`, `AddScoped`, dan `AddTransient` tersedia dalam bentuk generic, interface-ke-implementasi, dan factory; `AddSingleton` juga menerima instance. Component menerima service dengan `@inject` atau `[Inject]`. `NavigationManager` sudah didaftarkan untuk Anda.

## Routing

`Router`, `RouteView`, `LayoutView`, `NavLink` (dengan `NavLinkMatch`), dan `PageTitle` bekerja seperti di Blazor.

- Route template: literal, parameter `{id}`, opsional `{id?}`, dan catch-all `{*path}` / `{**path}`.
- Constraint: `int`, `long`, `bool`, `double`, `float`, dan `decimal` (tiga terakhir di-parse sebagai `double`). Nama constraint lain diterima tetapi tidak diberlakukan; nilainya diterima sebagai string.
- `NavigationManager`: `Uri`, `BaseUri`, `NavigateTo(uri, forceLoad, replace)`, `LocationChanged`, `ToAbsoluteUri`, `ToBaseRelativePath`, `Refresh`.
- Halaman dari assembly lain: `AdditionalAssemblies` pada `Router`.

Ketika router memakai ulang sebuah halaman untuk URL baru (`/companies/1` ke `/companies/2`), component-nya tidak dibuat ulang: gunakan `OnParametersSet(Async)` untuk memuat ulang data, sama seperti di Blazor.

## Form

`EditForm` (dengan `Model` atau `EditContext`, `OnSubmit`, `OnValidSubmit`, `OnInvalidSubmit`), `InputText` (dengan `Immediate`), `InputTextArea`, `InputNumber<T>`, `InputDate<T>`, `InputCheckbox`, `InputSelect<T>`, `ValidationSummary`, dan `ValidationMessage`.

Validasi dilakukan secara eksplisit: tambahkan validator ke `EditContext` yang mengembalikan pasangan (field, pesan) untuk setiap masalah, lalu sebutkan field pemilik sebuah pesan dengan `Field`:

```razor
<EditForm EditContext="context" OnValidSubmit="Save">
    <InputText @bind-Value="model.Email" />
    <ValidationMessage Field="Email" />
    <button type="submit">Save</button>
</EditForm>

@code {
    private readonly Signup model = new();
    private EditContext context = default!;

    protected override void OnInitialized()
    {
        context = new EditContext(model).AddValidator(_ => Problems());
    }

    private IEnumerable<KeyValuePair<string, string>> Problems()
    {
        if (!model.Email.Contains('@'))
            yield return new("Email", "Enter an email address.");
    }
}
```

Input HTML biasa dengan `@bind` berfungsi di mana saja, dan itulah yang paling banyak dipakai oleh sample app.

## Belum didukung

| Fitur Blazor | Sebagai gantinya |
| --- | --- |
| `DataAnnotationsValidator`, atribut bergaya `[Required]` | `EditContext.AddValidator`, atau validasi di handler submit |
| `ValidationMessage For="() => model.X"` | `ValidationMessage Field="X"` |
| `InputRadio`, `InputRadioGroup`, `InputFile` | `<input type="radio">` biasa; `Rustzor.Browser.FileSystem` untuk file |
| `Virtualize` | Bagi data per halaman (seperti yang dilakukan `DataGrid` dari `Rustzor.UI`) |
| `HeadOutlet`, `HeadContent`, `SectionOutlet` | `PageTitle`, atau `Document` di `Rustzor.Browser.DOM` |
| `HttpClient` | `FetchClient` dari `Rustzor.Browser.Fetch` |
| `IJSRuntime` | `Js`, `JsObject` (lihat [interop](interop.md)) |

Selengkapnya tentang cakupan runtime: [keterbatasan](limitations.md).
