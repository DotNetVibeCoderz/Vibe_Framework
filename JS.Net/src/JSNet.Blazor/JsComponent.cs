using Microsoft.AspNetCore.Components;

namespace JSNet.Blazor;

/// <summary>
/// A friendlier Blazor component base for JS.Net code-behind:
/// <code>
/// export partial class Clock extends JsComponent {
///     now = new Date();
///     protected override async mounted(): Promise&lt;void&gt; {
///         this.every(1000, () =&gt; this.setState(() =&gt; { this.now = new Date(); }));
///     }
/// }
/// </code>
/// Timers started with <see cref="every"/>/<see cref="after"/> are stopped automatically when the component is disposed.
/// </summary>
public abstract class JsComponent : ComponentBase, IDisposable
{
    private readonly List<IDisposable> _timers = [];

    [Inject] protected IServiceProvider services { get; set; } = default!;

    /// <summary>The Blazor <see cref="NavigationManager"/> (resolved on first use).</summary>
    protected NavigationManager navigation => (NavigationManager)services.GetService(typeof(NavigationManager))!;

    /// <summary>Runs once after the component is first initialized (OnInitializedAsync).</summary>
    protected virtual Task mounted() => Task.CompletedTask;

    /// <summary>Runs after every render (OnAfterRenderAsync); <paramref name="firstRender"/> is true the first time.</summary>
    protected virtual Task updated(bool firstRender) => Task.CompletedTask;

    /// <summary>Runs when the component is removed.</summary>
    protected virtual void unmounted() { }

    protected override Task OnInitializedAsync() => mounted();
    protected override Task OnAfterRenderAsync(bool firstRender) => updated(firstRender);

    /// <summary>Applies a change and re-renders (safe from timers and background threads).</summary>
    protected void setState(Action? change = null) => _ = InvokeAsync(() =>
    {
        change?.Invoke();
        StateHasChanged();
    });

    /// <summary>Repeats <paramref name="callback"/> every <paramref name="ms"/> milliseconds until disposed.</summary>
    protected IDisposable every(double ms, Action callback)
    {
        var timer = new Timer(_ => InvokeAsync(callback), null, TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(ms));
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Runs <paramref name="callback"/> once after <paramref name="ms"/> milliseconds.</summary>
    protected IDisposable after(double ms, Action callback)
    {
        var timer = new Timer(_ => InvokeAsync(callback), null, TimeSpan.FromMilliseconds(ms), Timeout.InfiniteTimeSpan);
        _timers.Add(timer);
        return timer;
    }

    protected void navigate(string url, bool forceLoad = false) => navigation.NavigateTo(url, forceLoad);

    public void Dispose()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
        unmounted();
        GC.SuppressFinalize(this);
    }
}
