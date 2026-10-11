# Components, routing and forms

`Rustzor.Components` is a Blazor-compatible component model. If you know Blazor WebAssembly, you know almost all of it: the same `.razor` syntax, the same base classes and the same lifecycle. This page lists what is there and, as precisely, what is not.

## Components

- `ComponentBase` with `OnInitialized(Async)`, `OnParametersSet(Async)`, `OnAfterRender(Async)`, `ShouldRender`, `StateHasChanged`, `InvokeAsync`, `SetParametersAsync`.
- `[Parameter]`, `[CascadingParameter]`, `[Parameter(CaptureUnmatchedValues = true)]`, `[EditorRequired]`, `[Inject]`.
- `RenderFragment` and `RenderFragment<T>` (child content and templates), `@key`, `@ref` (`ElementReference` and component references).
- Events: `@onclick`, `@oninput`, `@onchange`, `@onkeydown`, `@onsubmit`, drag and drop, pointer and focus events, with `:preventDefault` and `:stopPropagation`. `EventCallback` and `EventCallback<T>`.
- `@bind`, `@bind-Value`, `@bind:event` and `@bind:format` for `string`, `bool`, `int`, `long`, `double`, `decimal`, `DateTime`, their nullable forms, and other types through a generic converter.
- `LayoutComponentBase` and `@layout`, `CascadingValue<T>`, `DynamicComponent`, `ErrorBoundary`.
- `MarkupString` for trusted HTML.
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

## Hosting and dependency injection

```csharp
var builder = RustzorHostBuilder.CreateDefault(args);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddScoped<ICart, Cart>();
builder.Services.AddTransient(sp => new Clock(sp.GetRequiredService<Catalog>()));
builder.RootComponents.Add<App>("#app");
await builder.Build().RunAsync();
```

`AddSingleton`, `AddScoped` and `AddTransient` come in generic, interface-to-implementation and factory forms; `AddSingleton` also takes an instance. Components receive services with `@inject` or `[Inject]`. `NavigationManager` is registered for you.

## Routing

`Router`, `RouteView`, `LayoutView`, `NavLink` (with `NavLinkMatch`) and `PageTitle` work as in Blazor.

- Route templates: literals, parameters `{id}`, optional `{id?}` and catch-all `{*path}` / `{**path}`.
- Constraints: `int`, `long`, `bool`, `double`, `float` and `decimal` (the last three are parsed as `double`). Other constraint names are accepted but not enforced; the value arrives as a string.
- `NavigationManager`: `Uri`, `BaseUri`, `NavigateTo(uri, forceLoad, replace)`, `LocationChanged`, `ToAbsoluteUri`, `ToBaseRelativePath`, `Refresh`.
- Pages from other assemblies: `AdditionalAssemblies` on the `Router`.

When the router reuses a page for a new URL (`/companies/1` to `/companies/2`), the component is not recreated: use `OnParametersSet(Async)` to reload, as in Blazor.

## Forms

`EditForm` (with `Model` or `EditContext`, `OnSubmit`, `OnValidSubmit`, `OnInvalidSubmit`), `InputText` (with `Immediate`), `InputTextArea`, `InputNumber<T>`, `InputDate<T>`, `InputCheckbox`, `InputSelect<T>`, `ValidationSummary` and `ValidationMessage`.

Validation is explicit: add a validator to the `EditContext` that returns a (field, message) pair for every problem, and name the field a message belongs to with `Field`:

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

Plain HTML inputs with `@bind` work everywhere and are what the samples mostly use.

## Not supported yet

| Blazor feature | Instead |
| --- | --- |
| `DataAnnotationsValidator`, `[Required]`-style attributes | `EditContext.AddValidator`, or validate in the submit handler |
| `ValidationMessage For="() => model.X"` | `ValidationMessage Field="X"` |
| `InputRadio`, `InputRadioGroup`, `InputFile` | Plain `<input type="radio">`; `Rustzor.Browser.FileSystem` for files |
| `Virtualize` | Page the data (`Rustzor.UI`'s `DataGrid` does) |
| `HeadOutlet`, `HeadContent`, `SectionOutlet` | `PageTitle`, or `Document` in `Rustzor.Browser.DOM` |
| `HttpClient` | `FetchClient` from `Rustzor.Browser.Fetch` |
| `IJSRuntime` | `Js`, `JsObject` (see [interop](interop.md)) |

More about runtime coverage: [limitations](limitations.md).
