# Serializers: JSON, XML, Binary

`RustNet.Serialization` is reflection-free by design — the runtime has no
reflection, so serialization works through an explicit document model that
builds, parses and writes entirely on-device. One DOM (`JsonValue`) backs
both the JSON and binary formats; XML has its own element tree.

## JSON

```csharp
JsonValue doc = JsonValue.NewObject();
doc.Set("device", "boiler-1");
doc.Set("temp", 21.5);
doc.Set("ok", true);
JsonValue tags = JsonValue.NewArray();
tags.Add(JsonValue.FromString("iot"));
doc.Set("tags", tags);

string text = doc.ToJson();          // {"device":"boiler-1","temp":21.5,...}
JsonValue parsed = Json.Parse(text);
double t = parsed.Get("temp").AsDouble;
int n = parsed.Get("tags").Count;    // .At(i) for elements
```

Accessors: `AsInt/AsLong/AsDouble/AsBool/AsString`, `Get(key)/Has(key)`,
`At(index)/Count`, `IsNull`. Missing keys return a Null node — chainable
without null checks.

## XML

Namespace-free subset good for configuration and UI markup: elements,
attributes, text, self-closing tags, comments and `<?...?>` declarations.

```csharp
XmlNode root = Xml.Parse("<config interval=\"30\"><name>boiler</name></config>");
string every = root.GetAttr("interval");
string name = root.Child("name").Text;
root.SetAttr("interval", "60");
string back = root.ToXml();
```

## Binary

Tag-based binary encoding of the same `JsonValue` documents — roughly half
the JSON size, no text parsing on the receive path. Ideal for CAN/MQTT
payloads and `ExtMemory`/flash records.

```csharp
byte[] wire = BinarySerializer.Serialize(doc);
JsonValue round = BinarySerializer.Deserialize(wire);
```

## Streams (`RustNet.IO`)

`MemoryStream` (growable, `Read/Write/Seek/ToArray`), `FileStream`
(buffered over the device VFS, `Flush/Close` persist), and `BinaryPacker`
(little-endian ints/shorts/length-prefixed strings over a MemoryStream)
compose with the serializers:

```csharp
var fs = RustNet.IO.FileStream.Create("/data/state.bin");
fs.Write(BinarySerializer.Serialize(doc));
fs.Close();
```
