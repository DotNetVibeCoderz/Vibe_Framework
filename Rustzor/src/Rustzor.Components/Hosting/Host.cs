using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Rustzor.Interop;

namespace Rustzor.Components;

/// <summary>Builds a Rustzor app in Program.cs.</summary>
/// <example>
/// <code>
/// var builder = RustzorHostBuilder.CreateDefault(args);
/// builder.RootComponents.Add&lt;App&gt;("#app");
/// builder.Services.AddSingleton&lt;CartService&gt;();
/// await builder.Build().RunAsync();
/// </code>
/// </example>
public sealed class RustzorHostBuilder
{
    private RustzorHostBuilder()
    {
    }

    public static RustzorHostBuilder CreateDefault(string[]? args = null) => new();

    public ServiceCollection Services { get; } = new();

    public RootComponentCollection RootComponents { get; } = new();

    public RustzorHost Build()
    {
        Services.AddSingleton<NavigationManager>(_ => NavigationManager.Instance);
        Services.AddSingleton<IJSRuntime>(_ => BrowserJSRuntime.Instance);
        return new RustzorHost(new ServiceProvider(Services), RootComponents);
    }
}

public sealed class RootComponentCollection
{
    internal readonly List<(Type Type, string Selector, ParameterView Parameters)> Items = new();

    public void Add<TComponent>(string selector) where TComponent : IComponent =>
        Items.Add((typeof(TComponent), selector, ParameterView.Empty));

    public void Add(Type componentType, string selector, IDictionary<string, object?>? parameters = null) =>
        Items.Add((componentType, selector, parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters)));
}

/// <summary>A built app. <see cref="RunAsync"/> renders its root components.</summary>
public sealed class RustzorHost
{
    private readonly RootComponentCollection _roots;

    internal RustzorHost(ServiceProvider services, RootComponentCollection roots)
    {
        Services = services;
        _roots = roots;
    }

    public ServiceProvider Services { get; }

    /// <summary>
    /// Renders the root components and returns. Unlike Blazor's, this task
    /// completes at once: the browser's event loop keeps the app alive, and
    /// <c>Main</c> must return to give it back its thread.
    /// </summary>
    public Task RunAsync()
    {
        NavigationManager.Instance.Initialize();
        var renderer = new Renderer(Services);
        foreach (var (type, selector, parameters) in _roots.Items)
            renderer.AddRootComponent(type, selector, parameters);
        Js.Send("app.ready");
        return Task.CompletedTask;
    }
}

/// <summary>What rustzor.js calls into for the component model.</summary>
public static class RendererInterop
{
    /// <summary><c>"handlerId|type|json"</c>, sent when a listened-for DOM event fires.</summary>
    public static void DispatchEvent(string payload)
    {
        var first = payload.IndexOf('|');
        var second = payload.IndexOf('|', first + 1);
        var handlerId = int.Parse(payload.Substring(0, first));
        var type = payload.Substring(first + 1, second - first - 1);
        var json = JsonValue.Parse(payload.Substring(second + 1));
        Renderer.Current.DispatchEvent(handlerId, type, json);
    }

    /// <summary><c>"intercepted|url"</c>, sent on link clicks and back/forward.</summary>
    public static void LocationChanged(string payload)
    {
        var bar = payload.IndexOf('|');
        NavigationManager.Instance.OnBrowserNavigated(payload.Substring(bar + 1), payload[0] == '1');
    }
}
