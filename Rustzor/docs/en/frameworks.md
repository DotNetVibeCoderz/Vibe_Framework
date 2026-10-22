# Frameworks

Four higher-level packages sit on top of the browser APIs.

## Rustzor.Data

Databases in the browser, with nothing to install on a server.

| Type | What |
| --- | --- |
| `SqliteDb` | SQLite (sql.js) in memory, saved to IndexedDB under a key so it survives reloads. `OpenAsync`, `QueryAsync`, `RunAsync`, `TransactionAsync`, `SaveAsync`, `ExportAsync` |
| `IndexedDb`, `ObjectStore<T>` | IndexedDB with typed stores: `PutAsync`, `PutManyAsync`, `GetAsync`, `AllAsync`, `WhereAsync` (an index), `RangeAsync`, `DeleteAsync`, `ClearAsync`, `CountAsync` |
| `DuckDb` | DuckDB WASM for analytics over CSV and Parquet: `QueryAsync`, `RegisterUrlAsync`, `RegisterTextAsync`, `InsertAsync` |
| `QueryResult` | `Rows`, `Columns`, `Scalar`, `As<T>()` (map rows to objects by column name), `ToCsv()` |
| `Csv` | `Csv.Parse` |

```csharp
using Rustzor.Data;

var db = await SqliteDb.OpenAsync(persistKey: "my-shop");
await db.RunAsync("create table if not exists items (id integer primary key, name text, price real)");
await db.RunAsync("insert into items (name, price) values (?, ?)", "Kopi Gayo", 85000);
await db.SaveAsync();                                   // to IndexedDB

var cheap = (await db.QueryAsync("select name, price from items where price < ? order by price", 90000)).As<Item>();
```

```csharp
var idb = await IndexedDb.OpenAsync("kanban", 1, new StoreSchema("cards", "id", Indexes: new IndexSchema("byColumn", "column")));
var cards = idb.Store<Card>("cards");
await cards.PutAsync(new Card { Id = "c1", Column = "todo", Title = "Write docs" });
var todo = await cards.WhereAsync("byColumn", "todo");
```

