using System.Globalization;
using Microsoft.AspNetCore.Components.Rendering;

namespace Microsoft.AspNetCore.Components.Forms;

/// <summary>
/// The state of a form: its model, which fields were edited, and validation
/// messages.
/// </summary>
/// <remarks>
/// Blazor identifies a field by a lambda expression tree
/// (<c>ValueExpression</c>). Expression trees need System.Linq.Expressions,
/// which the browser runtime does not carry, so Rustzor names a field by a
/// string: <c>&lt;InputText @bind-Value="m.Name" Field="Name" /&gt;</c>.
/// Validation is a function the app supplies, not data annotations.
/// </remarks>
public sealed class EditContext
{
    private readonly Dictionary<string, List<string>> _messages = new();
    private readonly HashSet<string> _modified = new();
    private readonly List<Func<EditContext, IEnumerable<KeyValuePair<string, string>>>> _validators = new();

    public EditContext(object model) => Model = model;

    public object Model { get; }

    public event EventHandler? OnValidationStateChanged;
    public event EventHandler<string>? OnFieldChanged;

    /// <summary>Adds a validator: returns (field, message) pairs for every problem.</summary>
    public EditContext AddValidator(Func<EditContext, IEnumerable<KeyValuePair<string, string>>> validator)
    {
        _validators.Add(validator);
        return this;
    }

    public void NotifyFieldChanged(string field)
    {
        _modified.Add(field);
        OnFieldChanged?.Invoke(this, field);
    }

    public bool IsModified() => _modified.Count > 0;

    public bool IsModified(string field) => _modified.Contains(field);

    public void MarkAsUnmodified() => _modified.Clear();

    public bool Validate()
    {
        _messages.Clear();
        foreach (var v in _validators)
        {
            foreach (var kv in v(this)) AddMessage(kv.Key, kv.Value);
        }
        OnValidationStateChanged?.Invoke(this, EventArgs.Empty);
        return _messages.Count == 0;
    }

    public void AddMessage(string field, string message)
    {
        if (!_messages.TryGetValue(field, out var list)) _messages[field] = list = new List<string>();
        list.Add(message);
    }

    public void ClearMessages() => _messages.Clear();

    public IEnumerable<string> GetValidationMessages()
    {
        foreach (var list in _messages.Values)
            foreach (var m in list) yield return m;
    }

    public IEnumerable<string> GetValidationMessages(string field) =>
        _messages.TryGetValue(field, out var list) ? list : Enumerable.Empty<string>();

    public string FieldCssClass(string field)
    {
        var invalid = _messages.ContainsKey(field);
        var modified = _modified.Contains(field);
        return (modified ? "modified " : "") + (invalid ? "invalid" : "valid");
    }
}

/// <summary>A form bound to a model. Submitting runs validation and the matching callback.</summary>
public class EditForm : ComponentBase
{
    private EditContext? _context;

    [Parameter] public object? Model { get; set; }
    [Parameter] public EditContext? EditContext { get; set; }
    [Parameter] public RenderFragment<EditContext>? ChildContent { get; set; }
    [Parameter] public EventCallback<EditContext> OnSubmit { get; set; }
    [Parameter] public EventCallback<EditContext> OnValidSubmit { get; set; }
    [Parameter] public EventCallback<EditContext> OnInvalidSubmit { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    protected override void OnParametersSet()
    {
        if (EditContext is not null) _context = EditContext;
        else if (Model is not null && (_context is null || _context.Model != Model)) _context = new EditContext(Model);
    }

    private async Task HandleSubmit()
    {
        var ctx = _context!;
        if (OnSubmit.HasDelegate)
        {
            await OnSubmit.InvokeAsync(ctx);
            return;
        }
        if (ctx.Validate()) await OnValidSubmit.InvokeAsync(ctx);
        else await OnInvalidSubmit.InvokeAsync(ctx);
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "form");
        if (AdditionalAttributes is not null) builder.AddMultipleAttributes(1, AdditionalAttributes!);
        builder.AddAttribute(2, "onsubmit", EventCallback.Factory.Create(this, HandleSubmit));
        builder.AddEventPreventDefaultAttribute(3, "onsubmit", true);
        builder.OpenComponent<CascadingValue<EditContext>>(4);
        builder.AddComponentParameter(5, "Value", _context);
        builder.AddComponentParameter(6, "IsFixed", false);
        builder.AddComponentParameter(7, "ChildContent", (RenderFragment)(b => b.AddContent(8, ChildContent, _context!)));
        builder.CloseComponent();
        builder.CloseElement();
    }
}

/// <summary>The base of every input: a value, a ValueChanged callback, and the form it belongs to.</summary>
public abstract class InputBase<TValue> : ComponentBase
{
    [CascadingParameter] protected EditContext? CascadedEditContext { get; set; }

    [Parameter] public TValue? Value { get; set; }
    [Parameter] public EventCallback<TValue> ValueChanged { get; set; }

    /// <summary>The field name, for validation messages and CSS state.</summary>
    [Parameter] public string? Field { get; set; }

