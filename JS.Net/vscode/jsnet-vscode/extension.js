// JS.Net for VS Code — starts the JS.Net language server (`jsnet lsp`) over stdio.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

function createClient() {
    const config = vscode.workspace.getConfiguration("jsnet");
    const command = config.get("server.command", "jsnet");
    const args = config.get("server.args", ["lsp"]);
    const serverOptions = {
        run: { command, args, transport: TransportKind.stdio },
        debug: { command, args, transport: TransportKind.stdio },
    };
    const clientOptions = {
        documentSelector: [{ scheme: "file", language: "jsnet" }],
        synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher("**/*.jsn") },
    };
    return new LanguageClient("jsnet", "JS.Net Language Server", serverOptions, clientOptions);
}

async function activate(context) {
    client = createClient();
    context.subscriptions.push(vscode.commands.registerCommand("jsnet.restartServer", async () => {
        if (client) await client.stop();
        client = createClient();
        await client.start();
    }));
    try {
        await client.start();
    } catch (err) {
        vscode.window.showWarningMessage(
            "JS.Net language server could not start. Install the CLI with `dotnet tool install -g JSNet.Cli` or set `jsnet.server.command`.");
    }
}

async function deactivate() {
    if (client) await client.stop();
}

module.exports = { activate, deactivate };
