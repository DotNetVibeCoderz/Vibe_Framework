using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.DOM;

/// <summary>A rectangle in CSS pixels, from <c>getBoundingClientRect()</c>.</summary>
public readonly record struct DomRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>The document: title, theme, querying, and scripts or styles added at run time.</summary>
public static class Document
{
    public static string Title
    {
        get => Js.Call("dom.title").AsString();
        set => Js.Send("doc.title", Js.Args("title", value));
    }

    /// <summary>Sets an attribute on &lt;html&gt;, e.g. <c>data-theme</c>.</summary>
    public static void SetRootAttribute(string name, string? value) => Js.Send("dom.rootAttr", Js.Args("name", name, "value", value));

    public static string? GetRootAttribute(string name) => Js.Call("dom.getRootAttr", Js.Args("name", name)).AsStringOrNull();

    /// <summary>Sets a CSS custom property on the document: <c>SetCssVariable("--accent", "#f74c00")</c>.</summary>
    public static void SetCssVariable(string name, string value) => Js.Send("dom.cssVar", Js.Args("name", name, "value", value));

    /// <summary>Adds a stylesheet link once.</summary>
    public static void AddStylesheet(string href) => Js.Send("dom.addStyle", Js.Args("href", href));

    /// <summary>Loads a classic script once; completes when it has run.</summary>
    public static Task AddScriptAsync(string src) => Js.CallAsync("dom.addScript", Js.Args("src", src));

    /// <summary>Text of the first element matching a CSS selector, or null.</summary>
    public static string? QueryText(string selector) => Js.Call("dom.queryText", Js.Args("selector", selector)).AsStringOrNull();

    /// <summary>The element matching a selector, as a handle for the generic interop.</summary>
    public static JsObject? Query(string selector) => JsObject.FromWire(Js.Call("dom.query", Js.Args("selector", selector)));

    public static bool IsVisible => Js.Call("dom.visible").AsBool();

    /// <summary>Fires when the tab is hidden or shown; the argument is whether it is visible.</summary>
    public static IDisposable OnVisibilityChange(Action<bool> handler) =>
        JsObject.Global("document")!.On("visibilitychange", _ => handler(IsVisible));
}

/// <summary>An element captured with <c>@ref</c>.</summary>
public static class ElementExtensions
{
    public static void Focus(this ElementReference element) => Js.Send("dom.el", Js.Args("el", element.Id, "call", "focus"));

    public static void Blur(this ElementReference element) => Js.Send("dom.el", Js.Args("el", element.Id, "call", "blur"));

    public static void Click(this ElementReference element) => Js.Send("dom.el", Js.Args("el", element.Id, "call", "click"));

    /// <param name="block">"nearest" (the default) scrolls as little as possible; "start", "center" or "end" align the element.</param>
    public static void ScrollIntoView(this ElementReference element, bool smooth = true, string block = "nearest") =>
        Js.Send("dom.scrollIntoView", Js.Args("el", element.Id, "smooth", smooth, "block", block));

    /// <summary>Scrolls the element to its bottom, as a chat log does after a new message.</summary>
    public static void ScrollToBottom(this ElementReference element) => Js.Send("dom.scrollBottom", Js.Args("el", element.Id));

    public static DomRect GetBoundingRect(this ElementReference element)
    {
        var r = Js.Call("dom.rect", Js.Args("el", element.Id));
        return new DomRect(r["x"].AsDouble(), r["y"].AsDouble(), r["width"].AsDouble(), r["height"].AsDouble());
    }

    public static void SetStyle(this ElementReference element, string property, string? value) =>
        Js.Send("dom.style", Js.Args("el", element.Id, "prop", property, "value", value));

    public static void ToggleClass(this ElementReference element, string className, bool on) =>
        Js.Send("dom.toggleClass", Js.Args("el", element.Id, "cls", className, "on", on));

    public static string GetValue(this ElementReference element) => Js.Call("dom.value", Js.Args("el", element.Id)).AsString();

    public static void SetValue(this ElementReference element, string value) => Js.Send("dom.setValue", Js.Args("el", element.Id, "value", value));

    public static void SelectAll(this ElementReference element) => Js.Send("dom.el", Js.Args("el", element.Id, "call", "select"));

    public static Task RequestFullscreenAsync(this ElementReference element) => Js.CallAsync("dom.fullscreen", Js.Args("el", element.Id));

    /// <summary>A JS handle to the element, for the generic interop.</summary>
    public static JsObject AsJsObject(this ElementReference element) => JsObject.FromWire(Js.Call("dom.handle", Js.Args("el", element.Id)))!;

