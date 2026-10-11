using System.Text;
using System.Text.RegularExpressions;

namespace RustzorAppGen.Services;

/// <summary>What Jack knows about Rustzor: the system prompt and topic guides.</summary>
public static partial class RustzorKnowledge
{
    public const string AssistantName = "Jack - The Code Bender";

    public static string DefaultSystemPrompt => """
        You are Jack - The Code Bender, the coding assistant inside Rustzor App Generator, built by Gravicode Studios (led by Kang Fadhil).
        You build complete, working web apps with Rustzor: C# Razor components compiled to .NET IL and run in the browser by RustNetRuntime, a Rust runtime compiled to WebAssembly.

        How you work:
        1. If no project is open, create one with rustzor.create_project (pick the closest template, or "blank").
        2. Call project.get_project_overview before changing an existing project.
        3. Read rustzor.get_guide for the topics you need (always "pitfalls" once per conversation), and rustzor.get_library_api for any library whose API you are not sure of. Never invent APIs.
        4. Write complete files with project.write_file (whole file content, never placeholders or "..."), or small changes with project.edit_file.
        5. Run build.build_project after changes. Fix every error and build again until it succeeds. Only then tell the user it is done.
        6. Finish with a short summary: what you built, which files changed, and how to run it (Run button or F5).

        Quality bar: real content and realistic sample data, a polished UI using Rustzor.UI components and its rz-* classes, responsive layout, empty and error states, and no TODOs.
        Every app shows "Built by Gravicode Studios, led by Kang Fadhil" in its footer or about section.
        Answer in the user's language (English or Bahasa Indonesia). Be concise in chat; put the work in the files.
        Use the web, math and time tools when facts, calculations or dates matter rather than guessing.
        """;

    public static string SystemPrompt(AppSettings settings)
    {
        var prompt = string.IsNullOrWhiteSpace(settings.SystemPrompt) ? DefaultSystemPrompt : settings.SystemPrompt;
        return prompt + $"\n\nToday is {DateTime.Now:dddd, d MMMM yyyy}. Generated projects reference Rustzor via {settings.RustzorReference}.";
    }

    public static readonly string[] Topics =
        { "project", "components", "ui", "interop", "browser", "game", "graphics", "data", "ai", "charts", "bootstrap", "tailwind", "mui", "pitfalls" };

    public static string Guide(string topic) => topic.Trim().ToLowerInvariant() switch
    {
        "project" => Project,
        "components" => Components,
        "ui" => Ui,
        "interop" => Interop,
        "browser" => Browser,
        "game" => Game,
        "graphics" => Graphics,
        "data" => Data,
        "ai" => Ai,
        "charts" => Charts,
        "bootstrap" => Bootstrap,
        "tailwind" => Tailwind,
        "mui" => Mui,
        "pitfalls" => Pitfalls,
        _ => $"Unknown topic. Topics: {string.Join(", ", Topics)}.",
    };

    private const string Project = """
        # Rustzor project layout
        A Rustzor app is a Razor project (Sdk Microsoft.NET.Sdk.Razor, OutputType Exe, net10.0).
        - Program.cs:
            using Rustzor.Components;
            var builder = RustzorHostBuilder.CreateDefault(args);
            builder.Services.AddSingleton<MyStore>();      // DI: AddSingleton/AddScoped/AddTransient
            builder.RootComponents.Add<App>("#app");
            await builder.Build().RunAsync();
        - App.razor: <Router AppAssembly="typeof(App).Assembly"><Found Context="routeData"><RouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" /></Found><NotFound>…</NotFound></Router>
        - MainLayout.razor: @inherits LayoutComponentBase, renders @Body (usually inside <AppShell>).
        - Pages/*.razor with @page "/route". _Imports.razor holds @using lines for every library used.
        - wwwroot/index.html loads the runtime: <div id="app"></div><script type="module">import { start } from "./_rustzor/rustzor.js"; start();</script>
        - Add a library with the rustzor.add_package tool; never write its path by hand. (It writes a ProjectReference in source mode or a PackageReference in NuGet mode.) Library project names: Rustzor.UI, Rustzor.Browser.<Api> (Fetch, Storage, Canvas, Audio, Gamepad, WebSocket, WebGL, WebGPU, Notifications, Location, Sensors, Media, Video, Workers, WebRTC, WebXR, FileSystem, Push, BackgroundSync, Bluetooth, Serial, USB, HID, NFC, OffscreenCanvas, DOM, AI), Rustzor.Game, Rustzor.Graphics, Rustzor.Data, Rustzor.AI, Rustzor.Charts.ChartJs, Rustzor.Charts.ECharts, Rustzor.Charts.D3, Rustzor.UI.Bootstrap, Rustzor.UI.Tailwind, Rustzor.UI.Mui.
          Source mode path: $(RustzorRoot)\src\<Rustzor.UI|Browser\Rustzor.Browser.X|Frameworks\Rustzor.Game|Integrations\Rustzor.Charts.X>\<Name>.csproj
        - dotnet build produces bin/<Configuration>/net10.0/wwwroot, a static site (the Run button serves it).
        """;

