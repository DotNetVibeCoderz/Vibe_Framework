import * as vscode from "vscode";
import * as cp from "child_process";
import * as path from "path";
import * as fs from "fs";

let logChannel: vscode.OutputChannel;
let followTimer: NodeJS.Timeout | undefined;
let lastLogs = "";

function config<T>(key: string, fallback: T): T {
    return vscode.workspace.getConfiguration("rustnet").get<T>(key) ?? fallback;
}

function cli(): string {
    return config("cliPath", "rustnet");
}

function deviceArgs(): string[] {
    return ["--device", config("device", "tcp:127.0.0.1:7878")];
}

function runCli(args: string[], cwd?: string): Promise<{ ok: boolean; output: string }> {
    return new Promise((resolve) => {
        cp.execFile(cli(), args, { cwd, maxBuffer: 16 * 1024 * 1024 }, (err, stdout, stderr) => {
            resolve({ ok: !err, output: (stdout ?? "") + (stderr ?? "") });
        });
    });
}

async function showResult(title: string, args: string[]): Promise<void> {
    const { ok, output } = await runCli([...args, ...deviceArgs()]);
    logChannel.appendLine(`--- ${title} ---`);
    logChannel.appendLine(output.trim());
    logChannel.show(true);
    if (!ok) {
        vscode.window.showErrorMessage(`RustNet: ${title} failed — see output`);
    }
}

function findProjectDll(): string | undefined {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        return undefined;
    }
    const root = folders[0].uri.fsPath;
    const projName = path.basename(root);
    for (const cfg of ["Debug", "Release"]) {
        const candidate = path.join(root, "bin", cfg, "net10.0", `${projName}.dll`);
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }
    return undefined;
}

export function activate(context: vscode.ExtensionContext): void {
    logChannel = vscode.window.createOutputChannel("RustNet");

    // Debugger: bridge VSCode DAP to the on-device interpreter via the
    // rustnet-debugger adapter (over RNDP). Fills in device/key/program from
    // the RustNet settings when the launch config omits them.
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory("rustnet", {
            createDebugAdapterDescriptor() {
                const adapter = config<string>("debuggerPath", "rustnet-debugger");
                return adapter.toLowerCase().endsWith(".dll")
                    ? new vscode.DebugAdapterExecutable("dotnet", [adapter])
                    : new vscode.DebugAdapterExecutable(adapter, []);
            },
        }),
        vscode.debug.registerDebugConfigurationProvider("rustnet", {
            resolveDebugConfiguration(_folder, cfg) {
                cfg.device = cfg.device ?? config("device", "tcp:127.0.0.1:7878");
                cfg.key = cfg.key || config("signingKey", "");
                cfg.program = cfg.program ?? findProjectDll();
                return cfg;
            },
        }),
    );

    const register = (id: string, handler: () => Promise<void> | void) =>
        context.subscriptions.push(vscode.commands.registerCommand(id, handler));

    register("rustnet.deviceInfo", () => showResult("device info", ["info"]));
    register("rustnet.listApps", () => showResult("apps", ["apps", "list"]));
    register("rustnet.stopApp", () => showResult("stop", ["apps", "stop"]));
    register("rustnet.profile", () => showResult("profiler", ["profile"]));
    register("rustnet.showLogs", () => showResult("logs", ["logs", "-n", "200"]));

    register("rustnet.startApp", async () => {
        const name = await vscode.window.showInputBox({ prompt: "App name to start" });
        if (name) {
            await showResult(`start ${name}`, ["apps", "start", name]);
        }
    });

    register("rustnet.eraseApp", async () => {
        const name = await vscode.window.showInputBox({ prompt: "App name to erase" });
        if (name) {
            await showResult(`erase ${name}`, ["apps", "erase", name]);
        }
    });

    register("rustnet.flashApp", async () => {
        const dll = findProjectDll();
        if (!dll) {
            vscode.window.showErrorMessage(
                "RustNet: build the project first (bin/Debug/net10.0/<Project>.dll not found)");
            return;
        }
        const key = config("signingKey", "");
        if (!key) {
            vscode.window.showErrorMessage(
                "RustNet: set rustnet.signingKey in settings (generate with 'rustnet keys generate')");
            return;
        }
        const name = path.basename(dll, ".dll").toLowerCase();
        await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: `RustNet: flashing ${name}...` },
            () => showResult(`flash ${name}`, ["flash", dll, "--name", name, "--key", key, "--start"]));
    });

    register("rustnet.followLogs", () => {
        if (followTimer) {
            clearInterval(followTimer);
            followTimer = undefined;
            vscode.window.showInformationMessage("RustNet: stopped following logs");
            return;
        }
        logChannel.show(true);
        vscode.window.showInformationMessage("RustNet: following logs (run again to stop)");
        followTimer = setInterval(async () => {
            const { ok, output } = await runCli(["logs", "-n", "500", ...deviceArgs()]);
            if (ok && output !== lastLogs) {
                const fresh = output.startsWith(lastLogs) ? output.slice(lastLogs.length) : output;
                if (fresh.trim().length > 0) {
                    logChannel.append(fresh.startsWith("\n") ? fresh.slice(1) : fresh);
                }
                lastLogs = output;
            }
        }, 1000);
    });

    register("rustnet.captureDisplay", async () => {
        const folders = vscode.workspace.workspaceFolders;
        const outPath = path.join(folders?.[0]?.uri.fsPath ?? process.cwd(), "display.ppm");
        await showResult("display capture", ["display", "capture", "-o", outPath]);
        vscode.window.showInformationMessage(`RustNet: display captured to ${outPath}`);
    });

    register("rustnet.startVirtualDevice", () => {
        const terminal = vscode.window.createTerminal("RustNet Virtual Device");
        terminal.sendText(`${cli()} firmware run`);
        terminal.show();
    });

    register("rustnet.openSimulator", () => openSimulatorPanel(context));

    register("rustnet.newProject", async () => {
        const template = await vscode.window.showQuickPick(
            ["sensor-logger", "weather-check", "calculator", "xox-game",
             "display-testing", "filesystem-test", "wifi-mqtt",
             "datalogger-db", "can-gateway", "ui-dashboard", "cloud-telemetry", "image-viewer"],
            { placeHolder: "Choose a RustNet template" });
        if (!template) {
            return;
        }
        const name = await vscode.window.showInputBox({ prompt: "Project name" });
        if (!name) {
            return;
        }
        const cwd = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        const { ok, output } = await runCli(["new", template, name], cwd);
        logChannel.appendLine(output.trim());
        if (ok) {
            vscode.window.showInformationMessage(`RustNet: created ${name} from ${template}`);
        } else {
            vscode.window.showErrorMessage("RustNet: project creation failed — see output");
            logChannel.show(true);
        }
    });
}