    /// <summary>Reports the element's size whenever it changes (ResizeObserver).</summary>
    public static IDisposable OnResize(this ElementReference element, Action<DomRect> handler)
    {
        var id = Callbacks.Register(r => handler(new DomRect(0, 0, r["width"].AsDouble(), r["height"].AsDouble())));
        var token = Js.Call("dom.observeResize", Js.Args("el", element.Id, "id", id)).AsInt();
        return new Unsubscribe(id, token);
    }

    internal sealed class Unsubscribe : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Unsubscribe(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            Js.Send("js.off", Js.Args("token", _token));
            Callbacks.Unregister(_id);
            _token = 0;
        }
    }
}

/// <summary>The window: size, scrolling, timers by name, keyboard shortcuts, dialogs.</summary>
public static class Window
{
    public static double InnerWidth => Js.Call("dom.window").Prop("w");
    public static double InnerHeight => Js.Call("dom.window").Prop("h");
    public static double DevicePixelRatio => Js.Call("dom.window").Prop("dpr");

    /// <summary>True when the user prefers a dark colour scheme.</summary>
    public static bool PrefersDark => Js.Call("dom.prefersDark").AsBool();

    public static void ScrollTo(double x, double y, bool smooth = false) => Js.Send("dom.scrollTo", Js.Args("x", x, "y", y, "smooth", smooth));

    public static void Alert(string message) => Js.Send("dom.alert", Js.Args("message", message));

    public static bool Confirm(string message) => Js.Call("dom.confirm", Js.Args("message", message)).AsBool();

    public static string? Prompt(string message, string? defaultValue = null) =>
        Js.Call("dom.prompt", Js.Args("message", message, "value", defaultValue)).AsStringOrNull();

    public static void Open(string url, string target = "_blank") => Js.Send("dom.open", Js.Args("url", url, "target", target));

    public static void Print() => Js.Send("dom.print");

    /// <summary>Fires on every window resize with the new inner size.</summary>
    public static IDisposable OnResize(Action<double, double> handler) =>
        JsObject.OnWindow("resize", _ => handler(InnerWidth, InnerHeight));

    /// <summary>
    /// Fires on key presses anywhere on the page. <paramref name="handler"/> gets the
    /// key, and whether Ctrl/Cmd, Shift and Alt were held.
    /// </summary>
    public static IDisposable OnKeyDown(Action<KeyPress> handler) =>
        JsObject.OnWindow("keydown", e => handler(new KeyPress(e["key"].AsString(), e["code"].AsString(), e["ctrlKey"].AsBool() || e["metaKey"].AsBool(), e["shiftKey"].AsBool(), e["altKey"].AsBool())));

    public static IDisposable OnKeyUp(Action<KeyPress> handler) =>
        JsObject.OnWindow("keyup", e => handler(new KeyPress(e["key"].AsString(), e["code"].AsString(), e["ctrlKey"].AsBool() || e["metaKey"].AsBool(), e["shiftKey"].AsBool(), e["altKey"].AsBool())));

    /// <summary>Fires when the network goes on or off line.</summary>
    public static IDisposable OnOnlineChange(Action<bool> handler)
    {
        var on = JsObject.OnWindow("online", _ => handler(true));
        var off = JsObject.OnWindow("offline", _ => handler(false));
        return new Both(on, off);
    }

    public static bool IsOnline => Js.Call("dom.online").AsBool();

    private static double Prop(this JsonValue v, string name) => v[name].AsDouble();

    private sealed class Both : IDisposable
    {
        private readonly IDisposable _a, _b;

        public Both(IDisposable a, IDisposable b)
        {
            _a = a;
            _b = b;
        }

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }
    }
}

public readonly record struct KeyPress(string Key, string Code, bool Ctrl, bool Shift, bool Alt);

/// <summary>The clipboard.</summary>
public static class Clipboard
{
    public static Task WriteTextAsync(string text) => Js.CallAsync("dom.clipWrite", Js.Args("text", text));

    public static async Task<string> ReadTextAsync() => (await Js.CallAsync("dom.clipRead")).AsString();
}

/// <summary>Downloads made from C#: a text or data file the user saves.</summary>
public static class Download
{
    /// <summary>Saves <paramref name="content"/> as a file named <paramref name="fileName"/>.</summary>
    public static void Text(string fileName, string content, string mimeType = "text/plain") =>
        Js.Send("dom.download", Js.Args("name", fileName, "text", content, "type", mimeType));

    /// <summary>Saves a data: or blob: URL, e.g. a canvas export.</summary>
    public static void Url(string fileName, string url) => Js.Send("dom.download", Js.Args("name", fileName, "url", url));
}