    private const string Components = """
        # Components (Blazor-compatible)
        Razor syntax as in Blazor: @page, @code, @inject, [Parameter], EventCallback<T>, RenderFragment, @bind, @bind-Value, @onclick, @oninput, @onkeydown, @ref (ElementReference, await el.FocusAsync()), CascadingValue/[CascadingParameter], @key, NavLink, NavigationManager (NavigateTo), lifecycle OnInitialized[Async], OnParametersSet, OnAfterRender[Async](bool firstRender), StateHasChanged, InvokeAsync, IDisposable.
        Forms: EditForm, InputText, InputNumber, InputCheckbox, InputSelect, InputTextArea, DataAnnotationsValidator, ValidationMessage, ValidationSummary.
        Event args: MouseEventArgs (ClientX, OffsetX...), KeyboardEventArgs (Key, Code), ChangeEventArgs (Value), WheelEventArgs, PointerEventArgs.
        Timers: Rustzor.Interop.Scheduler.SetInterval(action, ms) returns IDisposable; Scheduler.SetTimeout(action, ms); await Task.Delay(ms) works.
        Call StateHasChanged() after changing state from a timer or callback (wrap with InvokeAsync).
        An event handler that starts async work must be `async Task` and await it. `_ = DoAsync()` in a void handler finishes without re-rendering the page.
        A [Parameter] the route does not supply keeps its old value when the same page is reused (e.g. "/edit/{Id}" then "/"): reset it in SetParametersAsync.
        Persist small things: localStorage writes serialize the whole value, so save deltas or seen items, not a 1,000-item list per keystroke.
        System.Text.RegularExpressions works (IsMatch, Match, Matches, Replace with $1/${name}/MatchEvaluator, Split); RegexOptions.RightToLeft is not supported.
        """;

    private const string Ui = """
        # Rustzor.UI (the default design system)
        Add @using Rustzor.UI. Components:
        - <AppShell Brand="Name" BrandIcon="rust" Title="Page" Items="nav">…</AppShell> with List<NavItem> nav = new() { NavItem.Heading("Main"), NavItem.Link("Dashboard", "", "home"), NavItem.Link("Orders", "orders", "cart") }. Optional TopBar and SidebarFooter fragments.
        - <PageHeader Title Eyebrow Text><Actions>buttons</Actions></PageHeader>
        - <Card Title Subtitle Class><Actions/>content</Card>
        - <StatCard Label Value="Rp 12.000" Delta="12.4" (percent, +/-) Hint Trend="List<double>" />
        - <DataGrid TItem="Row" Items="rows" Columns="cols" PageSize="10" Searchable="true" OnRowClick="..."/> with List<GridColumn<Row>> cols = new() { new("Name", r => r.Name), new("Total", r => r.Total) { Numeric = true, Format = "N2" }, new("Status", r => r.Status) { Template = r => @<Badge Kind="good">@r.Status</Badge> } }
        - Charts (SVG, themed): <LineChart Labels Series="List<ChartSeries>" Height ValueFormat/>, <BarChart Labels Series Height/>, <DonutChart Labels Values Center CenterLabel Size/>, <Sparkline Values Color Width Height/>; ChartSeries(name, values) { Color = "#..." }
        - <Badge Kind="good|bad|warn|accent|patina|plain">text</Badge>, <Avatar Name Size/>, <Icon Name="home" Size/>, <Progress Percent Color/>, <Tabs Titles @bind-Active/>, <Modal @bind-Open Title><Footer/></Modal>, <EmptyState Icon Title Text/>, <Field Label Help>input</Field>, <ThemeToggle/>, <Toaster/> (inside AppShell already).
        - Toasts.Success("Saved"), Toasts.Error("Failed"), Toasts.Warning(text), Toasts.Info(text). Theme.Toggle(), Theme.Set("dark").
        - CSS classes: rz-btn (+ primary, ghost, danger, small, icon), rz-input, rz-label, rz-grid rz-grid-2|3|4, rz-row, rz-stack, rz-muted, rz-num, rz-divider, rz-table inside rz-table-wrap, rz-switch.
        - Icons: home chart trend users user cart box settings search plus minus trash edit check x bell chat video mic map pin layers cpu sparkles sun moon download upload filter menu arrow-up arrow-down arrow-right calendar clock mail phone file image brush shapes cube gamepad factory wallet receipt send zap refresh eye star tag building thermometer gauge alert info logout rust
        - Design tokens (CSS variables): --rz-bg, --rz-surface, --rz-surface-2, --rz-text, --rz-muted, --rz-line, --rz-accent, --rz-accent-fg, --rz-chart-1..5, --rz-font-display, --rz-font-mono. Light and dark themes are built in.
        """;

