# Camp Editor Support

Camp release distributions include optional editor setup scripts. Run the script
for the editor you use, and rerun the same script after updating Camp.

From a Unix-like shell:

```sh
extras/editors/vscode/install.sh
extras/editors/sublime/install.sh
extras/editors/micro/install.sh
extras/editors/vim/install.sh
extras/editors/fresh/validate.sh
```

From PowerShell:

```powershell
& "extras\editors\vscode\install.ps1"
& "extras\editors\sublime\install.ps1"
& "extras\editors\micro\install.ps1"
& "extras\editors\vim\install.ps1"
```

VS Code installs the bundled extension, which includes syntax highlighting,
language-server support, and debugging. Sublime Text, micro, and Vim install
syntax highlighting and configure language-server support when the editor setup
has a supported path. Debug adapter setup is packaged only for VS Code for now.
Fresh installs Camp support through its package manager; see
`extras/editors/fresh/README.md`.

Each installer supports `--help` or `-Help` for editor-specific options such as
dry-run mode, syntax-only mode, and LSP opt-out.
