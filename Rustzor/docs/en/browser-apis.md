# Browser APIs

Each browser API is a separate package, `Rustzor.Browser.*`, so an app downloads only what it uses. Each one is C# over the bridge plus a small JavaScript module embedded in the assembly. Methods that wrap a promise are `async`; anything that keeps a subscription returns an `IDisposable`.

| Package | Main types | What for |
| --- | --- | --- |
| `Rustzor.Browser.DOM` | `Document`, `Window`, `ElementExtensions`, `Clipboard`, `Download` | Title, CSS variables, root attributes, scroll, focus, element size, keyboard shortcuts, print, clipboard, downloads |
| `Rustzor.Browser.Fetch` | `FetchClient`, `FetchRequest`, `FetchResponse` | HTTP, JSON, streamed responses. See [networking](networking.md) |
| `Rustzor.Browser.WebSocket` | `WebSocketClient`, `BroadcastChannel` | Sockets with reconnect; messages between tabs |
| `Rustzor.Browser.Storage` | `WebStorage.Local/Session`, `StorageManager`, `Cookies` | localStorage and sessionStorage (also as JSON), quota, persistence, cookies |
| `Rustzor.Browser.FileSystem` | `Files`, `PickedFile` | Open and save dialogs, drag and drop, the origin-private file system |
| `Rustzor.Browser.Notifications` | `Notifier`, `Vibration`, `AppBadge` | System notifications, vibration, the app-icon badge |
| `Rustzor.Browser.Push` | `PushManager` | Web Push subscriptions |
| `Rustzor.Browser.BackgroundSync` | `SyncManager`, `Outbox` | Work that waits for a connection |
| `Rustzor.Browser.Workers` | `WebWorker`, `ServiceWorkers` | Web Workers; service workers, offline and install prompts (PWA) |
| `Rustzor.Browser.Location` | `Geolocation`, `GeoPosition` | Current position, watching, distance |
| `Rustzor.Browser.Sensors` | `DeviceSensors`, `BatteryState` | Orientation, motion, generic sensors, ambient light, battery, keeping the screen on |
| `Rustzor.Browser.Media` | `MediaDevices`, `MediaStream`, `MediaRecorder` | Camera, microphone, screen capture, recording |
| `Rustzor.Browser.Audio` | `AudioEngine`, `SoundClip`, `Music` | Tones, sound effects, music, spectrum, speech synthesis |
| `Rustzor.Browser.Video` | `VideoPlayer` | Video playback control |
| `Rustzor.Browser.Canvas` | `Canvas2D`, `CanvasImage`, `CanvasInput` | 2D drawing with batched commands, images, pointer input |
| `Rustzor.Browser.OffscreenCanvas` | `Offscreen`, `OffscreenWorker` | Rendering in a worker |
| `Rustzor.Browser.WebGL` | `GlContext`, `Mat4` | WebGL 2 |
| `Rustzor.Browser.WebGPU` | `Gpu`, `GpuInfo`, `ShaderCanvas` | Adapter info, WGSL compute (`ComputeAsync`), shader canvases |
| `Rustzor.Browser.WebRTC` | `PeerConnection`, `RemoteStream` | Peer-to-peer media and data |
| `Rustzor.Browser.WebXR` | `Xr`, `XrSession` | VR and AR sessions |
| `Rustzor.Browser.Bluetooth` | `Ble`, `BleDevice` | Web Bluetooth (GATT) |
| `Rustzor.Browser.USB` | `UsbDevice` | WebUSB |
| `Rustzor.Browser.Serial` | `SerialPort` | Web Serial |
| `Rustzor.Browser.HID` | `HidDevice` | WebHID |
| `Rustzor.Browser.NFC` | `Nfc`, `NfcTag`, `NfcRecord` | Web NFC |
| `Rustzor.Browser.Gamepad` | `Gamepads`, `GamepadState` | Game controllers |
| `Rustzor.Browser.AI` | `BuiltInAi`, `AiSession` | The browser's built-in language model, where there is one |

Hardware APIs (Bluetooth, USB, Serial, HID, NFC) need a secure context, a user gesture to request a device, and a browser that implements them (mostly Chromium on desktop and Android). Check `IsSupported` before offering the feature.

## Examples

### DOM and keyboard

```csharp
using Rustzor.Browser.DOM;

Document.Title = "Orders · Kedai Senja";
Document.SetCssVariable("--rz-accent", "#1f5f3f");
using var keys = Window.OnKeyDown(k => { if (k.Key == "F9") Charge(); });
await searchBox.FocusAsync();                       // an ElementReference
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

For structured data use IndexedDB or SQLite from [`Rustzor.Data`](frameworks.md#rustzordata).

### Files

```csharp
using Rustzor.Browser.FileSystem;

var picked = await Files.PickAsync(".csv,.json");
if (picked.Count > 0) text = await picked[0].ReadTextAsync();
await Files.SaveTextAsync("report.csv", csv, "text/csv");
var dataUrl = await picked[0].ReadDataUrlAsync();   // an image, for <img src>
```

### Notifications and location

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

await AudioEngine.ResumeAsync();                    // after a click: browsers start audio muted
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
         .Flush();                                  // one bridge call for the whole batch
    }
}
```

### Between tabs

```csharp
using Rustzor.Browser.WebSocket;

var channel = new BroadcastChannel("serambi");
channel.OnMessage += json => Apply(Json.Deserialize<Message>(json));
channel.PostJson(message);
```

The [Serambi chat sample](samples.md#serambi-chat) is built on this.