    private const string Interop = """
        # JavaScript interop (Rustzor.Core, namespace Rustzor.Interop)
        - Js.Call(op, Js.Args("k", v, ...)) -> JsonValue (sync); Js.Send(op, args) (no result); await Js.CallAsync(op, args).
        - Generic ops: "js.eval" {code} evaluates JavaScript: Js.Call("js.eval", Js.Args("code", "location.hostname")).AsString().
        - "js.invoke" {path, args}: calls a global function by dotted path, e.g. Js.Call("js.invoke", Js.Args("path", "navigator.clipboard.writeText", "args", new[] { text })).
        - JsonValue: AsString, AsDouble, AsInt, AsBool, IsNull, ["key"], [index], Items, Keys. Json.Serialize(obj) (camelCase), Json.Deserialize<T>(json), Json.DeserializeList<T>(json).
        - Callbacks.Register(Action<JsonValue>) returns an id JavaScript can dispatch to.
        - IJSRuntime is also available via @inject for Blazor-style InvokeAsync/InvokeVoidAsync.
        - Prefer the typed Rustzor.Browser.* libraries over raw interop.
        """;

    private const string Browser = """
        # Browser API libraries (Rustzor.Browser.*). Use rustzor.get_library_api for full signatures.
        - Fetch: var http = new FetchClient("https://api.example.com"); await http.GetFromJsonAsync<T>(url), GetListAsync<T>, GetStringAsync, PostJsonAsync(url, body), SendAsync(new FetchRequest(url, "PUT").WithJson(x).WithBearer(token)), StreamAsync(request, chunk => ...). Responses: Status, Ok, Text, Json(), As<T>().
        - Storage: WebStorage.Local.Set/Get/SetJson/GetObject<T>/GetList<T>/Remove/Keys(); WebStorage.Session; Cookies.Get/Set; StorageManager.EstimateAsync().
        - Canvas: var g = Canvas2D.FromElement(canvasRef) (in OnAfterRender firstRender); fluent g.Fill("#hex").FillRect(x,y,w,h).Stroke(c).LineWidth(w).BeginPath().MoveTo().LineTo().Arc().FillPath().StrokePath().FillCircle(x,y,r).Font("16px sans-serif").FillText(t,x,y).Save().Translate().Rotate().Scale().Restore(); g.Clear(); g.Width/Height; ALWAYS g.Flush() at the end of a frame. CanvasImage.LoadAsync(url) + g.DrawImage(img,x,y,w,h). CanvasInput.OnPointer(canvasRef, sample => ...). GetPixels/PutPixels, ToDataUrl().
        - Audio: AudioEngine.Tone(freqHz, seconds, wave, volume), Noise, Notes(midi[]), LoadAsync(url) -> SoundClip.Play(), new Music(url).PlayAsync(), Speak(text, lang), Spectrum().
        - Gamepad: Gamepads.First() -> GamepadState (LeftX, LeftY, A, B, Start, Up...).
        - WebSocket: var ws = new WebSocketClient(url) { AutoReconnect = true }; ws.OnMessage += text => ...; await ws.ConnectAsync(); ws.Send/SendJson. BroadcastChannel(name) for cross-tab messages.
        - Notifications, Location (geolocation), Sensors, Media (camera/microphone), Video, Workers (+ service worker/PWA), WebRTC, WebGPU (Gpu.ComputeAsync, ShaderCanvas), WebGL (GlContext, Mat4), WebXR, FileSystem (file pickers), Push, BackgroundSync, Bluetooth, Serial, USB, HID, NFC, OffscreenCanvas, DOM, AI (Chrome built-in model: BuiltInAi).
        """;

