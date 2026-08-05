+++
nav_title = "10. Names And Symbols"
+++

# Names, Imports, Visibility, And Symbols

Names matter most when code becomes a library. Inside one file, a name is
mostly a convenience. At a module boundary, a name becomes part of the source
API, the metadata story, and sometimes the native ABI.

Camp separates those concerns. `using` affects source lookup. `namespace`
declares the source/API namespace for this file. `internal` lets other Camp
source in the current project see a declaration. `public` lets statically
linked Camp modules in the same final artifact see it. `export` puts a
declaration or projection on the external boundary. `@symbol` controls the
native symbol used for C-facing emission or interop.

Those are deliberately different tools. This chapter explains when to use each
one, and how the choices show up for consumers of a Camp library.

## Qualified Names

Use `::` to qualify a source name:

```camp
Std::Console.writeLine("ready");
```

The namespace prefix is a source lookup path, not a runtime object. Member
access still uses `.` after the qualified type, static class, or value:

```camp
Std::Console.writeLine("ready");
```

Here `Std::Console` is the qualified static class name, and `.writeLine` is
ordinary static member access.

Qualified names are useful at module boundaries, in examples, and anywhere two
imports would otherwise make an unqualified name ambiguous.

## `using`

`using` imports names for source lookup in the current file:

```camp
using Std;
```

It does not re-export those names, change their namespace, or affect their C
symbols. It only makes source code in this file easier to write.

When the standard library is enabled, each ordinary source file gets an implicit
root import equivalent to `using Std;`. Do not write it just as boilerplate. If
the file contains an explicit root `Std` import, that explicit import replaces
the implicit one for that file:

```camp
using Std as S;
```

or:

```camp
using Std { Console };
```

With either form, only the explicit alias or selected names are available from
the root `Std` namespace. Imports of unrelated namespaces or child namespaces
do not replace the implicit root import.

Selected imports bring in only the names you ask for:

```camp
using Std { min, max };

int smaller = min(left, right);
```

Import aliases give a namespace a shorter local name. For example, a platform
binding might export a deep namespace that you shorten at the top of a file:

```camp
using Native::Windows as Win;

Win::createWindow(title);
```

When a file imports several libraries with overlapping vocabulary, prefer
selected imports or qualification. A little explicitness at the call site is
better than making the reader guess which module supplied the name.

## `namespace`

`namespace` declares the source/API namespace for declarations in this file:

```camp
namespace Imaging;

export enum ImageError
{
	OK = 0,
	FAILED
}

export struct Size
{
	int width;
	int height;
}
```

A Camp consumer can qualify the exported declaration:

```camp
Imaging::Size imageSize = default;
```

This is source and API structure. It does not allocate a namespace object, and
it does not create a source prefix you write into ordinary names. Think of it
as the library's Camp name on the shelf.

For exported or native-facing declarations, the namespace also contributes to
the default emitted ABI name. A function declared as `Pixel::open` has a
source name of `open`, but its default C symbol is prefixed for the namespace.
Camp source still calls `open`, imports `Pixel`, or qualifies the source name
as `Pixel::open`; it does not call the generated ABI spelling.

## Private, `internal`, `public`, And `export`

Declarations are private unless marked otherwise.

```camp
struct DecodeState
{
	nuint offset;
}
```

A private declaration can support exported code, but consumers cannot name it.

`internal` makes a declaration visible to other Camp files in the current project:

```camp
internal struct DecodeState
{
	nuint offset;
}
```

Use `internal` for library-internal surface: helpers, shared implementation
types, or cross-file building blocks that are not part of the public ABI.

`public` makes a declaration available across static Camp modules that are
linked into one final artifact:

```camp
public struct PixelBuffer
{
	byte[] bytes;
}
```

Use `public` for declarations that package references or project references
need to compile against, but that should not automatically appear in the
external API of a shared library.

`export` puts a declaration directly on the external API and ABI boundary:

```camp
export struct ImageInfo
{
	int width;
	int height;
}
```

The distinction matters. An `internal` declaration may appear in broader
metadata views or private generated headers. A `public` declaration is visible
inside the final artifact. An `export` declaration appears in the exported Camp
API and the public native surface where the target emits one.

## Export Projections

Export projections let a module export a selected external view of a `public`
declaration. The source declaration keeps its original Camp name inside the
artifact; the projection controls the external API view.

```camp
namespace Pixel;

public enum PixelFormat
{
	RGBA,
	BGRA
}

export PixelFormat;
```

You can rename the external type:

```camp
export PixelFormat as px_format;
```

