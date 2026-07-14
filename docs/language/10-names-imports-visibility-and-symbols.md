# Names, Imports, Visibility, And Symbols

Names matter most when code becomes a library. Inside one file, a name is
mostly a convenience. At a module boundary, a name becomes part of the source
API, the metadata story, and sometimes the native ABI.

Camp separates those concerns. `using` affects source lookup. `export as`
places exported declarations in an API namespace. `public` lets other Camp
source in the build see a declaration without making it a public ABI promise.
`export` puts a declaration on the public boundary. `@symbol` controls the
native symbol used for C-facing emission or interop.

Those are deliberately different tools. This chapter explains when to use each
one, and how the choices show up for consumers of a Camp library.

## Qualified Names

Use `::` to qualify a source name:

```camp
Std::Console.writeLine("ready");
```

The namespace prefix is a source lookup path, not a runtime object. Member
access still uses `.` after the qualified type or value:

```camp
Std::Console.writeLine("ready");
```

Here `Std::Console` is the qualified type name, and `.writeLine` is ordinary
static member access.

Qualified names are useful at module boundaries, in examples, and anywhere two
imports would otherwise make an unqualified name ambiguous.

## `using`

`using` imports names for source lookup in the current file:

```camp
using Std;
```

It does not re-export those names, change their namespace, or affect their C
symbols. It only makes source code in this file easier to write.

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

## `export as`

`export as` declares the namespace where this file or module exposes its
exported declarations:

```camp
export as Imaging;

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
it does not by itself choose a C symbol. Think of it as the library's Camp name
on the shelf.

## Private, `public`, And `export`

Declarations are private unless marked otherwise.

```camp
struct DecodeState
{
	nuint offset;
}
```

A private declaration can support exported code, but consumers cannot name it.

`public` makes a declaration visible to other Camp files in the same build:

```camp
public struct DecodeState
{
	nuint offset;
}
```

Use `public` for library-internal surface: helpers, shared implementation
types, or cross-file building blocks that are not part of the public ABI.

`export` puts a declaration on the public API and ABI boundary:

```camp
export struct ImageInfo
{
	int width;
	int height;
}
```

The distinction matters. A `public` declaration may appear in broader metadata
views or private generated headers. An `export` declaration appears in the
exported Camp API and the public native surface where the target emits one.

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

Conceptually, a C-facing public header can expose the struct layout and keep
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

An exported function is callable from the public boundary:

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

## Symbol Rules

Symbol overrides are checked because they affect linkable names. The symbol
must be a valid emitted identifier for the target, must not be a reserved word,
and must not collide with another emitted symbol.

Valid uses include functions and methods, variables, static fields, inline
constants, enum declarations, and enum members where those declarations
support native symbols:

```camp
@symbol("ImageCount")
export int imageCount = 0;
```

```camp
@symbol("Difficulty")
export enum DifficultyLevel : ushort
{
	@symbol("DIFFICULTY_EASY") EASY = 0,
	HARD
}
```

For struct, class, interface, newtype, alias, parameter, and instance-field
declarations, check the generated header before depending on a native spelling.
Those surfaces may have ABI-facing names derived from the Camp declaration
rather than from a source-level override.

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

When Camp emits C for a library, exported declarations belong in the public
native surface. `public` declarations can still be visible inside the build,
but they do not become public ABI by themselves.

```camp
export int exportedValue = 3;
public int publicValue = 4;

export int exportedAdd(int value)
{
	return value + exportedValue;
}

public int publicAdd(int value)
{
	return exportedAdd(value) + publicValue;
}
```

A shared-library C header might expose only the exported pieces:

```c
extern int exportedValue;
int exportedAdd(int value);
```

The private generated header can still contain `publicValue` and `publicAdd`
for files inside the same build. That split is why `public` and `export` are
separate words.

## Organizing A Small Library

A small library often ends up with this shape:

```camp
export as Imaging;

public struct DecodeState
{
	nuint offset;
}

export struct ImageInfo
{
	int width;
	int height;
}

export class Image;

export enum ImageError
{
	OK = 0,
	FAILED
}

@symbol("camp_image_open")
export extern Image* openImage(const char[] path, thrown ImageError error);

export ImageInfo getImageInfo(Image* image)
{
	return { .width = 320, .height = 200 };
}
```

The choices tell different audiences what they need:

- Camp callers see `Imaging::Image`, `Imaging::ImageInfo`, `openImage`, and
  `getImageInfo`.
- C callers see exported symbols such as `camp_image_open`.
- Other files in the same Camp build can use `DecodeState`.
- External consumers cannot depend on `DecodeState` or the fields of `Image`.

That is the point of the naming system: source names stay pleasant, exported
API stays intentional, and native symbols stay stable where the ABI needs them.
