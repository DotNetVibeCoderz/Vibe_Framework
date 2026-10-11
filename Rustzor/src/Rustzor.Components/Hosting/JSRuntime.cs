using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Microsoft.JSInterop;

/// <summary>Calls JavaScript functions by name, as in Blazor.</summary>
public interface IJSRuntime
{
    ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args);
}

public static class JSRuntimeExtensions
{
    public static ValueTask<TValue> InvokeAsync<TValue>(this IJSRuntime js, string identifier, params object?[]? args) =>
        js.InvokeAsync<TValue>(identifier, args);

    public static async ValueTask InvokeVoidAsync(this IJSRuntime js, string identifier, params object?[]? args)
    {
        await js.InvokeAsync<JsonValue>(identifier, args);
    }
}

/// <summary>The browser's JS runtime: <c>identifier</c> is a path from <c>window</c>, e.g. <c>"localStorage.setItem"</c>.</summary>
internal sealed class BrowserJSRuntime : IJSRuntime
{
    public static readonly BrowserJSRuntime Instance = new();

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        var w = new JsonWriter();
        w.BeginObject().Prop("path", identifier).Name("args").BeginArray();
        if (args is not null)
        {
            foreach (var a in args)
            {
                if (a is ElementReference el) w.BeginObject().Prop("$el", el.Id).EndObject();
                else w.Any(a);
            }
        }
        w.EndArray().EndObject();
        var result = await Js.CallAsync("js.invoke", JsonValue.Parse(w.ToString()));
        return (TValue)Json.FromJson(result, typeof(TValue))!;
    }
}