The [CRM sample](samples.md#nusantara-crm) uses SQLite; the [POS](samples.md#kedai-senja-pos) and [chat](samples.md#serambi-chat) samples use IndexedDB.

## Rustzor.Game

A 2D game framework: a fixed-step loop, input, rigid-body physics, an entity-component system, a camera, particles, tweens, timers, sound and simple networking.

```razor
<GameView Height="360" OnStart="Start" OnUpdate="Update" OnDraw="Draw" />

@code {
    private Body? player;

    private void Start(GameContext g)
    {
        g.Physics.Gravity = new Vec2(0, 600);
        g.Physics.Add(Body.Box(new Vec2(g.Width / 2, g.Height - 10), g.Width, 20, 0));   // mass 0: static floor
        player = g.Physics.Add(Body.Box(new Vec2(g.Width / 2, g.Height - 60), 40, 40));
    }

    private void Update(GameContext g, double dt)
    {
        player!.Velocity = new Vec2(g.Input.Axis().X * 320, player.Velocity.Y);
        if (g.Input.WasPressed("Space") && player.OnGround) player.Velocity = new Vec2(player.Velocity.X, -420);
    }

    private void Draw(GameContext g)
    {
        g.Canvas.Fill("#111827").FillRect(0, 0, g.Width, g.Height);
        g.Canvas.Fill("#f97316").FillRect(player!.Position.X - 20, player.Position.Y - 20, 40, 40);
    }
}
```

`GameContext` gives `Canvas`, `Loop` (`Fps`, `Paused`, `TimeScale`), `Input` (`IsDown`, `WasPressed`, `WasReleased`, `Axis`, `Pointer`), `Physics` (`Body.Circle`, `Body.Box`, collisions), `World` (entities), `Camera`, `Particles`, `Tweens`, `Timers`, `Random`, `Width`, `Height`. Keyboard, pointer, touch and gamepad all feed `Input`.

![A physics scene in Rustzor.Game](../images/fw-game.png)

### Multiplayer

`NetSession` joins players into a room: `NetSession.Local(room)` between tabs of one browser over a `BroadcastChannel`, or `await NetSession.Connect("wss://…", room)` through a WebSocket relay. Every message is a small envelope `{from, type, data}`; peers announce themselves and send heartbeats, so `Peers`, `OnPeerJoined` and `OnPeerLeft` track who is there.

```csharp
var net = NetSession.Local("rt05");
net.On("move", m => Move(m.From, m.Data["x"].AsDouble(), m.Data["y"].AsDouble()));
net.Send("move", new { x = 3.5, y = 1.2 });
net.SendJson("snap", snapshotJson);   // already JSON: written once, never walked again
```

For state sent many times a second, write it once with `JsonWriter` and send it with `SendJson`; between tabs it then crosses as an object (`BroadcastChannel.PostRaw`), with nothing escaped or parsed twice. The [Galasin sample](samples.md#galasin-gobak-sodor) runs a 3D match for eight across tabs this way: one tab referees at 20 Hz and hands over when it closes.

## Rustzor.Graphics

Scene graphs for editors and viewers.

- `Scene2D`: nodes (`Node.Rect`, `Ellipse`, `Line`, `Path`, `Label`, `Picture`, `Group`), hit testing, selection, a viewport with pan and zoom, rendering to a `Canvas2D`, export to SVG and to JSON and back.
- `Scene3D`: `Model`s built from `Mesh.Cube`, `Box`, `Sphere`, `Cylinder`, `Plane` or a parsed `.obj`, an `OrbitCamera`, picking, rendered by `Renderer3D` on WebGL.
- `Color`, `Transform2D`, and `PixelOps` (grayscale, blur and other per-pixel filters).

```csharp
var scene = new Scene2D { GridSize = 20 };
scene.Add(Node.Rect(40, 40, 120, 80, "#fdba74", 10));
scene.Add(Node.Label(40, 160, "Rustzor.Graphics", 22, "#1c1917"));
scene.Render(canvas, selection);
string svg = scene.ToSvg();

var renderer = new Renderer3D(gl);
var world = new Scene3D();
world.Add(new Model(Mesh.Sphere(0.9), "#14b8a6") { Position = new Vec3(1.2, 0.9, 0) });
renderer.Render(world);
```

![2D and 3D scenes in Rustzor.Graphics](../images/fw-graphics.png)

## Rustzor.AI

Language models from the browser.

| Type | What |
| --- | --- |
| `IChatModel` | `CompleteAsync(messages, options)` and `StreamAsync` |
| `OpenAiChat` | OpenAI and compatible endpoints; `OpenAiChat.Azure(...)`, `OpenAiChat.Ollama(...)`; `EmbedAsync` |
| `ClaudeChat` | Anthropic's Messages API |
| `IToolChatModel`, `ChatTool`, `ToolCall`, `ToolAgent` | Tool calling: tools as JSON Schema, calls streamed and assembled, a loop that runs them; cancellable |
| `BuiltInChat` | The browser's built-in model, where available |
| `Conversation` | A chat with history and a system prompt; `SendAsync(text, onPiece)` streams |
| `RagPipeline` | Retrieval-augmented answers: `IndexAsync(id, text)`, `AskAsync(question)` with sources |
| `VectorStore`, `TextChunker`, `IEmbedder`, `LocalEmbedder`, `RemoteEmbedder` | The pieces of a RAG pipeline |
| `LocalAi` | Small in-browser models: embeddings, classification, generation |

```csharp
using Rustzor.AI;

IChatModel model = new ClaudeChat(apiKey, "claude-sonnet-5-5");
var chat = new Conversation(model, "You answer in one short sentence.");
await chat.SendAsync("Name the capital of Indonesia.", piece => { reply += piece; StateHasChanged(); });
```

Tool calling: an `IToolChatModel` (`OpenAiChat`, `ClaudeChat`) streams one turn with `StreamTurnAsync(messages, tools, onText, options, cancellationToken)` and returns the text, the tool calls (arguments assembled from their streamed pieces), the usage and the stop reason. `ToolAgent` runs the loop: it calls the tools you register, sends their results back, and stops when the model answers in text. Cancelling the token aborts the request in the browser.

```csharp
var agent = new ToolAgent(OpenAiChat.Azure(endpoint, key, "gpt-5-mini"))
{
    OnText = piece => { reply += piece; StateHasChanged(); },
    OnToolCall = call => Console.WriteLine($"{call.Name} {call.Arguments}"),
};
agent.Register(new ChatTool("calculator", "Evaluate an arithmetic expression.",
    """{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}"""),
    call => Calc.Evaluate(call.Args["expression"].AsString()).ToString());
var turn = await agent.RunAsync(messages, new ChatOptions { MaxTokens = 4096 }, stop.Token);
```

The [Loket sample](samples.md#loket-front-desk) uses all of it, with an offline demo model behind the same interface.

An API key used in a browser is visible to anyone who opens the page. For a public app, call the model from your server (the [API Playground](samples.md#api-playground) shows a server next to a Rustzor client) or use a proxy that holds the key; keep keys in the browser for personal tools and demos.

![A streamed answer from Rustzor.AI](../images/fw-ai-claude.png)
