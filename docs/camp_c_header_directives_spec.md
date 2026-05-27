# C Header Directives and Foreign Declarations

Camp can include C header files in the generated C source for a Camp source file.

This feature exists for foreign interop. A C header can contain arbitrary C preprocessor content, declarations, macros, pragmas, and target-specific material that the Camp compiler does not parse or understand. For that reason, a Camp file must explicitly declare any C header it depends on at the top of the file.

Camp does not implicitly include C headers merely because a foreign symbol is used. Future tooling may provide wider project-level convenience, but the language-level rule is file-local and explicit.

## Header directive prelude

C header directives may appear only in the file prelude.

The file prelude is the initial sequence of file-level directives before ordinary Camp declarations. Once an ordinary declaration appears, no further C header directive may appear in that file.

There are two C header directives:

```camp
#include <header.h>
#include "header.h"

#require <header.h>
#require "header.h"
```

Both forms name a C header using C-style include spelling.

Angle-bracket and quoted header names are distinct. The Camp compiler preserves the written spelling when emitting C `#include` directives. The compiler does not attempt to canonicalize include paths or determine that two different header spellings refer to the same physical file.

A file may not name the same header spelling with both `#include` and `#require`. `#require` is the stronger form.

## Header identity

A header identity is the exact C include spelling written in the directive.

These are distinct header identities:

```camp
#require <sys/stat.h>
#require "sys/stat.h"
#require <../include/sys/stat.h>
```

This rule is intentional. Camp is not a C preprocessor and does not model include search paths.

## `#include`: private foreign header dependency

`#include` declares a private dependency on a C header.

```camp
#include <string.h>

extern void* memcpy(void* dst, const void* src, nuint count);

void copyBytes(byte[] dst, const byte[] src)
{
	memcpy(dst.elements, src.elements, src.length);
}
```

Rules:

- the generated private header for the Camp source file includes the named C header
- declarations exported from other Camp files and associated with the same header may be used, subject to ordinary visibility and namespace rules
- ordinary `extern` declarations associated with the header are allowed
- `export extern` declarations associated with the header are not allowed
- exported declarations from this file may not mention declarations associated with the header in their public ABI surface

`#include` is the correct form when a Camp source file needs a C declaration only for its own implementation.

The dependency does not leak into the generated public header.

## `#require`: public foreign header dependency

`#require` declares a public-capable dependency on a C header.

```camp
#require <sqlite3.h>

export extern struct sqlite3;
export extern int sqlite3_open(char* filename, sqlite3** db);
export extern int sqlite3_close(sqlite3* db);
```

Rules:

- the generated private header for the Camp source file includes the named C header
- declarations exported from other Camp files and associated with the same header may be used, subject to ordinary visibility and namespace rules
- ordinary `extern` declarations associated with the header are allowed
- `export extern` declarations associated with the header are allowed
- exported declarations from this file may mention declarations associated with the header in their public ABI surface
- if the file's exported ABI surface depends on declarations associated with the header, the generated public header includes the named C header

`#require` is the correct form when the Camp file defines a public wrapper or public ABI surface that depends on a C header.

## Associated foreign declarations

An `extern` declaration may be associated with a C header named by `#include` or `#require` in the same file.

```camp
#require <sys/stat.h>

export extern struct stat;
export extern int stat(char* path, stat* buffer);
```

An associated foreign declaration declares a Camp-visible symbol whose definition, storage, layout, or callable implementation is supplied by the C header and linked C code.

Associated `extern` declarations generate no C implementation code.

They are a signal to the Camp compiler that the declaration exists outside Camp and may be named in Camp type checking and code generation.

This rule applies uniformly to foreign declarations that Camp can describe, including:

- functions
- variables
- enums
- structs
- params declarations where appropriate
- newtypes
- classes or opaque types where appropriate

A file may also contain `extern` declarations that are not associated with a header directive. Those follow the ordinary foreign declaration rules and do not gain header-based visibility or header emission behavior.

## Visibility from other Camp files

A Camp file may use exported foreign declarations from another Camp file only when all ordinary visibility rules are satisfied.

Header directives do not replace namespace imports.

For example, a file may still need `using` or namespace qualification to name exported declarations from another module.

```camp
#include <sqlite3.h>
using Sqlite;

void closePrivate(sqlite3* db)
{
	sqlite3_close(db);
}
```

The header relationship authorizes the foreign dependency. Ordinary Camp visibility rules still decide whether the symbol can be named directly.

## Export restrictions for `#include`

A file that uses `#include` may not export a declaration whose public ABI surface mentions a declaration associated with that included header.

