# Networking

Ada empat cara berkomunikasi dengan server, masing-masing berupa satu package. [Sample API Playground](samples.md#api-playground) menjalankan keempatnya terhadap satu server ASP.NET Core dan menampilkan setiap frame.

## HTTP: `Rustzor.Browser.Fetch`

`HttpClient` tidak tersedia di runtime browser; `FetchClient` adalah `fetch` milik browser dengan API C#.

```csharp
using Rustzor.Browser.Fetch;

var http = new FetchClient("https://api.example.com");
http.DefaultHeaders["Authorization"] = "Bearer " + token;

List<Book> books = await http.GetListAsync<Book>("/api/books?sort=rating");
Book one = await http.GetFromJsonAsync<Book>("/api/books/1");

var created = await http.PostJsonAsync("/api/books", new { title = "Pulang", author = "Leila S. Chudori" });
if (created.Status == 201) Toasts.Success("Added");

var r = await http.SendAsync(new FetchRequest("/api/books/2", "PUT") { TimeoutMs = 10_000 }.WithJson(book));
r.EnsureSuccess();                                   // melempar FetchException untuk 4xx dan 5xx
```

`FetchResponse` memiliki `Status`, `StatusText`, `Ok`, `Headers`, `Text`, `Json()`, `As<T>()`, dan `AsList<T>()`. Untuk body yang datang sepotong-sepotong (server-sent events, token dari LLM), `StreamAsync` memberikan setiap chunk begitu chunk itu tiba.

Panggilan cross-origin membutuhkan CORS di server, sama seperti kode browser lainnya.

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

`BroadcastChannel` di package yang sama meneruskan pesan antar-tab dari situs yang sama tanpa server.

## SignalR: `Rustzor.Net.SignalR`

Client untuk hub SignalR ASP.NET Core: JSON hub protocol di atas WebSocket, dengan negotiation, handler bertipe, hasil (result), stream, dan reconnect otomatis. API-nya mengikuti client dari Microsoft.

```csharp
using Rustzor.Net.SignalR;

var hub = new HubConnectionBuilder()
    .WithUrl("/hubs/chat")                       // relatif terhadap halaman, atau absolut
    .WithAutomaticReconnect()                    // mencoba ulang setelah 0, 2, 10, dan 30 detik
    .Build();

hub.On<string, string, DateTime>("Message", (who, text, at) => { messages.Add($"{who}: {text}"); StateHasChanged(); });
hub.Reconnecting += () => status = "reconnecting";

await hub.StartAsync();
await hub.SendAsync("Join", "lobby", "Ayu");                     // tanpa result
int sum = await hub.InvokeAsync<int>("Add", 19, 23);             // dengan result
await hub.StreamAsync<int>("Countdown", n => ticks.Add(n), 10);  // IAsyncEnumerable<int> di server
```

`HubException` membawa error dari server untuk invocation yang gagal. Event `Frame` melaporkan setiap pesan yang dikirim dan diterima; dari situlah playground menggambar wire log-nya. Di sisi server tidak ada yang istimewa: cukup `builder.Services.AddSignalR()` dan `app.MapHub<ChatHub>("/hubs/chat")`. Client ini hanya memakai WebSocket (tanpa server-sent events maupun long polling).

## gRPC-Web: `Rustzor.Net.GrpcWeb`

gRPC dari browser harus lewat gRPC-Web. `GrpcWebChannel` melakukan panggilan unary dan server-streaming melalui `fetch` dengan format biner `application/grpc-web+proto`, membaca frame saat di-stream, memetakan trailer ke `RpcException`, dan mendukung pembatalan (cancellation).

Tidak ada code generator di browser: message berupa class yang mengimplementasikan `IProtoMessage`, satu baris per field, dengan nomor yang sama seperti di file `.proto`.

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
                default: r.Skip(); break;     // field yang tidak dikenal client ini
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

`ProtoWriter` dan `ProtoReader` mendukung `int32`, `int64`, `uint32`, `uint64`, `sint32`, `sint64`, `bool`, enum, `double`, `float`, `string`, `bytes`, nested message, dan repeated field (packed maupun tidak). Client streaming dan bidirectional streaming tidak dimungkinkan lewat gRPC-Web di browser.

Di server ASP.NET Core, aktifkan gRPC-Web:

```csharp
builder.Services.AddGrpc();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
app.MapGrpcService<InventoryService>();
```

Untuk client cross-origin, izinkan juga CORS dan expose header `Grpc-Status` dan `Grpc-Message`.