    [Parameter] public string? DisplayName { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    protected string CssClass
    {
        get
        {
            string? cls = null;
            if (AdditionalAttributes is not null && AdditionalAttributes.TryGetValue("class", out var c)) cls = c?.ToString();
            if (CascadedEditContext is null || Field is null) return cls ?? "";
            var state = CascadedEditContext.FieldCssClass(Field);
            return string.IsNullOrEmpty(cls) ? state : cls + " " + state;
        }
    }

    protected string? CurrentValueAsString
    {
        get => FormatValueAsString(Value);
        set
        {
            if (!TryParseValueFromString(value, out var parsed)) return;
            Value = parsed;
            _ = ValueChanged.InvokeAsync(parsed);
            if (CascadedEditContext is not null && Field is not null) CascadedEditContext.NotifyFieldChanged(Field);
        }
    }

    protected virtual string? FormatValueAsString(TValue? value) => value?.ToString();

    protected abstract bool TryParseValueFromString(string? value, out TValue result);

    protected void AddCommonAttributes(RenderTreeBuilder builder, int sequence)
    {
        if (AdditionalAttributes is not null)
        {
            foreach (var kv in AdditionalAttributes)
                if (kv.Key != "class") builder.AddAttribute(sequence, kv.Key, kv.Value);
        }
        var css = CssClass;
        if (css.Length > 0) builder.AddAttribute(sequence + 1, "class", css);
    }

    protected EventCallback<ChangeEventArgs> ChangeHandler() =>
        EventCallback.Factory.Create<ChangeEventArgs>(this, e => CurrentValueAsString = e.Value?.ToString());
}

public class InputText : InputBase<string?>
{
    [Parameter] public bool Immediate { get; set; }

    protected override bool TryParseValueFromString(string? value, out string? result)
    {
        result = value;
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(3, "value", CurrentValueAsString);
        builder.AddAttribute(4, Immediate ? "oninput" : "onchange", ChangeHandler());
        builder.CloseElement();
    }
}

public class InputTextArea : InputBase<string?>
{
    protected override bool TryParseValueFromString(string? value, out string? result)
    {
        result = value;
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "textarea");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(3, "value", CurrentValueAsString);
        builder.AddAttribute(4, "onchange", ChangeHandler());
        builder.CloseElement();
    }
}

public class InputNumber<TValue> : InputBase<TValue>
{
    protected override string? FormatValueAsString(TValue? value) => value switch
    {
        null => null,
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => ((double)f).ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    protected override bool TryParseValueFromString(string? value, out TValue result)
    {
        result = BindConverter.Convert<TValue>(value, default!);
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", "number");
        builder.AddAttribute(2, "step", "any");
        AddCommonAttributes(builder, 3);
        builder.AddAttribute(5, "value", CurrentValueAsString);
        builder.AddAttribute(6, "onchange", ChangeHandler());
        builder.CloseElement();
    }
}

public class InputDate<TValue> : InputBase<TValue>
{
    protected override string? FormatValueAsString(TValue? value) => value is DateTime d ? d.ToString("yyyy-MM-dd") : null;

    protected override bool TryParseValueFromString(string? value, out TValue result)
    {
        result = BindConverter.Convert<TValue>(value, default!);
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", "date");
        AddCommonAttributes(builder, 2);
        builder.AddAttribute(4, "value", CurrentValueAsString);
        builder.AddAttribute(5, "onchange", ChangeHandler());
        builder.CloseElement();
    }
}

public class InputCheckbox : InputBase<bool>
{
    protected override bool TryParseValueFromString(string? value, out bool result)
    {
        result = value == "true" || value == "True" || value == "on";
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", "checkbox");
        AddCommonAttributes(builder, 2);
        builder.AddAttribute(4, "checked", Value);
        builder.AddAttribute(5, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            var on = e.Value is bool b ? b : e.Value?.ToString() == "true";
            Value = on;
            _ = ValueChanged.InvokeAsync(on);
            if (CascadedEditContext is not null && Field is not null) CascadedEditContext.NotifyFieldChanged(Field);
        }));
        builder.CloseElement();
    }
}

public class InputSelect<TValue> : InputBase<TValue>
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override bool TryParseValueFromString(string? value, out TValue result)
    {
        result = BindConverter.Convert<TValue>(value, default!);
        return true;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "select");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(3, "value", CurrentValueAsString);
        builder.AddAttribute(4, "onchange", ChangeHandler());
        builder.AddContent(5, ChildContent);
        builder.CloseElement();
    }
}

/// <summary>Lists every validation message in the form.</summary>
public class ValidationSummary : ComponentBase
{
    [CascadingParameter] private EditContext? Context { get; set; }

    protected override void OnParametersSet()
    {
        if (Context is not null)
        {
            Context.OnValidationStateChanged -= OnChanged;
            Context.OnValidationStateChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => StateHasChanged();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null) return;
        var any = false;
        foreach (var m in Context.GetValidationMessages())
        {
            if (!any)
            {
                builder.OpenElement(0, "ul");
                builder.AddAttribute(1, "class", "validation-errors");
                any = true;
            }
            builder.OpenElement(2, "li");
            builder.AddAttribute(3, "class", "validation-message");
            builder.AddContent(4, m);
            builder.CloseElement();
        }
        if (any) builder.CloseElement();
    }
}

/// <summary>The messages for one field.</summary>
public class ValidationMessage : ComponentBase
{
    [CascadingParameter] private EditContext? Context { get; set; }
    [Parameter] public string Field { get; set; } = "";

    protected override void OnParametersSet()
    {
        if (Context is not null)
        {
            Context.OnValidationStateChanged -= OnChanged;
            Context.OnValidationStateChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => StateHasChanged();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Context is null) return;
        foreach (var m in Context.GetValidationMessages(Field))
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "validation-message");
            builder.AddContent(2, m);
            builder.CloseElement();
        }
    }
}
