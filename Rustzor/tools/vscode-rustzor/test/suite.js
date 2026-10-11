// Runs inside VS Code: `code --extensionDevelopmentPath=<ext> --extensionTestsPath=<this folder> <workspace>`.
const vscode = require("vscode");
const assert = require("assert");
const path = require("path");

exports.run = async function () {
  const report = (text) => process.env.RUSTZOR_EXT_REPORT && require("fs").writeFileSync(process.env.RUSTZOR_EXT_REPORT, text);
  try {
    await check(report);
  } catch (e) {
    report("FAIL " + e.message);
    throw e;
  }
};

async function check(report) {
  const ext = vscode.extensions.all.find((e) => e.packageJSON.name === "rustzor");
  assert.ok(ext, "extension is loaded");
  await ext.activate();
  const commands = await vscode.commands.getCommands(true);
  const ours = ext.packageJSON.contributes.commands.map((c) => c.command);
  const missing = ours.filter((c) => !commands.includes(c));
  assert.deepStrictEqual(missing, [], "every contributed command is registered");
  const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  const projects = await vscode.workspace.findFiles("**/*.csproj", "**/{bin,obj}/**");
  const apps = projects.filter((u) => ext.exports.isRustzorApp(u.fsPath));
  assert.ok(apps.length >= 1, "the workspace's Rustzor app is detected");
  report(`OK commands=${ours.length} apps=${apps.map((u) => path.basename(u.fsPath)).join(",")} folder=${path.basename(folder)}`);
}
