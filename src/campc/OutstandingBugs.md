# Outstanding Bugs

Next bug number: CAMPC-008.

- **CAMPC-002:** Live local package sources work during compilation, but the
  package-management commands are still version-oriented. A local source root
  such as `packages/ext-win32/src` can be consumed with `--use-source`, but
  commands like restore, search, and install still assume published package
  versions. That will feel strange for packages that are intentionally developed
  live and revised frequently. Add package-management behavior that understands
  live source roots without requiring a publish/version cycle.

- **CAMPC-007:** Native link dependencies from packages are not propagated to
  final consumers. A library or executable that uses `ext-win32` gets the
  package API/static artifact, but the package's `#build --reference user32 gdi32
  kernel32` does not flow into the final link command. This is especially visible
  through project references: a referenced static library can call `MessageBoxW`,
  while the consuming executable links only the `.lib` and misses `user32`. Work
  around by adding the native `--reference` values directly to the executable
  project until package/project-reference metadata carries native references
  transitively.