// ---------------------------------------------------------------------
// Simulator panel: live view of the virtual device — display framebuffer,
// GPIO pins, bus/netif state and the log tail — polled through the CLI.
// ---------------------------------------------------------------------

let simPanel: vscode.WebviewPanel | undefined;
let simTimer: NodeJS.Timeout | undefined;

function parsePpm(buf: Buffer): { width: number; height: number; rgba: number[] } | undefined {
    // P6\n<w> <h>\n255\n<binary rgb>
    const text = buf.toString("latin1", 0, Math.min(64, buf.length));
    const m = /^P6\s+(\d+)\s+(\d+)\s+255\s/.exec(text);
    if (!m) {
        return undefined;
    }
    const width = parseInt(m[1], 10);
    const height = parseInt(m[2], 10);
    const start = m[0].length;
    const rgba: number[] = new Array(width * height * 4);
    for (let i = 0; i < width * height; i++) {
        rgba[i * 4] = buf[start + i * 3];
        rgba[i * 4 + 1] = buf[start + i * 3 + 1];
        rgba[i * 4 + 2] = buf[start + i * 3 + 2];
        rgba[i * 4 + 3] = 255;
    }
    return { width, height, rgba };
}

async function pollSimulator(tmpPpm: string): Promise<void> {
    if (!simPanel) {
        return;
    }
    const [io, logs] = await Promise.all([
        runCli(["io", ...deviceArgs()]),
        runCli(["logs", "-n", "40", ...deviceArgs()]),
    ]);
    let display: { width: number; height: number; rgba: number[] } | undefined;
    const cap = await runCli(["display", "capture", "-o", tmpPpm, ...deviceArgs()]);
    if (cap.ok && fs.existsSync(tmpPpm)) {
        display = parsePpm(fs.readFileSync(tmpPpm));
    }
    let ioState: unknown = undefined;
    try {
        ioState = JSON.parse(io.output.trim());
    } catch {
        // device offline or busy — panel shows the last state
    }
    simPanel.webview.postMessage({
        io: ioState,
        logs: logs.ok ? logs.output.split("\n").slice(-40).join("\n") : "(device offline)",
        display,
    });
}