For types, a bare projection exports the type and all exportable in-scope
declared members that are visible enough to export. `{}` exports only the type:

```camp
export Container {};
export Container {} as px_container;
```

A member block exports selected in-scope members, optionally with external
member names. The type rename appears at the end, and renamed member symbols
use the projected type name as their prefix:

```camp
export Container
{
	Container as make,
	~Container as unmake,
	getValue as value,
	setValue as put_value,
	DEFAULT_CAPACITY
} as px_container;
```

Constructors and destructors are named the same way they are declared inside the
type. For overloaded methods, project the real declared callable name rather
than relying on a display overload name.

Members declared out of scope can be projected individually:

```camp
export Container.getDefaultCapacity as default_capacity;
```

Projecting a member does not project its containing type. Every type that
appears in an exported signature must itself be exported or projected by the
same artifact. The compiler reports missing dependency projections instead of
silently leaking internal names.

Class interface implementations are also explicit in the projection. A class
projection can list the interfaces that should remain visible outside the
artifact:

```camp
export Image { draw, dispose }: Drawable, Disposable;
```

Interfaces implemented internally but not listed are not exposed through the
projected external view. Struct interface implementations are not exported in
this V1 projection model.

Base class relationships are implicit. If both a base class and a derived class
are exported or projected, consumers can see the relationship. If only the
derived class is exported, the external view simply omits the base relationship.
Listing a base class in the projection interface list is an error; export the
base class separately when that relationship should be visible.

Only one export projection of a source declaration is allowed in a final
artifact. That keeps the external API from having two competing public names for
the same declaration.

## Exported Types And ABI Shape

You have already learned that exported structs and classes make different ABI
promises. Naming and visibility are what put those promises on the boundary.

```camp
export struct ImageInfo
{
	int width;
	int height;
}

export class Image;
```

Conceptually, a C-facing external header can expose the struct layout and keep
the class opaque:

```c
typedef struct Image Image;

typedef struct ImageInfo {
	int width;
	int height;
} ImageInfo;
```

If callers need visible data layout, export a struct. If callers need a stable
handle whose implementation can change, export a class pointer surface.

## Exported Functions

An exported function is callable from the external boundary:

```camp
export ImageInfo getImageInfo(Image* image)
{
	return { .width = 320, .height = 200 };
}
```

Expanded Camp forms still have source-level spelling in Camp API output, but
the C ABI receives the lowered shape. For example:

```camp
export Image* openImage(const char[] path, thrown ImageError error);
```

Because `openImage` is exported, every type in its public signature must be
exported too. In this example, that means `Image` and `ImageError`. If another
exported function returns `ImageInfo`, that struct must be exported as well.

That Camp signature may have a C-facing shape like:

```c
Image *openImage(const char *path, size_t path_length, ImageError *error);
```

The exact spelling belongs to the selected target and emitter. The design
lesson is stable: arrays, delegates, `thrown`, `within`, and other source
forms are part of the Camp signature, while the native header receives the ABI
components needed to call it.

## `extern`

`extern` says the implementation lives outside the current Camp body:

```camp
extern int nativeHelper(int value);
```

`extern` does not automatically export the declaration. A plain `extern`
helper can stay private:

```camp
@symbol("strlen")
extern nuint nativeStringLength(const char* text);

export nuint textLength(const char* text)
{
	return nativeStringLength(text);
}
```

Use `export extern` when the native declaration itself is part of your public
surface:

```camp
export extern void flushLog();
```

Use a private `extern` plus an exported wrapper when the native shape is too
raw for ordinary Camp callers.

## `@symbol`

`@symbol("Name")` overrides the native symbol emitted or imported for a
declaration:

```camp
@symbol("SetWindowTextA")
extern bool setWindowText(int window, astring text);
```

Camp callers should use the source name:

```camp
bool ok = setWindowText(window, "Ready");
```

The emitted or imported C symbol is `SetWindowTextA`:

```c
bool SetWindowTextA(int window, const char *text);
```

Use `@symbol` for native compatibility, stable ABI names, platform APIs, and
cases where the C name cannot or should not match the Camp source name.

```camp
@symbol("camp_image_open")
export extern Image* openImage(const char[] path, thrown ImageError error);
```

A public C-facing declaration may then use the symbol name:

```c
Image *camp_image_open(const char *path, size_t path_length, ImageError *error);
```

`@symbol` does not change the Camp namespace, does not make a private
declaration exported, and should not be used as a source organization tool. It
changes native symbol identity.