    private const string Game = """
        # Rustzor.Game (needs Rustzor.Game; namespace Rustzor.Game)
        The easiest start is the GameView component:
            <GameView Height="480" OnStart="Start" OnUpdate="Update" OnDraw="Draw" />
            void Start(GameContext g) { ... g.Physics.Add(Body.Circle(new Vec2(x, y), r)); }
            void Update(GameContext g, double dt) { var axis = g.Input.Axis(); if (g.Input.WasPressed("Space")) ...; }
            void Draw(GameContext g) { var c = g.Canvas; c.Fill("#0b0f14").FillRect(0, 0, g.Width, g.Height); ... }  // GameView flushes for you
        GameContext: Canvas (Canvas2D), Input, Loop (Fps, Time, Paused, TimeScale), World (ECS), Physics, Camera (Camera2D), Particles, Tweens, Timers, Random (Rng), Width, Height, State (Dictionary).
        - Input: IsDown/WasPressed/WasReleased("KeyW"|"ArrowLeft"|"Space"|"Enter"), Axis() (WASD/arrows/gamepad, length <= 1), Action (space/enter/click), Pointer (Vec2), PointerDown, Clicks, Wheel, Gamepad.
        - Vec2 record struct with + - * /, Length, Normalized(), Rotated(), FromAngle, Dot, Distance, Lerp. Rect2 (Contains, Intersects). MathX.Clamp/Lerp/Damp/Wrap. Rng(seed).Range/Chance/Pick/InCircle.
        - PhysicsWorld: Gravity, Add(Body.Circle(pos, r, mass) | Body.Box(center, w, h, mass)); mass 0 = static. Body: Position, Velocity, Restitution, Friction, IsTrigger, Layer/Mask, Tag, OnGround, ApplyImpulse. Events OnCollision/OnTrigger (Contact A B Normal Depth). Raycast, QueryPoint.
        - World (ECS): var e = World.Spawn().With(new Position(...)).With(new Velocity(...)).Entity; World.Each<A,B>((id, a, b) => World.Set(id, ...)); World.Destroy(entity); AddSystem((world, dt) => ...). Components are plain records/structs.
        - Camera2D: Follow(target, dt), Zoom, Shake(px), Begin(canvas)/End(canvas) around world drawing, ScreenToWorld.
        - Particles.Burst(pos, count, color, speed, life, size), Particles.Draw(canvas). Tweens.Add(from, to, seconds, v => ..., Ease.OutBounce). Timers.After/Every.
        - Sfx.Jump/Coin/Hit/Explosion/Laser/PowerUp/GameOver/Click (synthesised, no files). Audio needs a user gesture first.
        - Multiplayer: var net = NetSession.Local("room") (tabs of one browser) or await NetSession.Connect("wss://relay", "room"); net.On("move", m => ...); net.Send("move", new { x, y }); Peers, OnPeerJoined/Left.
        - Keep per-frame work small: the interpreter runs ~10-20M IL ops/s; avoid allocating in hot loops, prefer fields of doubles over many Vec2 temporaries when you have hundreds of objects.
        """;

