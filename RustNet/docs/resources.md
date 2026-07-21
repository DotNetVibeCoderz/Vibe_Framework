# Embedded resources (`RustNet.Resources`)

Bundle assets — images, fonts, UI XML, any bytes — into the app so they
travel with it and are available at runtime, without a separate flash step
or base64 hacks in code.

## Embedding

Add the file as an `EmbeddedResource` in the app's csproj. `LogicalName`
pins the runtime name (otherwise it's `<RootNamespace>.<path>`):

```xml
<ItemGroup>
  <EmbeddedResource Include="assets/logo.gif">
    <LogicalName>logo.gif</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

The MetadataProcessor reads every embedded manifest resource from the
compiled assembly and copies it into the RNX module (format v4). So the
resource ships inside the signed `.rnx` — one artifact, one flash.

## Reading

```csharp
using RustNet.Resources;

if (Resource.Exists("logo.gif"))
{
    byte[] bytes = Resource.GetBytes("logo.gif");   // raw bytes
    Bitmap logo = Bitmap.Decode(bytes);             // e.g. decode + draw
    Display.DrawImage(0, 0, logo.Width, logo.Height, logo.ToRgb565Bytes());
}

string layout = Resource.GetString("ui.xml");       // text resource (UTF-8)
UiElement screen = Ui.LoadXml(layout);
```

## Verified path

RNX v4 round-trips resources (Rust unit test), and an end-to-end run
proves it on the virtual device: an app embeds an 8×6 GIF, the
MetadataProcessor packs it into the RNX, and on the device
`Resource.GetBytes` returns the bytes, `Bitmap.Decode` decodes them, and
the drawn framebuffer matches the embedded image (left half red, right
half green). So: **embed the asset → it travels in the RNX → read + decode
+ draw on the chip.**

Template: `rustnet new image-viewer <name>`.
