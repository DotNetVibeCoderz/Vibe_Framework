# Rustzor progress

Newest first. Each entry says what was built and how it was checked.

## 2026-10-22: Rupa design-tool sample

- `samples/Rupa`: a Figma-like vector design tool. Routes `/` (files kept in
  the browser, with thumbnails; new, open `.rupa.json`, delete, restore the
  sample) and `/file/{Id}` (the editor). Seed file: Lintas, three screens of an
  invented Jabodetabek commuter-rail app, four components, twelve colour styles.
- Editor: infinite canvas (Space/middle/trackpad pan, Ctrl+wheel and pinch
  zoom to the pointer, Shift+1/Shift+2); frame, rectangle, ellipse, line, pen,
  text and image tools; select, marquee, deep select, move with snapping and
  spacing readouts, eight resize handles, rotation, group, z-order, duplicate,
  copy/paste, nudge, undo/redo; layers with rename/hide/lock; properties incl.
  auto layout; components with instances that follow the main and keep text
  overrides, detach; colour styles; SVG and PNG export (fonts inlined);
  autosave to OPFS; Figma's shortcuts. Its own browser module handles canvas
  input (one message per animation frame), text measuring and export.
- Performance (interpreted, 300 shapes on the page, one pointer move per
  frame): about 12 ms of work per move dragging one shape, 11 ms dragging 167,
  11 ms dragging the whole frame; 56 fps in headless Chrome. Getting there:
  per-layer views that render only when their layer changes, a separate child
  list per container, the drag shown by the browser (C# redraws once on
  release), snapping by binary search over sorted edges, overlay guides as
  two paths, and the two library changes below.
- Library: `Rustzor.Components` renderer patches same-shaped child lists pairwise
  without building identity strings and tables; `Rustzor.Core` `JsonWriter`
  writes strings that need no escaping in one append (every interop call);
  `js/rustzor.js` creates `image`, `marker`, `filter` and the other missing
  SVG tags in the SVG namespace (an `<image>` drew nothing before).
