# Jack The Code Bender — the Designer's code assistant

A chat panel inside [RustNet.Designer](designer.md) that designs screens and
writes app code **into the tool** rather than into a reply: ask for a dashboard
and it appears on the canvas; ask for the loop behind it and it appears in the
code pane.

Built on **Semantic Kernel**, with a choice of OpenAI, Anthropic, Gemini or a
local Ollama. Everything configurable lives in
`dotnet/tools/RustNet.Designer/App.config`.

```bash
dotnet run --project dotnet/tools/RustNet.Designer
```

Press **ASSISTANT** in the command strip (or `Ctrl+J`) to show and hide the
panel.

## Setting it up

The panel is inert until a provider has credentials. `App.config` is in the
repository, so it ships nothing but `${ENV_VAR}` placeholders, resolved at
startup:

```xml
<add key="Assistant.OpenAI.ApiKey" value="${OPENAI_API_KEY}" />
```

Real keys go in one of two places, neither of them tracked:

1. **The environment** — export `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`,
   `GEMINI_API_KEY`, `TAVILY_API_KEY` and leave the placeholders alone.
2. **`rustnet-designer.secrets.config`** — copy the `.example` beside it, fill in
   the keys you have, and they win over the placeholders.
   `<appSettings file="rustnet-designer.secrets.config">` in `App.config` pulls it
   in, and `*.secrets.config` is gitignored.

Nothing writes a resolved key back into `App.config` — saving settings from the
panel leaves `${...}` untouched. Keep it that way: a key pasted into `App.config`
is one commit away from being permanent.