    private const string Graphics = """
        # Rustzor.Graphics (namespace Rustzor.Graphics)
        - 2D scene: var scene = new Scene2D { GridSize = 20, Background = "#fff" }; scene.Add(Node.Rect(x,y,w,h,"#fdba74",radius)), Node.Ellipse, Node.Line(x1,y1,x2,y2,stroke,width), Node.Path(points, closed, fill, stroke), Node.Label(x,y,text,size,fill), Node.Picture(x,y,w,h,url), Node.Group(nodes). Node props: X Y Width Height Rotation Fill Stroke StrokeWidth Opacity CornerRadius Text FontSize Visible Locked Data.
          scene.Render(canvas2d, selection); canvas.Flush(); scene.HitTest(screenX, screenY); scene.NodesInRect; scene.View (Viewport: Pan, ZoomAt(sx, sy, factor), Fit, ScreenToWorld); BringToFront/SendToBack; ToSvg(); ToJson()/Scene2D.FromJson(json); await LoadImagesAsync().
        - Color.Parse("#hex"), Color.FromHsl(h,s,l), ToCss(), Lerp, Luminance. Transform2D.
        - PixelOps on RGBA byte[] from canvas.GetPixels: Grayscale, Invert, Sepia, Brightness, Contrast, Saturation, Threshold, Posterize, BoxBlur, Sharpen, EdgeDetect, Emboss, Histogram, FloodFill. These are slow per pixel in the interpreter; for live previews prefer canvas.Filter("blur(4px) grayscale(1)") (GPU), and publish with AOT for heavy pixel work.
        - 3D (WebGL 2): var gl = GlContext.FromElement(canvasRef); var r = new Renderer3D(gl); var world = new Scene3D(); world.Add(new Model(Mesh.Cube(1), "#f97316") { Position = new Vec3(0, 0.5, 0) }); Mesh.Box/Sphere/Cylinder/Plane/ParseObj(text); world.Camera (OrbitCamera: Orbit(dx,dy), Zoom(f), Pan, Frame(min,max)); r.Render(world); world.Pick(x, y, cssWidth, cssHeight) -> Model; Model.Selected highlights.
        """;

    private const string Data = """
        # Rustzor.Data (namespace Rustzor.Data)
        - IndexedDB: using var db = await IndexedDb.OpenAsync("app", 1, new StoreSchema("orders", "id", Indexes: new IndexSchema("byCustomer", "customerId"))); var orders = db.Store<Order>("orders"); await orders.PutAsync(o); PutManyAsync; GetAsync(key); AllAsync(); WhereAsync("byCustomer", 42); RangeAsync(lo, hi); DeleteAsync; CountAsync. T needs a parameterless constructor; key paths use camelCase JSON names. Keep the db open for the app's lifetime (register a service) rather than reopening per call.
        - SQLite: var sql = await SqliteDb.OpenAsync("persistKey"); await sql.RunAsync("create table if not exists ...", args...); var r = await sql.QueryAsync("select ... where x = ?", value); r.Rows, r.Columns, r[row, "col"], r.Scalar, r.As<T>(); await sql.SaveAsync() persists to IndexedDB; TransactionAsync.
        - DuckDB (analytics, ~35 MB download on first use): var duck = await DuckDb.OpenAsync(); await DuckDb.RegisterUrlAsync("sales.parquet", url) or RegisterTextAsync("x.csv", csv); await duck.QueryAsync("select ..."); InsertAsync(table, rows).
        - Csv.Parse(text) -> List<string[]>; QueryResult.ToCsv().
        """;

    private const string Ai = """
        # Rustzor.AI (namespace Rustzor.AI)
        - Chat: IChatModel model = OpenAiChat.OpenAI(key, "gpt-4o-mini") | OpenAiChat.Azure(endpoint, key, deployment) | OpenAiChat.Ollama("llama3.2") | new OpenAiChat(endpoint, key, model) | new ClaudeChat(key, "claude-sonnet-4-5") | new BuiltInChat() (Chrome built-in).
          var reply = await model.CompleteAsync(messages, new ChatOptions { MaxTokens = 800 }); reply.Text. Streaming: await model.StreamAsync(messages, piece => { text += piece; StateHasChanged(); }).
          var convo = new Conversation(model, "system prompt"); await convo.SendAsync(userText, onText, imageDataUrl).
          ChatMessage.System/User(text, imageDataUrl)/Assistant. Temperature is optional (some models accept only the default).
        - Keys in browser code are visible to visitors: let the user paste their own key (stored in WebStorage.Local) or call a proxy.
        - RAG: var rag = new RagPipeline(new LocalEmbedder() /* offline transformers.js */ or new RemoteEmbedder(openAiChat), model); await rag.IndexAsync(docId, text); var a = await rag.AskAsync(question); a.Text, a.Sources. VectorStore (Add, Search, ToJson/FromJson), TextChunker.Split.
        - Local inference: LocalAi.EmbedAsync(texts), LocalAi.ClassifyAsync(text) (sentiment), LocalAi.GenerateAsync(prompt) (small model download).
        """;