function openSimulatorPanel(context: vscode.ExtensionContext): void {
    if (simPanel) {
        simPanel.reveal();
        return;
    }
    simPanel = vscode.window.createWebviewPanel(
        "rustnetSimulator", "RustNet Simulator", vscode.ViewColumn.Beside,
        { enableScripts: true, retainContextWhenHidden: true });
    simPanel.webview.html = simulatorHtml();
    const tmpPpm = path.join(context.globalStorageUri?.fsPath ?? require("os").tmpdir(), "rustnet-sim.ppm");
    fs.mkdirSync(path.dirname(tmpPpm), { recursive: true });
    simTimer = setInterval(() => void pollSimulator(tmpPpm), 1000);
    simPanel.onDidDispose(() => {
        if (simTimer) {
            clearInterval(simTimer);
            simTimer = undefined;
        }
        simPanel = undefined;
    });
}

function simulatorHtml(): string {
    return `<!DOCTYPE html>
<html>
<head>
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground);
         background: var(--vscode-editor-background); padding: 10px; }
  h3 { margin: 12px 0 6px 0; }
  #display { image-rendering: pixelated; border: 1px solid #666; background: #000; }
  .pins { display: grid; grid-template-columns: repeat(12, 26px); gap: 4px; }
  .pin { width: 24px; height: 24px; border-radius: 12px; text-align: center;
         line-height: 24px; font-size: 9px; background: #333; color: #bbb; }
  .pin.high { background: #2f9e44; color: #fff; }
  table { border-collapse: collapse; font-size: 12px; }
  td, th { border: 1px solid #555; padding: 2px 8px; text-align: left; }
  #logs { font-family: var(--vscode-editor-font-family); font-size: 11px;
          white-space: pre; overflow-x: auto; background: #1a1a1a; color: #9cdcfe;
          padding: 6px; height: 220px; overflow-y: scroll; }
</style>
</head>
<body>
  <h3>Display</h3>
  <canvas id="display" width="160" height="128" style="width:320px"></canvas>
  <h3>GPIO</h3>
  <div class="pins" id="pins"></div>
  <h3>Network &amp; Buses</h3>
  <table id="net"><tr><th>interface</th><th>up</th><th>ip</th></tr></table>
  <div id="misc" style="margin-top:6px;font-size:12px"></div>
  <h3>Logs</h3>
  <div id="logs">(waiting for device...)</div>
<script>
  const pinsDiv = document.getElementById('pins');
  for (let i = 0; i < 24; i++) {
    const d = document.createElement('div');
    d.className = 'pin';
    d.id = 'pin' + i;
    d.textContent = i;
    pinsDiv.appendChild(d);
  }
  window.addEventListener('message', (event) => {
    const { io, logs, display } = event.data;
    if (display) {
      const canvas = document.getElementById('display');
      canvas.width = display.width;
      canvas.height = display.height;
      canvas.style.width = (display.width * 2) + 'px';
      const ctx = canvas.getContext('2d');
      const img = ctx.createImageData(display.width, display.height);
      img.data.set(display.rgba);
      ctx.putImageData(img, 0, 0);
    }
    if (io) {
      (io.pins || []).forEach((v, i) => {
        const el = document.getElementById('pin' + i);
        if (el) { el.className = v ? 'pin high' : 'pin'; }
      });
      const net = document.getElementById('net');
      net.innerHTML = '<tr><th>interface</th><th>up</th><th>ip</th></tr>' +
        (io.netifs || []).map(n =>
          '<tr><td>' + n.kind + '</td><td>' + (n.up ? 'up' : 'down') + '</td><td>' +
          (n.ip || '-') + '</td></tr>').join('');
      const wd = io.watchdog || {};
      document.getElementById('misc').textContent =
        'CAN rx pending: ' + (io.can_rx || []).join(', ') +
        '  |  watchdog: ' + (wd.running ? ('running (' + wd.timeout_ms + 'ms)') : 'stopped');
    }
    if (logs) {
      const el = document.getElementById('logs');
      el.textContent = logs;
      el.scrollTop = el.scrollHeight;
    }
  });
</script>
</body>
</html>`;
}

export function deactivate(): void {
    if (followTimer) {
        clearInterval(followTimer);
    }
    if (simTimer) {
        clearInterval(simTimer);
    }
}
