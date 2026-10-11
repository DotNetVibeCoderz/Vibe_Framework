using Microsoft.AspNetCore.Components.Rendering;

namespace Microsoft.AspNetCore.Components;

/// <summary>
/// The base class of components, with Blazor's lifecycle: OnInitialized(Async),
/// OnParametersSet(Async), ShouldRender, OnAfterRender(Async) and a re-render
/// after every event handler.
/// </summary>
public abstract class ComponentBase : IComponent, IHandleEvent, IHandleAfterRender
{
    private readonly RenderFragment _renderFragment;
    private RenderHandle _renderHandle;
    private bool _initialized;
    private bool _hasNeverRendered = true;
    private bool _hasPendingQueuedRender;
    private bool _hasCalledOnAfterRender;

    protected ComponentBase()
    {
        _renderFragment = builder =>
        {
            _hasPendingQueuedRender = false;
            _hasNeverRendered = false;
            BuildRenderTree(builder);
        };
    }

    protected virtual void BuildRenderTree(RenderTreeBuilder builder)
    {
    }

    protected virtual void OnInitialized()
    {
    }

    protected virtual Task OnInitializedAsync() => Task.CompletedTask;

    protected virtual void OnParametersSet()
    {
    }

    protected virtual Task OnParametersSetAsync() => Task.CompletedTask;

    protected virtual bool ShouldRender() => true;

    protected virtual void OnAfterRender(bool firstRender)
    {
    }

    protected virtual Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;

    /// <summary>Asks for a re-render. Several calls before the render coalesce into one.</summary>
    protected void StateHasChanged()
    {
        if (_hasPendingQueuedRender) return;
        if (_hasNeverRendered || ShouldRender())
        {
            _hasPendingQueuedRender = true;
            try
            {
                _renderHandle.Render(_renderFragment);
            }
            catch
            {
                _hasPendingQueuedRender = false;
                throw;
            }
        }
    }

    protected Task InvokeAsync(Action workItem) => _renderHandle.Dispatcher.InvokeAsync(workItem);

    protected Task InvokeAsync(Func<Task> workItem) => _renderHandle.Dispatcher.InvokeAsync(workItem);

    protected Task DispatchExceptionAsync(Exception exception)
    {
        Rustzor.Interop.Js.Log(2, exception.ToString());
        return Task.CompletedTask;
    }

    void IComponent.Attach(RenderHandle renderHandle)
    {
        if (_renderHandle.IsInitialized) throw new InvalidOperationException("The render handle is already set.");
        _renderHandle = renderHandle;
    }

    public virtual Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        if (!_initialized)
        {
            _initialized = true;
            return RunInitAndSetParametersAsync();
        }
        return CallOnParametersSetAsync();
    }

    private async Task RunInitAndSetParametersAsync()
    {
        OnInitialized();
        var task = OnInitializedAsync();
        if (!task.IsCompleted)
        {
            // Render once while the work runs, so a "Loading..." state shows.
            StateHasChanged();
            try
            {
                await task;
            }
            catch
            {
                if (!task.IsCanceled) throw;
            }
        }
        else if (task.IsFaulted && task.Exception is not null)
        {
            throw task.Exception.InnerException ?? task.Exception;
        }
        await CallOnParametersSetAsync();
    }

    private Task CallOnParametersSetAsync()
    {
        OnParametersSet();
        var task = OnParametersSetAsync();
        var shouldAwait = !task.IsCompleted;
        StateHasChanged();
        return shouldAwait ? CallStateHasChangedOnAsyncCompletion(task) : Task.CompletedTask;
    }

    private async Task CallStateHasChangedOnAsyncCompletion(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            if (task.IsCanceled) return;
            throw;
        }
        StateHasChanged();
    }

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        var task = callback.InvokeAsync(arg);
        var shouldAwait = !task.IsCompleted;
        StateHasChanged();
        return shouldAwait ? CallStateHasChangedOnAsyncCompletion(task) : Task.CompletedTask;
    }

    Task IHandleAfterRender.OnAfterRenderAsync()
    {
        var firstRender = !_hasCalledOnAfterRender;
        _hasCalledOnAfterRender = true;
        OnAfterRender(firstRender);
        return OnAfterRenderAsync(firstRender);
    }
}

/// <summary>A layout: a component that wraps a page, rendered where it writes <see cref="Body"/>.</summary>
public abstract class LayoutComponentBase : ComponentBase
{
    [Parameter]
    public RenderFragment? Body { get; set; }
}

/// <summary>Supplies a value to every descendant with a matching <c>[CascadingParameter]</c>.</summary>
public class CascadingValue<TValue> : ComponentBase, ICascadingValueComponent
{
    [Parameter] public TValue? Value { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public bool IsFixed { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    bool ICascadingValueComponent.CanSupply(Type type, string? name)
    {
        if (name is not null || Name is not null) return string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);
        return Value is not null && type.IsInstanceOfType(Value);
    }

    object? ICascadingValueComponent.CurrentValue => Value;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
}

/// <summary>Renders a component whose type is chosen at run time.</summary>
public sealed class DynamicComponent : ComponentBase
{
    [Parameter] public Type Type { get; set; } = null!;
    [Parameter] public IDictionary<string, object?>? Parameters { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, Type);
        if (Parameters is not null)
            foreach (var kv in Parameters) builder.AddComponentParameter(1, kv.Key, kv.Value);
        builder.CloseComponent();
    }
}

/// <summary>Catches exceptions from its child content and shows <see cref="ErrorContent"/> instead.</summary>
public class ErrorBoundary : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment<Exception>? ErrorContent { get; set; }

    protected Exception? CurrentException { get; private set; }

    public void Recover()
    {
        CurrentException = null;
        StateHasChanged();
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (CurrentException is null)
        {
            builder.AddContent(0, ChildContent);
            return;
        }
        if (ErrorContent is not null)
        {
            builder.AddContent(1, ErrorContent(CurrentException));
            return;
        }
        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "rz-error-boundary");
        builder.AddContent(4, "An error has occurred.");
        builder.CloseElement();
    }
}