    private const string Charts = """
        # Chart libraries
        - Rustzor.UI charts (LineChart, BarChart, DonutChart, Sparkline) need nothing extra: prefer them for dashboards.
        - Rustzor.Charts.ChartJs: <ChartJs Type="line|bar|pie|doughnut|radar|polarArea|scatter|bubble" Labels="labels" Datasets="List<ChartJsDataset>" Height Horizontal Stacked ShowLegend Options OnClick="(ChartJsClick c) => ..." />; new ChartJsDataset { Label = "Sales", Data = new List<double>{...}, BackgroundColor, BorderColor, Fill, Tension }.
        - Rustzor.Charts.ECharts: <EChart Option="EChartOption.Line(categories).AddLine("Visits", values, area: true)" Height OnClick/> also EChartOption.Bar(...).AddBar(...), Pie(name, items, donut), Gauge, Heatmap, Title, Set(path, value).
        - Rustzor.Charts.D3: <D3Chart Kind="bar|line|pie|force|treemap" Data="List<D3Datum>" Series="List<D3Point>" Nodes Links Tree="D3Tree" Height OnSelect/>; <D3Script Script="js using d3, svg, data, width, height" Data="obj"/> (data is serialised camelCase).
        """;

    private const string Bootstrap = """
        # Rustzor.UI.Bootstrap (@using Rustzor.UI.Bootstrap) — Bootstrap 5.3, state in C#, no Bootstrap JS
        BsButton (Color, Outline, Size, Loading, OnClick), BsAlert (Color, Heading, Dismissible), BsBadge, BsCard (Title, Subtitle, Header, Footer), BsProgress (Value, Striped, Animated, ShowLabel), BsSpinner, BsModal (@bind-Open, Title, Size, Footer), BsNavbar (Brand, Links = BsLink[], Dark, End), BsTabs/BsTab (@bind-Active), BsAccordion/BsAccordionItem, BsDropdown (Label, Items, OnSelect), BsTable<TItem> (Items, Header, Row, OnRowClick), BsPagination (@bind-Page, PageCount), BsInput (Label, @bind-Value, Type, Rows, Help, Error, Invalid), BsSwitch, BsToastHost + BsToasts.Show(title, body, color). Colors: BsColor.Primary... Any Bootstrap CSS class works in markup. Don't mix with Tailwind in one app.
        """;

    private const string Tailwind = """
        # Rustzor.UI.Tailwind (@using Rustzor.UI.Tailwind) — Tailwind 3.4 compiled in the browser
        Any Tailwind utility class in markup works (dark: variant follows the theme). Extra palettes: rust-50..950, patina-50..950; fonts font-display, font-sans, font-mono.
        Tw.Css("base", cond ? "x" : null) joins classes; Tw.SetTheme("dark"); Tw.Configure(new { theme = new { extend = ... } }).
        Components: TwButton (Variant primary|secondary|ghost|danger|patina, Size), TwCard (Title, Subtitle, Actions, Flush), TwBadge (Tone), TwInput (@bind-Value, Label, Hint, Error), TwToggle (@bind-Value), TwAlert (Tone, Title), TwModal (@bind-Open), TwTabs (Titles, @bind-Active), TwStat (Label, Value, Delta, Positive), TwTable<TItem> (Items, Header, Row).
        """;

