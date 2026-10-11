# JavaScript interop

Most apps never touch JavaScript: the libraries wrap the browser APIs. When you need something they do not cover, `Rustzor.Core` (namespace `Rustzor.Interop`) gives you three levels.

## 1. Any JavaScript object: `JsObject`

```csharp
using Rustzor.Interop;

using var nav = JsObject.Global("navigator")!;          // null when the path does not exist
var lang = nav.Get("language").AsString();                 // "id-ID"

using var map = JsObject.New("Map")!;
map.Call("set", "kopi", 28000);
var price = map.Call("get", "kopi").AsDouble();

var dpr = JsObject.Eval("window.devicePixelRatio").AsDouble();

using var sub = JsObject.OnWindow("resize", e => Console.WriteLine("resized"));
```

`JsObject` holds a handle to a live JavaScript value: `Get`, `Set`, `Call`, `CallAsync` (awaits a promise), `GetObject`/`CallObject` (when the result is itself an object), `On` (events), `Has`. Dispose it when done so the handle is released.

## 2. Named operations: `Js`

Libraries talk to the browser through **ops**: a name and JSON arguments, implemented once in JavaScript.

```csharp
JsonValue v = Js.Call("geo.supported");                          // synchronous, returns JSON
Js.Send("doc.title", Js.Args("title", "Orders · Kedai Senja"));  // no result
JsonValue r = await Js.CallAsync("fetch.send", request);         // waits for a promise
```

`Js.Args("name", value, "name2", value2…)` builds the argument object. A failing op throws `JsException`, whose message is the JavaScript error's.

## 3. Callbacks and timers

Calls from JavaScript into C# go through registered callbacks:

```csharp
var id = Callbacks.Register(payload => Console.WriteLine(payload["text"].AsString()));
// pass `id` to an op; JavaScript calls dispatch(id, { text: "hi" })
Callbacks.Unregister(id);
```

Timers run on the browser's event loop. `Task.Delay` works and is routed here.

```csharp
IDisposable ticker = Scheduler.SetInterval(() => { now = DateTime.Now; StateHasChanged(); }, 1000);
int once = Scheduler.SetTimeout(() => Toasts.Info("Saved"), 1500);
Scheduler.RequestAnimationFrame(t => Draw(t));
await Scheduler.Delay(250);
```

## JSON

`System.Text.Json` is not shipped (it is large, and the interop wire is JSON already). `Rustzor.Interop` has a small, fast replacement:

```csharp
string text = Json.Serialize(order);                       // camelCase property names
Order o = Json.Deserialize<Order>(text);                   // classes, records, lists, arrays,
List<Order> all = Json.DeserializeList<Order>(text);       // dictionaries, enums, nullables,
var byId = Json.DeserializeDictionary<Order>(text);        // DateTime, DateTimeOffset, Guid, decimal

JsonValue v = JsonValue.Parse(text);
var first = v["items"][0]["name"].AsString();

var w = new JsonWriter();
w.BeginObject().Prop("id", 7).Prop("name", "Kopi").EndObject();
```

## Writing a library

A library is a class library that references `Rustzor.Core` (and `Rustzor.Components` if it has components). Its browser code goes in a file named `rustzor.module.js`, embedded as a resource:

```xml
<ItemGroup>
  <EmbeddedResource Include="rustzor.module.js" LogicalName="rustzor.module.js" />
  <EmbeddedResource Include="rustzor.module.css" LogicalName="rustzor.module.css" Condition="Exists('rustzor.module.css')" />
</ItemGroup>
```

The module exports `install`, which receives the bridge and registers ops:

```js
// rustzor.module.js
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  ops["battery.level"] = async () => (await navigator.getBattery()).level;

  ops["clock.watch"] = ({ id }) => {
    const timer = setInterval(() => dispatch(id, { now: Date.now() }), 1000);
    return addHandle(timer);                  // an int the C# side can hold
  };
  ops["clock.stop"] = ({ h }) => {
    clearInterval(getHandle(h));
    releaseHandle(h);
  };
}
```

```csharp
public static class Battery
{
    public static async Task<double> LevelAsync() => (await Js.CallAsync("battery.level")).AsDouble();
}
```

The full context is `{ ops, runtime, dispatch, complete, addHandle, getHandle, releaseHandle, eventToWire, elementOf }`; `elementOf(id)` turns an `ElementReference`'s id into the DOM element. When an app references the library, the bundler extracts the module to `_rustzor/modules/` and `rustzor.js` installs it at startup; a `rustzor.module.css` is linked before the app's own stylesheets.

If the module wraps a large third-party script, load it lazily from a pinned CDN URL the first time an op needs it, and let apps override the URL (the integrations read `window.RustzorCdn`).