This matters most for namespaced `extern` declarations. If the native function
already exists under an exact platform spelling, give the Camp wrapper a
readable source name and attach `@symbol` for the ABI name:

```camp
namespace Posix;

@symbol("getpid")
public extern int getpid();
```

Without `@symbol`, the declaration uses Camp's normal namespace-prefixed
default symbol, which is what you usually want for Camp-authored libraries but
not for existing platform APIs.

Camp API output preserves the Camp declaration name and the `@symbol` attribute
instead of renaming the declaration to the native symbol.

## Symbol Rules

Symbol overrides are checked because they affect linkable names. The symbol
must be a valid emitted identifier for the target, must not be a reserved word,
and must not collide with another emitted symbol.

Valid uses include ABI-visible type declarations (`class`, `struct`,
`interface`, `enum`, and `newtype`), functions and methods, variables, static
fields, inline constants, and enum members where those declarations support
native symbols:

```camp
@symbol("ImageCount")
export int imageCount = 0;
```

```camp
@symbol("NativeWidget")
export class Widget
{
	export static int getDefaultSize() => 12;

	@symbol("NativeWidget_reset_now")
	export static void resetNow()
	{
	}
}
```

```camp
@symbol("Difficulty")
export enum DifficultyLevel : ushort
{
	@symbol("DIFFICULTY_EASY") EASY = 0,
	HARD
}
```

For a type declaration, the override becomes the type's effective native
symbol. Generated ABI helpers and default static member symbols use that
effective type symbol as their prefix, so `Widget.getDefaultSize` above emits
with a `NativeWidget_` prefix. A member-level `@symbol` overrides the full
member symbol and wins over the containing type's default prefix.

When no `@symbol` is present, namespaced declarations receive namespace-based
default ABI names. Top-level functions, globals, and constants use a namespace
prefix separated with `_`; type names use a joined namespace/type prefix, and
members use the containing type's effective native symbol as their prefix.
These are ABI names only. Camp source continues to use source names, imports,
member access, and `Namespace::name` qualification.

Aliases, parameters, generic parameters, and instance fields do not accept
`@symbol`.

Once a declaration has a symbol override, the default generated native name is
not the ABI name to depend on. Treat the override as the stable
native name.

## Aliases

An alias gives another source name to an existing named thing:

```camp
export alias TCHAR = wchar;

export extern void setTitle(TCHAR* text);
```

Aliases are not nominal types. They help with platform spelling,
compatibility, and readability. If two values need to be kept distinct even
though they share representation, use a `newtype` instead.

Generated Camp API output may preserve the exported alias declaration, while C
emission uses the resolved underlying spelling. Conceptually:

```c
void setTitle(wchar_t *text);
```

Use aliases to smooth a boundary, not to hide a major semantic difference.

## Public Headers And Private Headers

When Camp emits C for a library, exported declarations and export projections
belong in the public native surface. `internal` and `public` declarations can
still be visible inside the build, but they do not become public ABI by
themselves.

```camp
export int exportedValue = 3;
internal int internalValue = 4;
public int artifactValue = 5;

export int exportedAdd(int value)
{
	return value + exportedValue;
}

internal int internalAdd(int value)
{
	return exportedAdd(value) + internalValue;
}
```

A shared-library C header might expose only the exported pieces:

```c
extern int exportedValue;
int exportedAdd(int value);
```

The private generated header can still contain `internalValue` and `internalAdd`
for files inside the same build, and artifact-internal Camp API views can still
contain `artifactValue`. That split is why `internal`, `public`, and `export`
are separate words.

## Organizing A Small Library

A small library often ends up with this shape:

```camp
namespace Imaging;

internal struct DecodeState
{
	nuint offset;
}

public struct ImageInfo
{
	int width;
	int height;
}

public class Image;

public enum ImageError
{
	OK = 0,
	FAILED
}

@symbol("camp_image_open")
public extern Image* openImage(const char[] path, thrown ImageError error);

public ImageInfo getImageInfo(Image* image)
{
	return { .width = 320, .height = 200 };
}

export Image;
export ImageInfo;
export ImageError;
export openImage;
export getImageInfo;
```

The choices tell different audiences what they need:

- Camp callers see `Imaging::Image`, `Imaging::ImageInfo`, `openImage`, and
  `getImageInfo`.
- C callers see exported symbols such as `camp_image_open`.
- Other files in the same Camp build can use `DecodeState`.
- External consumers cannot depend on `DecodeState` or the fields of `Image`.

That is the point of the naming system: source names stay pleasant, exported
API stays intentional, and native symbols stay stable where the ABI needs them.
