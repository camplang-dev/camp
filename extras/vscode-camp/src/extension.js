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
let testStatusItem;
let coverStatusItem;
let campTerminal;
let extensionContext;
let coverageEnabled = false;
let coverageByPath = new Map();
let coveredLineDecoration;
let uncoveredLineDecoration;

function activate(context) {
  extensionContext = context;
  buildStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  buildStatusItem.text = "$(tools) Build";
  buildStatusItem.tooltip = "Build the current Camp project";
  buildStatusItem.command = "camp.build";

  runStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 99);
  runStatusItem.text = "$(play) Run";
  runStatusItem.tooltip = "Run the current Camp project";
  runStatusItem.command = "camp.run";

  debugStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 98);
  debugStatusItem.text = "$(debug-alt) Debug";
  debugStatusItem.tooltip = "Debug the current Camp project";
  debugStatusItem.command = "camp.debug";

  testStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 97);
  testStatusItem.text = "$(beaker) Test";
  testStatusItem.tooltip = "Test the current Camp project";
  testStatusItem.command = "camp.testProject";

  coverStatusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 96);
  coverStatusItem.text = "$(graph) Cover";
  coverStatusItem.tooltip = "Run coverage for the current Camp project";
  coverStatusItem.command = "camp.coverProject";

  coveredLineDecoration = vscode.window.createTextEditorDecorationType({
    isWholeLine: true,
    backgroundColor: "rgba(88, 166, 92, 0.18)"
  });
  uncoveredLineDecoration = vscode.window.createTextEditorDecorationType({
    isWholeLine: true,
    backgroundColor: "rgba(128, 128, 128, 0.18)"
  });

  const debugAdapterFactory = new CampDebugAdapterFactory();
  const debugConfigurationProvider = new CampDebugConfigurationProvider();

  context.subscriptions.push(
    buildStatusItem,
    runStatusItem,
    debugStatusItem,
    testStatusItem,
    coverStatusItem,
    coveredLineDecoration,
    uncoveredLineDecoration,
    vscode.commands.registerCommand("camp.build", () => runCampCommand("build")),
    vscode.commands.registerCommand("camp.run", () => runCampCommand("run")),
    vscode.commands.registerCommand("camp.debug", debugCurrentProject),
    vscode.commands.registerCommand("camp.testProject", () => runCampCommand("test")),
    vscode.commands.registerCommand("camp.coverProject", () => runCampCommand("cover")),
    vscode.commands.registerCommand("camp.coverage.toggle", toggleCoverage),
    vscode.commands.registerCommand("camp.coverage.clear", clearCoverage),
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
    testStatusItem.show();
    coverStatusItem.show();
  } else {
    buildStatusItem.hide();
    runStatusItem.hide();
    debugStatusItem.hide();
    testStatusItem.hide();
    coverStatusItem.hide();
  }
  applyCoverageDecorations(editor);
}

async function runCampCommand(command) {
  const context = await getActiveProjectContext();
  if (!context) {
    vscode.window.showWarningMessage("Open a Camp file before running this command.");
    return;
  }

  const compilerPath = getCompilerPath();
  const quotedCompiler = quoteShell(compilerPath);
  const args = [quotedCompiler, command, quoteShell(context.project)];
  if (command === "cover") {
    args.push("--coverage-format", "json");
  }

  const terminal = getCampTerminal();
  terminal.show(true);
  terminal.sendText(`cd ${quoteShell(context.cwd)}`);
  terminal.sendText(args.join(" "));
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

async function getActiveProjectContext() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || (editor.document.languageId !== "camp" && editor.document.languageId !== "campbuild")) {
    return undefined;
  }

  if (editor.document.isDirty) {
    await editor.document.save();
  }

  const documentPath = editor.document.uri.fsPath;
  const projectPath = editor.document.languageId === "campbuild"
    ? documentPath
    : findNearestCampBuild(documentPath);
  const project = projectPath || documentPath;
  const cwd = projectPath
    ? path.dirname(projectPath)
    : path.dirname(documentPath);
  return { editor, documentPath, project, cwd };
}

