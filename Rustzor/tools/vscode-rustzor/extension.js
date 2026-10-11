// Rustzor for VS Code: commands over the `rustzor` CLI, a serve button, snippets.
// Built by Gravicode Studios, led by Kang Fadhil.
const vscode = require("vscode");
const path = require("path");
const fs = require("fs");
const { execFile } = require("child_process");

const DOCS = "https://github.com/DotNetVibeCoderz/Vibe_Framework/tree/main/Rustzor/docs/en";

// The packages `rustzor add` knows, with what each is for.
const PACKAGES = [
  ["ui", "Rustzor.UI", "Component kit: app shell, data grid, charts, modals, toasts"],
  ["fetch", "Rustzor.Browser.Fetch", "HTTP and JSON with FetchClient"],
  ["storage", "Rustzor.Browser.Storage", "localStorage, sessionStorage, cookies"],
  ["data", "Rustzor.Data", "SQLite, IndexedDB and DuckDB in the browser"],
  ["signalr", "Rustzor.Net.SignalR", "SignalR hub client"],
  ["grpc", "Rustzor.Net.GrpcWeb", "gRPC-Web with a protobuf reader and writer"],
  ["websocket", "Rustzor.Browser.WebSocket", "WebSocket and BroadcastChannel"],
  ["dom", "Rustzor.Browser.DOM", "Document, window, keyboard, clipboard, print"],
  ["files", "Rustzor.Browser.FileSystem", "Open and save files, drag and drop, OPFS"],
  ["notifications", "Rustzor.Browser.Notifications", "Notifications, vibration, app badge"],
  ["workers", "Rustzor.Browser.Workers", "Web workers, service workers, PWA install"],
  ["location", "Rustzor.Browser.Location", "Geolocation"],
  ["media", "Rustzor.Browser.Media", "Camera, microphone, screen capture, recording"],
  ["audio", "Rustzor.Browser.Audio", "Tones, sounds, music, speech"],
  ["canvas", "Rustzor.Browser.Canvas", "2D canvas drawing"],
  ["webgl", "Rustzor.Browser.WebGL", "WebGL 2"],
  ["webgpu", "Rustzor.Browser.WebGPU", "WebGPU compute and shader canvases"],
  ["webrtc", "Rustzor.Browser.WebRTC", "Peer-to-peer media and data"],
  ["game", "Rustzor.Game", "Game loop, input, physics, particles"],
  ["graphics", "Rustzor.Graphics", "2D and 3D scene graphs"],
  ["ai", "Rustzor.AI", "Chat models, RAG, embeddings"],
  ["echarts", "Rustzor.Charts.ECharts", "Apache ECharts"],
  ["chartjs", "Rustzor.Charts.ChartJs", "Chart.js"],
  ["d3", "Rustzor.Charts.D3", "D3"],
  ["bootstrap", "Rustzor.UI.Bootstrap", "Bootstrap components"],
  ["tailwind", "Rustzor.UI.Tailwind", "Tailwind in the browser"],
  ["mui", "Rustzor.UI.Mui", "Material UI components"],
  ["sensors", "Rustzor.Browser.Sensors", "Orientation, motion, battery, wake lock"],
  ["bluetooth", "Rustzor.Browser.Bluetooth", "Web Bluetooth"],
  ["usb", "Rustzor.Browser.USB", "WebUSB"],
  ["serial", "Rustzor.Browser.Serial", "Web Serial"],
  ["hid", "Rustzor.Browser.HID", "WebHID"],
  ["nfc", "Rustzor.Browser.NFC", "Web NFC"],
  ["gamepad", "Rustzor.Browser.Gamepad", "Game controllers"],
  ["webxr", "Rustzor.Browser.WebXR", "VR and AR"],
];

let serveTerminal;
let statusItem;

function cli() {
  return vscode.workspace.getConfiguration("rustzor").get("cliPath") || "rustzor";
}

/** Whether a .csproj is a Rustzor app (built with the Rustzor SDK). */
function isRustzorApp(file) {
  try {
    const text = fs.readFileSync(file, "utf8");
    return /Rustzor\.Sdk/.test(text) || /Rustzor\.InRepo/.test(text);
  } catch {
    return false;
  }
}

/** The project a command acts on: the one clicked, the active file's, or a pick. */
async function pickProject(uri) {
  if (uri && uri.fsPath.endsWith(".csproj")) return uri.fsPath;
  const active = vscode.window.activeTextEditor?.document.uri.fsPath;
  if (active) {
    let dir = path.dirname(active);
    for (let i = 0; i < 6; i++) {
      const found = fs.readdirSync(dir).find((f) => f.endsWith(".csproj"));
      if (found) return path.join(dir, found);
      const up = path.dirname(dir);
      if (up === dir) break;
      dir = up;
    }
  }
  const all = (await vscode.workspace.findFiles("**/*.csproj", "**/{bin,obj,node_modules}/**", 200)).map((u) => u.fsPath);
  const apps = all.filter(isRustzorApp);
  const choices = apps.length ? apps : all;
  if (choices.length === 0) {
    vscode.window.showWarningMessage("No .csproj in this workspace. Create one with Rustzor: New Project.");
    return undefined;
  }
  if (choices.length === 1) return choices[0];
  const pick = await vscode.window.showQuickPick(
    choices.map((f) => ({ label: path.basename(f), description: vscode.workspace.asRelativePath(f), file: f })),
    { placeHolder: "Which project?" }
  );
  return pick?.file;
}

function run(name, cwd, command) {
  const terminal = vscode.window.createTerminal({ name, cwd });
  terminal.show();
  terminal.sendText(command);
  return terminal;
}

