# RustzorAppGen

RustzorAppGen (`tools/RustzorAppGen`) is a desktop code editor, built with Avalonia, for making Rustzor apps with an AI assistant: **Jack - The Code Bender**.

![RustzorAppGen with Jack](../images/appgen-jack.png)

## What it has

- A VS Code-like layout: file explorer, code editor with line numbers and highlighting, logs panel and status bar.
- Menu and toolbar: New Project (blank or from a template), Open Project/File, Close Project, Go To Line, Format Code, Build, Run, Deploy, Exit.
- A chat panel with Jack: pick the model, attach images, `Ctrl+Enter` to send, clear the thread; the panel can be resized and hidden.
- A settings dialog for the AI provider, model, API key, endpoint, temperature and system prompt, saved in `app.config`.

## Jack

Jack works through tools (Semantic Kernel functions): it creates projects from templates; lists, reads, writes, edits, deletes and searches the project's files; looks up Rustzor guides and library APIs; adds packages; builds the project and reads the compiler's errors to fix what it wrote; and runs the app. It can also search the web, read a page, and do small utility work (dates and time zones, unit conversion, JSON formatting, colour palettes, hashing). It knows Rustzor's rules (what the runtime supports, how async handlers and route parameters behave) from a built-in knowledge file, so its code targets Rustzor rather than generic Blazor.

Providers: OpenAI, Azure OpenAI, Anthropic Claude, Google Gemini and Ollama (local).

![An app Jack built from a prompt](../images/appgen-jack-result.png)

## Templates

New Project offers 21 templates, each a complete app that builds and runs:

| Group | Templates |
| --- | --- |
| Games | arcade-breakout, space-dodger, physics-sandbox |
| Science | lorenz-explorer, stats-lab, periodic-table |
| Work | kanban-board, invoice-maker, crm-lite |
| Industry | machine-monitor, warehouse-inventory |
| Education | quiz-master, math-flashcards |
| Simulators | epidemic-sir, solar-system |
| Tools | markdown-notes, unit-converter, pomodoro-timer |
| AI | ai-chat |
| Data | sales-dashboard |
| | blank |

`tools/RustzorAppGen/scripts/test-templates.sh` generates every template, builds it and opens it in headless Chrome; all 21 pass.

## Run it

```bash
dotnet run --project tools/RustzorAppGen
```

Put an API key for one provider in Settings (or `app.config`), pick a template or a blank project, and ask Jack for what you want.