- RustNetRuntime (uncommitted, with conformance checks in `SortChecks.cs`):
  `Array.Sort(keys, items)`, `(keys, items, comparer)` and `(array, index,
  length)`; built-in exceptions get .NET's hierarchy, so `catch
  (ArgumentException)` catches a runtime `ArgumentOutOfRangeException`;
  `new Dictionary(dictionary)` copies its source. Conformance 472/472 on .NET
  and RustCLR, `cargo test --workspace` and `tests/embedded.sh` pass.
- Verified: `tools/e2e/rupa.mjs` (32 checks), every route of every sample
  renders after the library changes, `meet.mjs` passes; screenshots by
  `tools/e2e/rupa-shots.mjs`.

## 2026-10-22: Ruang Redaksi video-call sample

- `samples/Meet`: video calls for Kabar Pesisir, a cooperative of five coastal
  newsrooms, between tabs of one browser with no server. Tabs in a room
  announce themselves on a `BroadcastChannel` every 1.5 s; for each pair the
  smaller id calls, so nobody offers twice; offers, answers and ICE
  candidates go over the same channel, then media and a data channel go peer
  to peer. Routes: `/` (rooms, with who is in each, live), `/r/{RoomId}`
  (pre-join check, then the call), `/recordings`.
- Pre-join: camera on a preview monitor with title-safe guides, the
  microphone on a 24-segment PPM, camera and microphone pickers, name strap
  and bureau, who is already in the room. The call: a grid sized with
  container query units, lower-third straps, a tally lamp on the active
  speaker from audio levels, mute, camera, screen share in a spotlight, chat,
  raised hands in order, reactions, the meeting's rundown moved for everyone,
  a timecode, leave or end for everyone. Recording mixes everyone's picture
  and voice into one 1280×720 video in the tab, shown to the others as REC.
  No camera: microphone only and a slate with initials; no devices at all:
  watch, listen and chat.
- Its own look: a news studio. Sofia Sans Extra Condensed straps and slugs,
  Onest, Martian Mono timecode; studio grey-blue with tally red and cue
  amber; picture areas stay dark in both themes.
- Libraries:
  - `Rustzor.Browser.WebRTC`: every connection has an audio and a video slot
    even with nothing to send (a peer without a camera still receives video,
    and a track can be added later); `ReplaceTrackAsync` (screen sharing
    without renegotiating); `OnDataOpen`/`DataOpen`; `Remote`;
    `RemoteStream.Level()` and `VideoSize`; `AttachTo(..., muted)`; the
    remote stream's handle and meter are released on `Dispose`.
  - `Rustzor.Browser.Media`: `MediaMixer` (several streams into one canvas
    grid with labels, and their sound mixed); `MediaRecorder` takes any
    stream handle and `FinishAsync` returns the size; microphone choice in
    `GetUserMediaAsync`; `HasAudio`/`HasVideo`; `OnEnded` (the browser's
    "Stop sharing"); level meters closed with their stream.
  - `tools/e2e/chrome.mjs`: one launcher for `probe.mjs` and `pages.mjs`;
    `RZ_FAKE_MEDIA=1` adds a fake camera, microphone and screen.
- Verified: warning-free build; every route renders with no console errors
  (`pages.mjs` with fake media); `node tools/e2e/meet.mjs` passes all 23
  checks: two tabs see each other's video, chat crosses the data channel, mute,
  a raised hand and a reaction show on the other side, the rundown moves for
  both, the tally lights, recording is announced and the saved 1280×720 video
  shows the call (94% of a sampled frame is the fake cameras' picture), screen
  share reaches the other tab and returns to the grid, a third tab without a
  camera sees the others and shows a slate, leaving removes the tile, ending
  for everyone ends the call for the others, and the call fits a 400 px phone
  without sideways scrolling. No runtime changes were needed.

## 2026-10-11: Jati Lestari ERP sample

- `samples/Erp`: ERP for a teak furniture workshop in Jepara, on `Rustzor.UI`
  and IndexedDB. Overview (cash, receivables, payables, stock value, what
  needs attention, sales against cost of sales, this month's profit and loss,
  stock by warehouse); sales (order, delivery note, invoice with PPN 11%,
  payment, overdue invoices); purchasing (order, goods receipt, bill payment,
  "reorder what is low"); production (bills of materials with wages, work
  orders from planned to finished, standard cost and margin); inventory in
  three warehouses at moving-average cost, stock cards and transfers; and
  accounting: journal, trial balance, profit and loss by period, balance
  sheet, cash expenses. Every action posts a balanced journal entry; the
  inventory page checks each stock group against its ledger account.
- Its own look: oiled-teak sidebar with grain, document numbers stencilled
  like export-crate marks (Big Shoulders Stencil), the order's paper trail as
  a row of crate tags; Hanken Grotesk and Fragment Mono; light and dark.
- Seed: eleven weeks played through the same actions (purchases, batches,
  sales, payments, rent and salaries), so every figure comes from a document.
  The simulation also runs on .NET and on `rustnet run` from a console
  harness; both produce the same history.
- RustNetRuntime: `DateTime.ParseExact` (one format or several) and
  `TryParseExact`, invariant culture, custom and standard formats; a
  conformance check (`DateChecks.cs`) and unit tests. Found while building:
  RustCLR's LINQ is eager (documented), so a key selector with side effects
  runs at the call; the seed draws its random numbers outside selectors.
- Verified in headless Chrome: every route renders with no console errors;
  a scripted cycle (purchase 2 m³ of teak and receive it, sell 4 chairs,
  deliver, invoice and collect, make 2 tables) checks the stock changes, each
  posting (inventory 29.000.000, PPN masukan 3.190.000, receivable 8.214.000,
  PPN keluaran 814.000, wages 1.700.000), the balanced trial balance and
  balance sheet, the ledger reconciliation, a reload keeping the data and the
  reset. No sideways scrolling at 400 px on any screen.

## 2026-10-11: release 0.1.0-preview.2

- All 45 packages at `0.1.0-preview.2`, the first release with `Rustzor.Cli`
  and `Rustzor.Templates`, so `dotnet tool install -g Rustzor.Cli --prerelease`
  works from nuget.org. Docs, READMEs and the catalog point at the new version.
- VS Code extension: publisher `GravicodeStudios`; the CLI runs through
  `execFile` with an argument list, never a shell. The Marketplace rejects the
  upload ("suspicious content") whatever the manifest, README, icon or snippets;
  it waits on Marketplace support. The `.vsix` from CI installs by hand.

## 2026-10-11: phase 5, tooling

- `Rustzor.Cli`, the `rustzor` command (a .NET tool): `new` (installs the
  templates of its own version when missing), `serve` (Kestrel dev server with
  SPA fallback and no-cache headers; rebuilds on save and reloads open pages
  through server-sent events; a failed build is shown over the page with
  project-relative paths), `publish` (with `--aot`, and a size report raw and
  gzipped), `add` (50 short names such as `ui`, `fetch`, `camera`), `info`.
- `Rustzor.Templates`: `rustzor` (Rustzor.UI shell with Home, Counter and
  Tasks in localStorage), `rustzor-empty`, `rustzorlib` (component, C# over a
  browser API and its `rustzor.module.js`). Packing writes the package
  version into each template.
- VS Code extension (`tools/vscode-rustzor`): commands for new project,
  serve and stop, publish (also AOT), add package (searchable list), info,
  install the CLI, docs; a serve button in the status bar; Razor, C# and
  JavaScript snippets. Tested inside VS Code (commands registered, Rustzor
  app detected); packed as a `.vsix` by CI.
- Package catalog: `docs/catalog/index.html` (search, group filters, copy
  buttons, both themes) and `docs/en|id/packages.md`, generated from the
  projects by `tools/e2e/make-catalog.mjs`; CI fails when they are stale.
  45 packages now, with icons for the two new ones.
- Verified: a CLI installed from the packed packages created all three
  templates; `serve` rebuilt in about 3 s and reloaded the open page, showed a
  syntax error over the page and cleared it after the fix; an app using a
  `rustzorlib` library got the library's browser module; `publish --aot`
  produced a 671 KB (gzipped) site. `scripts/tooling-smoke.sh` repeats this in CI.
- Docs: `docs/en/tooling.md` and `docs/id/tooling.md`; getting started, the
  READMEs, limitations, architecture and building from source updated.

## 2026-10-11: packages, documentation, release pipeline

**Published:** `0.1.0-preview.1`, all 43 packages, to nuget.org by the
`rustzor-v0.1.0-preview.1` tag (run 38132584251: CI at the release version,
package smoke test passed, 43 pushes). Rustzor now lives in
`Vibe_Framework/Rustzor`.

- Every package has its own icon (`assets/icons`, 43 icons from
  `tools/e2e/make-icons.mjs`: the Rustzor hex mark, a label per package, a
  colour per group) and a real package README (`assets/nuget-readme.md`).
- `scripts/pack.sh` packs all 43 packages at `0.1.0-preview.1` (or a given
  version). `Rustzor.Sdk` now carries `tools/Rustzor.Build` and a generated
  `Sdk/Version.props`, so its implicit `Rustzor.Components` reference always
  matches its own version. Fixed: an app on `Rustzor.Sdk` built as a library
  (the Razor SDK's `OutputType` won over the SDK's default).
- `scripts/package-smoke.sh` builds an app from the packed packages alone and
  runs it in headless Chrome; it passes.
- `Cargo.toml` takes RustNetRuntime from Vibe_Runtime pinned to commit
  `25cd43b` (the fixes above, pushed); `.cargo/config.toml` can point at a
  local checkout. Built from the pinned commit alone: same runtime.
- GitHub Actions: `eng/workflows/rustzor-ci.yml` (runtime, solution, pack,
  package smoke test) and `rustzor-publish.yml` (CI at the release version,
  then push to nuget.org with `NUGET_API_KEY`; tag `rustzor-v*` or manual
  with a dry run).
- Documentation: `README.md` and `README.id.md` with screenshots, and fifteen
  pages each in `docs/en` and `docs/id` (getting started, architecture,
  components, interop, UI kit, browser APIs, frameworks, integrations,
  networking, build modes, samples, AppGen, building from source,
  limitations). `CLAUDE.md` now has the real commands.

## 2026-10-11: API Playground, SignalR and gRPC-Web clients

- New library `Rustzor.Net.SignalR`: an ASP.NET Core SignalR client with the
  JSON hub protocol over WebSockets (negotiate, handshake, `SendAsync`,
  `InvokeAsync<T>`, `StreamAsync<T>`, typed `On<T1..T3>`, pings, automatic
  reconnect, a `Frame` event for logging).
- New library `Rustzor.Net.GrpcWeb`: gRPC-Web over fetch
  (`application/grpc-web+proto`) with unary and server-streaming calls,
  cancellation, trailers and `RpcException`, frames read as they stream in,
  and a small protobuf `ProtoWriter`/`ProtoReader` for hand-written messages.
- `samples/ApiPlayground`: an ASP.NET Core .NET 10 server (REST on
  `/api/books`, a raw WebSocket on `/ws`, a SignalR hub on `/hubs/chat`, the
  gRPC service `playground.Inventory` through `UseGrpcWeb`) that also serves
  the Rustzor client. The client has one workspace per protocol and a "wire"
  log of every request, response and frame. Every flow was driven in Chrome:
  REST 200/201/404, WebSocket echo and ticks, SignalR rooms, a result, a
  stream and a hub error, gRPC unary, NOT_FOUND, two streams and a cancel.
  Screenshots: `docs/images/sample-api-*.png`.
- RustNetRuntime fixes found by the playground (conformance 463 checks,
  cross-assembly fixture 8, `cargo test --workspace` 471 passing):
  - Overloads that differ only in generic arity across assemblies bound to
    the wrong one (`On<T>(string, Action<T>)` ran as `On(string, Action<object[]>)`).
  - `CancellationToken` and `CancellationTokenSource` had no members; now
    `Token`, `Cancel`, `IsCancellationRequested`, `CanBeCanceled`,
    `Register` (newest first, as .NET), `ThrowIfCancellationRequested` and
    `OperationCanceledException` (which `TaskCanceledException` derives from).
  - `Enumerable.AsEnumerable` and `Encoding.GetString(byte[], int, int)` added.

## 2026-10-11: decimal, Jack's add_package, and the first sample app

- Sample 1 of 13, `samples/Crm` (Nusantara CRM): overview with funnel, forecast
  and needs-attention list; deal board with drag and drop between stages;
  companies with detail pages (deals, people, activities); contacts; activity
  timeline grouped by due date; SQL reports with an ask-the-database box.
  Data lives in SQLite (`Rustzor.Data`) persisted in the browser. Checked with
  `tools/e2e/pages.mjs` (all 7 routes render, no console errors) and
  `probe.mjs` flows: drag a deal, change stage, add deal/company/contact/
  activity, tick activities, filter by kind, run, refuse and fail SQL; changes
  survive a reload. Screenshots: `docs/images/sample-crm*.png`.
- Sample 2 of 13, `samples/Pos` (Kedai Senja POS): café till with a product
  grid by category, drink options (size, hot or iced, sugar, extras), the
  open order drawn as a thermal-paper ticket, promo codes, PB1 10% tax,
  rounding to Rp 100, cash with tender buttons and change, QRIS, card with an
  EDC approval code, printable 58 mm receipt, order history with void and
  reprint, shift report (sales by hour, by method, best sellers, cash drawer
  count and close), menu editor with sold-out switch, F2 search and F9
  charge. Data in IndexedDB (`Rustzor.Data`). Every flow driven with
  `probe.mjs`; works at 400 px with a bottom order bar. Screenshots:
  `docs/images/sample-pos*.png`.
- Sample 4 of 13, `samples/Chat` (Serambi, Studio Lontar's team chat): each
  browser tab signs in as a person, and tabs talk through `BroadcastChannel`
  with messages kept in IndexedDB, so two tabs chat live with no server.
  Channels and direct messages with unread counts and a "new" line, threads,
  reactions, pins, edit and delete, image attachments, search, typing
  indicators across tabs, Markdown-lite with escaped HTML, @mentions, and
  three simulated teammates who answer when mentioned or messaged. Driven
  with two Playwright pages in one browser context. Screenshots:
  `docs/images/sample-chat*.png`.
- RustNetRuntime fixes found by the chat (conformance 460 checks, new
  `ActivatorChecks.cs` and `TextChecks.cs`):
  - `Activator.CreateInstance` of a framework collection skipped its native
    constructor, so a `Dictionary<,>` built by the JSON reader ignored writes.
  - Half of a surrogate pair vanished in `char.ToString()`,
    `StringBuilder.Append(char)`, `StringBuilder.Append(string)` and
    `string.Concat` over chars: every emoji was lost when text was rebuilt one
    char at a time. `char.ConvertFromUtf32`, `ConvertToUtf32`, `IsSurrogate`,
    `IsHighSurrogate`, `IsLowSurrogate` and `IsSurrogatePair` added.
  - A boxed `char` or `bool` printed as a number (`(object)'x'` was "120").
- Sample 3 of 13, `samples/Dashboard` (Arus, Java–Bali grid operations): a
  simulated power system ticking every 2 s, with merit-order dispatch,
  frequency, spinning reserve, renewables share and carbon intensity;
  24-hour stacked generation and mix charts through `Rustzor.Charts.ECharts`;
  a one-line diagram of the 500 kV backbone where dashes run with the flow
  and colour shows loading, with a region drill-down; a plant fleet table;
  demand against forecast, daily peaks and a week-by-hour heatmap; an alarm
  log with acknowledgement; and a "lose a unit" drill that trips a plant
  (the frequency dips to about 49.75 Hz and recovers within 30 s). Screenshots:
  `docs/images/sample-dashboard*.png`.
- RustNetRuntime fixes found by the dashboard (conformance now 456 checks,
  new `EnumChecks.cs`; `cargo test --workspace` 471 passing):
  - Enum arrays of six or more elements (initialised from an image blob by
    `RuntimeHelpers.InitializeArray`) came back all zeros, so
    `static readonly Fuel[] All = { ... }` held six copies of the first value.
  - A boxed enum or plain struct compared and hashed by reference
    (`object.Equals`, `GetHashCode`), so `HashSet<object>` held duplicates.
  - `Enum.CompareTo` was missing.
- `ElementReference.ScrollIntoView(block:)` takes "start", "center" or "end";
  `probe.mjs` has `FULL=0` for viewport-only screenshots.
- RustNetRuntime: `System.Decimal` (96-bit mantissa, .NET rounding, parse and
  format, `Math` overloads, LINQ `Sum`/`Average`), checked by conformance
  `DecimalChecks.cs` (453 checks on .NET, interpreted and JIT;
  `cargo test --workspace` 471 passing).
- Rustzor: `@bind` for `decimal`, `int?`, `double?`, `decimal?`;
  `Toasts.Warning`/`Toasts.Info`; failed async JS operations report the JS
  message alone (no `async:` prefix); `StatCard` sparkline uses the accent when
  there is no delta (it was red).
- RustzorAppGen: Jack has `rustzor.add_package` (project or package reference
  plus `_Imports.razor`). With real Azure gpt-5-mini Jack built a tip
  calculator on `decimal` and added a package on request; both ran.
- New `tools/e2e/pages.mjs <base> <outdir> routes...`: screenshot every route
  and fail on console errors.

## 2026-10-11: RustzorAppGen templates, and the bugs they found

- RustzorAppGen (`tools/RustzorAppGen`): Avalonia editor with explorer, editor,
  logs, status bar, settings in `app.config`, and Jack (Semantic Kernel tools
  over OpenAI, Azure OpenAI, Claude, Gemini, Ollama).
- 21 templates, each generated, built and opened in headless Chrome by
  `tools/RustzorAppGen/scripts/test-templates.sh` (21 of 21 pass):
  - Game: arcade-breakout, space-dodger, physics-sandbox.
  - Science: lorenz-explorer, stats-lab, periodic-table.
  - Work: kanban-board (IndexedDB, drag and drop), invoice-maker (live paper
    preview, print, CSV, history), crm-lite (contacts, pipeline, follow-ups).
  - Industry: machine-monitor (live telemetry, OEE, alarms), warehouse-inventory
    (SQLite, movements, SQL console).
  - Education: quiz-master (timed, streaks), math-flashcards (Leitner boxes,
    mastery map).
  - Simulator: epidemic-sir (SEIR with RK4), solar-system (Kepler orbits).
  - Tools: markdown-notes, unit-converter, pomodoro-timer.
  - AI: ai-chat (OpenAI, Azure, Claude, Ollama, Chrome built-in, offline demo);
    checked against real Azure OpenAI gpt-5-mini, streaming and Markdown.
  - Data: sales-dashboard (filters, period comparison, CSV import/export).
  - blank.
  Interactive flows of each were driven with `tools/e2e/probe.mjs` (now has
  `drag` and `eval` steps).
- More Rustzor fixes: `ElementReference.FocusAsync()`; `GameView` re-fits its
  canvas when the layout changes (it stretched); error logs print the stack.
- More RustNetRuntime fixes (conformance 446 checks, cross-assembly 7, a new GC
  stress test, `cargo test --workspace` 465 passing):
  - Garbage collection: `newobj` collected after popping the constructor's
    arguments, so `new Tag("t" + i)` could lose its string; a native's
    arguments and the results of managed callbacks it makes were unrooted.
    `crates/rustclr-bcl/tests/gc_natives.rs` runs LINQ and constructors under a
    collector that runs constantly.
  - `System.Text.RegularExpressions`: a new .NET-compatible engine.
  - `Random(seed)` reproduces .NET's sequences; `new Random()` differs per run.
  - `string.Length`, `s[i]`, `Substring` and `StringBuilder[i]` no longer copy
    the whole string per call (a 90 KB JSON parse went from 62 s to 0.65 s).
  - `Exception.ToString()` includes the stack trace.
- Rustzor fixes:
  - `Json` deserializes `List<T>`, arrays, dictionaries, enums, nullables,
    `DateTimeOffset`, `Guid`, `TimeSpan`; a property the JSON has no usable
    value for keeps its initial value (lists used to come back null).
    Serializing stops with a clear error at 64 levels instead of overflowing.
    `Json.DeserializeDictionary<T>` added.
  - Library stylesheets load before the app's own, so `app.css` and
    `template.css` win over `Rustzor.UI` at equal specificity.
  - `@onclick:stopPropagation` works on an element with no `@onclick`.
  - AppGen: a project named like a generated type (`App`, `Home`) gets a
    `Site` suffix for its namespace; new `.cs` files use the project namespace.
- RustNetRuntime fixes (uncommitted; conformance 436 checks, cross-assembly
  fixture 6 checks and now compiled unoptimised, `cargo test --workspace`
  457 passing, no_std targets build):
  - `await` in a Debug build: a class state machine resumed with a pointer
    instead of itself, so every field read after the first real suspension
    was null. Every `dotnet build` app with async I/O hit this.
  - Interface dispatch onto an override in another assembly whose parameter
    is a library struct ran the base method (`IComponent.SetParametersAsync`
    on a page never reached the page's override).
  - `DateTimeOffset` (UTC and host offsets), `Guid.ToString("N"/"B"/"P")`,
    `Guid.TryParse`, `TimeSpan.Parse`, static fields `TimeSpan.Zero`,
    `DateTime.MinValue`, `Guid.Empty` and friends.
  - Reflection: `PropertyType`/`FieldType`/`ParameterType`/`ReturnType` keep
    framework generic arguments and array element types, `GetElementType`,
    `GetIndexParameters`, `Nullable.GetUnderlyingType`, `Array.CreateInstance`,
    `Array.SetValue/GetValue`, `Enum.ToObject`, non-generic `IList` and
    `IDictionary`, `Convert.ToInt32(object)` on a boxed enum.

## 2026-10-11: frameworks, interpreter performance, runtime fixes

- Four frameworks in `src/Frameworks`, all building with 0 warnings:
  - `Rustzor.Game`: ECS `World` (sparse-set component stores, systems),
    fixed-step `GameLoop`, `Input` (keyboard/pointer/gamepad in one bridge
    call per frame), `PhysicsWorld` (circles/boxes, impulses, friction,
    spatial hash, raycasts, triggers), `Camera2D`, `Particles`, `Tweens`,
    `Timers`, `Sfx`, `NetSession` (BroadcastChannel or WebSocket relay),
    and a `GameView` component.
  - `Rustzor.Graphics`: `Scene2D` (nodes, pan/zoom viewport, hit testing,
    selection handles, SVG and JSON export), `PixelOps` (AOT-friendly image
    filters), `Scene3D` + `Renderer3D` on WebGL 2 (primitives, OBJ loader,
    orbit camera, ray picking, Lambert/hemisphere lighting, grid).
  - `Rustzor.Data`: typed IndexedDB stores, SQLite (sql.js) with
    persistence, DuckDB WASM, CSV.
  - `Rustzor.AI`: OpenAI/Azure/Ollama-compatible and Claude chat with
    streaming, conversations, embeddings, `VectorStore`, `RagPipeline`,
    local inference (transformers.js, Chrome built-in model).
- Checked in Chrome with `tests/FrameworksSmoke`: game runs with physics and
  particles; 2D scene hit test and selection, 3D render and pick, pixel ops;
  IndexedDB/SQLite/DuckDB queries return the right rows; AI page against
  **real Azure OpenAI (gpt-5-mini)**: streaming chat, completion, and RAG with
  citation all succeed. Claude returns 400 for the test key because it is not
  scoped to a workspace (`ClaudeChat` now accepts a `workspaceId`).
- Interpreter performance (RustNetRuntime): flat offset table for branches,
  no `Arc` clone per instruction, `RowMap` (dense vectors) for every
  row-keyed token table, cached native bindings per method, pooled frame
  vectors, precomputed local defaults, reused struct constructor cells,
  arithmetic/compare fast paths, and `StringBuilder.Append` in place (it was
  quadratic). Calls ~2.7x faster, `List<T>` 2x, physics 2x, canvas command
  batches 35x; allocations in the physics benchmark 327k -> 4.9k.
  Remaining: per-instruction dispatch (~40 cycles) and struct-heavy code;
  physics with many bodies should move to an AOT-compiled array core.
- Runtime correctness fixes, each with a conformance check (fixture now 425
  checks) plus a new two-assembly fixture (`tests/fixtures/CrossAssembly`):
  generic constructions and generic method calls over the caller's type
  parameters, `typeof(T)`/`new T()` inside async state machines, interface
  dispatch across assemblies, exception subclasses losing `Message`,
  record-struct `ToString` in interpolation, `TickCount64`, `WebUtility`,
  `Array.Clone`, interpolated `StringBuilder.Append`, `string.Create`,
  `Convert.ToByte(s, base)`, `double.CompareTo`. `cargo test --workspace`:
  457 passing; no_std targets build; minimal tier threshold 253,700 bytes.

## 2026-10-11: UI and chart integrations

- Six integration libraries in `src/Integrations`:
  `Rustzor.Charts.ChartJs`, `Rustzor.Charts.ECharts`, `Rustzor.Charts.D3`,
  `Rustzor.UI.Bootstrap`, `Rustzor.UI.Tailwind`, `Rustzor.UI.Mui`.
  Each loads its library from a CDN at a pinned version on first use;
  `window.RustzorCdn` points them at a mirror for offline apps.
- Checked in Chrome with `tests/IntegrationsSmoke`: every chart kind renders,
  chart clicks reach C#, Bootstrap tabs/accordion/dropdown/pagination/modal/toasts
  work, Tailwind classes compile and bind, MUI text field/select/switch/tabs/
  card slots/app bar round-trip through C#.
- RustNetRuntime fixes found by this work (in `Vibe_Runtime/RustNetRuntime`,
  uncommitted, conformance fixture now 417 checks, `cargo test --workspace`
  456 passing, no_std targets still build):
  - Arrays, strings and the native collections now satisfy their interfaces
    for `is`/`as`/casts (`list is IEnumerable` was false).
  - `IEnumerable<T>.GetEnumerator()` on an array through the interface.
  - Non-generic `IDictionary` and `DictionaryEntry`.
  - Explicit-interface natives (`Dictionary.IDictionary.GetEnumerator`).
  - Classes deriving from `Dictionary`/`List` dispatch to the native base.
  - Values returned as `object` by `IEnumerator.Current`,
    `PropertyInfo.GetValue` and `FieldInfo.GetValue` are boxed, so
    `o is double` works.

## Earlier work (before this log)

- Phase 1 complete and verified in a browser: runtime on wasm32, Razor
  components with events, `@bind`, routing, forms, DI; `dotnet build`
  produces a runnable site through the Rustzor SDK.
- AOT tier: IL to WebAssembly compiler; output matches dotnet; about 37x
  faster than the interpreter on the benchmark (`tests/AotBench`).
- 27 `Rustzor.Browser.*` libraries compile.
- `Rustzor.UI` design system renders correctly (`tests/UiSmoke`).
