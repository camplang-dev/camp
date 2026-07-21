const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");
const {
  createDebugConfiguration,
  createTestDebugConfiguration,
  findNearestCampBuild,
  getCompilerPath: deriveCompilerPath,
  getDebugAdapterPath: deriveDebugAdapterPath,
  normalizeTestCommandArgument
} = require("./campPaths");

let client;
let buildStatusItem;
let runStatusItem;
let debugStatusItem;
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

  debugStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 98);
  debugStatusItem.text = "$(debug-alt) Camp Debug";
  debugStatusItem.tooltip = "Debug the current Camp project";
  debugStatusItem.command = "camp.debug";

  const debugAdapterFactory = new CampDebugAdapterFactory();
  const debugConfigurationProvider = new CampDebugConfigurationProvider();

  context.subscriptions.push(
    buildStatusItem,
    runStatusItem,
    debugStatusItem,
    vscode.commands.registerCommand("camp.build", () => runCampCommand("build")),
    vscode.commands.registerCommand("camp.run", () => runCampCommand("run")),
    vscode.commands.registerCommand("camp.debug", debugCurrentProject),
    vscode.commands.registerCommand("camp.test.run", (argument) => runCampTestCommand("test", argument)),
    vscode.commands.registerCommand("camp.test.cover", (argument) => runCampTestCommand("cover", argument)),
    vscode.commands.registerCommand("camp.test.debug", debugCampTest),
    vscode.commands.registerCommand("camp.restartServer", restartLanguageServer),
    vscode.debug.registerDebugAdapterDescriptorFactory("camp", debugAdapterFactory),
    vscode.debug.registerDebugConfigurationProvider("camp", debugConfigurationProvider),
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
  const environment = {
    ...process.env
  };
  if (getTraceEnabled()) {
    const traceDirectory = getTraceDirectory(context);
    fs.mkdirSync(traceDirectory, { recursive: true });
    environment.CAMP_LSP_TRACE = "1";
    environment.CAMP_LSP_TRACE_DIR = traceDirectory;
  } else {
    environment.CAMP_LSP_TRACE = "0";
    delete environment.CAMP_LSP_TRACE_DIR;
  }
  const serverOptions = {
    command: serverPath,
    args: [],
    transport: TransportKind.stdio,
    options: {
      env: environment
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

function getTraceEnabled() {
  return vscode.workspace.getConfiguration("camp").get("server.trace") === true;
}

function getTraceDirectory(context) {
  const configured = vscode.workspace.getConfiguration("camp").get("server.traceDirectory");
  if (typeof configured === "string" && configured.trim().length > 0) {
    return configured.trim();
  }
  return path.join(context.globalStorageUri.fsPath, "lsp-traces");
}

function getCompilerPath() {
  return deriveCompilerPath(getServerPath());
}

function getDebugAdapterPath() {
  return deriveDebugAdapterPath(vscode.workspace.getConfiguration("camp").get("debugAdapter.path"), getServerPath());
}

function getNativeDebugBackend() {
  return vscode.workspace.getConfiguration("camp").get("debug.nativeBackend") || "auto";
}

function updateStatusItems(editor) {
  const show = editor && (editor.document.languageId === "camp" || editor.document.languageId === "campbuild");
  if (show) {
    buildStatusItem.show();
    runStatusItem.show();
    debugStatusItem.show();
  } else {
    buildStatusItem.hide();
    runStatusItem.hide();
    debugStatusItem.hide();
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

async function runCampTestCommand(command, argument) {
  const normalized = normalizeTestCommandArgument(argument);
  if (!normalized.project || !normalized.filter) {
    vscode.window.showWarningMessage("Camp test command is missing its project or test ID.");
    return;
  }

  const editor = vscode.window.activeTextEditor;
  if (editor && editor.document.isDirty) {
    await editor.document.save();
  }

  const compilerPath = getCompilerPath();
  const terminal = getCampTerminal();
  const args = [quoteShell(compilerPath), command, quoteShell(normalized.project), "--filter", quoteShell(normalized.filter)];
  if (command === "cover") {
    args.push("--coverage-format", "json");
  }
  terminal.show(true);
  terminal.sendText(`cd ${quoteShell(normalized.cwd || path.dirname(normalized.project))}`);
  terminal.sendText(args.join(" "));
}

async function debugCurrentProject() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || (editor.document.languageId !== "camp" && editor.document.languageId !== "campbuild")) {
    vscode.window.showWarningMessage("Open a Camp file before debugging.");
    return;
  }

  if (editor.document.isDirty) {
    await editor.document.save();
  }

  const documentPath = editor.document.uri.fsPath;
  const configuration = createDebugConfiguration(documentPath, editor.document.languageId, getNativeDebugBackend());
  const workspaceFolder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
  await vscode.debug.startDebugging(workspaceFolder, configuration);
}

async function debugCampTest(argument) {
  const normalized = normalizeTestCommandArgument(argument);
  if (!normalized.project || !normalized.filter) {
    vscode.window.showWarningMessage("Camp test debug command is missing its project or test ID.");
    return;
  }
  const editor = vscode.window.activeTextEditor;
  if (editor && editor.document.isDirty) {
    await editor.document.save();
  }
  const configuration = createTestDebugConfiguration(normalized, getNativeDebugBackend());
  const workspaceFolder = editor ? vscode.workspace.getWorkspaceFolder(editor.document.uri) : undefined;
  await vscode.debug.startDebugging(workspaceFolder, configuration);
}

function getCampTerminal() {
  if (!campTerminal || campTerminal.exitStatus) {
    campTerminal = vscode.window.createTerminal("Camp");
  }
  return campTerminal;
}

function quoteShell(value) {
  if (process.platform === "win32") {
    return `"${value.replace(/"/g, '\\"')}"`;
  }
  return `'${value.replace(/'/g, "'\\''")}'`;
}

class CampDebugAdapterFactory {
  createDebugAdapterDescriptor() {
    return new vscode.DebugAdapterExecutable(getDebugAdapterPath(), []);
  }
}

class CampDebugConfigurationProvider {
  resolveDebugConfiguration(folder, config) {
    if (!config.type) {
      config.type = "camp";
    }
    if (!config.request) {
      config.request = "launch";
    }
    if (!config.name) {
      config.name = "Debug Camp";
    }
    if (!config.backend) {
      config.backend = getNativeDebugBackend();
    }
    if (!config.args) {
      config.args = [];
    }
    if (config.stopOnEntry === undefined) {
      config.stopOnEntry = false;
    }
    if (!config.project) {
      const editor = vscode.window.activeTextEditor;
      if (editor && (editor.document.languageId === "camp" || editor.document.languageId === "campbuild")) {
        const generated = createDebugConfiguration(editor.document.uri.fsPath, editor.document.languageId, config.backend);
        config.project = generated.project;
        config.cwd = config.cwd || generated.cwd;
      } else if (folder) {
        config.project = "${workspaceFolder}";
        config.cwd = "${workspaceFolder}";
      }
    }
    return config;
  }
}

module.exports = {
  activate,
  deactivate,
  createDebugConfiguration,
  createTestDebugConfiguration,
  findNearestCampBuild,
  getCompilerPath,
  getDebugAdapterPath,
  normalizeTestCommandArgument
};
