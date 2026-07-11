# Package System

## Package Specs

A package spec has a name and optional version:

```text
textlib
textlib@1.2.0
```

Package references can appear on the command line or in `#build` pragmas.

## Version Specs

Versions use semantic-version-like ordering with major, minor, patch, and an
optional suffix. A package lookup can request an exact version or choose from
available package folders according to compiler package resolution.

## Link Kinds

Dependency link kinds describe how a dependency is consumed:

| Kind | Meaning |
|---|---|
| `api` | Use API surface only. |
| `static` | Build or link a static artifact. |
| `shared` | Build or link a shared artifact. |

Package and project-reference specs can include link-kind suffixes where the
command accepts them.

## Package Sources

Package sources map a source name to a local folder.

```sh
campc pkg add-source local-libs ../packages --local app.campbuild
```

Sources can be stored globally or in a local build file.

## Global And Local Package Roots

The global package root is under the compiler repository cache. The local
package root is `cache/pkg` under the current working directory. Local installs
are preferred for project-specific dependency restoration.

## Installing And Uninstalling

`campc pkg install pkg@version` copies a package from configured sources into a
package root. `--global` selects the global package root. `uninstall` removes an
installed package.

## `--use`, `--use-source`, And `#build`

`--use` adds an installed package dependency. `--use-source` defines a package
source for resolution. The same options can be written in `#build` pragmas.

```camp
#build --use-source local-libs ../packages
#build --use textlib@1.2.0
```

## Restore Behavior

`campc restore` reads effective package uses and package sources, then installs
missing packages into the local package root when they are not already
available globally or locally.

## Project References

`--project-reference` builds and references another Camp project response file.
Project references can specify a link kind with a suffix such as `:static` or
`:shared`. The compiler detects project-reference cycles and reuses current
artifacts when inputs are up to date.
