const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
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
let clientReady;
let buildStatusItem;
let runStatusItem;
let debugStatusItem;
let testStatusItem;
let coverStatusItem;
let campTerminal;
let extensionContext;
let testController;
let testRunProfile;
let testCoverProfile;
let testDebugProfile;
let testDataById = new Map();
let coverageDetailsByFileCoverage = new WeakMap();

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

  const debugAdapterFactory = new CampDebugAdapterFactory();
  const debugConfigurationProvider = new CampDebugConfigurationProvider();

  context.subscriptions.push(
    buildStatusItem,
    runStatusItem,
    debugStatusItem,
    testStatusItem,
    coverStatusItem,
    vscode.commands.registerCommand("camp.build", () => runCampCommand("build")),
    vscode.commands.registerCommand("camp.run", () => runCampCommand("run")),
    vscode.commands.registerCommand("camp.debug", debugCurrentProject),
    vscode.commands.registerCommand("camp.testProject", () => runCampCommand("test")),
    vscode.commands.registerCommand("camp.coverProject", coverCurrentProject),
    vscode.commands.registerCommand("camp.test.run", (argument) => runCampTestCommand("test", argument)),
    vscode.commands.registerCommand("camp.test.cover", coverCurrentTest),
    vscode.commands.registerCommand("camp.test.debug", debugCampTest),
    vscode.commands.registerCommand("camp.restartServer", restartLanguageServer),
    vscode.debug.registerDebugAdapterDescriptorFactory("camp", debugAdapterFactory),
    vscode.debug.registerDebugConfigurationProvider("camp", debugConfigurationProvider),
    vscode.window.onDidChangeActiveTextEditor(updateStatusItems)
  );

  setupTestExplorer(context);
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
  clientReady = client.start();
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

function setupTestExplorer(context) {
  testController = vscode.tests.createTestController("campTests", "Camp");
  testController.resolveHandler = async () => refreshTestExplorer();
  testRunProfile = testController.createRunProfile("Run", vscode.TestRunProfileKind.Run, runTestExplorerRequest, true);
  testCoverProfile = testController.createRunProfile("Cover", vscode.TestRunProfileKind.Coverage, coverTestExplorerRequest, false);
  testCoverProfile.loadDetailedCoverage = async (_testRun, fileCoverage) => coverageDetailsByFileCoverage.get(fileCoverage) || [];
  testDebugProfile = testController.createRunProfile("Debug", vscode.TestRunProfileKind.Debug, debugTestExplorerRequest, false);
  context.subscriptions.push(testController, testRunProfile, testCoverProfile, testDebugProfile);
}

async function coverCurrentProject() {
  const context = await getActiveProjectContext();
  if (!context) {
    vscode.window.showWarningMessage("Open a Camp file before running coverage.");
    return;
  }
  await refreshTestExplorer();
  const tests = collectAllTestItems();
  if (tests.length === 0) {
    await coverProjectWithoutDiscoveredTests(context);
    return;
  }
  await runTestExplorerCommand("cover", new vscode.TestRunRequest(undefined, undefined, testCoverProfile), undefined);
}

async function coverCurrentTest(argument) {
  const normalized = normalizeTestCommandArgument(argument);
  if (!normalized.project || !normalized.filter) {
    vscode.window.showWarningMessage("Camp test coverage command is missing its project or test ID.");
    return;
  }
  await refreshTestExplorer();
  const item = findTestItem(normalized.filter);
  if (!item) {
    vscode.window.showWarningMessage("Camp could not find the selected test in Test Explorer.");
    return;
  }
  await runTestExplorerCommand("cover", new vscode.TestRunRequest([item], undefined, testCoverProfile), undefined);
}

