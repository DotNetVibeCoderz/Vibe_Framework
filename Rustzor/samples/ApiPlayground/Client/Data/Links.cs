using Rustzor.Browser.WebSocket;
using Rustzor.Net.GrpcWeb;
using Rustzor.Net.SignalR;

namespace ApiPlayground.Data;

/// <summary>
/// The open connections, kept while the person moves between screens: a
/// WebSocket and a SignalR hub stay connected when you look at REST.
/// </summary>
public static class Links
{
    public static WebSocketClient? Socket { get; set; }
    public static HubConnection? Hub { get; set; }
    public static string HubName { get; set; } = "";
    public static string HubRoom { get; set; } = "lobby";

    /// <summary>A channel to the current server, rebuilt when the address changes.</summary>
    public static GrpcWebChannel Grpc
    {
        get
        {
            if (_grpc is null || _grpc.Address != Server.Address.TrimEnd('/'))
            {
                _grpc = new GrpcWebChannel(Server.Address);
                _grpc.Frame += f => Wire.Log(Protocol.Grpc, f.Direction == "send" ? "out" : "in",
                    f.Kind == "trailers" ? "trailers" : $"data frame, {f.Length} byte message", f.Hex, f.Kind == "trailers" ? 0 : f.Length + 5);
            }
            return _grpc;
        }
    }

    private static GrpcWebChannel? _grpc;

    public static bool SocketOpen => Socket?.State == "open";
    public static bool HubOpen => Hub?.State == HubConnectionState.Connected;

    /// <summary>Raised when a connection opens or closes, so the rail can show it.</summary>
    public static event Action? Changed;

    public static void Notify() => Changed?.Invoke();
}
