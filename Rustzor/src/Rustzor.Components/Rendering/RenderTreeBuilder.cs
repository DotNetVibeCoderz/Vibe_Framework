namespace Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// What a component's <c>BuildRenderTree</c> writes into. The Razor compiler
/// generates calls to exactly these methods.
/// </summary>
/// <remarks>
/// Blazor records a flat array of frames and diffs that. Rustzor builds the
/// virtual nodes directly: each <c>Open*</c> pushes a node, each <c>Close*</c>
/// pops it, and the renderer diffs node trees. Sequence numbers are kept and
/// are what identify a node across renders, exactly as in Blazor, so a
/// <c>foreach</c> keeps its elements and an <c>@if</c> swaps cleanly.
/// </remarks>
public sealed class RenderTreeBuilder
{
    internal readonly List<VNode> Roots = new();
    private readonly List<VNode> _stack = new();

    private List<VNode> Children
    {
        get
        {
            if (_stack.Count == 0) return Roots;
            return _stack[_stack.Count - 1] switch
            {
                VElement e => e.Children,
                VRegion r => r.Children,
                _ => throw new InvalidOperationException("Content cannot be added inside a component; pass it as ChildContent."),
            };
        }
    }

    private VNode? Current => _stack.Count == 0 ? null : _stack[_stack.Count - 1];

    /// <summary>The most recently added node, which <see cref="SetKey"/> applies to.</summary>
    private VNode? _lastOpened;

    public void Clear()
    {
        Roots.Clear();
        _stack.Clear();
        _lastOpened = null;
    }

    // ── elements ─────────────────────────────────────────────────────────

    public void OpenElement(int sequence, string elementName)
    {
        var e = new VElement(sequence, elementName);
        Children.Add(e);
        _stack.Add(e);
        _lastOpened = e;
    }

    public void CloseElement() => Pop<VElement>("CloseElement");

    public void OpenRegion(int sequence)
    {
        var r = new VRegion(sequence);
        Children.Add(r);
        _stack.Add(r);
        _lastOpened = r;
    }

    public void CloseRegion() => Pop<VRegion>("CloseRegion");

    public void OpenComponent<TComponent>(int sequence) where TComponent : IComponent => OpenComponent(sequence, typeof(TComponent));

    public void OpenComponent(int sequence, Type componentType)
    {
        var c = new VComponent(sequence, componentType);
        Children.Add(c);
        _stack.Add(c);
        _lastOpened = c;
    }

    public void CloseComponent() => Pop<VComponent>("CloseComponent");

    private void Pop<T>(string what) where T : VNode
    {
        if (Current is not T) throw new InvalidOperationException(what + " does not match the open node.");
        _stack.RemoveAt(_stack.Count - 1);
    }

    // ── content ──────────────────────────────────────────────────────────

    public void AddMarkupContent(int sequence, string? markupContent)
    {
        if (string.IsNullOrEmpty(markupContent)) return;
        Children.Add(new VMarkup(sequence, markupContent));
    }

    public void AddContent(int sequence, string? textContent)
    {
        Children.Add(new VText(sequence, textContent ?? string.Empty));
    }

    public void AddContent(int sequence, MarkupString markupContent) => AddMarkupContent(sequence, markupContent.Value);

    public void AddContent(int sequence, RenderFragment? fragment)
    {
        if (fragment is null) return;
        OpenRegion(sequence);
        fragment(this);
        CloseRegion();
    }

    public void AddContent<TValue>(int sequence, RenderFragment<TValue>? fragment, TValue value)
    {
        if (fragment is null) return;
        AddContent(sequence, fragment(value));
    }