| Key | What it does |
|---|---|
| `Assistant.Provider` | `OpenAI` \| `Anthropic` \| `Gemini` \| `Ollama` |
| `Assistant.Persona` | the system prompt; `\n` is honoured, since XML attributes collapse real newlines |
| `Assistant.Temperature`, `.TopP`, `.MaxTokens` | sampling |
| `Assistant.Streaming` | stream the reply as it is written |
| `Assistant.HistoryTurns` | how many past messages are replayed each turn (`0` = all) |
| `Assistant.<Provider>.Model` / `.Models` | the current model, and the picker's choices |
| `Assistant.<Provider>.Endpoint` | OpenAI-compatible gateway, or the Ollama daemon |
| `Assistant.DataDirectory` | sessions + uploads; blank = `%APPDATA%\RustNet\Designer\assistant` |
| `Assistant.WorkspaceRoot` | the checkout the docs/template functions read; blank = `$(RUSTNET_SDK)`, else auto-detected |
| `Tools.Enabled` | let the model call functions at all |
| `Tools.Tavily.ApiKey` | internet search ([tavily.com](https://tavily.com)); without it search reports itself unavailable instead of failing the turn |
| `Assistant.<Provider>.ApiKey` | credentials — see above; `${ENV_VAR}` or the secrets file |
| `Tools.Http.MaxChars` | cap on what one fetched page or file may add to the context |
| `Attachments.MaxImageBytes`, `.MaxDocumentChars` | upload limits |

The panel's **SETTINGS** drawer edits the persona, sampling, streaming and the
model, and writes them back to `App.config` — so the file stays the source of
truth rather than a one-time seed. In a `dotnet run` session that file is
`bin/Debug/net10.0-windows/rustnet-designer.dll.config`; copy changes back to
`App.config` to keep them.

### Providers

OpenAI, Gemini and Ollama use their first-party Semantic Kernel connectors.
Anthropic has no SK connector, so `Anthropic.SDK`'s `IChatClient` is bridged in
with `AsChatCompletionService()` and `UseFunctionInvocation()` supplies the tool
loop the other connectors run internally. All four are covered by
`--selftest`, which builds a kernel per provider with placeholder credentials —
so a connector API change shows up there rather than in your first chat.

Ollama needs no key, only a reachable daemon:

```xml
<add key="Assistant.Provider" value="Ollama" />
<add key="Assistant.Ollama.Endpoint" value="http://localhost:11434" />
<add key="Assistant.Ollama.Model" value="qwen2.5-coder:14b" />
```

## What it can do to the Designer

The assistant reaches the editor through one narrow interface
(`IDesignerBridge`), so a tool call can do these things and nothing else:

| Function | Effect |
|---|---|
| `get_current_layout` | reads the canvas as RustNet.UI XML |
| `describe_panel` | panel size, characters per line per text scale, current selection |
| `validate_layout_xml` | parses without applying; reports the outline, plus warnings for text wider than the panel and `x`/`y` a layout container will ignore |
| `apply_layout_xml` | replaces the canvas — this is how a generated screen appears |
| `set_generated_code` / `get_generated_code` | the code pane |
| `rgb565` / `rgb565_from_hex` | colour conversion, reporting the quantised colour the panel will really show |

Grounding functions, so the model works from the contracts instead of memory:
`get_ui_reference` (every element kind and attribute), `get_graphics_reference`
(which `Display` calls are native intrinsics and which are managed helpers),
`get_language_limits` (what the interpreter accepts, and the traps: untyped catch
clauses, partial reflection, same-frame `ref`).

Workspace functions, read-only and confined to `Assistant.WorkspaceRoot`:
`list_rustnet_docs`, `read_rustnet_doc`, `list_templates`, `read_template`,
`find_managed_api` (greps `dotnet/src` for a real signature before the model
calls something that does not exist), `list_attachments`, `read_attachment`.
`save_generated_file` is the only writer and can only write under
`<DataDirectory>/generated`.

Plus `search_web`, `fetch_page`, `fetch_file`, `calculate`, `statistics`,
`convert_base`, `get_current_datetime`, `date_add`, `date_difference`,
`format_duration`.

Every function the model calls appears as a pill above the reply, in call order.

## Sessions

Conversations are independent and kept as one JSON file each under
`<DataDirectory>/sessions`, with their uploads beside them in
`<DataDirectory>/attachments/<sessionId>`.

- **New session** starts an empty one; it names itself from your first message.
- **Reset** empties a session in place — same id, same position in the list.
- **Delete** removes the session and its uploads.

## Attachments

**Image** uploads are copied into the session folder and travel to the model as
image content — the bytes, not a link, because a hosted model cannot open a path
on this machine. **Document** uploads are copied too, and the message carries
their link plus an inlined text excerpt; the model pulls the rest with
`read_attachment` when it needs it. Binary types it cannot read are attached as a
link and labelled as such rather than silently ignored.

Two prompts in the gallery are built for this: *Rebuild this from a screenshot*
and *Turn this sketch into a layout*.

## The transcript

Markdown is rendered to HTML by Markdig and shown in WebView2: pipe tables,
images, `<video>`/`<audio>` for media links, block quotes, and fenced code in a
card with a language chip and its own actions —

- **Apply to canvas**, offered only for an ```xml block that starts with
  `<window`
- **Send to code pane**
- **Copy**

The page is served from a virtual host mapped to the data directory rather than
pushed in as a string, which is what gives it a real `https` origin: without one,
attached images cannot load and the clipboard API is unavailable. Links open in
your browser, not in the panel.

If the WebView2 runtime is missing the panel falls back to a plain-text
transcript and says so, rather than failing to open.

## Asking without the window

One turn, on stdout, no UI:

```bash
rustnet-designer --ask "Design a 320x240 boiler dashboard, then apply it."
rustnet-designer --ask "What is RGB565?" --no-tools
rustnet-designer --ask "Adapt this to 160x128" --layout ui.xml --provider OpenAI --model gpt-4o
```

The reply streams as it arrives, each function call is announced as
`[calls <name>]`, and afterwards whatever the assistant did to the canvas or the
code pane is printed — the designer it talks to is a console stand-in, so
`apply_layout_xml` prints the layout instead of drawing it. `--provider`,
`--model` and `--no-tools` override `App.config` for the one run, and the session
is thrown away rather than added to the panel's list.

This is how the model path gets exercised: streaming, tool calling and search all
need credentials, so they cannot be covered by `--selftest`.

## Prompt gallery

**Prompts** in the composer opens 41 ready-to-run prompts across Dashboards,
Controls, Instruments, Status, Data, Backend, Composition and Research. They are
written to produce an artefact — each names the panel size, says what is on
screen, and ends by telling the assistant to apply the result — because a prompt
that asks for "a nice dashboard" gets a paragraph back instead of a layout.
Clicking one drops it in the composer to edit before sending.

## Verified path

`rustnet-designer --selftest` (exit code 0, no display needed) covers the
assistant headlessly: settings parsing,
session create/save/load/reset/delete, uploads (classification, excerpts,
same-name collisions, the size cap), markdown and code rendering including the
base64 code payload, the expression evaluator, HTML-to-text, the design functions
against a fake designer, that every `[KernelFunction]` builds a valid schema with
no duplicate names, that a kernel builds for all four providers with plugins
attached, the C# and XML formatters, and that a layout survives `ToXml` →
`LoadXml` with its padding, gap, orientation, border and radio group intact.

A live turn cannot be covered there — it needs credentials — so it is exercised
with `--ask`. Against OpenAI `gpt-4o` and Tavily, all of this has been run for
real: streaming, `get_ui_reference` → `validate_layout_xml` → `apply_layout_xml`
putting a generated dashboard on the canvas, `search_web` returning a cited
datasheet fact alongside `calculate`, and `find_managed_api` +
`read_rustnet_doc` + `set_generated_code` filling the code pane.
