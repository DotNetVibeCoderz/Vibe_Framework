using Rustzor.Interop;

namespace Rustzor.Browser.Fetch;

/// <summary>An HTTP response from the browser's <c>fetch</c>.</summary>
public sealed class FetchResponse
{
    public int Status { get; init; }
    public string StatusText { get; init; } = "";
    public bool Ok => Status >= 200 && Status < 300;
    public string Url { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new();
    public string Text { get; init; } = "";

    /// <summary>The body parsed as JSON.</summary>
    public JsonValue Json() => JsonValue.Parse(Text);

    public T As<T>() where T : new() => Interop.Json.Deserialize<T>(Text);

    public List<T> AsList<T>() => Interop.Json.DeserializeList<T>(Text);

    /// <summary>Throws <see cref="FetchException"/> unless the status is 2xx.</summary>
    public FetchResponse EnsureSuccess()
    {
        if (!Ok) throw new FetchException(this);
        return this;
    }
}

public sealed class FetchException : Exception
{
    public FetchException(FetchResponse response) : base($"HTTP {response.Status} {response.StatusText} from {response.Url}") =>
        Response = response;

    public FetchResponse Response { get; }
}

/// <summary>A request: method, headers, body, and how long to wait.</summary>
public sealed class FetchRequest
{
    public FetchRequest(string url, string method = "GET")
    {
        Url = url;
        Method = method;
    }

    public string Url { get; set; }
    public string Method { get; set; }
    public Dictionary<string, string> Headers { get; } = new();
    public string? Body { get; set; }
    public int TimeoutMs { get; set; }
    /// <summary>"same-origin" (default), "include" or "omit".</summary>
    public string Credentials { get; set; } = "same-origin";

    public FetchRequest WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public FetchRequest WithJson(object? body)
    {
        Body = body as string ?? Interop.Json.Serialize(body);
        Headers["Content-Type"] = "application/json";
        return this;
    }

    public FetchRequest WithBearer(string token) => WithHeader("Authorization", "Bearer " + token);
}

/// <summary>
/// HTTP for Rustzor apps, over the browser's <c>fetch</c>. Register it as a
/// service with a base address, as Blazor apps do with <c>HttpClient</c>.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddSingleton(new FetchClient("https://api.example.com/"));
/// var todos = await Http.GetJsonAsync("todos");
/// </code>
/// </example>
public sealed class FetchClient
{
    public FetchClient(string? baseAddress = null) => BaseAddress = baseAddress ?? "";

    public string BaseAddress { get; set; }
    public Dictionary<string, string> DefaultHeaders { get; } = new();

    /// <summary>Called with every failed request before it is thrown.</summary>
    public Action<FetchResponse>? OnError { get; set; }

    public Task<FetchResponse> GetAsync(string url) => SendAsync(new FetchRequest(url));

    public async Task<string> GetStringAsync(string url) => (await GetAsync(url)).EnsureSuccess().Text;

    public async Task<JsonValue> GetJsonAsync(string url) => (await GetAsync(url)).EnsureSuccess().Json();

    public async Task<T> GetFromJsonAsync<T>(string url) where T : new() => (await GetAsync(url)).EnsureSuccess().As<T>();

    public async Task<List<T>> GetListAsync<T>(string url) => (await GetAsync(url)).EnsureSuccess().AsList<T>();

    public Task<FetchResponse> PostJsonAsync(string url, object? body) => SendAsync(new FetchRequest(url, "POST").WithJson(body));

    public Task<FetchResponse> PutJsonAsync(string url, object? body) => SendAsync(new FetchRequest(url, "PUT").WithJson(body));

    public Task<FetchResponse> PatchJsonAsync(string url, object? body) => SendAsync(new FetchRequest(url, "PATCH").WithJson(body));

    public Task<FetchResponse> DeleteAsync(string url) => SendAsync(new FetchRequest(url, "DELETE"));

    public async Task<FetchResponse> SendAsync(FetchRequest request)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("url", Resolve(request.Url));
        w.Prop("method", request.Method);
        w.Prop("credentials", request.Credentials);
        w.Prop("timeout", request.TimeoutMs);
        w.Name("headers").BeginObject();
        foreach (var kv in DefaultHeaders) w.Prop(kv.Key, kv.Value);
        foreach (var kv in request.Headers) w.Prop(kv.Key, kv.Value);
        w.EndObject();
        w.Prop("body", request.Body);
        w.EndObject();
        var r = await Js.CallAsync("fetch.send", JsonValue.Parse(w.ToString()));
        var headers = new Dictionary<string, string>();
        foreach (var key in r["headers"].Keys) headers[key] = r["headers"][key].AsString();
        var response = new FetchResponse
        {
            Status = r["status"].AsInt(),
            StatusText = r["statusText"].AsString(),
            Url = r["url"].AsString(),
            Headers = headers,
            Text = r["text"].AsString(),
        };
        if (!response.Ok) OnError?.Invoke(response);
        return response;
    }

    /// <summary>
    /// Streams a response body as it arrives, line by line or chunk by chunk —
    /// what a server-sent events or LLM token stream needs.
    /// </summary>
    public Task StreamAsync(FetchRequest request, Action<string> onChunk) => StreamAsync(request, onChunk, default);

    /// <summary>
    /// Streams a response body; cancelling <paramref name="cancellationToken"/> aborts the
    /// request in the browser and fails the task with <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task StreamAsync(FetchRequest request, Action<string> onChunk, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        var id = 0;
        var finished = false;
        id = Callbacks.Register(msg =>
        {
            if (finished) return;
            switch (msg["kind"].AsString())
            {
                case "chunk":
                    onChunk(msg["text"].AsString());
                    break;
                case "done":
                    Callbacks.Unregister(id);
                    tcs.TrySetResult(true);
                    break;
                default:
                    Callbacks.Unregister(id);
                    tcs.TrySetException(new JsException("fetch.stream", msg["error"].AsString()));
                    break;
            }
        });
        var w = new JsonWriter();
        w.BeginObject();
        w.Prop("id", id);
        w.Prop("url", Resolve(request.Url));
        w.Prop("method", request.Method);
        w.Name("headers").BeginObject();
        foreach (var kv in DefaultHeaders) w.Prop(kv.Key, kv.Value);
        foreach (var kv in request.Headers) w.Prop(kv.Key, kv.Value);
        w.EndObject();
        w.Prop("body", request.Body);
        w.EndObject();
        Js.InvokeVoid("fetch.stream", w.ToString());
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (finished || tcs.Task.IsCompleted) return;
                finished = true;
                Callbacks.Unregister(id);
                Js.Send("fetch.abort", Js.Args("id", id));
                tcs.TrySetException(new OperationCanceledException("The request was cancelled."));
            });
        }
        return tcs.Task;
    }

    private string Resolve(string url)
    {
        if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("/") || BaseAddress.Length == 0) return url;
        return BaseAddress.EndsWith("/") ? BaseAddress + url : BaseAddress + "/" + url;
    }
}
