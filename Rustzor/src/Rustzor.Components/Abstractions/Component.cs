// The Blazor component surface, implemented for Rustzor.
//
// These types deliberately carry the Microsoft.AspNetCore.Components names:
// the Razor compiler emits code against exactly these names, so matching them
// is what lets an ordinary .razor file compile for Rustzor. The implementation
// underneath is Rustzor's own, rendering through a virtual-DOM diff to
// rustzor.js rather than through Blazor's renderer.

using Microsoft.AspNetCore.Components.Rendering;

namespace Microsoft.AspNetCore.Components;

/// <summary>Anything the renderer can host.</summary>
public interface IComponent
{
    void Attach(RenderHandle renderHandle);
    Task SetParametersAsync(ParameterView parameters);
}

/// <summary>A component that wants to handle its own events (ComponentBase re-renders after each).</summary>
public interface IHandleEvent
{
    Task HandleEventAsync(EventCallbackWorkItem item, object? arg);
}

/// <summary>A component that wants to know when its output reached the DOM.</summary>
public interface IHandleAfterRender
{
    Task OnAfterRenderAsync();
}

/// <summary>A piece of UI: writes frames into a builder.</summary>
public delegate void RenderFragment(RenderTreeBuilder builder);

/// <summary>A piece of UI parameterised by a value, e.g. a list item template.</summary>
public delegate RenderFragment RenderFragment<TValue>(TValue value);

/// <summary>A component's link to the renderer that hosts it.</summary>
public readonly struct RenderHandle
{
    private readonly Renderer? _renderer;
    private readonly int _componentId;

    internal RenderHandle(Renderer renderer, int componentId)
    {
        _renderer = renderer;
        _componentId = componentId;
    }

    public bool IsInitialized => _renderer is not null;

    public bool IsRenderingOnMetadataUpdate => false;

    public Dispatcher Dispatcher => Dispatcher.Instance;

    internal Renderer Renderer => _renderer ?? throw new InvalidOperationException("The render handle is not initialized.");

    internal int ComponentId => _componentId;

    public void Render(RenderFragment renderFragment) => Renderer.Enqueue(_componentId, renderFragment);
}

/// <summary>There is one thread in the browser, so dispatching is calling.</summary>
public sealed class Dispatcher
{
    public static readonly Dispatcher Instance = new();

    public bool CheckAccess() => true;

    public Task InvokeAsync(Action workItem)
    {
        workItem();
        return Task.CompletedTask;
    }

    public Task InvokeAsync(Func<Task> workItem) => workItem();

    public Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) => Task.FromResult(workItem());

    public Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) => workItem();
}

/// <summary>HTML that is inserted as markup, not escaped.</summary>
public readonly struct MarkupString
{
    public MarkupString(string value) => Value = value;

    public string Value { get; }

    public static explicit operator MarkupString(string value) => new(value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>A reference to a rendered element, captured with <c>@ref</c>.</summary>
public readonly struct ElementReference
{
    public ElementReference(string id) => Id = id;

    /// <summary>The renderer's id for the element; rustzor.js resolves it to the DOM node.</summary>
    public string Id { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Id);
}

// ── attributes the Razor compiler and the renderer read ─────────────────────

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParameterAttribute : Attribute
{
    /// <summary>Collects attributes that match no other parameter.</summary>
    public bool CaptureUnmatchedValues { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CascadingParameterAttribute : Attribute
{
    public string? Name { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class InjectAttribute : Attribute
{
    public object? Key { get; init; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class EditorRequiredAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SupplyParameterFromQueryAttribute : Attribute
{
    public string? Name { get; set; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RouteAttribute : Attribute
{
    public RouteAttribute(string template) => Template = template;

    public string Template { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class LayoutAttribute : Attribute
{
    public LayoutAttribute(Type layoutType) => LayoutType = layoutType;

    public Type LayoutType { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class EventHandlerAttribute : Attribute
{
    public EventHandlerAttribute(string attributeName, Type eventArgsType)
        : this(attributeName, eventArgsType, false, false)
    {
    }

    public EventHandlerAttribute(string attributeName, Type eventArgsType, bool enableStopPropagation, bool enablePreventDefault)
    {
        AttributeName = attributeName;
        EventArgsType = eventArgsType;
        EnableStopPropagation = enableStopPropagation;
        EnablePreventDefault = enablePreventDefault;
    }

    public string AttributeName { get; }
    public Type EventArgsType { get; }
    public bool EnableStopPropagation { get; }
    public bool EnablePreventDefault { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class BindElementAttribute : Attribute
{
    public BindElementAttribute(string element, string? suffix, string valueAttribute, string changeAttribute)
    {
        Element = element;
        Suffix = suffix;
        ValueAttribute = valueAttribute;
        ChangeAttribute = changeAttribute;
    }

    public string Element { get; }
    public string? Suffix { get; }
    public string ValueAttribute { get; }
    public string ChangeAttribute { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class BindInputElementAttribute : Attribute
{
    public BindInputElementAttribute(string? type, string? suffix, string? valueAttribute, string? changeAttribute, bool isInvariantCulture, string? format)
    {
        Type = type;
        Suffix = suffix;
        ValueAttribute = valueAttribute;
        ChangeAttribute = changeAttribute;
        IsInvariantCulture = isInvariantCulture;
        Format = format;
    }

    public string? Type { get; }
    public string? Suffix { get; }
    public string? ValueAttribute { get; }
    public string? ChangeAttribute { get; }
    public bool IsInvariantCulture { get; }
    public string? Format { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class CascadingTypeParameterAttribute : Attribute
{
    public CascadingTypeParameterAttribute(string name) => Name = name;

    public string Name { get; }
}
