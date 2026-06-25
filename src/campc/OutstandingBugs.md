# Outstanding Bugs

Next bug number: CAMPC-006.

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
