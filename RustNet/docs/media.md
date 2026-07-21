# Media (`RustNet.Media`) — camera capture

Chip-gated media peripherals. v0.8 opens with **camera capture**; audio and
video follow. Where the board has no camera interface the runtime returns a
"not supported" error; the virtual device carries a colour-bar test sensor so
capture pipelines can be built and pixel-verified without optics.

## Camera

```csharp
using RustNet.Media;
using RustNet.Graphics;

Display.Init(160, 120);
Camera.Configure(160, 120, PixelFormat.Rgb565);   // size + format
byte[] frame = Camera.Capture();                   // width*height*2 bytes
Display.DrawImage(0, 0, Camera.Width(), Camera.Height(), frame);
Display.Present();
```

| Member | Effect |
|---|---|
| `Configure(w, h, format)` | set frame size and pixel format (`Rgb565` or `Grayscale`) |
| `Capture()` | grab one frame as raw bytes (RGB565 LE: `w*h*2`; grayscale: `w*h`) |
| `Width()` / `Height()` | the configured frame dimensions |

A captured RGB565 frame is display-ready — it blits straight through
`Display.DrawImage`, and (via `RustNet.Drawing.Bitmap`) can be processed like
any other image.

## Audio playback

PCM playback over the I2S HAL:

```csharp
using RustNet.Media;

Audio.Configure(44100, 16, 1);              // sample rate, bits, channels
byte[] pcm = ReadWav(...);                   // little-endian 16-bit PCM
int accepted = Audio.Play(pcm);              // samples queued to the sink
int total = Audio.SamplesPlayed();           // cumulative since boot
```

| Member | Effect |
|---|---|
| `Configure(sampleRate, bits, channels)` | set up the I2S sink |
| `Play(pcm)` | queue LE 16-bit PCM; returns samples accepted |
| `SamplesPlayed()` | cumulative samples the sink has taken |

On the virtual device the samples flow into the I2S simulator (so playback
is testable without a DAC/amp); on real silicon they reach the I2S
peripheral. Intrinsics: `RustNet.Media.Audio::Configure/Play/SamplesPlayed`.

## MJPEG video

A clip is a sequence of JPEG frames, each length-prefixed (u32 LE) — the
simplest streamable container. Frames are JPEG-encoded by the runtime, so a
captured camera frame records directly, and playback decodes each frame back
to a `RustNet.Drawing.Bitmap`.

```csharp
using RustNet.Media;
using RustNet.Drawing;

// Record
var clip = new MjpegWriter();
clip.AddFrame(Camera.Capture(), Camera.Width(), Camera.Height(), quality: 90);
byte[] mjpeg = clip.ToBytes();               // store / stream

// Play
var reader = new MjpegReader(mjpeg);
for (int i = 0; i < reader.Count; i++)
{
    Bitmap f = Bitmap.Decode(reader.Frame(i));   // JPEG -> RGB565
    Display.DrawImage(0, 0, f.Width, f.Height, f.ToRgb565Bytes());
    Display.Present();
}
```

`Video.EncodeJpeg(rgb565, w, h, quality)` is the runtime intrinsic behind
`AddFrame` (RGB565 → JPEG). `MjpegWriter.AddJpegFrame` appends an
already-encoded frame. Verified on-device: a captured colour-bar frame is
JPEG-encoded (valid `FF D8` SOI), written into a 3-frame clip, read back and
decoded — the drawn framebuffer still shows all eight bars.

## VNC server

Stream the device framebuffer over the network so a desktop VNC client can
watch the display live:

```csharp
using RustNet.Media;

Vnc.Start(5900);                 // RFB server on TCP :5900 (runs in the background)
// … keep drawing to the display; clients see updates on request …
bool up = Vnc.IsRunning();
Vnc.Stop();
```

The server speaks **RFB 3.8** with security type `None`, a 32bpp true-colour
pixel format, and `Raw`-encoded full-frame updates — enough for standard VNC
viewers (TightVNC, RealVNC, `vncviewer`) to connect and see the panel. Input
events are parsed and dropped for now. It runs on a background thread, so
start it once and carry on drawing.

Verified on-device: an app paints a red/green split and calls `Vnc.Start`; a
VNC client completes the handshake, reads `ServerInit` (32×16, 32bpp,
"RustNet"), requests a frame and decodes the Raw rectangle — the pixels match
what was drawn (left red, right green). Encoder logic (ServerInit /
FramebufferUpdate) is unit-tested too.

## Architecture

`Camera` is a `rustnet-hal` trait (`camera.rs`); the host simulator
(`rustnet-hal-host::SimCamera`) delivers a deterministic 8-bar SMPTE-style
test frame. Real silicon (e.g. an ESP32 DVP/OV2640 sensor) plugs its own
`Camera` implementation into the firmware's `SharedState`. Intrinsics:
`RustNet.Media.Camera::ConfigureRaw/Capture/Width/Height` in
`runtime/firmware/src/apphost.rs`.

## Verified path

Unit test: `SimCamera` produces the right frame length, white first bar /
black last bar, and grayscale luma; zero dimensions are rejected. On-device:
an app configures a 64×32 camera, captures, and blits the frame to the
display — the captured framebuffer shows all eight colour bars at their
expected RGB565 values (white, yellow, cyan, green, magenta, red, blue,
black).
