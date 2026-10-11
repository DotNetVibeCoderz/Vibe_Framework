namespace Microsoft.AspNetCore.Components.Rendering;

/// <summary>A node of a component's rendered output.</summary>
/// <remarks>
/// <see cref="DomId"/> names the DOM node in rustzor.js's table. For an element,
/// text or markup node it is that node. A component or region is a range of
/// DOM nodes, and its id names an empty comment that closes the range: its
/// content is always inserted before that marker, which is how a child that
/// re-renders on its own knows where its output goes.
/// </remarks>
internal abstract class VNode
{
    protected VNode(int sequence) => Sequence = sequence;

    public int Sequence { get; }
    public object? Key { get; set; }
    public int DomId;

    /// <summary>What must match for a node to be patched rather than replaced.</summary>
    public abstract string Identity { get; }
}

internal sealed class VText : VNode
{
    public VText(int sequence, string text) : base(sequence) => Text = text;

    public string Text;

    public override string Identity => "t";
}

internal sealed class VMarkup : VNode
{
    public VMarkup(int sequence, string html) : base(sequence) => Html = html;

    public readonly string Html;

    public override string Identity => "m";
}

internal sealed class VAttribute
{
    public VAttribute(string name, object value)
    {
        Name = name;
        Value = value;
    }

    public readonly string Name;

    /// <summary>A string, or an <see cref="EventCallback"/> for <c>on*</c> attributes.</summary>
    public readonly object Value;

    /// <summary>For an event: the id rustzor.js reports when it fires.</summary>
    public int HandlerId;

    public bool IsEvent => Value is EventCallback;
}

internal sealed class VElement : VNode
{
    public VElement(int sequence, string tag) : base(sequence) => Tag = tag;

    public readonly string Tag;
    public readonly List<VAttribute> Attributes = new();
    public readonly List<VNode> Children = new();

    /// <summary>preventDefault (1) and stopPropagation (2), by event attribute name.</summary>
    public readonly Dictionary<string, int> Flags = new();

    public Action<ElementReference>? ReferenceCapture;

    public override string Identity => "e:" + Tag;
}

internal sealed class VRegion : VNode
{
    public VRegion(int sequence) : base(sequence)
    {
    }

    public readonly List<VNode> Children = new();

    public override string Identity => "r";
}

internal sealed class VComponent : VNode
{
    public VComponent(int sequence, Type type) : base(sequence) => Type = type;

    public readonly Type Type;
    public readonly List<ParameterValue> Parameters = new();
    public Action<object>? ReferenceCapture;
    public ComponentState? State;

    public override string Identity => "c:" + Type.FullName;
}

/// <summary>A live component: the instance, its place in the DOM, its last output.</summary>
internal sealed class ComponentState
{
    public ComponentState(int id, IComponent component, ComponentState? parent)
    {
        Id = id;
        Component = component;
        Parent = parent;
    }

    public readonly int Id;
    public readonly IComponent Component;
    public readonly ComponentState? Parent;

    /// <summary>The DOM element the component's nodes live in.</summary>
    public int ParentDomId;

    /// <summary>The comment that closes the component's range.</summary>
    public int MarkerId;

    public List<VNode> Tree = new();
    public List<ParameterValue>? LastParameters;
    public bool Disposed;
}
