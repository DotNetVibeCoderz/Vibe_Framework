using Rustzor.Interop;

namespace Rustzor.Browser.NFC;

/// <summary>One record read from an NFC tag.</summary>
public sealed record NfcRecord(string RecordType, string MediaType, string Data);

/// <summary>A tag that was read: its serial number and records.</summary>
public sealed record NfcTag(string SerialNumber, List<NfcRecord> Records);

/// <summary>Web NFC (Chrome on Android): read and write NDEF tags.</summary>
public static class Nfc
{
    public static bool IsSupported => Js.Call("nfc.supported").AsBool();

    /// <summary>Starts scanning; every tag brought near is reported until disposed.</summary>
    public static async Task<IDisposable> ScanAsync(Action<NfcTag> onTag, Action<string>? onError = null)
    {
        var id = Callbacks.Register(v =>
        {
            if (v.Has("error"))
            {
                onError?.Invoke(v["error"].AsString());
                return;
            }
            var records = new List<NfcRecord>();
            foreach (var r in v["records"].Items) records.Add(new NfcRecord(r["type"].AsString(), r["mediaType"].AsString(), r["data"].AsString()));
            onTag(new NfcTag(v["serial"].AsString(), records));
        });
        var token = (await Js.CallAsync("nfc.scan", Js.Args("id", id))).AsInt();
        return new Stop(id, token);
    }

    /// <summary>Writes a text record (or a URL record) to the next tag.</summary>
    public static Task WriteTextAsync(string text, bool asUrl = false) => Js.CallAsync("nfc.write", Js.Args("text", text, "url", asUrl));

    private sealed class Stop : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Stop(int id, int token)
        {
            _id = id;
            _token = token;
        }

        public void Dispose()
        {
            if (_token == 0) return;
            Js.Send("js.off", Js.Args("token", _token));
            Callbacks.Unregister(_id);
            _token = 0;
        }
    }
}
