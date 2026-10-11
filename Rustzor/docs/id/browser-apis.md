# API Browser

Setiap API browser adalah package terpisah, `Rustzor.Browser.*`, sehingga aplikasi hanya mengunduh apa yang dipakainya. Masing-masing berupa kode C# di atas bridge ditambah modul JavaScript kecil yang di-embed di dalam assembly. Method yang membungkus promise bersifat `async`; apa pun yang menyimpan subscription mengembalikan `IDisposable`.

| Package | Type utama | Kegunaan |
| --- | --- | --- |
| `Rustzor.Browser.DOM` | `Document`, `Window`, `ElementExtensions`, `Clipboard`, `Download` | Judul, variabel CSS, atribut root, scroll, focus, ukuran elemen, shortcut keyboard, print, clipboard, download |
| `Rustzor.Browser.Fetch` | `FetchClient`, `FetchRequest`, `FetchResponse` | HTTP, JSON, response streaming. Lihat [jaringan](networking.md) |
| `Rustzor.Browser.WebSocket` | `WebSocketClient`, `BroadcastChannel` | Socket dengan reconnect; pesan antar-tab |
| `Rustzor.Browser.Storage` | `WebStorage.Local/Session`, `StorageManager`, `Cookies` | localStorage dan sessionStorage (juga sebagai JSON), kuota, persistensi, cookie |
| `Rustzor.Browser.FileSystem` | `Files`, `PickedFile` | Dialog buka dan simpan, drag and drop, origin-private file system |
| `Rustzor.Browser.Notifications` | `Notifier`, `Vibration`, `AppBadge` | Notifikasi sistem, getaran, badge ikon aplikasi |
| `Rustzor.Browser.Push` | `PushManager` | Subscription Web Push |
| `Rustzor.Browser.BackgroundSync` | `SyncManager`, `Outbox` | Pekerjaan yang menunggu koneksi tersedia |
| `Rustzor.Browser.Workers` | `WebWorker`, `ServiceWorkers` | Web Worker; service worker, mode offline, dan prompt instalasi (PWA) |
| `Rustzor.Browser.Location` | `Geolocation`, `GeoPosition` | Posisi saat ini, pemantauan posisi, jarak |
| `Rustzor.Browser.Sensors` | `DeviceSensors`, `BatteryState` | Orientasi, gerakan, sensor generik, cahaya sekitar, baterai, menjaga layar tetap menyala |
| `Rustzor.Browser.Media` | `MediaDevices`, `MediaStream`, `MediaRecorder` | Kamera, mikrofon, tangkapan layar, perekaman |
| `Rustzor.Browser.Audio` | `AudioEngine`, `SoundClip`, `Music` | Nada, efek suara, musik, spektrum, sintesis suara (speech synthesis) |
| `Rustzor.Browser.Video` | `VideoPlayer` | Kontrol pemutaran video |
| `Rustzor.Browser.Canvas` | `Canvas2D`, `CanvasImage`, `CanvasInput` | Menggambar 2D dengan perintah yang di-batch, gambar, input pointer |
| `Rustzor.Browser.OffscreenCanvas` | `Offscreen`, `OffscreenWorker` | Rendering di dalam worker |
| `Rustzor.Browser.WebGL` | `GlContext`, `Mat4` | WebGL 2 |
| `Rustzor.Browser.WebGPU` | `Gpu`, `GpuInfo`, `ShaderCanvas` | Info adapter, compute WGSL (`ComputeAsync`), shader canvas |
| `Rustzor.Browser.WebRTC` | `PeerConnection`, `RemoteStream` | Media dan data peer-to-peer |
| `Rustzor.Browser.WebXR` | `Xr`, `XrSession` | Sesi VR dan AR |
| `Rustzor.Browser.Bluetooth` | `Ble`, `BleDevice` | Web Bluetooth (GATT) |
| `Rustzor.Browser.USB` | `UsbDevice` | WebUSB |
| `Rustzor.Browser.Serial` | `SerialPort` | Web Serial |
| `Rustzor.Browser.HID` | `HidDevice` | WebHID |
| `Rustzor.Browser.NFC` | `Nfc`, `NfcTag`, `NfcRecord` | Web NFC |
| `Rustzor.Browser.Gamepad` | `Gamepads`, `GamepadState` | Game controller |
| `Rustzor.Browser.AI` | `BuiltInAi`, `AiSession` | Language model bawaan browser, jika tersedia |

