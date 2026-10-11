using Rustzor.Interop;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>What <c>onchange</c> and <c>oninput</c> carry: the element's new value.</summary>
    public class ChangeEventArgs : EventArgs
    {
        public object? Value { get; set; }
    }

    namespace CompilerServices
    {
        /// <summary>Helpers the Razor compiler's generated code calls.</summary>
        public static class RuntimeHelpers
        {
            public static T TypeCheck<T>(T value) => value;

            public static EventCallback<T> CreateInferredEventCallback<T>(object receiver, Action<T> callback, T value) =>
                EventCallback.Factory.Create(receiver, callback);

            public static EventCallback<T> CreateInferredEventCallback<T>(object receiver, Func<T, Task> callback, T value) =>
                EventCallback.Factory.Create(receiver, callback);

            public static Action<T> CreateInferredBindSetter<T>(Action<T> callback, T value) => callback;

            public static Func<T, Task> CreateInferredBindSetter<T>(Func<T, Task> callback, T value) => callback;

            public static void InvokeSynchronousDelegate(Action callback) => callback();

            public static Task InvokeAsynchronousDelegate(Action callback)
            {
                callback();
                return Task.CompletedTask;
            }

            public static Task InvokeAsynchronousDelegate(Func<Task> callback) => callback();
        }
    }
}

namespace Microsoft.AspNetCore.Components.Web
{
    public class MouseEventArgs : EventArgs
    {
        public long Detail { get; set; }
        public double ScreenX { get; set; }
        public double ScreenY { get; set; }
        public double ClientX { get; set; }
        public double ClientY { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double PageX { get; set; }
        public double PageY { get; set; }
        public double MovementX { get; set; }
        public double MovementY { get; set; }
        public long Button { get; set; }
        public long Buttons { get; set; }
        public bool CtrlKey { get; set; }
        public bool ShiftKey { get; set; }
        public bool AltKey { get; set; }
        public bool MetaKey { get; set; }
        public string Type { get; set; } = "";
    }

    public class PointerEventArgs : MouseEventArgs
    {
        public long PointerId { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Pressure { get; set; }
        public string PointerType { get; set; } = "";
        public bool IsPrimary { get; set; }
    }

    public class WheelEventArgs : MouseEventArgs
    {
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
        public double DeltaZ { get; set; }
        public long DeltaMode { get; set; }
    }

    public class DragEventArgs : MouseEventArgs
    {
    }

    public class KeyboardEventArgs : EventArgs
    {
        public string Key { get; set; } = "";
        public string Code { get; set; } = "";
        public float Location { get; set; }
        public bool Repeat { get; set; }
        public bool CtrlKey { get; set; }
        public bool ShiftKey { get; set; }
        public bool AltKey { get; set; }
        public bool MetaKey { get; set; }
        public string Type { get; set; } = "";
        public bool IsComposing { get; set; }
    }

    public class FocusEventArgs : EventArgs
    {
        public string? Type { get; set; }
    }

    public class ClipboardEventArgs : EventArgs
    {
        public string Type { get; set; } = "";
    }

    public class ProgressEventArgs : EventArgs
    {
        public bool LengthComputable { get; set; }
        public long Loaded { get; set; }
        public long Total { get; set; }
        public string Type { get; set; } = "";
    }

    public class TouchPoint
    {
        public double ClientX { get; set; }
        public double ClientY { get; set; }
    }

    public class TouchEventArgs : EventArgs
    {
        public TouchPoint[] Touches { get; set; } = Array.Empty<TouchPoint>();
        public bool CtrlKey { get; set; }
        public bool ShiftKey { get; set; }
        public bool AltKey { get; set; }
        public bool MetaKey { get; set; }
        public string Type { get; set; } = "";
    }

    public class ErrorEventArgs : EventArgs
    {
        public string? Message { get; set; }
        public string? Type { get; set; }
    }

