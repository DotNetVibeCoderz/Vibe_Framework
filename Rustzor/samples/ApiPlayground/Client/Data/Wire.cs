using Rustzor.Browser.Storage;

namespace ApiPlayground.Data;

public enum Protocol { Rest, WebSocket, SignalR, Grpc }

/// <summary>One thing that crossed the wire: a request, a response, a frame.</summary>
public sealed class WireEntry
{
    public int Id { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
    public Protocol Protocol { get; init; }
    /// <summary>"out" (browser to server), "in", or "note" for state changes.</summary>
    public string Direction { get; init; } = "out";
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public int Bytes { get; init; }
}

/// <summary>
/// The wire log every screen writes to: the signature of the playground. It
/// keeps the last 400 entries and tells the panel when something arrives.
/// </summary>
public static class Wire
{
    private static int _next;

    public static List<WireEntry> Entries { get; } = new();
    public static event Action? Changed;

    public static void Log(Protocol protocol, string direction, string summary, string detail = "", int bytes = -1)
    {
        Entries.Add(new WireEntry
        {
            Id = ++_next,
            Protocol = protocol,
            Direction = direction,
            Summary = summary,
            Detail = detail,
            Bytes = bytes >= 0 ? bytes : System.Text.Encoding.UTF8.GetByteCount(detail),
        });
        if (Entries.Count > 400) Entries.RemoveAt(0);
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Entries.Clear();
        Changed?.Invoke();
    }

    public static string Name(Protocol p) => p switch { Protocol.Rest => "REST", Protocol.WebSocket => "WebSocket", Protocol.SignalR => "SignalR", _ => "gRPC-Web" };
    public static string Key(Protocol p) => p switch { Protocol.Rest => "rest", Protocol.WebSocket => "ws", Protocol.SignalR => "signalr", _ => "grpc" };

    public static string Size(int bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.0} KB";
}

/// <summary>Where the server is: the page's own origin unless the person points elsewhere.</summary>
public static class Server
{
    private const string Key = "api-playground-server";

    public static string Address
    {
        get
        {
            try { return WebStorage.Local.Get(Key) ?? ""; } catch { return ""; }
        }
        set
        {
            var v = value.Trim().TrimEnd('/');
            if (v.Length == 0) WebStorage.Local.Remove(Key);
            else WebStorage.Local.Set(Key, v);
        }
    }

    /// <summary>An endpoint on the server: "/api/books" becomes "http://host/api/books" when an address is set.</summary>
    public static string Url(string path) => Address + path;

    /// <summary>The origin calls go to: the address, or the page's own.</summary>
    public static string Origin => Address.Length > 0 ? Address : Rustzor.Interop.Js.Call("js.eval", Rustzor.Interop.Js.Args("code", "location.origin")).AsString();

    /// <summary>A WebSocket URL for a path: http becomes ws, https becomes wss.</summary>
    public static string SocketUrl(string path) => "ws" + Origin.Substring(4) + path;
}
