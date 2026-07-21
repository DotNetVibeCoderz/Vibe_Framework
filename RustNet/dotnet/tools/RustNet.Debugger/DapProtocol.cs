using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RustNet.Debugger;

/// <summary>
/// Debug Adapter Protocol transport: reads and writes `Content-Length`-framed
/// JSON messages (the wire format VSCode speaks to a debug adapter). Messages
/// are exposed as <see cref="JsonObject"/> so the session can handle them
/// dynamically. Writes are serialized behind a lock so the event poller and the
/// request handler can both emit safely.
/// </summary>
public sealed class DapProtocol(Stream input, Stream output)
{
    private readonly object _writeLock = new();
    private int _seq;

    /// <summary>Read one DAP message, or null at end of stream.</summary>
    public JsonObject? Read()
    {
        int? length = ReadContentLength();
        if (length is null)
        {
            return null;
        }
        var buf = new byte[length.Value];
        int read = 0;
        while (read < buf.Length)
        {
            int n = input.Read(buf, read, buf.Length - read);
            if (n <= 0)
            {
                return null;
            }
            read += n;
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(buf)) as JsonObject;
    }

    private int? ReadContentLength()
    {
        // Headers are ASCII lines terminated by CRLF, ending with a blank line.
        int contentLength = -1;
        var line = new StringBuilder();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0)
            {
                return null;
            }
            if (b == '\r')
            {
                continue;
            }
            if (b == '\n')
            {
                if (line.Length == 0)
                {
                    return contentLength >= 0 ? contentLength : null;
                }
                string header = line.ToString();
                const string prefix = "Content-Length:";
                if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(header[prefix.Length..].Trim(), out int len))
                {
                    contentLength = len;
                }
                line.Clear();
            }
            else
            {
                line.Append((char)b);
            }
        }
    }

    private void Write(JsonObject message)
    {
        message["seq"] = ++_seq;
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }

    public void SendResponse(JsonObject request, JsonObject? body = null, bool success = true, string? message = null)
    {
        var resp = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.GetValue<int>() ?? 0,
            ["success"] = success,
            ["command"] = request["command"]?.GetValue<string>() ?? "",
        };
        if (message is not null)
        {
            resp["message"] = message;
        }
        if (body is not null)
        {
            resp["body"] = body;
        }
        Write(resp);
    }

    public void SendEvent(string eventName, JsonObject? body = null)
    {
        var evt = new JsonObject
        {
            ["type"] = "event",
            ["event"] = eventName,
        };
        if (body is not null)
        {
            evt["body"] = body;
        }
        Write(evt);
    }

    /// <summary>Emit an OutputEvent (shows in the Debug Console).</summary>
    public void Output(string text, string category = "console") =>
        SendEvent("output", new JsonObject { ["category"] = category, ["output"] = text });
}
