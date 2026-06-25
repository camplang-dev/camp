# Outstanding Bugs

Next bug number: CAMPC-008.

- **CAMPC-001:** Recursive glob patterns do not match files directly under the
  glob root. A pattern such as `src/**/*.camp` currently requires at least one
  subdirectory below `src`, so it matches `src/ui/button.camp` but misses
  `src/main.camp`. This makes project files more awkward than they should be,
  because common source layouts need both `src/*.camp` and `src/**/*.camp`.
  Update glob matching so `**/` can match zero or more directories.

- **CAMPC-002:** Live local package sources work during compilation, but the
  package-management commands are still version-oriented. A local source root
  such as `packages/ext-win32/src` can be consumed with `--use-source`, but
  commands like restore, search, and install still assume published package
  versions. That will feel strange for packages that are intentionally developed
  live and revised frequently. Add package-management behavior that understands
  live source roots without requiring a publish/version cycle.

- **CAMPC-004:** Transitive project references are not covered by a focused
  regression test. A sample layout where app references library B, and library B
  references library A, should build without the app needing to repeat B's
  private project references by hand. Add a test that proves generated API
  headers, static libraries, and target-qualified artifact directories flow
  correctly through that chain.

- **CAMPC-005:** Project references do not explicitly detect cycles. A project
  reference graph such as A referencing B while B references A can recurse
  through builds until it fails indirectly or exhausts resources. Track the
  active project-reference stack and report a direct diagnostic that names the
  cycle.

- **CAMPC-006:** Derived class constructors emit an invalid base-constructor
  call in C. When a class derives from another ordinary class, both explicit and
  synthesized derived constructors can emit `Base_op_initnew()` without passing
  the derived `this` pointer converted to the base type. MSVC rejects the
  generated C with "too few arguments for call." Work around by avoiding
  inheritance in native-built classes until base constructor emission passes the
  receiver.

- **CAMPC-007:** Native link dependencies from packages are not propagated to
  final consumers. A library or executable that uses `ext-win32` gets the
  package API/static artifact, but the package's `#build --reference user32 gdi32
  kernel32` does not flow into the final link command. This is especially visible
  through project references: a referenced static library can call `MessageBoxW`,
  while the consuming executable links only the `.lib` and misses `user32`. Work
  around by adding the native `--reference` values directly to the executable
  project until package/project-reference metadata carries native references
  transitively.
