using Microsoft.AspNetCore.Components;
using Rustzor.Interop;

namespace Rustzor.Browser.Video;

/// <summary>Controls a &lt;video&gt; (or &lt;audio&gt;) element captured with <c>@ref</c>.</summary>
public sealed class VideoPlayer
{
    private readonly ElementReference _element;

    public VideoPlayer(ElementReference element) => _element = element;

    public Task PlayAsync() => Js.CallAsync("video.play", Js.Args("el", _element.Id));

    public void Pause() => Js.Send("video.do", Js.Args("el", _element.Id, "action", "pause"));

    public double CurrentTime
    {
        get => State()["time"].AsDouble();
        set => Js.Send("video.set", Js.Args("el", _element.Id, "prop", "currentTime", "value", value));
    }

    public double Duration => State()["duration"].AsDouble();

    public bool Paused => State()["paused"].AsBool();

    public double Volume
    {
        get => State()["volume"].AsDouble();
        set => Js.Send("video.set", Js.Args("el", _element.Id, "prop", "volume", "value", value));
    }

    public bool Muted
    {
        set => Js.Send("video.set", Js.Args("el", _element.Id, "prop", "muted", "value", value));
    }

    public double PlaybackRate
    {
        set => Js.Send("video.set", Js.Args("el", _element.Id, "prop", "playbackRate", "value", value));
    }

    public void Load(string url) => Js.Send("video.set", Js.Args("el", _element.Id, "prop", "src", "value", url));

    public Task EnterPictureInPictureAsync() => Js.CallAsync("video.pip", Js.Args("el", _element.Id));

    /// <summary>The current frame as an image data URL (a snapshot or thumbnail).</summary>
    public string CaptureFrame(string type = "image/jpeg") => Js.Call("video.frame", Js.Args("el", _element.Id, "type", type)).AsString();

    /// <summary>Progress events while playing: current time and duration.</summary>
    public IDisposable OnTimeUpdate(Action<double, double> handler)
    {
        var id = Callbacks.Register(v => handler(v["time"].AsDouble(), v["duration"].AsDouble()));
        var token = Js.Call("video.onTime", Js.Args("el", _element.Id, "id", id)).AsInt();
        return new Off(id, token);
    }

    private JsonValue State() => Js.Call("video.state", Js.Args("el", _element.Id));

    private sealed class Off : IDisposable
    {
        private readonly int _id;
        private int _token;

        public Off(int id, int token)
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