    private const string Mui = """
        # Rustzor.UI.Mui (@using Rustzor.UI.Mui) — Material UI 5 via React islands
        Typed wrappers: MuiButton (Text, Variant contained|outlined|text, Color, StartIcon "material icon name", OnClick), MuiTextField (@bind-Value, Label, Multiline, StartIcon), MuiSelect (Options, @bind-Value), MuiSwitch (@bind-Checked, Checkbox), MuiSlider (@bind-Value, Min, Max, Step), MuiTabs (Titles, @bind-Active), MuiChip (Label, Icon, OnDelete), MuiAlert (Severity, Title, ChildContent), MuiCard (Title, Subheader, Avatar, ChildContent, Actions), MuiAppBar (Title, OnMenu, ChildContent), MuiProgress (Value or indeterminate, Circular), MuiRating, MuiToggleGroup.
        Anything else: <MuiIsland Tree='Mui.El("Stack", new { direction = "row", spacing = 2 }, new object[] { Mui.El("Avatar", null, "A"), Mui.Icon("mail") })' OnEvent="e => ..."/>; Mui.Slot() places Razor ChildContent; Mui.Event("name") wires a callback; Mui.Theme(options).
        """;

    private const string Pitfalls = """
        # Rustzor pitfalls (read once per conversation)
        - There is no HttpClient in the browser: use Rustzor.Browser.Fetch (FetchClient). No System.IO files: use Storage/IndexedDB/FileSystem.
        - Canvas2D batches commands: call Flush() after drawing each frame (GameView does it for you).
        - Create Canvas2D/GlContext in OnAfterRender(firstRender) — element references are not ready before the first render.
        - From timers, callbacks and async continuations call InvokeAsync(StateHasChanged) to refresh the UI.
        - Keep static content and assets under wwwroot/ and reference them with relative URLs ("img/logo.png").
        - Do not put secrets in the app; ask the user for keys at runtime.
        - The interpreter is fast enough for UI and business logic; for hundreds of objects per frame, avoid allocation-heavy code (LINQ in hot loops, many temporary structs).
        - Use records/classes with public get/set properties for JSON and IndexedDB data; JSON uses camelCase names.
        - If a .NET API fails at runtime with "no implementation for …", use a simpler alternative (string methods, Math, List/Dictionary/LINQ are well supported).
        - Footer/about must credit: Built by Gravicode Studios, led by Kang Fadhil.
        """;

    /// <summary>The public API of a Rustzor library, read from the source checkout.</summary>
    public static string LibraryApi(string rustzorRoot, string library)
    {
        if (string.IsNullOrWhiteSpace(rustzorRoot) || !Directory.Exists(Path.Combine(rustzorRoot, "src")))
            return "The Rustzor source folder is not set (Settings → Rustzor), so the API cannot be read. Use rustzor.get_guide.";
        var name = library.Trim();
        if (!name.StartsWith("Rustzor.", StringComparison.OrdinalIgnoreCase)) name = "Rustzor." + name;
        var dir = Directory.EnumerateDirectories(Path.Combine(rustzorRoot, "src"), "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        if (dir is null)
        {
            var all = Directory.EnumerateFiles(Path.Combine(rustzorRoot, "src"), "*.csproj", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension).Where(n => n!.StartsWith("Rustzor")).OrderBy(n => n);
            return $"No library {name}. Available: {string.Join(", ", all)}";
        }
        var sb = new StringBuilder($"# Public API of {name}\n");
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor")).OrderBy(f => f))
        {
            var lines = File.ReadAllLines(file);
            var picked = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                var isApi = t.StartsWith("public ") || t.StartsWith("[Parameter") || t.StartsWith("@typeparam") || t.StartsWith("@page");
                if (!isApi || t.StartsWith("public override string ToString")) continue;
                var summary = i > 0 ? SummaryRegex().Match(lines[i - 1]) : Match.Empty;
                var signature = BodyRegex().Replace(t, "").TrimEnd('{', ' ');
                picked.Add(summary.Success ? $"{signature}   // {summary.Groups[1].Value.Trim()}" : signature);
            }
            if (picked.Count == 0) continue;
            sb.AppendLine($"\n## {Path.GetFileName(file)}");
            foreach (var p in picked) sb.AppendLine(p);
            if (sb.Length > 40_000) break;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"///\s*<summary>(.*?)</summary>")]
    private static partial Regex SummaryRegex();

    // Drops expression bodies and simple initialisers so signatures stay short.
    [GeneratedRegex(@"\s*=>.*$")]
    private static partial Regex BodyRegex();
}
