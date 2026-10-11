using System.Globalization;

namespace Microsoft.AspNetCore.Components;

/// <summary>A delegate plus the component that should re-render after it runs.</summary>
public readonly struct EventCallback
{
    public static readonly EventCallbackFactory Factory = new();
    public static readonly EventCallback Empty = default;

    internal readonly IHandleEvent? Receiver;
    internal readonly MulticastDelegate? Delegate;

    public EventCallback(IHandleEvent? receiver, MulticastDelegate? @delegate)
    {
        Receiver = receiver;
        Delegate = @delegate;
    }

    public bool HasDelegate => Delegate is not null;

    public Task InvokeAsync(object? arg) => EventCallbackWorkItem.Invoke(Receiver, Delegate, arg);

    public Task InvokeAsync() => InvokeAsync(null);

    internal object? UnpackForRenderer() => Delegate is null ? null : this;
}

/// <summary>A typed callback, e.g. <c>EventCallback&lt;MouseEventArgs&gt;</c>.</summary>
public readonly struct EventCallback<TValue> : Microsoft.AspNetCore.Components.Rendering.IEventCallbackLike
{
    EventCallback Microsoft.AspNetCore.Components.Rendering.IEventCallbackLike.Untyped => AsUntyped();

    public static readonly EventCallback<TValue> Empty = default;

    internal readonly IHandleEvent? Receiver;
    internal readonly MulticastDelegate? Delegate;

    public EventCallback(IHandleEvent? receiver, MulticastDelegate? @delegate)
    {
        Receiver = receiver;
        Delegate = @delegate;
    }

    public bool HasDelegate => Delegate is not null;

    public Task InvokeAsync(TValue? arg) => EventCallbackWorkItem.Invoke(Receiver, Delegate, arg);

    public Task InvokeAsync() => InvokeAsync(default);

    internal EventCallback AsUntyped() => new(Receiver, Delegate);
}

/// <summary>The work an event does: the delegate and its argument.</summary>
public readonly struct EventCallbackWorkItem
{
    private readonly MulticastDelegate? _delegate;

    public EventCallbackWorkItem(MulticastDelegate? @delegate) => _delegate = @delegate;

    public static readonly EventCallbackWorkItem Empty = default;

    public Task InvokeAsync(object? arg) => InvokeDelegate(_delegate, arg);

    internal static Task Invoke(IHandleEvent? receiver, MulticastDelegate? @delegate, object? arg)
    {
        if (receiver is not null) return receiver.HandleEventAsync(new EventCallbackWorkItem(@delegate), arg);
        return InvokeDelegate(@delegate, arg);
    }

    internal static Task InvokeDelegate(MulticastDelegate? @delegate, object? arg)
    {
        // The factory normalises every typed delegate to one of these three
        // shapes when the callback is created, so invoking never needs
        // reflection: Action<MouseEventArgs> arrives here as Func<object?, Task>.
        switch (@delegate)
        {
            case null:
                return Task.CompletedTask;
            case Func<object?, Task> withArg:
                return withArg(arg) ?? Task.CompletedTask;
            case Action action:
                action();
                return Task.CompletedTask;
            case Func<Task> func:
                return func() ?? Task.CompletedTask;
            default:
                throw new InvalidOperationException("Unsupported event handler delegate: " + @delegate.GetType().Name);
        }
    }
}

/// <summary>What <c>EventCallback.Factory</c> is; the Razor compiler calls these.</summary>
public sealed class EventCallbackFactory
{
    private static IHandleEvent? AsReceiver(object receiver) => receiver as IHandleEvent;

    // A cast, not `arg is T`: the closure's class is generic over T, and a
    // type test inside it needs T at run time, which an erased runtime may not
    // have there. The renderer only ever passes a T or null.
    internal static Func<object?, Task> Wrap<T>(Action<T> callback) => arg =>
    {
        callback(arg is null ? default! : (T)arg);
        return Task.CompletedTask;
    };

    internal static Func<object?, Task> Wrap<T>(Func<T, Task> callback) => arg => callback(arg is null ? default! : (T)arg);