async function toggleCoverage() {
  if (coverageEnabled) {
    clearCoverage();
    return;
  }

  const context = await getActiveProjectContext();
  if (!context) {
    vscode.window.showWarningMessage("Open a Camp file before toggling coverage.");
    return;
  }

  const artifacts = findCoverageArtifacts(context);
  if (!artifacts) {
    const choice = await vscode.window.showWarningMessage(
      "No Camp coverage results were found for the current project.",
      "Generate Coverage"
    );
    if (choice === "Generate Coverage") {
      await runCampCommand("cover");
    }
    return;
  }

  try {
    coverageByPath = loadCoverageDecorations(artifacts, context);
    coverageEnabled = true;
    for (const editor of vscode.window.visibleTextEditors) {
      applyCoverageDecorations(editor);
    }
    vscode.window.showInformationMessage(`Loaded Camp coverage from ${path.basename(artifacts.resultsPath)}.`);
  } catch (error) {
    coverageByPath = new Map();
    coverageEnabled = false;
    vscode.window.showErrorMessage(`Could not load Camp coverage: ${error.message}`);
  }
}

function clearCoverage() {
  coverageEnabled = false;
  coverageByPath = new Map();
  for (const editor of vscode.window.visibleTextEditors) {
    editor.setDecorations(coveredLineDecoration, []);
    editor.setDecorations(uncoveredLineDecoration, []);
  }
}

function applyCoverageDecorations(editor) {
  if (!editor || editor.document.languageId !== "camp") {
    return;
  }
  if (!coverageEnabled) {
    editor.setDecorations(coveredLineDecoration, []);
    editor.setDecorations(uncoveredLineDecoration, []);
    return;
  }
  const key = normalizePath(editor.document.uri.fsPath);
  const lines = coverageByPath.get(key) || { covered: [], uncovered: [] };
  editor.setDecorations(coveredLineDecoration, lines.covered.map(lineRange));
  editor.setDecorations(uncoveredLineDecoration, lines.uncovered.map(lineRange));
}

function lineRange(line) {
  return new vscode.Range(Math.max(0, line - 1), 0, Math.max(0, line - 1), 0);
}

function findCoverageArtifacts(context) {
  const root = context.cwd;
  const maps = [];
  const results = [];
  collectCoverageArtifacts(root, maps, results);
  if (maps.length === 0 || results.length === 0) {
    return undefined;
  }

  const resultsByPrefix = new Map(results.map(file => [stripCoverageSuffix(file.path, ".camp-coverage-results.json"), file]));
  const pairs = [];
  for (const mapFile of maps) {
    const resultFile = resultsByPrefix.get(stripCoverageSuffix(mapFile.path, ".camp-coverage-map.csv"));
    if (resultFile) {
      pairs.push({ mapPath: mapFile.path, resultsPath: resultFile.path, mtimeMs: Math.max(mapFile.mtimeMs, resultFile.mtimeMs) });
    }
  }
  if (pairs.length > 0) {
    pairs.sort((a, b) => b.mtimeMs - a.mtimeMs);
    return pairs[0];
  }

  maps.sort((a, b) => b.mtimeMs - a.mtimeMs);
  results.sort((a, b) => b.mtimeMs - a.mtimeMs);
  return { mapPath: maps[0].path, resultsPath: results[0].path };
}

function collectCoverageArtifacts(directory, maps, results, depth = 0) {
  if (depth > 8) {
    return;
  }
  let entries;
  try {
    entries = fs.readdirSync(directory, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (entry.name === ".git" || entry.name === "node_modules" || entry.name === "obj") {
      continue;
    }
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      collectCoverageArtifacts(fullPath, maps, results, depth + 1);
      continue;
    }
    if (!entry.isFile()) {
      continue;
    }
    if (entry.name.endsWith(".camp-coverage-map.csv")) {
      maps.push({ path: fullPath, mtimeMs: getModifiedTime(fullPath) });
    } else if (entry.name.endsWith(".camp-coverage-results.json")) {
      results.push({ path: fullPath, mtimeMs: getModifiedTime(fullPath) });
    }
  }
}

