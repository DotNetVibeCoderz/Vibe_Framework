using System.Collections.Generic;
using System.Text;
using RustNet.Designer.Assistant;

namespace RustNet.Designer.Avalonia;

/// <summary>
/// A conversation as one markdown document.
/// </summary>
/// <remarks>
/// <para>
/// The WPF panel rendered each message to HTML and pushed it into a WebView2,
/// appending with JavaScript so a streaming reply did not redraw the page.
/// There is no browser here: Markdown.Avalonia turns markdown into real
/// controls, and it takes the text of a whole document.
/// </para>
/// <para>
/// So the transcript is a document rather than a list of bubbles, and the cost
/// moves: nothing has to marshal HTML across a bridge, but a streaming reply
/// re-parses the conversation on every flush. That is bounded by the 90 ms
/// flush interval and by sessions being per-topic; a conversation long enough
/// to feel it is one that wanted splitting anyway.
/// </para>
/// </remarks>
internal static class TranscriptMarkdown
{
    /// <summary>The finished messages, plus an optional reply still arriving.</summary>
    public static string Document(
        IEnumerable<ChatMessage> messages,
        string? liveText = null,
        IReadOnlyList<string>? liveTools = null)
    {
        var sb = new StringBuilder();
        bool any = false;

        foreach (ChatMessage m in messages)
        {
            any = true;
            AppendMessage(sb, m);
        }

        if (liveText != null || (liveTools is { Count: > 0 }))
        {
            any = true;
            sb.Append("### JACK\n\n");
            if (liveTools is { Count: > 0 })
            {
                sb.Append("`").Append(string.Join("` `", liveTools)).Append("`\n\n");
            }
            sb.Append(liveText ?? "");
            // A caret while the answer is still arriving, so a pause reads as
            // "still going" rather than "finished, and that was all".
            sb.Append(" ▌\n\n");
        }

        return any ? sb.ToString() : EmptyState();
    }

    private static void AppendMessage(StringBuilder sb, ChatMessage m)
    {
        sb.Append(m.Role == ChatRole.User ? "### YOU\n\n" : "### JACK\n\n");

        if (m.ToolCalls.Count > 0)
        {
            sb.Append("`").Append(string.Join("` `", m.ToolCalls)).Append("`\n\n");
        }

        foreach (ChatAttachment a in m.Attachments)
        {
            sb.Append(a.Kind == AttachmentKind.Image ? "!" : "")
              .Append('[').Append(a.FileName).Append("](").Append(a.WebUrl).Append(")\n\n");
        }

        sb.Append(m.Text.Length > 0 ? m.Text : "_(no text)_").Append("\n\n");

        if (m.IsError)
        {
            sb.Append("> That turn failed.\n\n");
        }
        sb.Append("---\n\n");
    }

    /// <summary>What the panel says before anything has been asked.</summary>
    public static string EmptyState() =>
        """
        ## Jack The Code Bender

        Ask for a screen and it lands on your canvas; ask for app code and it
        lands in the code pane.

        - "Design a 320x240 boiler dashboard with flow, return and burner load, then apply it."
        - "Critique the layout on my canvas and apply a better version."
        - "Write the MQTT loop for this screen."

        Press **Prompts** for a gallery of these.
        """;

    /// <summary>The same, with a line about which service is wired up.</summary>
    public static string EmptyState(string provider, string model, bool configured)
    {
        string keyLine = configured
            ? $"Wired to `{provider}` / `{model}`."
            : $"`{provider}` has no API key yet. Put one in `Assistant.{provider}.ApiKey` in "
              + "app.config, or export the environment variable the placeholder names.";
        return EmptyState().Replace("## Jack The Code Bender\n", "## Jack The Code Bender\n\n" + keyLine + "\n");
    }
}