    public EventCallback Create(object receiver, EventCallback callback) => callback;
    public EventCallback Create(object receiver, Action callback) => new(AsReceiver(receiver), callback);
    public EventCallback Create(object receiver, Action<object> callback) => new(AsReceiver(receiver), Wrap(callback));
    public EventCallback Create(object receiver, Func<Task> callback) => new(AsReceiver(receiver), callback);
    public EventCallback Create(object receiver, Func<object, Task> callback) => new(AsReceiver(receiver), Wrap(callback));

    public EventCallback<TValue> Create<TValue>(object receiver, string callback) =>
        throw new InvalidOperationException("Event handlers must be delegates, not strings.");
    public EventCallback<TValue> Create<TValue>(object receiver, EventCallback callback) => new(callback.Receiver, callback.Delegate);
    public EventCallback<TValue> Create<TValue>(object receiver, EventCallback<TValue> callback) => callback;
    public EventCallback<TValue> Create<TValue>(object receiver, Action callback) => new(AsReceiver(receiver), callback);
    public EventCallback<TValue> Create<TValue>(object receiver, Action<TValue> callback) => new(AsReceiver(receiver), Wrap(callback));
    public EventCallback<TValue> Create<TValue>(object receiver, Func<Task> callback) => new(AsReceiver(receiver), callback);
    public EventCallback<TValue> Create<TValue>(object receiver, Func<TValue, Task> callback) => new(AsReceiver(receiver), Wrap(callback));

    public EventCallback<TValue> CreateInferred<TValue>(object receiver, Action<TValue> callback, TValue value) => Create(receiver, callback);
    public EventCallback<TValue> CreateInferred<TValue>(object receiver, Func<TValue, Task> callback, TValue value) => Create(receiver, callback);
}

/// <summary>The <c>@bind</c> setters: parse the browser's string into the field's type.</summary>
public static class EventCallbackFactoryBinderExtensions
{
    private static EventCallback<ChangeEventArgs> Binder<T>(object receiver, Action<T> setter, Func<object?, T> convert) =>
        new(receiver as IHandleEvent, EventCallbackFactory.Wrap<ChangeEventArgs>(e => setter(convert(e.Value))));

    private static EventCallback<ChangeEventArgs> BinderAsync<T>(object receiver, Func<T, Task> setter, Func<object?, T> convert) =>
        new(receiver as IHandleEvent, EventCallbackFactory.Wrap<ChangeEventArgs>(e => setter(convert(e.Value))));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<string?> setter, string? existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => v?.ToString());
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<string?, Task> setter, string? existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => v?.ToString());

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<bool> setter, bool existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, BindConverter.ToBool);
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<bool, Task> setter, bool existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, BindConverter.ToBool);

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<int> setter, int existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.ToInt(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<int, Task> setter, int existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => BindConverter.ToInt(v, existingValue));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<long> setter, long existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => (long)BindConverter.ToDouble(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<long, Task> setter, long existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => (long)BindConverter.ToDouble(v, existingValue));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<double> setter, double existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.ToDouble(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<double, Task> setter, double existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => BindConverter.ToDouble(v, existingValue));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<decimal> setter, decimal existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.ToDecimal(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Func<decimal, Task> setter, decimal existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => BindConverter.ToDecimal(v, existingValue));

    // Nullable numbers: an empty input is null rather than zero.
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<int?> setter, int? existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.IsEmpty(v) ? null : BindConverter.ToInt(v, existingValue ?? 0));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<double?> setter, double? existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.IsEmpty(v) ? null : BindConverter.ToDouble(v, existingValue ?? 0));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<decimal?> setter, decimal? existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.IsEmpty(v) ? null : BindConverter.ToDecimal(v, existingValue ?? 0m));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<float> setter, float existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => (float)BindConverter.ToDouble(v, existingValue));

    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<DateTime> setter, DateTime existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.ToDateTime(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder(this EventCallbackFactory factory, object receiver, Action<DateTime> setter, DateTime existingValue, string format, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.ToDateTime(v, existingValue));

    public static EventCallback<ChangeEventArgs> CreateBinder<T>(this EventCallbackFactory factory, object receiver, Action<T> setter, T existingValue, CultureInfo? culture = null) =>
        Binder(receiver, setter, v => BindConverter.Convert(v, existingValue));
    public static EventCallback<ChangeEventArgs> CreateBinder<T>(this EventCallbackFactory factory, object receiver, Func<T, Task> setter, T existingValue, CultureInfo? culture = null) =>
        BinderAsync(receiver, setter, v => BindConverter.Convert(v, existingValue));
}

