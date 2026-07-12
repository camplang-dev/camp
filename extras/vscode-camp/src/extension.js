const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;
let buildStatusItem;
let runStatusItem;
let campTerminal;
let extensionContext;

function activate(context) {
  extensionContext = context;
  buildStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  buildStatusItem.text = "$(tools) Camp Build";
  buildStatusItem.tooltip = "Build the current Camp project";
  buildStatusItem.command = "camp.build";

  runStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
  runStatusItem.text = "$(play) Camp Run";
  runStatusItem.tooltip = "Run the current Camp project";
  runStatusItem.command = "camp.run";

  context.subscriptions.push(
    buildStatusItem,
    runStatusItem,
    vscode.commands.registerCommand("camp.build", () => runCampCommand("build")),
    vscode.commands.registerCommand("camp.run", () => runCampCommand("run")),
    vscode.commands.registerCommand("camp.restartServer", restartLanguageServer),
    vscode.window.onDidChangeActiveTextEditor(updateStatusItems)
  );

  updateStatusItems(vscode.window.activeTextEditor);
  startLanguageServer(context);
}

function deactivate() {
  if (client) {
    return client.stop();
  }
  return undefined;
}

function startLanguageServer(context) {
  const serverPath = getServerPath();
  const traceDirectory = path.join(context.globalStorageUri.fsPath, "lsp-traces");
  fs.mkdirSync(traceDirectory, { recursive: true });
  const serverOptions = {
    command: serverPath,
    args: [],
    transport: TransportKind.stdio,
    options: {
      env: {
        ...process.env,
        CAMP_LSP_TRACE_DIR: traceDirectory
      }
    }
  };
  const clientOptions = {
    documentSelector: [{ scheme: "file", language: "camp" }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher("**/*.{camp,campbuild}")
    }
  };

  client = new LanguageClient("camp-lsp", "Camp Language Server", serverOptions, clientOptions);
  if (context.subscriptions) {
    context.subscriptions.push(client);
  }
  client.start();
}

async function restartLanguageServer() {
  if (client) {
    await client.stop();
    client = undefined;
  }
  startLanguageServer(extensionContext);
}

function getServerPath() {
  return vscode.workspace.getConfiguration("camp").get("server.path") || "camp-lsp";
}

function getCompilerPath() {
  const serverPath = getServerPath();
  const serverBase = process.platform === "win32" ? "camp-lsp.exe" : "camp-lsp";
  const compilerBase = process.platform === "win32" ? "campc.exe" : "campc";
  if (path.basename(serverPath).toLowerCase() === serverBase.toLowerCase()) {
    const directory = path.dirname(serverPath);
    if (directory && directory !== ".") {
      return path.join(directory, compilerBase);
    }
  }
  return compilerBase;
}

function updateStatusItems(editor) {
  const show = editor && (editor.document.languageId === "camp" || editor.document.languageId === "campbuild");
  if (show) {
    buildStatusItem.show();
    runStatusItem.show();
  } else {
    buildStatusItem.hide();
    runStatusItem.hide();
  }
}

async function runCampCommand(command) {
  const editor = vscode.window.activeTextEditor;
  if (!editor || (editor.document.languageId !== "camp" && editor.document.languageId !== "campbuild")) {
    vscode.window.showWarningMessage("Open a Camp file before running this command.");
    return;
  }

  if (editor.document.isDirty) {
    await editor.document.save();
  }

  const documentPath = editor.document.uri.fsPath;
  const projectPath = editor.document.languageId === "campbuild"
    ? documentPath
    : findNearestCampBuild(documentPath);
  const targetPath = projectPath || documentPath;
  const cwd = projectPath
    ? path.dirname(projectPath)
    : path.dirname(documentPath);
  const compilerPath = getCompilerPath();
  const quotedCompiler = quoteShell(compilerPath);
  const quotedTarget = quoteShell(targetPath);

  const terminal = getCampTerminal();
  terminal.show(true);
  terminal.sendText(`cd ${quoteShell(cwd)}`);
  terminal.sendText(`${quotedCompiler} ${command} ${quotedTarget}`);
}

function getCampTerminal() {
  if (!campTerminal || campTerminal.exitStatus) {
    campTerminal = vscode.window.createTerminal("Camp");
  }
  return campTerminal;
}

function findNearestCampBuild(filePath) {
  let directory = fs.statSync(filePath).isDirectory() ? filePath : path.dirname(filePath);
  while (true) {
    const preferred = path.join(directory, `${path.basename(directory)}.campbuild`);
    if (fs.existsSync(preferred)) {
      return preferred;
    }

    const candidates = fs.readdirSync(directory)
      .filter(file => file.toLowerCase().endsWith(".campbuild"))
      .sort();
    if (candidates.length === 1) {
      return path.join(directory, candidates[0]);
    }

    const parent = path.dirname(directory);
    if (parent === directory) {
      return undefined;
    }
    directory = parent;
  }
}

function quoteShell(value) {
  if (process.platform === "win32") {
    return `"${value.replace(/"/g, '\\"')}"`;
  }
  return `'${value.replace(/'/g, "'\\''")}'`;
}

module.exports = {
  activate,
  deactivate
};