    public void AddContent(int sequence, object? textContent)
    {
        switch (textContent)
        {
            case null:
                AddContent(sequence, (string?)null);
                return;
            case RenderFragment f:
                AddContent(sequence, f);
                return;
            case MarkupString m:
                AddMarkupContent(sequence, m.Value);
                return;
            case double d:
                AddContent(sequence, d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            default:
                AddContent(sequence, textContent.ToString());
                return;
        }
    }

    // ── attributes ───────────────────────────────────────────────────────

    public void AddAttribute(int sequence, string name) => AddAttribute(sequence, name, true);

    public void AddAttribute(int sequence, string name, bool value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        // An HTML boolean attribute is present or absent; false means absent.
        if (value) Element().Attributes.Add(new VAttribute(name, string.Empty));
    }

    public void AddAttribute(int sequence, string name, string? value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        if (value is not null) Element().Attributes.Add(new VAttribute(name, value));
    }

    public void AddAttribute(int sequence, string name, MulticastDelegate? value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        if (value is null) return;
        var callback = value switch
        {
            Action a => new EventCallback(null, a),
            Func<Task> f => new EventCallback(null, f),
            _ => new EventCallback(null, value),
        };
        Element().Attributes.Add(new VAttribute(name, callback));
    }

    public void AddAttribute(int sequence, string name, EventCallback value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        if (value.HasDelegate) Element().Attributes.Add(new VAttribute(name, value));
    }

    public void AddAttribute<TArgument>(int sequence, string name, EventCallback<TArgument> value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        if (value.HasDelegate) Element().Attributes.Add(new VAttribute(name, value.AsUntyped()));
    }

    public void AddAttribute(int sequence, string name, object? value)
    {
        if (Current is VComponent c) { c.Parameters.Add(new ParameterValue(name, value)); return; }
        switch (value)
        {
            case null:
                return;
            case bool b:
                AddAttribute(sequence, name, b);
                return;
            case string s:
                AddAttribute(sequence, name, s);
                return;
            case EventCallback ec:
                AddAttribute(sequence, name, ec);
                return;
            case MulticastDelegate d:
                AddAttribute(sequence, name, d);
                return;
            case double dbl:
                AddAttribute(sequence, name, dbl.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            default:
                // EventCallback<T> arrives here when the static type was lost.
                if (value is IEventCallbackLike like)
                {
                    AddAttribute(sequence, name, like.Untyped);
                    return;
                }
                AddAttribute(sequence, name, value.ToString());
                return;
        }
    }

    /// <summary>Adds a component parameter (the Razor compiler's .NET 8+ form).</summary>
    public void AddComponentParameter(int sequence, string name, object? value)
    {
        if (Current is not VComponent c) throw new InvalidOperationException("AddComponentParameter outside a component.");
        c.Parameters.Add(new ParameterValue(name, value));
    }

    public void AddMultipleAttributes(int sequence, IEnumerable<KeyValuePair<string, object>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes) AddAttribute(sequence, kv.Key, kv.Value);
    }

    public void AddMultipleAttributes(int sequence, IReadOnlyDictionary<string, object?>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes) AddAttribute(sequence, kv.Key, kv.Value);
    }

    public void AddEventPreventDefaultAttribute(int sequence, string eventName, bool value) =>
        Element().Flags[eventName] = (Element().Flags.TryGetValue(eventName, out var f) ? f & ~1 : 0) | (value ? 1 : 0);

    public void AddEventStopPropagationAttribute(int sequence, string eventName, bool value) =>
        Element().Flags[eventName] = (Element().Flags.TryGetValue(eventName, out var f) ? f & ~2 : 0) | (value ? 2 : 0);

    public void SetKey(object? value)
    {
        if (_lastOpened is null) throw new InvalidOperationException("SetKey must follow OpenElement or OpenComponent.");
        _lastOpened.Key = value;
    }

    public void SetUpdatesAttributeName(string updatesAttributeName)
    {
        // The renderer always writes `value` and `checked` as properties, which
        // is what this exists to tell Blazor's renderer.
    }

    public void AddElementReferenceCapture(int sequence, Action<ElementReference> elementReferenceCaptureAction) =>
        Element().ReferenceCapture = elementReferenceCaptureAction;

    public void AddComponentReferenceCapture(int sequence, Action<object> componentReferenceCaptureAction)
    {
        if (Current is not VComponent c) throw new InvalidOperationException("@ref on a component outside a component.");
        c.ReferenceCapture = componentReferenceCaptureAction;
    }

    public void AddComponentRenderMode(object? renderMode)
    {
        // Rustzor renders everything in the browser; there is one mode.
    }

    public void AddNamedEvent(string eventType, string assignedName)
    {
    }

    private VElement Element() =>
        Current as VElement ?? throw new InvalidOperationException("Attributes must follow OpenElement or OpenComponent.");
}

/// <summary>Lets <c>EventCallback&lt;T&gt;</c> boxed as object be recognised.</summary>
internal interface IEventCallbackLike
{
    EventCallback Untyped { get; }
}