/// <summary>How a value is written into an attribute and read back.</summary>
public static class BindConverter
{
    public static string? FormatValue(string? value, CultureInfo? culture = null) => value;
    public static bool FormatValue(bool value, CultureInfo? culture = null) => value;
    public static string FormatValue(int value, CultureInfo? culture = null) => value.ToString();
    public static string FormatValue(long value, CultureInfo? culture = null) => value.ToString();
    public static string FormatValue(double value, CultureInfo? culture = null) => value.ToString(CultureInfo.InvariantCulture);
    public static string FormatValue(float value, CultureInfo? culture = null) => ((double)value).ToString(CultureInfo.InvariantCulture);
    public static string FormatValue(decimal value, CultureInfo? culture = null) => value.ToString(CultureInfo.InvariantCulture);
    public static string? FormatValue(int? value, CultureInfo? culture = null) => value?.ToString();
    public static string? FormatValue(double? value, CultureInfo? culture = null) => value?.ToString(CultureInfo.InvariantCulture);
    public static string? FormatValue(decimal? value, CultureInfo? culture = null) => value?.ToString(CultureInfo.InvariantCulture);
    public static string FormatValue(DateTime value, CultureInfo? culture = null) => value.ToString("yyyy-MM-dd");
    public static string FormatValue(DateTime value, string format, CultureInfo? culture = null) => value.ToString(format);

    public static object? FormatValue<T>(T value, CultureInfo? culture = null)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case double d: return d.ToString(CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture);
            case DateTime dt: return dt.ToString("yyyy-MM-dd");
            case Enum e: return e.ToString();
            default: return value.ToString();
        }
    }

    internal static bool ToBool(object? v) => v switch
    {
        bool b => b,
        string s => s == "true" || s == "on",
        _ => false,
    };

    internal static int ToInt(object? v, int fallback)
    {
        var s = v?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : fallback;
    }

    internal static double ToDouble(object? v, double fallback)
    {
        var s = v?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    internal static bool IsEmpty(object? v) => string.IsNullOrWhiteSpace(v?.ToString());

    internal static decimal ToDecimal(object? v, decimal fallback)
    {
        var s = v?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    internal static DateTime ToDateTime(object? v, DateTime fallback)
    {
        var s = v?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        return DateTime.TryParse(s, out var d) ? d : fallback;
    }

    internal static T Convert<T>(object? v, T fallback)
    {
        object? result;
        if (typeof(T) == typeof(string)) result = v?.ToString();
        else if (typeof(T) == typeof(int)) result = ToInt(v, fallback is int i ? i : 0);
        else if (typeof(T) == typeof(double)) result = ToDouble(v, fallback is double d ? d : 0);
        else if (typeof(T) == typeof(decimal)) result = ToDecimal(v, fallback is decimal m ? m : 0m);
        else if (typeof(T) == typeof(long)) result = (long)ToDouble(v, 0);
        else if (typeof(T) == typeof(float)) result = (float)ToDouble(v, 0);
        else if (typeof(T) == typeof(bool)) result = ToBool(v);
        else if (typeof(T) == typeof(DateTime)) result = ToDateTime(v, fallback is DateTime dt ? dt : default);
        else if (typeof(T).IsEnum) result = Enum.Parse(typeof(T), v?.ToString() ?? "");
        else result = v;
        return result is null ? default! : (T)result;
    }

    public static bool TryConvertTo<T>(object? obj, CultureInfo? culture, out T? value)
    {
        value = Convert<T>(obj, default!);
        return true;
    }
}
