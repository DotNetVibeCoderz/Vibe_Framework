# Interop JavaScript

Sebagian besar aplikasi tidak pernah menyentuh JavaScript: library-library sudah membungkus API browser. Ketika Anda butuh sesuatu yang belum dicakup, `Rustzor.Core` (namespace `Rustzor.Interop`) menyediakan tiga tingkat akses.

## 1. Objek JavaScript apa pun: `JsObject`

```csharp
using Rustzor.Interop;

using var nav = JsObject.Global("navigator")!;          // null jika path tidak ada
var lang = nav.Get("language").AsString();                 // "id-ID"

using var map = JsObject.New("Map")!;
map.Call("set", "kopi", 28000);
var price = map.Call("get", "kopi").AsDouble();

var dpr = JsObject.Eval("window.devicePixelRatio").AsDouble();

using var sub = JsObject.OnWindow("resize", e => Console.WriteLine("resized"));
```

`JsObject` memegang handle ke nilai JavaScript yang hidup: `Get`, `Set`, `Call`, `CallAsync` (menunggu sebuah promise), `GetObject`/`CallObject` (jika hasilnya juga berupa objek), `On` (event), `Has`. Dispose objek ini setelah selesai agar handle-nya dilepas.

## 2. Operasi bernama: `Js`

Library berkomunikasi dengan browser melalui **ops**: sebuah nama dan argumen JSON, yang diimplementasikan sekali di JavaScript.

```csharp
JsonValue v = Js.Call("geo.supported");                          // sinkron, mengembalikan JSON
Js.Send("doc.title", Js.Args("title", "Orders · Kedai Senja"));  // tanpa hasil
JsonValue r = await Js.CallAsync("fetch.send", request);         // menunggu sebuah promise
```

`Js.Args("name", value, "name2", value2…)` membangun objek argumen. Op yang gagal melempar `JsException`, dengan pesan yang sama seperti pesan error JavaScript-nya.

## 3. Callback dan timer

Panggilan dari JavaScript ke C# melewati callback yang sudah didaftarkan:

```csharp
var id = Callbacks.Register(payload => Console.WriteLine(payload["text"].AsString()));
// kirim `id` ke sebuah op; JavaScript memanggil dispatch(id, { text: "hi" })
Callbacks.Unregister(id);
```

Timer berjalan di event loop browser. `Task.Delay` berfungsi dan diarahkan ke sini.

```csharp
IDisposable ticker = Scheduler.SetInterval(() => { now = DateTime.Now; StateHasChanged(); }, 1000);
int once = Scheduler.SetTimeout(() => Toasts.Info("Saved"), 1500);
Scheduler.RequestAnimationFrame(t => Draw(t));
await Scheduler.Delay(250);
```

## JSON

`System.Text.Json` tidak disertakan (ukurannya besar, dan jalur interop sudah memakai JSON). `Rustzor.Interop` punya pengganti yang kecil dan cepat:

```csharp
string text = Json.Serialize(order);                       // nama property camelCase
Order o = Json.Deserialize<Order>(text);                   // class, record, list, array,
List<Order> all = Json.DeserializeList<Order>(text);       // dictionary, enum, nullable,
var byId = Json.DeserializeDictionary<Order>(text);        // DateTime, DateTimeOffset, Guid, decimal

JsonValue v = JsonValue.Parse(text);
var first = v["items"][0]["name"].AsString();

var w = new JsonWriter();
w.BeginObject().Prop("id", 7).Prop("name", "Kopi").EndObject();
```

## Menulis library

Library adalah class library yang me-reference `Rustzor.Core` (dan `Rustzor.Components` jika memiliki component). Kode browser-nya ditaruh di file bernama `rustzor.module.js`, yang di-embed sebagai resource:

```xml
<ItemGroup>
  <EmbeddedResource Include="rustzor.module.js" LogicalName="rustzor.module.js" />
  <EmbeddedResource Include="rustzor.module.css" LogicalName="rustzor.module.css" Condition="Exists('rustzor.module.css')" />
</ItemGroup>
```

Modul tersebut mengekspor `install`, yang menerima bridge dan mendaftarkan ops:

```js
// rustzor.module.js
export function install({ ops, dispatch, addHandle, getHandle, releaseHandle, elementOf }) {
  ops["battery.level"] = async () => (await navigator.getBattery()).level;

  ops["clock.watch"] = ({ id }) => {
    const timer = setInterval(() => dispatch(id, { now: Date.now() }), 1000);
    return addHandle(timer);                  // sebuah int yang bisa dipegang sisi C#
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

Context lengkapnya adalah `{ ops, runtime, dispatch, complete, addHandle, getHandle, releaseHandle, eventToWire, elementOf }`; `elementOf(id)` mengubah id milik sebuah `ElementReference` menjadi elemen DOM. Ketika aplikasi me-reference library tersebut, bundler mengekstrak modulnya ke `_rustzor/modules/` dan `rustzor.js` memasangnya saat startup; `rustzor.module.css` di-link sebelum stylesheet milik aplikasi.

Jika modul membungkus script pihak ketiga berukuran besar, muat script itu secara lazy dari URL CDN yang versinya dikunci saat pertama kali sebuah op membutuhkannya, dan izinkan aplikasi meng-override URL tersebut (integrasi-integrasi membaca `window.RustzorCdn`).
