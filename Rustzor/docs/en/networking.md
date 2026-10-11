# Networking

Four ways to talk to a server, each a package. The [API Playground sample](samples.md#api-playground) runs all four against one ASP.NET Core server and shows every frame.

## HTTP: `Rustzor.Browser.Fetch`

`HttpClient` is not available in the browser runtime; `FetchClient` is the browser's `fetch` with a C# API.

```csharp
using Rustzor.Browser.Fetch;

var http = new FetchClient("https://api.example.com");
http.DefaultHeaders["Authorization"] = "Bearer " + token;

List<Book> books = await http.GetListAsync<Book>("/api/books?sort=rating");
Book one = await http.GetFromJsonAsync<Book>("/api/books/1");

var created = await http.PostJsonAsync("/api/books", new { title = "Pulang", author = "Leila S. Chudori" });
if (created.Status == 201) Toasts.Success("Added");

var r = await http.SendAsync(new FetchRequest("/api/books/2", "PUT") { TimeoutMs = 10_000 }.WithJson(book));
r.EnsureSuccess();                                   // throws FetchException on 4xx and 5xx
```

`FetchResponse` has `Status`, `StatusText`, `Ok`, `Headers`, `Text`, `Json()`, `As<T>()` and `AsList<T>()`. For a body that arrives in pieces (server-sent events, an LLM's tokens), `StreamAsync` hands you each chunk as it arrives.

Cross-origin calls need CORS on the server, as with any browser code.

## WebSocket: `Rustzor.Browser.WebSocket`

```csharp
using Rustzor.Browser.WebSocket;

var ws = new WebSocketClient("wss://example.com/ws") { AutoReconnect = true };
ws.OnMessage += text => log.Add(text);
ws.OnClose += (code, reason) => status = $"closed {code}";
await ws.ConnectAsync();
ws.Send("ping");
ws.SendJson(new { type = "subscribe", room = "lobby" });
```

`BroadcastChannel` in the same package passes messages between tabs of the same site without a server.

## SignalR: `Rustzor.Net.SignalR`

A client for ASP.NET Core SignalR hubs: the JSON hub protocol over WebSockets, with negotiation, typed handlers, results, streams and automatic reconnect. Its API follows Microsoft's client.

```csharp
using Rustzor.Net.SignalR;

var hub = new HubConnectionBuilder()
    .WithUrl("/hubs/chat")                       // relative to the page, or absolute
    .WithAutomaticReconnect()                    // retries after 0, 2, 10 and 30 seconds
    .Build();

hub.On<string, string, DateTime>("Message", (who, text, at) => { messages.Add($"{who}: {text}"); StateHasChanged(); });
hub.Reconnecting += () => status = "reconnecting";

await hub.StartAsync();
await hub.SendAsync("Join", "lobby", "Ayu");                     // no result
int sum = await hub.InvokeAsync<int>("Add", 19, 23);             // a result
await hub.StreamAsync<int>("Countdown", n => ticks.Add(n), 10);  // IAsyncEnumerable<int> on the server
```

A `HubException` carries the server's error for a failed invocation. The `Frame` event reports every message sent and received, which is how the playground draws its wire log. Server side, nothing special is needed: `builder.Services.AddSignalR()` and `app.MapHub<ChatHub>("/hubs/chat")`. The client uses WebSockets only (no server-sent events or long polling).

## gRPC-Web: `Rustzor.Net.GrpcWeb`

gRPC from a browser goes through gRPC-Web. `GrpcWebChannel` makes unary and server-streaming calls over `fetch` with the binary `application/grpc-web+proto` format, reads frames as they stream in, maps trailers to `RpcException`, and supports cancellation.

There is no code generator in the browser: messages are classes implementing `IProtoMessage`, a line per field, numbered as in the `.proto` file.

```proto
service Inventory {
  rpc GetItem (GetItemRequest) returns (Item);
  rpc WatchPrices (WatchRequest) returns (stream PriceTick);
}
message GetItemRequest { int32 id = 1; }
message Item { int32 id = 1; string name = 2; double price = 4; repeated string tags = 6; }
```

```csharp
using Rustzor.Net.GrpcWeb;

public sealed class GetItemRequest : IProtoMessage
{
    public int Id { get; set; }
    public void WriteTo(ProtoWriter w) => w.Int32(1, Id);
    public void ReadFrom(ProtoReader r) { while (r.Next()) if (r.Field == 1) Id = r.Int32(); else r.Skip(); }
}

public sealed class Item : IProtoMessage
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public List<string> Tags { get; } = new();

    public void WriteTo(ProtoWriter w) { w.Int32(1, Id); w.String(2, Name); w.Double(4, Price); w.Strings(6, Tags); }

    public void ReadFrom(ProtoReader r)
    {
        while (r.Next())
            switch (r.Field)
            {
                case 1: Id = r.Int32(); break;
                case 2: Name = r.String(); break;
                case 4: Price = r.Double(); break;
                case 6: Tags.Add(r.String()); break;
                default: r.Skip(); break;     // fields this client does not know
            }
    }
}
```

```csharp
var channel = new GrpcWebChannel("https://localhost:5001");
try
{
    var item = await channel.UnaryAsync<GetItemRequest, Item>("/playground.Inventory/GetItem", new GetItemRequest { Id = 1 });
}
catch (RpcException e) when (e.Status == StatusCode.NotFound) { /* e.Detail */ }

using var cts = new CancellationTokenSource();
await channel.ServerStreamingAsync<WatchRequest, PriceTick>(
    "/playground.Inventory/WatchPrices", request, tick => prices.Add(tick), cancel: cts.Token);
```

`ProtoWriter` and `ProtoReader` cover `int32`, `int64`, `uint32`, `uint64`, `sint32`, `sint64`, `bool`, enums, `double`, `float`, `string`, `bytes`, nested messages and repeated fields (packed or not). Client streaming and bidirectional streaming are not possible over gRPC-Web in a browser.

On an ASP.NET Core server, enable gRPC-Web:

```csharp
builder.Services.AddGrpc();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
app.MapGrpcService<InventoryService>();
```

For a cross-origin client, also allow CORS and expose the `Grpc-Status` and `Grpc-Message` headers.