function getModifiedTime(filePath) {
  try {
    return fs.statSync(filePath).mtimeMs;
  } catch {
    return 0;
  }
}

function stripCoverageSuffix(filePath, suffix) {
  return filePath.slice(0, -suffix.length);
}

function loadCoverageDecorations(artifacts, context) {
  const map = parseCoverageMap(fs.readFileSync(artifacts.mapPath, "utf8"));
  const results = JSON.parse(fs.readFileSync(artifacts.resultsPath, "utf8"));
  if (results.format !== "camp.coverage-results" || results.version !== 1 || !Array.isArray(results.files)) {
    throw new Error("coverage results JSON is not a Camp coverage results file.");
  }

  const uncoveredByPath = new Map();
  for (const file of results.files) {
    if (typeof file.path === "string" && Array.isArray(file.uncoveredLines)) {
      uncoveredByPath.set(file.path, new Set(file.uncoveredLines.filter(Number.isInteger)));
    }
  }

  const byPath = new Map();
  for (const counter of map.counters) {
    if (counter.kind !== "l") {
      continue;
    }
    const sourcePath = map.files.get(counter.fileId);
    if (!sourcePath) {
      continue;
    }
    const resolvedPath = normalizePath(resolveSourcePath(sourcePath, context));
    const lines = byPath.get(resolvedPath) || { covered: [], uncovered: [] };
    const uncovered = uncoveredByPath.get(sourcePath)?.has(counter.line) === true;
    if (uncovered) {
      lines.uncovered.push(counter.line);
    } else {
      lines.covered.push(counter.line);
    }
    byPath.set(resolvedPath, lines);
  }

  for (const lines of byPath.values()) {
    lines.covered = Array.from(new Set(lines.covered)).sort((a, b) => a - b);
    lines.uncovered = Array.from(new Set(lines.uncovered)).sort((a, b) => a - b);
  }
  return byPath;
}

function parseCoverageMap(text) {
  const files = new Map();
  const counters = [];
  let version = 0;
  for (const line of text.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n")) {
    if (line.length === 0) {
      continue;
    }
    const row = parseCsvRow(line);
    if (row[0] === "v" && row.length === 2) {
      version = Number(row[1]);
    } else if (row[0] === "p" && row.length === 3) {
      files.set(Number(row[1]), row[2]);
    } else if (row[0] === "c" && row.length === 6) {
      counters.push({
        id: Number(row[1]),
        kind: row[2],
        fileId: Number(row[3]),
        line: Number(row[4])
      });
    }
  }
  if (version !== 1) {
    throw new Error("coverage map CSV is not a supported Camp coverage map.");
  }
  return { files, counters };
}

function parseCsvRow(line) {
  const fields = [];
  let current = "";
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (quoted) {
      if (ch === "\"") {
        if (line[i + 1] === "\"") {
          current += "\"";
          i++;
        } else {
          quoted = false;
        }
      } else {
        current += ch;
      }
    } else if (ch === ",") {
      fields.push(current);
      current = "";
    } else if (ch === "\"" && current.length === 0) {
      quoted = true;
    } else {
      current += ch;
    }
  }
  fields.push(current);
  return fields;
}

function resolveSourcePath(sourcePath, context) {
  if (path.isAbsolute(sourcePath)) {
    return sourcePath;
  }
  const cwdPath = path.resolve(context.cwd, sourcePath);
  if (fs.existsSync(cwdPath)) {
    return cwdPath;
  }
  return path.resolve(path.dirname(context.project), sourcePath);
}

function normalizePath(value) {
  const normalized = path.normalize(value);
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
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
