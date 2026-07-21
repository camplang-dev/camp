const fs = require("fs");
const path = require("path");

function executableName(name, platform = process.platform) {
  return platform === "win32" ? `${name}.exe` : name;
}

function deriveSiblingTool(configuredPath, sourceToolName, targetToolName, platform = process.platform) {
  const pathImpl = platform === "win32" ? path.win32 : path;
  const sourceBase = executableName(sourceToolName, platform).toLowerCase();
  const targetBase = executableName(targetToolName, platform);
  if (pathImpl.basename(configuredPath).toLowerCase() === sourceBase) {
    const directory = pathImpl.dirname(configuredPath);
    if (directory && directory !== ".") {
      return pathImpl.join(directory, targetBase);
    }
  }
  return targetBase;
}

function getCompilerPath(serverPath, platform = process.platform) {
  return deriveSiblingTool(serverPath || executableName("camp-lsp", platform), "camp-lsp", "campc", platform);
}

function getDebugAdapterPath(configuredDebugPath, serverPath, platform = process.platform) {
  if (typeof configuredDebugPath === "string" && configuredDebugPath.trim().length > 0) {
    return configuredDebugPath.trim();
  }
  return deriveSiblingTool(serverPath || executableName("camp-lsp", platform), "camp-lsp", "camp-dap", platform);
}

function findNearestCampBuild(filePath, fsImpl = fs, pathImpl = path) {
  let directory = fsImpl.statSync(filePath).isDirectory() ? filePath : pathImpl.dirname(filePath);
  while (true) {
    const preferred = pathImpl.join(directory, `${pathImpl.basename(directory)}.campbuild`);
    if (fsImpl.existsSync(preferred)) {
      return preferred;
    }

    const candidates = fsImpl.readdirSync(directory)
      .filter(file => file.toLowerCase().endsWith(".campbuild"))
      .sort();
    if (candidates.length === 1) {
      return pathImpl.join(directory, candidates[0]);
    }

    const parent = pathImpl.dirname(directory);
    if (parent === directory) {
      return undefined;
    }
    directory = parent;
  }
}

function createDebugConfiguration(documentPath, languageId, nativeBackend, fsImpl = fs, pathImpl = path) {
  const projectPath = languageId === "campbuild"
    ? documentPath
    : findNearestCampBuild(documentPath, fsImpl, pathImpl);
  const targetPath = projectPath || documentPath;
  const cwd = projectPath
    ? pathImpl.dirname(projectPath)
    : pathImpl.dirname(documentPath);
  return {
    name: "Debug Camp",
    type: "camp",
    request: "launch",
    project: targetPath,
    cwd,
    args: [],
    stopOnEntry: false,
    backend: nativeBackend || "auto"
  };
}

function normalizeTestCommandArgument(argument = {}) {
  return {
    project: argument.project || argument.Project || "",
    cwd: argument.cwd || argument.Cwd || "",
    filter: argument.filter || argument.Filter || "",
    sourcefile: argument.sourcefile || argument.Sourcefile || "",
    sourceline: argument.sourceline || argument.Sourceline || 0
  };
}

function createTestDebugConfiguration(argument, nativeBackend) {
  const normalized = normalizeTestCommandArgument(argument);
  return {
    name: normalized.filter ? `Debug Camp Test ${normalized.filter}` : "Debug Camp Test",
    type: "camp",
    request: "launch",
    project: normalized.project,
    cwd: normalized.cwd || path.dirname(normalized.project),
    args: [],
    stopOnEntry: false,
    backend: nativeBackend || "auto",
    testFilter: normalized.filter
  };
}

module.exports = {
  createDebugConfiguration,
  createTestDebugConfiguration,
  deriveSiblingTool,
  executableName,
  findNearestCampBuild,
  getCompilerPath,
  getDebugAdapterPath,
  normalizeTestCommandArgument
};
