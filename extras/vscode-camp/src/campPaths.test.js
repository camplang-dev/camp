const assert = require("assert");
const test = require("node:test");
const path = require("path");
const {
  createDebugConfiguration,
  findNearestCampBuild,
  getCompilerPath,
  getDebugAdapterPath
} = require("./campPaths");

function fakeFs(files, directories = []) {
  const fileSet = new Set(files);
  const directorySet = new Set(directories);
  return {
    statSync(value) {
      return {
        isDirectory() {
          return directorySet.has(value);
        }
      };
    },
    existsSync(value) {
      return fileSet.has(value) || directorySet.has(value);
    },
    readdirSync(directory) {
      const prefix = directory.endsWith(path.sep) ? directory : directory + path.sep;
      const names = new Set();
      for (const file of fileSet) {
        if (file.startsWith(prefix)) {
          const rest = file.slice(prefix.length);
          if (rest.length > 0 && !rest.includes(path.sep)) {
            names.add(rest);
          }
        }
      }
      return Array.from(names);
    }
  };
}

test("derives campc and camp-dap beside camp-lsp", () => {
  assert.strictEqual(getCompilerPath("/repo/bin/camp-lsp", "darwin"), "/repo/bin/campc");
  assert.strictEqual(getDebugAdapterPath("", "/repo/bin/camp-lsp", "darwin"), "/repo/bin/camp-dap");
  assert.strictEqual(getCompilerPath("C:\\repo\\bin\\camp-lsp.exe", "win32"), "C:\\repo\\bin\\campc.exe");
  assert.strictEqual(getDebugAdapterPath("", "C:\\repo\\bin\\camp-lsp.exe", "win32"), "C:\\repo\\bin\\camp-dap.exe");
});

test("configured debug adapter path wins", () => {
  assert.strictEqual(getDebugAdapterPath("/custom/camp-dap", "/repo/bin/camp-lsp", "darwin"), "/custom/camp-dap");
});

test("finds nearest campbuild and builds launch config", () => {
  const source = "/work/app/src/main.camp";
  const project = "/work/app/app.campbuild";
  const fsImpl = fakeFs([source, project], ["/", "/work", "/work/app", "/work/app/src"]);
  assert.strictEqual(findNearestCampBuild(source, fsImpl, path), project);

  assert.deepStrictEqual(createDebugConfiguration(source, "camp", "lldb", fsImpl, path), {
    name: "Debug Camp",
    type: "camp",
    request: "launch",
    project,
    cwd: "/work/app",
    args: [],
    stopOnEntry: false,
    backend: "lldb"
  });
});

test("falls back to active camp file when no campbuild exists", () => {
  const source = "/work/tool/main.camp";
  const fsImpl = fakeFs([source], ["/", "/work", "/work/tool"]);
  assert.deepStrictEqual(createDebugConfiguration(source, "camp", "auto", fsImpl, path), {
    name: "Debug Camp",
    type: "camp",
    request: "launch",
    project: source,
    cwd: "/work/tool",
    args: [],
    stopOnEntry: false,
    backend: "auto"
  });
});