/** Checks that the CLI runs; offers to install it when it does not. */
function ensureCli() {
  return new Promise((resolve) => {
    execFile(cli(), ["--version"], (error) => {
      if (!error) return resolve(true);
      vscode.window
        .showWarningMessage("The rustzor command was not found. Install it now (dotnet tool install -g Rustzor.Cli)?", "Install", "Cancel")
        .then((answer) => {
          if (answer === "Install") installCli();
          resolve(false);
        });
    });
  });
}

function installCli() {
  run("Rustzor: install", undefined, "dotnet tool update -g Rustzor.Cli --prerelease");
}

async function newProject() {
  if (!(await ensureCli())) return;
  const template = await vscode.window.showQuickPick(
    [
      { label: "App", description: "Rustzor.UI shell with Home, Counter and Tasks pages", value: "app" },
      { label: "Empty", description: "One page, no UI kit", value: "empty" },
      { label: "Library", description: "Components and a browser module for other apps", value: "lib" },
    ],
    { placeHolder: "What kind of project?" }
  );
  if (!template) return;
  const name = await vscode.window.showInputBox({
    prompt: "Project name",
    value: template.value === "lib" ? "MyRustzorLibrary" : "MyRustzorApp",
    validateInput: (v) => (/^[A-Za-z_][A-Za-z0-9_.]*$/.test(v) ? undefined : "Letters, digits, '_' and '.', starting with a letter"),
  });
  if (!name) return;
  const folder = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: "Create here" });
  if (!folder?.length) return;
  const parent = folder[0].fsPath;
  const target = path.join(parent, name);
  execFile(cli(), ["new", template.value, name], { cwd: parent }, async (error, stdout, stderr) => {
    if (error) {
      vscode.window.showErrorMessage(`rustzor new failed: ${(stderr || stdout || error.message).trim()}`);
      return;
    }
    const open = await vscode.window.showInformationMessage(`Created ${name}.`, "Open", "Open in New Window");
    if (open) vscode.commands.executeCommand("vscode.openFolder", vscode.Uri.file(target), open === "Open in New Window");
  });
}

async function serve(uri) {
  const project = await pickProject(uri);
  if (!project || !(await ensureCli())) return;
  serveTerminal?.dispose();
  const cfg = vscode.workspace.getConfiguration("rustzor");
  const open = cfg.get("openBrowser") ? " --open" : "";
  serveTerminal = run("Rustzor: serve", path.dirname(project), `${cli()} serve "${path.basename(project)}" --port ${cfg.get("port")}${open}`);
  updateStatus(true);
}

function stop() {
  serveTerminal?.dispose();
  serveTerminal = undefined;
  updateStatus(false);
}

async function publish(uri, aot) {
  const project = await pickProject(uri);
  if (!project || !(await ensureCli())) return;
  run("Rustzor: publish", path.dirname(project), `${cli()} publish "${path.basename(project)}"${aot ? " --aot" : ""}`);
}

async function addPackage(uri) {
  const project = await pickProject(uri);
  if (!project || !(await ensureCli())) return;
  const picks = await vscode.window.showQuickPick(
    PACKAGES.map(([alias, id, detail]) => ({ label: id, description: alias, detail, alias })),
    { placeHolder: "Add Rustzor packages", canPickMany: true, matchOnDescription: true, matchOnDetail: true }
  );
  if (!picks?.length) return;
  run("Rustzor: add", path.dirname(project), `${cli()} add ${picks.map((p) => p.alias).join(" ")} --project "${path.basename(project)}"`);
}

async function info(uri) {
  const project = await pickProject(uri);
  if (!project || !(await ensureCli())) return;
  run("Rustzor: info", path.dirname(project), `${cli()} info "${path.basename(project)}"`);
}

function updateStatus(serving) {
  if (!statusItem) return;
  statusItem.text = serving ? "$(debug-stop) Rustzor: stop" : "$(play) Rustzor: serve";
  statusItem.command = serving ? "rustzor.stop" : "rustzor.serve";
  statusItem.tooltip = serving ? "Stop the Rustzor development server" : "Build, serve and reload on save";
}

async function activate(context) {
  const reg = (id, fn) => context.subscriptions.push(vscode.commands.registerCommand(id, fn));
  reg("rustzor.newProject", newProject);
  reg("rustzor.serve", serve);
  reg("rustzor.stop", stop);
  reg("rustzor.publish", (uri) => publish(uri, false));
  reg("rustzor.publishAot", (uri) => publish(uri, true));
  reg("rustzor.addPackage", addPackage);
  reg("rustzor.info", info);
  reg("rustzor.installCli", installCli);
  reg("rustzor.openDocs", () => vscode.env.openExternal(vscode.Uri.parse(DOCS)));

  context.subscriptions.push(
    vscode.window.onDidCloseTerminal((t) => {
      if (t === serveTerminal) {
        serveTerminal = undefined;
        updateStatus(false);
      }
    })
  );

  // The serve button appears only in a workspace with a Rustzor app.
  const projects = await vscode.workspace.findFiles("**/*.csproj", "**/{bin,obj,node_modules}/**", 200);
  if (projects.some((u) => isRustzorApp(u.fsPath))) {
    statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
    updateStatus(false);
    statusItem.show();
    context.subscriptions.push(statusItem);
  }

  // What other extensions (and the tests) get from vscode.extensions.getExtension(...).exports.
  return { isRustzorApp, packages: PACKAGES };
}

function deactivate() {
  serveTerminal?.dispose();
}

module.exports = { activate, deactivate };