API perangkat keras (Bluetooth, USB, Serial, HID, NFC) membutuhkan secure context, gestur pengguna untuk meminta akses perangkat, dan browser yang mengimplementasikannya (umumnya Chromium di desktop dan Android). Periksa `IsSupported` sebelum menawarkan fitur tersebut.

## Contoh

### DOM dan keyboard

```csharp
using Rustzor.Browser.DOM;

Document.Title = "Orders · Kedai Senja";
Document.SetCssVariable("--rz-accent", "#1f5f3f");
using var keys = Window.OnKeyDown(k => { if (k.Key == "F9") Charge(); });
await searchBox.FocusAsync();                       // sebuah ElementReference
receiptPanel.ScrollIntoView(block: "start");
Window.Print();
```

### Storage

```csharp
using Rustzor.Browser.Storage;

WebStorage.Local.SetJson("cart", cart);
var saved = WebStorage.Local.GetObject<Cart>("cart");
WebStorage.Session.Set("who", "ayu");               // per tab
var (used, quota) = await StorageManager.EstimateAsync();
```

Untuk data terstruktur, gunakan IndexedDB atau SQLite dari [`Rustzor.Data`](frameworks.md#rustzordata).

### File

```csharp
using Rustzor.Browser.FileSystem;

var picked = await Files.PickAsync(".csv,.json");
if (picked.Count > 0) text = await picked[0].ReadTextAsync();
await Files.SaveTextAsync("report.csv", csv, "text/csv");
var dataUrl = await picked[0].ReadDataUrlAsync();   // sebuah gambar, untuk <img src>
```

### Notifikasi dan lokasi

```csharp
using Rustzor.Browser.Notifications;
using Rustzor.Browser.Location;

if (await Notifier.RequestPermissionAsync()) Notifier.Show("Order #058 is ready", "Table 12");
var here = await Geolocation.GetCurrentPositionAsync(highAccuracy: true);
var meters = Geolocation.DistanceMeters(here.Latitude, here.Longitude, -6.9175, 107.6191);
```

### Audio

```csharp
using Rustzor.Browser.Audio;

await AudioEngine.ResumeAsync();                    // setelah klik: browser memulai audio dalam keadaan mute
AudioEngine.Tone(880, 0.12, "square");
var coin = await AudioEngine.LoadAsync("sfx/coin.wav");
coin.Play(volume: 0.6);
AudioEngine.Speak("Selamat datang", "id-ID");
```

### Canvas

```razor
<canvas @ref="canvas" style="width:100%;height:320px"></canvas>

@code {
    private ElementReference canvas;

    protected override void OnAfterRender(bool first)
    {
        if (!first) return;
        var g = Canvas2D.FromElement(canvas);
        g.Fill("#1f5f3f").FillRect(20, 20, 120, 80)
         .Font("bold 20px sans-serif").Fill("#fff").FillText("Rustzor", 32, 66)
         .Flush();                                  // satu panggilan bridge untuk seluruh batch
    }
}
```

### Antar-tab

```csharp
using Rustzor.Browser.WebSocket;

var channel = new BroadcastChannel("serambi");
channel.OnMessage += json => Apply(Json.Deserialize<Message>(json));
channel.PostJson(message);
```

[Sample Serambi chat](samples.md#serambi-chat) dibangun di atas fitur ini.