async function coverProjectWithoutDiscoveredTests(context) {
  const request = new vscode.TestRunRequest(undefined, undefined, testCoverProfile);
  const run = testController.createTestRun(request, "Camp Coverage");
  try {
    const output = await executeCampTestGroup("cover", { project: context.project, cwd: context.cwd, tests: [] }, run.token);
    reportCoverage(run, output.coverageArtifacts, context);
    if (output.tests.length > 0) {
      vscode.window.showInformationMessage("Camp coverage was generated, but no Test Explorer items were available for test results.");
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Camp coverage failed: ${error.message}`);
  } finally {
    run.end();
  }
}

async function refreshTestExplorer() {
  if (!testController || !client) {
    return;
  }
  const context = await getActiveProjectContext();
  if (!context) {
    return;
  }
  try {
    await clientReady;
    const response = await client.sendRequest("camp/tests", {
      textDocument: { uri: context.editor.document.uri.toString() }
    });
    const tests = Array.isArray(response?.tests) ? response.tests : [];
    replaceTestExplorerItems(tests);
  } catch (error) {
    vscode.window.showWarningMessage(`Camp test discovery failed: ${error.message}`);
  }
}

function replaceTestExplorerItems(tests) {
  testDataById = new Map();
  testController.items.replace([]);
  const fileItems = new Map();
  for (const raw of tests) {
    const test = normalizeExplorerTest(raw);
    if (!test.id || !test.path) {
      continue;
    }
    testDataById.set(test.id, test);
    const fileKey = normalizePath(test.path);
    let fileItem = fileItems.get(fileKey);
    if (!fileItem) {
      const uri = vscode.Uri.file(test.path);
      fileItem = testController.createTestItem("file:" + fileKey, workspaceRelativePath(test.path), uri);
      fileItems.set(fileKey, fileItem);
      testController.items.add(fileItem);
    }
    const item = testController.createTestItem(test.id, test.name || test.id, vscode.Uri.file(test.path));
    item.range = toVsCodeRange(test.range);
    fileItem.children.add(item);
  }
}

function normalizeExplorerTest(test) {
  return {
    id: test.id || test.Id || "",
    name: test.name || test.Name || "",
    qualifiedName: test.qualifiedName || test.QualifiedName || "",
    path: test.path || test.Path || "",
    range: test.range || test.Range,
    project: test.project || test.Project || "",
    cwd: test.cwd || test.Cwd || "",
    skipped: test.skipped ?? test.Skipped ?? false,
    skipReason: test.skipReason || test.SkipReason || "",
    runnerSignature: test.runnerSignature || test.RunnerSignature || ""
  };
}

function toVsCodeRange(range) {
  const start = range?.start || range?.Start || {};
  const end = range?.end || range?.End || start;
  return new vscode.Range(
    start.line ?? start.Line ?? 0,
    start.character ?? start.Character ?? 0,
    end.line ?? end.Line ?? start.line ?? start.Line ?? 0,
    end.character ?? end.Character ?? start.character ?? start.Character ?? 0
  );
}

function workspaceRelativePath(filePath) {
  const workspaceFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(filePath));
  if (workspaceFolder) {
    return path.relative(workspaceFolder.uri.fsPath, filePath) || path.basename(filePath);
  }
  return path.basename(filePath);
}

async function runTestExplorerRequest(request, token) {
  await runTestExplorerCommand("test", request, token);
}

async function coverTestExplorerRequest(request, token) {
  await runTestExplorerCommand("cover", request, token);
}

async function debugTestExplorerRequest(request) {
  await refreshTestExplorer();
  const tests = collectRequestedTestData(request);
  if (tests.length === 0) {
    return;
  }
  const test = tests[0];
  await debugCampTest({
    project: test.project,
    cwd: test.cwd,
    filter: test.id,
    sourcefile: test.path,
    sourceline: test.range?.start?.line ?? 0
  });
}

async function runTestExplorerCommand(command, request, token) {
  await refreshTestExplorer();
  const tests = collectRequestedTestData(request);
  const run = testController.createTestRun(request);
  if (tests.length === 0) {
    run.end();
    return;
  }
  for (const test of tests) {
    const item = findTestItem(test.id);
    if (item) {
      run.enqueued(item);
    }
  }
  const groups = groupTestsByProject(tests);
  for (const group of groups) {
    if (token?.isCancellationRequested) {
      break;
    }
    for (const test of group.tests) {
      const item = findTestItem(test.id);
      if (item) {
        run.started(item);
      }
    }
    try {
      const output = await executeCampTestGroup(command, group, token);
      reportTestResults(run, group.tests, output.tests);
      if (command === "cover") {
        reportCoverage(run, output.coverageArtifacts, group);
      }
    } catch (error) {
      for (const test of group.tests) {
        const item = findTestItem(test.id);
        if (item) {
          run.errored(item, new vscode.TestMessage(error.message));
        }
      }
    }
  }
  run.end();
}

function collectRequestedTestData(request) {
  const included = request.include && request.include.length > 0
    ? request.include.flatMap(collectLeafTestItems)
    : collectAllTestItems();
  const excluded = new Set((request.exclude || []).flatMap(collectLeafTestItems).map(item => item.id));
  return included
    .filter(item => !excluded.has(item.id))
    .map(item => testDataById.get(item.id))
    .filter(Boolean);
}

function collectAllTestItems() {
  const result = [];
  testController.items.forEach(item => {
    result.push(...collectLeafTestItems(item));
  });
  return result;
}

function collectLeafTestItems(item) {
  const result = [];
  if (testDataById.has(item.id)) {
    result.push(item);
    return result;
  }
  item.children.forEach(child => {
    result.push(...collectLeafTestItems(child));
  });
  return result;
}

function groupTestsByProject(tests) {
  const groups = new Map();
  for (const test of tests) {
    const key = normalizePath(test.project) + "\n" + normalizePath(test.cwd || path.dirname(test.project));
    let group = groups.get(key);
    if (!group) {
      group = { project: test.project, cwd: test.cwd || path.dirname(test.project), tests: [] };
      groups.set(key, group);
    }
    group.tests.push(test);
  }
  return Array.from(groups.values());
}

async function executeCampTestGroup(command, group, token) {
  const outputDir = path.join(extensionContext.globalStorageUri.fsPath, "test-runs", Date.now().toString(36) + "-" + Math.random().toString(36).slice(2));
  fs.mkdirSync(outputDir, { recursive: true });
  const args = [
    command,
    group.project,
    "--test-result-format",
    "json",
    "--test-output-dir",
    outputDir
  ];
  if (command === "cover") {
    args.push("--coverage-format", "json", "--coverage-output-dir", outputDir);
  }
  for (const test of group.tests) {
    args.push("--filter", test.id);
  }
  const result = await execFile(getCompilerPath(), args, { cwd: group.cwd }, token);
  try {
    const tests = readLatestTestResults(outputDir);
    const coverageArtifacts = command === "cover" ? findCoverageArtifacts(outputDir) : undefined;
    if (command === "cover" && !coverageArtifacts) {
      throw new Error("campc did not write Camp coverage artifacts.");
    }
    return { tests, coverageArtifacts };
  } catch (error) {
    if (result.code !== 0) {
      throw new Error((result.stderr || result.stdout || error.message).trim());
    }
    throw error;
  }
}

function execFile(file, args, options, token) {
  return new Promise((resolve, reject) => {
    const child = childProcess.spawn(file, args, {
      cwd: options.cwd,
      windowsHide: true
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", chunk => {
      stdout += chunk.toString();
    });
    child.stderr.on("data", chunk => {
      stderr += chunk.toString();
    });
    const cancellation = token?.onCancellationRequested(() => child.kill());
    child.on("error", error => {
      cancellation?.dispose();
      reject(error);
    });
    child.on("close", code => {
      cancellation?.dispose();
      resolve({ code, stdout, stderr });
    });
  });
}

function readLatestTestResults(directory) {
  const files = [];
  collectFiles(directory, file => file.endsWith(".camp-test-results.json"), files);
  if (files.length === 0) {
    throw new Error("campc did not write a Camp test results JSON file.");
  }
  files.sort((a, b) => getModifiedTime(b) - getModifiedTime(a));
  const parsed = JSON.parse(fs.readFileSync(files[0], "utf8"));
  if (parsed.format !== "camp.test-results" || !Array.isArray(parsed.tests)) {
    throw new Error("Camp test results JSON has an unsupported format.");
  }
  return parsed.tests;
}

function collectFiles(directory, predicate, result, depth = 0) {
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
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      collectFiles(fullPath, predicate, result, depth + 1);
    } else if (entry.isFile() && predicate(fullPath)) {
      result.push(fullPath);
    }
  }
}

function reportTestResults(run, requestedTests, results) {
  const resultsById = new Map(results.map(result => [result.id, result]));
  for (const test of requestedTests) {
    const item = findTestItem(test.id);
    if (!item) {
      continue;
    }
    const result = resultsById.get(test.id);
    if (!result) {
      run.errored(item, new vscode.TestMessage("Camp did not report a result for this test."));
      continue;
    }
    const duration = typeof result.durationMs === "number" ? result.durationMs : undefined;
    if (result.outcome === "passed") {
      run.passed(item, duration);
    } else if (result.outcome === "skipped") {
      run.skipped(item);
    } else {
      run.failed(item, createTestMessage(result), duration);
    }
  }
}

function createTestMessage(result) {
  const failure = result.failure;
  const message = failure?.message || result.summary || result.outcome || "Camp test failed.";
  const testMessage = new vscode.TestMessage(message);
  if (failure?.sourcefile && failure.sourceline > 0) {
    testMessage.location = new vscode.Location(vscode.Uri.file(resolveFailureSource(failure.sourcefile)), lineRange(failure.sourceline));
  }
  return testMessage;
}

function resolveFailureSource(sourcefile) {
  if (path.isAbsolute(sourcefile)) {
    return sourcefile;
  }
  const active = vscode.window.activeTextEditor?.document.uri.fsPath;
  if (active) {
    const project = findNearestCampBuild(active);
    const root = project ? path.dirname(project) : path.dirname(active);
    return path.resolve(root, sourcefile);
  }
  return sourcefile;
}

function findTestItem(id) {
  let found;
  testController.items.forEach(fileItem => {
    const child = fileItem.children.get(id);
    if (child) {
      found = child;
    }
  });
  return found;
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

function lineRange(line) {
  return new vscode.Range(Math.max(0, line - 1), 0, Math.max(0, line - 1), 0);
}

function findCoverageArtifacts(directory) {
  const maps = [];
  const results = [];
  collectFiles(directory, file => file.endsWith(".camp-coverage-map.csv"), maps);
  collectFiles(directory, file => file.endsWith(".camp-coverage-results.json"), results);
  if (maps.length === 0 || results.length === 0) {
    return undefined;
  }

  const resultsByPrefix = new Map(results.map(file => [stripCoverageSuffix(file, ".camp-coverage-results.json"), file]));
  const pairs = [];
  for (const mapFile of maps) {
    const resultFile = resultsByPrefix.get(stripCoverageSuffix(mapFile, ".camp-coverage-map.csv"));
    if (resultFile) {
      pairs.push({ mapPath: mapFile, resultsPath: resultFile, mtimeMs: Math.max(getModifiedTime(mapFile), getModifiedTime(resultFile)) });
    }
  }
  if (pairs.length > 0) {
    pairs.sort((a, b) => b.mtimeMs - a.mtimeMs);
    return pairs[0];
  }

  maps.sort((a, b) => getModifiedTime(b) - getModifiedTime(a));
  results.sort((a, b) => getModifiedTime(b) - getModifiedTime(a));
  return { mapPath: maps[0], resultsPath: results[0] };
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

function reportCoverage(run, artifacts, context) {
  for (const entry of loadVsCodeCoverage(artifacts, context)) {
    coverageDetailsByFileCoverage.set(entry.fileCoverage, entry.details);
    run.addCoverage(entry.fileCoverage);
  }
}

function loadVsCodeCoverage(artifacts, context) {
  const map = parseCoverageMap(fs.readFileSync(artifacts.mapPath, "utf8"));
  const results = JSON.parse(fs.readFileSync(artifacts.resultsPath, "utf8"));
  if (results.format !== "camp.coverage-results" || results.version !== 1 || !Array.isArray(results.files)) {
    throw new Error("coverage results JSON is not a Camp coverage results file.");
  }

  const uncoveredByPath = new Map();
  const metricsByPath = new Map();
  for (const file of results.files) {
    if (typeof file.path !== "string") {
      continue;
    }
    if (Array.isArray(file.uncoveredLines)) {
      uncoveredByPath.set(file.path, new Set(file.uncoveredLines.filter(Number.isInteger)));
    }
    metricsByPath.set(file.path, file);
  }

  const files = new Map();
  for (const counter of map.counters) {
    if (counter.kind !== "l") {
      continue;
    }
    const sourcePath = map.files.get(counter.fileId);
    if (!sourcePath) {
      continue;
    }
    const resolvedPath = normalizePath(resolveSourcePath(sourcePath, context));
    const file = files.get(resolvedPath) || { uriPath: resolveSourcePath(sourcePath, context), sourcePaths: new Set(), lines: new Map() };
    file.sourcePaths.add(sourcePath);
    const uncovered = uncoveredByPath.get(sourcePath)?.has(counter.line) === true;
    file.lines.set(counter.line, file.lines.get(counter.line) === true || !uncovered);
    files.set(resolvedPath, file);
  }

  const coverage = [];
  for (const file of files.values()) {
    const lines = Array.from(file.lines.entries()).sort((a, b) => a[0] - b[0]);
    const covered = lines.filter(([, wasCovered]) => wasCovered).length;
    const details = lines.map(([line, wasCovered]) => new vscode.StatementCoverage(wasCovered, new vscode.Position(Math.max(0, line - 1), 0)));
    const metric = combinedMetricForSourcePaths(metricsByPath, file.sourcePaths, "function");
    const functionCoverage = metric ? new vscode.TestCoverageCount(metric.covered, metric.total) : undefined;
    const fileCoverage = new vscode.FileCoverage(
      vscode.Uri.file(file.uriPath),
      new vscode.TestCoverageCount(covered, lines.length),
      undefined,
      functionCoverage
    );
    coverage.push({ fileCoverage, details });
  }
  return coverage;
}

function combinedMetricForSourcePaths(metricsByPath, sourcePaths, metricName) {
  let covered = 0;
  let total = 0;
  for (const sourcePath of sourcePaths) {
    const metric = metricsByPath.get(sourcePath)?.[metricName];
    if (metric && Number.isInteger(metric.covered) && Number.isInteger(metric.total)) {
      covered += metric.covered;
      total += metric.total;
    }
  }
  return total > 0 ? { covered, total } : undefined;
}

function parseCoverageMap(text) {
  const files = new Map();
  const names = new Map();
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
    } else if (row[0] === "n" && row.length === 3) {
      names.set(Number(row[1]), row[2]);
    } else if (row[0] === "c" && row.length === 6) {
      counters.push({
        id: Number(row[1]),
        kind: row[2],
        fileId: Number(row[3]),
        line: Number(row[4]),
        nameId: Number(row[5])
      });
    }
  }
  if (version !== 1) {
    throw new Error("coverage map CSV is not a supported Camp coverage map.");
  }
  return { files, names, counters };
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
