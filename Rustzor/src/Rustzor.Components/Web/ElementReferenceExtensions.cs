using Rustzor.Interop;

namespace Microsoft.AspNetCore.Components;

/// <summary>Blazor's <c>FocusAsync</c>, so code written for Blazor keeps working.</summary>
public static class ElementReferenceExtensions
{
    /// <summary>Moves keyboard focus to the element, after it has rendered.</summary>
    public static ValueTask FocusAsync(this ElementReference element, bool preventScroll = false)
    {
        if (!element.IsEmpty && Js.IsBrowser)
            Js.Send("el.focus", Js.Args("el", element.Id, "preventScroll", preventScroll));
        return ValueTask.CompletedTask;
    }
}
