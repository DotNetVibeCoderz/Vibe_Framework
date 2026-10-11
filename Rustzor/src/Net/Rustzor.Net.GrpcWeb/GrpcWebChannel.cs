using Rustzor.Interop;

namespace Rustzor.Net.GrpcWeb;

/// <summary>gRPC status codes.</summary>
public enum StatusCode
{
    OK = 0, Cancelled = 1, Unknown = 2, InvalidArgument = 3, DeadlineExceeded = 4, NotFound = 5, AlreadyExists = 6,
    PermissionDenied = 7, ResourceExhausted = 8, FailedPrecondition = 9, Aborted = 10, OutOfRange = 11,
    Unimplemented = 12, Internal = 13, Unavailable = 14, DataLoss = 15, Unauthenticated = 16,
}

/// <summary>A call that ended with a status other than OK.</summary>
public sealed class RpcException : Exception
{
    public RpcException(StatusCode status, string detail, IReadOnlyDictionary<string, string> trailers)
        : base($"Status(StatusCode=\"{status}\", Detail=\"{detail}\")")
    {
        Status = status;
        Detail = detail;
        Trailers = trailers;
    }

    public StatusCode Status { get; }
    public string Detail { get; }
    public IReadOnlyDictionary<string, string> Trailers { get; }
}

/// <summary>What a frame on the wire was, for logging and teaching.</summary>
public sealed record GrpcFrame(string Direction, string Kind, int Length, string Hex);

/// <summary>
/// gRPC-Web over the browser's fetch (<c>application/grpc-web+proto</c>).
/// Each message travels in a five-byte frame: a flag (0 data, 0x80 trailers)
/// and a big-endian length. Unary and server-streaming calls are supported,
/// which is all gRPC-Web offers a browser. The server needs
/// <c>app.UseGrpcWeb()</c> (ASP.NET Core) or an Envoy proxy.
/// </summary>
/// <example>
/// <code>
/// var channel = new GrpcWebChannel("https://localhost:5001");
/// var item = await channel.UnaryAsync&lt;GetItemRequest, Item&gt;("/playground.Inventory/GetItem", new GetItemRequest { Id = 7 });
/// await channel.ServerStreamingAsync&lt;WatchRequest, PriceTick&gt;("/playground.Inventory/Watch", new WatchRequest(), tick => prices.Add(tick));
/// </code>
/// </example>
public sealed class GrpcWebChannel
{
    public GrpcWebChannel(string address = "") => Address = address.TrimEnd('/');

    /// <summary>The server, e.g. "https://api.example.com"; empty for the page's own origin.</summary>
    public string Address { get; }

    /// <summary>Metadata sent with every call, such as an authorization header.</summary>
    public Dictionary<string, string> DefaultMetadata { get; } = new();

    /// <summary>Every frame sent and received.</summary>
    public event Action<GrpcFrame>? Frame;

    public async Task<TResponse> UnaryAsync<TRequest, TResponse>(string method, TRequest request, IDictionary<string, string>? metadata = null, CancellationToken cancel = default)
        where TRequest : IProtoMessage
        where TResponse : IProtoMessage, new()
    {
        TResponse? response = default;
        var count = 0;
        await CallAsync(method, request, metadata, data =>
        {
            response = ProtoReader.Decode<TResponse>(data);
            count++;
        }, cancel);
        if (count != 1)
            throw new RpcException(StatusCode.Internal, $"A unary call returned {count} messages.", new Dictionary<string, string>());
        return response!;
    }

    /// <summary>Calls a server-streaming method; <paramref name="onMessage"/> runs as each message arrives.</summary>
    public Task ServerStreamingAsync<TRequest, TResponse>(string method, TRequest request, Action<TResponse> onMessage, IDictionary<string, string>? metadata = null, CancellationToken cancel = default)
        where TRequest : IProtoMessage
        where TResponse : IProtoMessage, new() =>
        CallAsync(method, request, metadata, data => onMessage(ProtoReader.Decode<TResponse>(data)), cancel);

    private Task CallAsync(string method, IProtoMessage request, IDictionary<string, string>? metadata, Action<byte[]> onData, CancellationToken cancel)
    {
        var payload = ProtoWriter.Encode(request);
        var framed = new byte[payload.Length + 5];
        framed[1] = (byte)(payload.Length >> 24);
        framed[2] = (byte)(payload.Length >> 16);
        framed[3] = (byte)(payload.Length >> 8);
        framed[4] = (byte)payload.Length;
        Array.Copy(payload, 0, framed, 5, payload.Length);
        Frame?.Invoke(new GrpcFrame("send", "data", payload.Length, Hex(framed)));

        var headers = new Dictionary<string, string>(DefaultMetadata);
        if (metadata is not null)
            foreach (var kv in metadata) headers[kv.Key] = kv.Value;

        var done = new TaskCompletionSource<bool>();
        var id = 0;
        id = Callbacks.Register(e =>
        {
            switch (e["kind"].AsString())
            {
                case "message":
                    var data = Convert.FromBase64String(e["data"].AsString());
                    Frame?.Invoke(new GrpcFrame("receive", "data", data.Length, Hex(data)));
                    try { onData(data); }
                    catch (Exception ex) { Finish(); done.TrySetException(ex); }
                    break;
                case "end":
                    Finish();
                    var trailers = new Dictionary<string, string>();
                    foreach (var k in e["trailers"].Keys) trailers[k] = e["trailers"][k].AsString();
                    if (trailers.Count > 0)
                        Frame?.Invoke(new GrpcFrame("receive", "trailers", 0, string.Join(", ", trailers.Select(kv => kv.Key + ": " + kv.Value))));
                    var status = trailers.TryGetValue("grpc-status", out var s) && int.TryParse(s, out var code) ? (StatusCode)code : StatusCode.Unknown;
                    if (status == StatusCode.OK) done.TrySetResult(true);
                    else
                    {
                        var detail = trailers.TryGetValue("grpc-message", out var m) ? Uri.UnescapeDataString(m) : e["error"].AsStringOrNull() ?? "";
                        done.TrySetException(new RpcException(status, detail, trailers));
                    }
                    break;
            }
        });
        void Finish() => Callbacks.Unregister(id);

        if (cancel.CanBeCanceled)
            cancel.Register(() =>
            {
                Js.Send("grpcweb.cancel", Js.Args("id", id));
                Finish();
                done.TrySetException(new RpcException(StatusCode.Cancelled, "Call cancelled.", new Dictionary<string, string>()));
            });

        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("id", id);
        w.Prop("url", Address + method);
        w.Prop("body", Convert.ToBase64String(framed));
        w.Name("headers").BeginObject();
        foreach (var kv in headers) w.Prop(kv.Key, kv.Value);
        w.EndObject();
        w.EndObject();
        Js.Send("grpcweb.call", JsonValue.Parse(w.ToString()));
        return done.Task;
    }

    private static string Hex(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, 48);
        var parts = new string[shown];
        for (var i = 0; i < shown; i++) parts[i] = bytes[i].ToString("x2");
        return string.Join(" ", parts) + (bytes.Length > shown ? " …" : "");
    }
}