```camp
#include <sys/stat.h>

extern struct stat;
extern int stat(char* path, stat* buffer);

export bool exists(StringView path)
{
	...
}

export stat getStat(StringView path); // ERROR
```

The first export is valid if its public ABI surface does not mention `stat` or another declaration associated with `<sys/stat.h>`.

The second export is invalid because it would require users of the generated public header to understand a type from a private C header dependency.

Use `#require` when the exported ABI surface depends on the C header.

```camp
#require <sys/stat.h>

export extern struct stat;
export extern int stat(char* path, stat* buffer);

export stat getStat(StringView path); // OK
```

## `export extern`

`export extern` is allowed only for declarations associated with a header named by `#require`.

```camp
#require <sqlite3.h>

export extern struct sqlite3;
export extern int sqlite3_open(char* filename, sqlite3** db);
```

This means:

- the declaration is visible as part of the Camp module's exported ABI surface
- the declaration itself still generates no implementation code
- files that use the declaration must name the same header identity with `#include` or `#require`

A file that names the header with `#include` may use the exported declaration privately, but may not re-export it or expose it through its own exported ABI surface.

A file that names the header with `#require` may use the declaration in its own exported ABI surface.

## Public and private header emission

The generated private header for a Camp source file includes every C header named by `#include` or `#require` in that file.

The generated public header includes a C header named by `#require` only when the file's exported ABI surface depends on declarations associated with that header.

A `#include` header is never emitted into the generated public header merely because the source file used it privately.

A `#require` header that is not used by the exported ABI surface may still be emitted in the private header only. The compiler may warn when `#require` could be reduced to `#include`.

## Exported non-extern declarations that use foreign types

A non-`extern` exported declaration may mention a foreign declaration associated with a header only if that header was named with `#require`.

```camp
#require <sqlite3.h>
using Sqlite;

export class Database
{
	sqlite3* handle;

	Database(sqlite3* handle)
	{
		this.handle = handle;
	}
}
```

The generated public header must include `<sqlite3.h>` if the public ABI surface exposes `sqlite3` or another associated declaration.

If the foreign type is used only in private fields of an exported class whose public layout is opaque, the public ABI surface may not need the C header. In that case, the generated private header includes the C header, and the generated public header includes it only if an exported callable surface or layout-visible exported type requires it.

## Examples

### Private use of a C function

```camp
#include <string.h>

extern void* memcpy(void* dst, const void* src, nuint count);

void copyBytes(byte[] dst, const byte[] src)
{
	memcpy(dst.elements, src.elements, src.length);
}
```

The generated private header includes `<string.h>`. The generated public header does not.

### Public wrapper over a C library

```camp
#require <sqlite3.h>

export extern struct sqlite3;
export extern int sqlite3_open(char* filename, sqlite3** db);
export extern int sqlite3_close(sqlite3* db);

export class Database
{
	sqlite3* handle;
}
```

The `export extern` declarations are allowed because the file uses `#require`.

The generated public header includes `<sqlite3.h>` if any exported public ABI surface requires `sqlite3` to be visible.

### Private consumer of a public wrapper

```camp
#include <sqlite3.h>
using Sqlite;

void closeNow(sqlite3* db)
{
	sqlite3_close(db);
}
```

This file may use exported declarations associated with `<sqlite3.h>`, but it may not expose them in its own exported ABI surface.

### Public extension of a wrapper

```camp
#require <sqlite3.h>
using Sqlite;

export int getDatabaseStatus(sqlite3* db)
{
	...
}
```

This file may expose `sqlite3` in its exported ABI surface because it uses `#require`.

## Diagnostics

The compiler should diagnose these cases:

```camp
void f();
#include <x.h> // ERROR: C header directive after ordinary declaration
```

```camp
#include <x.h>
#require <x.h> // ERROR: same header spelling named by both forms
```

```camp
#include <x.h>

export extern int x_doWork(); // ERROR: export extern requires #require
```

```camp
#include <x.h>

extern struct X;
export void useX(X* value); // ERROR: exported ABI surface depends on #include-only header
```

```camp
#require <x.h>

extern struct X;
export void useX(X* value); // OK
```

The compiler may also warn when:

- a header directive names a header that is never associated with a used foreign declaration
- `#require` is used but no exported ABI surface depends on that header
- multiple header spellings appear likely to refer to the same physical header, if the build environment can determine that

## Design summary

`#include` is for private use of a C header.

`#require` is for public ABI dependency on a C header.

`extern` declares Camp-visible symbols supplied outside Camp and generates no implementation code.

`export extern` is allowed only when the source file uses `#require` for the associated header.

A file that wants to use foreign declarations privately names the header with `#include`.

A file that wants to export foreign declarations, or export declarations whose ABI surface depends on them, names the header with `#require`.