    // The event names the Razor compiler recognises, and the argument type each
    // carries. The compiler reads these attributes; nothing executes them.
    [EventHandler("onfocus", typeof(FocusEventArgs), true, true)]
    [EventHandler("onblur", typeof(FocusEventArgs), true, true)]
    [EventHandler("onfocusin", typeof(FocusEventArgs), true, true)]
    [EventHandler("onfocusout", typeof(FocusEventArgs), true, true)]
    [EventHandler("onmouseover", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmouseout", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmouseleave", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmouseenter", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmousemove", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmousedown", typeof(MouseEventArgs), true, true)]
    [EventHandler("onmouseup", typeof(MouseEventArgs), true, true)]
    [EventHandler("onclick", typeof(MouseEventArgs), true, true)]
    [EventHandler("ondblclick", typeof(MouseEventArgs), true, true)]
    [EventHandler("onwheel", typeof(WheelEventArgs), true, true)]
    [EventHandler("onmousewheel", typeof(WheelEventArgs), true, true)]
    [EventHandler("oncontextmenu", typeof(MouseEventArgs), true, true)]
    [EventHandler("ondrag", typeof(DragEventArgs), true, true)]
    [EventHandler("ondragend", typeof(DragEventArgs), true, true)]
    [EventHandler("ondragenter", typeof(DragEventArgs), true, true)]
    [EventHandler("ondragleave", typeof(DragEventArgs), true, true)]
    [EventHandler("ondragover", typeof(DragEventArgs), true, true)]
    [EventHandler("ondragstart", typeof(DragEventArgs), true, true)]
    [EventHandler("ondrop", typeof(DragEventArgs), true, true)]
    [EventHandler("onkeydown", typeof(KeyboardEventArgs), true, true)]
    [EventHandler("onkeyup", typeof(KeyboardEventArgs), true, true)]
    [EventHandler("onkeypress", typeof(KeyboardEventArgs), true, true)]
    [EventHandler("onchange", typeof(ChangeEventArgs), true, true)]
    [EventHandler("oninput", typeof(ChangeEventArgs), true, true)]
    [EventHandler("oninvalid", typeof(EventArgs), true, true)]
    [EventHandler("onreset", typeof(EventArgs), true, true)]
    [EventHandler("onselect", typeof(EventArgs), true, true)]
    [EventHandler("onselectstart", typeof(EventArgs), true, true)]
    [EventHandler("onselectionchange", typeof(EventArgs), true, true)]
    [EventHandler("onsubmit", typeof(EventArgs), true, true)]
    [EventHandler("onbeforecopy", typeof(EventArgs), true, true)]
    [EventHandler("onbeforecut", typeof(EventArgs), true, true)]
    [EventHandler("onbeforepaste", typeof(EventArgs), true, true)]
    [EventHandler("oncopy", typeof(ClipboardEventArgs), true, true)]
    [EventHandler("oncut", typeof(ClipboardEventArgs), true, true)]
    [EventHandler("onpaste", typeof(ClipboardEventArgs), true, true)]
    [EventHandler("ontouchcancel", typeof(TouchEventArgs), true, true)]
    [EventHandler("ontouchend", typeof(TouchEventArgs), true, true)]
    [EventHandler("ontouchmove", typeof(TouchEventArgs), true, true)]
    [EventHandler("ontouchstart", typeof(TouchEventArgs), true, true)]
    [EventHandler("ongotpointercapture", typeof(PointerEventArgs), true, true)]
    [EventHandler("onlostpointercapture", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointercancel", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerdown", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerenter", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerleave", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointermove", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerout", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerover", typeof(PointerEventArgs), true, true)]
    [EventHandler("onpointerup", typeof(PointerEventArgs), true, true)]
    [EventHandler("oncanplay", typeof(EventArgs), true, true)]
    [EventHandler("onended", typeof(EventArgs), true, true)]
    [EventHandler("onloadeddata", typeof(EventArgs), true, true)]
    [EventHandler("onpause", typeof(EventArgs), true, true)]
    [EventHandler("onplay", typeof(EventArgs), true, true)]
    [EventHandler("onplaying", typeof(EventArgs), true, true)]
    [EventHandler("ontimeupdate", typeof(EventArgs), true, true)]
    [EventHandler("onvolumechange", typeof(EventArgs), true, true)]
    [EventHandler("onload", typeof(ProgressEventArgs), true, true)]
    [EventHandler("onerror", typeof(ErrorEventArgs), true, true)]
    [EventHandler("onscroll", typeof(EventArgs), true, true)]
    [EventHandler("ontoggle", typeof(EventArgs), true, true)]
    [EventHandler("onanimationend", typeof(EventArgs), true, true)]
    [EventHandler("ontransitionend", typeof(EventArgs), true, true)]
    public static class EventHandlers
    {
    }

    // What @bind means on each element: which attribute holds the value and
    // which event reports a change.
    [BindElement("select", null, "value", "onchange")]
    [BindElement("textarea", null, "value", "onchange")]
    [BindInputElement(null, null, "value", "onchange", isInvariantCulture: false, format: null)]
    [BindInputElement(null, "value", "value", "onchange", isInvariantCulture: false, format: null)]
    [BindInputElement("checkbox", null, "checked", "onchange", isInvariantCulture: false, format: null)]
    [BindInputElement("text", null, "value", "onchange", isInvariantCulture: false, format: null)]
    [BindInputElement("number", null, "value", "onchange", isInvariantCulture: true, format: null)]
    [BindInputElement("number", "value", "value", "onchange", isInvariantCulture: true, format: null)]
    [BindInputElement("date", null, "value", "onchange", isInvariantCulture: true, format: "yyyy-MM-dd")]
    [BindInputElement("date", "value", "value", "onchange", isInvariantCulture: true, format: "yyyy-MM-dd")]
    [BindInputElement("datetime-local", null, "value", "onchange", isInvariantCulture: true, format: "yyyy-MM-ddTHH:mm:ss")]
    [BindInputElement("month", null, "value", "onchange", isInvariantCulture: true, format: "yyyy-MM")]
    [BindInputElement("time", null, "value", "onchange", isInvariantCulture: true, format: "HH:mm:ss")]
    [BindInputElement("range", null, "value", "onchange", isInvariantCulture: true, format: null)]
    [BindInputElement("color", null, "value", "onchange", isInvariantCulture: true, format: null)]
    public static class BindAttributes
    {
    }

    /// <summary>Turns the browser's event payload into the typed event args.</summary>
    internal static class EventArgsFactory
    {
        public static EventArgs Create(string type, JsonValue e)
        {
            switch (type)
            {
                case "change":
                case "input":
                    return new ChangeEventArgs
                    {
                        // rustzor.js sends a checkbox's `checked` as the value.
                        Value = e["value"].IsBool ? e["value"].AsBool() : (object?)e["value"].AsStringOrNull(),
                    };
                case "keydown":
                case "keyup":
                case "keypress":
                    return new KeyboardEventArgs
                    {
                        Type = type,
                        Key = e["key"].AsString(),
                        Code = e["code"].AsString(),
                        Repeat = e["repeat"].AsBool(),
                        CtrlKey = e["ctrlKey"].AsBool(),
                        ShiftKey = e["shiftKey"].AsBool(),
                        AltKey = e["altKey"].AsBool(),
                        MetaKey = e["metaKey"].AsBool(),
                    };
                case "wheel":
                case "mousewheel":
                    var wheel = new WheelEventArgs { DeltaX = e["deltaX"].AsDouble(), DeltaY = e["deltaY"].AsDouble() };
                    FillMouse(wheel, type, e);
                    return wheel;
                case "focus":
                case "blur":
                case "focusin":
                case "focusout":
                    return new FocusEventArgs { Type = type };
                case "copy":
                case "cut":
                case "paste":
                    return new ClipboardEventArgs { Type = type };
                case "touchstart":
                case "touchmove":
                case "touchend":
                case "touchcancel":
                    var touches = new List<TouchPoint>();
                    foreach (var t in e["touches"].Items) touches.Add(new TouchPoint { ClientX = t[0].AsDouble(), ClientY = t[1].AsDouble() });
                    return new TouchEventArgs { Type = type, Touches = touches.ToArray() };
                case "error":
                    return new ErrorEventArgs { Type = type, Message = e["message"].AsStringOrNull() };
            }
            if (type.StartsWith("pointer") || type == "gotpointercapture" || type == "lostpointercapture")
            {
                var p = new PointerEventArgs { PointerId = e["pointerId"].AsLong(), PointerType = e["pointerType"].AsString(), Pressure = (float)e["pressure"].AsDouble(), IsPrimary = e["isPrimary"].AsBool() };
                FillMouse(p, type, e);
                return p;
            }
            if (type.StartsWith("mouse") || type == "click" || type == "dblclick" || type == "contextmenu")
            {
                var m = new MouseEventArgs();
                FillMouse(m, type, e);
                return m;
            }
            if (type.StartsWith("drag") || type == "drop")
            {
                var d = new DragEventArgs();
                FillMouse(d, type, e);
                return d;
            }
            return EventArgs.Empty;
        }

        private static void FillMouse(MouseEventArgs m, string type, JsonValue e)
        {
            m.Type = type;
            m.ClientX = e["clientX"].AsDouble();
            m.ClientY = e["clientY"].AsDouble();
            m.OffsetX = e["offsetX"].AsDouble();
            m.OffsetY = e["offsetY"].AsDouble();
            m.PageX = e["pageX"].AsDouble();
            m.PageY = e["pageY"].AsDouble();
            m.MovementX = e["movementX"].AsDouble();
            m.MovementY = e["movementY"].AsDouble();
            m.Button = e["button"].AsLong();
            m.Buttons = e["buttons"].AsLong();
            m.Detail = e["detail"].AsLong();
            m.CtrlKey = e["ctrlKey"].AsBool();
            m.ShiftKey = e["shiftKey"].AsBool();
            m.AltKey = e["altKey"].AsBool();
            m.MetaKey = e["metaKey"].AsBool();
        }
    }
}
