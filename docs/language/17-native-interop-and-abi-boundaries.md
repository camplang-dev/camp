+++
nav_title = "17. Native Interop"
+++

# Native Interop And ABI Boundaries

Camp is designed for programs that have to meet C at the door. Sometimes that
means calling a platform function directly. Sometimes it means exposing a clean
Camp library to C callers. Most of the time it means both: a raw native binding
near the boundary, then a smaller and safer Camp surface for the rest of the
program.

You have already seen the pieces: exported functions, structs, classes,
newtypes, arrays, strings, delegates, interfaces, iterators, async functions,
`thrown`, `within`, `@symbol`, call specs, and target specs. This chapter puts
them together from the ABI side. The C snippets here are intentionally
C-shaped sketches: the selected target still owns exact spelling, typedefs,
export/import decoration, and helper names, but the shapes are the ones a Camp
programmer should expect.

## A First Native Wrapper

The smallest interop surface is an `extern` function:

```camp
@symbol("puts")
extern int cPuts(string text);

void writeNativeLine(const char[] text)
{
	string terminated = text.copyString() finally delete;
	cPuts(terminated);
}
```

The imported C function is the familiar one:

```c
int puts(const char *text);
```

The Camp wrapper accepts counted UTF-8 text, makes an owned zero-terminated copy
for the native call, and deletes the copy when the function exits. That is the
pattern you will use often: keep the raw shape close to the ABI, then expose
the Camp shape you want callers to use.

## Source Surface And Native Shape

Camp signatures are source contracts. Native headers receive the ABI components
needed to call those contracts from C.

| Camp source form | ABI idea |
|---|---|
| `int`, `uint`, `nuint`, `bool` | target primitive C spelling |
| `struct Size` | visible struct layout when exported |
| `class Image` | opaque handle type when exported |
| `extern class NativeWindow` | foreign opaque object pointer |
| `byte[]` | element pointer plus length |
| `string` | zero-terminated UTF-8 pointer |
| `wstring`, `astring` | zero-terminated wide/system-code-page pointer |
| `delegate`, `once`, `iter`, `async` | callable pointer plus context/protocol state |
| `out T` | caller-provided result storage |
| `thrown E` | caller-provided error/status storage or status return shape |
| `within allocator` | allocator context supplied through the call |

The important habit is to read both views at once. The Camp declaration says
what the library means. The lowered C shape says what a native caller must
pass.

## Exported Functions

An exported function is part of the public Camp API and the native ABI surface:

```camp
export Image* openImage(const char[] path, thrown ImageError error);
```

A C-facing shape may look like this:

```c
Image *openImage(
	const char *path_elements,
	size_t path_length,
	ImageError *error);
```

The caller supplies the counted text components and an error slot. On success,
`error` receives the default value for `ImageError`; on failure, the function
writes the failure value and follows the Camp `thrown` contract.

When a function is exported, every type needed to understand its public
signature must be exported too. If the function returns `Image*`, callers need
to know that `Image` exists. If it uses `ImageError`, callers need the enum
definition or an exported reference to it.

## Structs And Classes Across The Boundary

Structs expose layout. Classes expose identity without exposing fields.

```camp
export struct ImageInfo
{
	int width;
	int height;
	bool hasAlpha;
}

export class Image;
```

The public C-facing view can expose `ImageInfo` directly and keep `Image`
opaque:

```c
typedef struct Image Image;

typedef struct ImageInfo {
	int width;
	int height;
	bool hasAlpha;
} ImageInfo;
```

This is one of Camp's most important ABI distinctions. Changing an exported
struct field changes the public layout contract. Changing private fields in a
class does not change the public class layout, because callers only hold
pointers to the opaque type.

Use a struct when visible layout is the API. Use a class when the implementation
should be allowed to change without changing the ABI.

## Extern Classes And Opaque Native Values

An `extern class` names a foreign object type whose storage Camp does not own:

```camp
export extern class NativeWindow
{
	export extern NativeWindow();
	export extern ~NativeWindow();
	export extern void show();
}
```

Camp code can name `NativeWindow*`, call the declared extern methods, and use
`new` or `delete` only when the corresponding extern constructor or destructor
surface exists. Camp does not invent instance fields or layout for an extern
class.

A C-facing view is simply opaque:

```c
typedef struct NativeWindow NativeWindow;

NativeWindow *NativeWindow_create(void);
void NativeWindow_show(NativeWindow *window);
```

Classes expose a single `_create` helper rather than public allocation/delete
operator helpers. Destruction and release should be modeled with the native
API's explicit cleanup surface, not by depending on generated class operator
helpers across the public ABI.

Extern classes may inherit only from other extern classes. Ordinary classes and
extern classes stay in separate object models because one is Camp-owned storage
and the other is foreign opaque storage.

## Shadow Classes For Foreign Extension

Some native or cross-module APIs own an opaque object but still provide an
attachment slot for caller state. A shadow class lets Camp layer fields,
interfaces, and virtual shadow behavior over that existing object without
changing the object's native layout.

The base surface must expose shadow hooks with the required signatures. The
method names are not fixed; the compiler selects the hooks by `@getshadow` and
`@setshadow`, not by their names.

```camp
export extern class NativeControl
{
	@getshadow
	export extern escaped void* getShadow(const this);

	@setshadow
	export extern void setShadow(escaped void* value);
}
```

A shadow class derives from that base and stores its own fields in generated
shadow data:

```camp
shadow class ButtonView: NativeControl
{
	int buttonId;

	ButtonView(int buttonId)
	{
		this.buttonId = buttonId;
	}

	void destroyControl()
	{
		delete this;
		delete shadow;
	}
}
```

The `ButtonView*` pointer is still physically a `NativeControl*` pointer. The
compiler allocates a separate shadow data block, installs it through
`@setshadow`, and rewrites `this.clickCount` through `@getshadow`. Interface
slots and virtual shadow dispatch state also live in the shadow data.

Use a shadow class when the base library owns the object and layout, and you
need a Camp view that adds state or interface behavior. Use an ordinary class
when Camp owns the storage. Shadow classes cannot declare destructors.

`delete shadow` deletes only the generated shadow data. It does not delete the
base object, call a destructor, or clear the base object's hook storage. If the
base object needs teardown, call the base API that performs that teardown. The
base class will often own destroy semantics itself, perhaps through reference
counting or by providing a manual destroy method.

A base class may also expose a host interface for lifecycle callbacks:

```camp
export interface IControlHost
{
	void handleEvent() = default;
	void handleDestroy() = default;
}

export extern class NativeControl
{
	export extern void SetHost(escaped IControlHost* host);
}
```

A shadow class can implement that interface and use the destroy callback to
release its shadow data:

```camp
shadow class ButtonView: NativeControl, IControlHost
{
	int buttonId;

	ButtonView(int buttonId)
	{
		this.Host = this;
		this.buttonId = buttonId;
	}

	void handleDestroy(): IControlHost
	{
		delete shadow;
	}
}
```

## Symbols And Native Spelling

The Camp name is for Camp lookup. The native symbol is for the ABI.

```camp
@symbol("CreateFileW")
extern _winapi void* createFileWide(wstring path, uint access);
```

Camp callers use the readable source name:

```camp
void* handle = createFileWide(path, access);
```

The native import uses the requested spelling and call convention:

```c
void *__winapi CreateFileW(const wchar_t *path, unsigned int access);
```

`@symbol` is also useful for exported functions, inline constants, enum types,
enum members, class/struct/interface/newtype declarations, static fields, and
type members whose native names must match an existing ABI. It does not change
source lookup. On type declarations, the override becomes the native type
symbol and the default prefix for generated ABI helpers and static members.
Once a declaration has a symbol override, treat the override as the stable ABI
name and the Camp name as the stable source name.

## Call Specs And Type Specs

Call specs describe native calling conventions:

```camp
extern _cdecl int compareBytes(const void* left, const void* right);

export newtype fn _stdcall int WindowProcedure(
	WindowHandle window,
	uint message,
	nuint wparam,
	nint lparam);
```

The selected target defines which call specs are valid. A call spec is part of
the callable's ABI contract and participates in callable compatibility.

Type specs describe target-specific carrier variants:

```camp
byte* _far videoMemory;
fn* _near interruptHandler;
nint _huge segmentOffset;
```

These matter on targets that distinguish pointer spaces, function-pointer
domains, or natural-integer carriers. A type spec applies to the carrier it is
written on; it does not automatically tunnel through arrays, delegates, generic
arguments, or other constructed types.

## Newtypes, Enums, And Inline Constants

Newtypes are the right way to give native handles a Camp name:

```camp
export newtype FileHandle: nint
{
	export void close();
}
```

The ABI carrier is still `nint`, but Camp source sees `FileHandle` as a
separate type. That prevents accidentally passing a timer handle where a file
handle belongs.

Enums use an explicit integer representation at the ABI boundary:

```camp
@symbol("ImageStatus")
export fixed enum ImageStatus: int
{
	@symbol("IMAGE_STATUS_OK") OK = 0,
	@symbol("IMAGE_STATUS_INVALID_DATA") INVALID_DATA,
	@symbol("IMAGE_STATUS_UNSUPPORTED_FORMAT") UNSUPPORTED_FORMAT
}
```

A C header uses a typedef for the exported enum carrier plus constants:

```c
typedef int ImageStatus;

#define IMAGE_STATUS_OK 0
#define IMAGE_STATUS_INVALID_DATA 1
#define IMAGE_STATUS_UNSUPPORTED_FORMAT 2
```

Inline constants are similar in spirit: they become typed native constants
instead of addressable storage.

```camp
export inline uint MAX_IMAGE_PLANES = 4;
```

```c
#define MAX_IMAGE_PLANES ((unsigned int)4)
```

Use UPPER_SNAKE_CASE for inline constants and enum values. That keeps Camp
source and generated C surfaces aligned.

## Arrays And Counted Text

Camp arrays are pointer-plus-length spans:

```camp
export void writeBytes(FileHandle handle, const byte[] data, thrown IoError error);
```

A C-shaped call surface carries both components:

```c
void writeBytes(
	FileHandle handle,
	const unsigned char *data_elements,
	size_t data_length,
	IoError *error);
```

This is ideal for modern C APIs that already use counted buffers. It is also
the right shape for binary data, strings with embedded zeroes, slices, and
views into larger storage.

For old C APIs that expect only a pointer, say that in the binding:

```camp
extern int nativeWrite(int fd, const void* data, nuint length);

nuint writeRaw(int fd, const byte[] data)
{
	return (nuint)nativeWrite(fd, data.elements, data.length);
}
```

The raw pointer is explicit, and the wrapper decides how to supply the length.

## Zero-Terminated Strings

Use `string`, `wstring`, and `astring` for native APIs that expect
zero-terminated text:

```camp
@symbol("puts")
extern int cPuts(string text);

@symbol("MessageBoxW")
extern _winapi int messageBoxWide(void* parent, wstring text, wstring caption, uint options);
```

Use counted arrays when the API needs an explicit length:

```camp
export void appendUtf8(const char[] text);
export void appendWide(const wchar[] text);
export void appendAscii(const achar[] text);
```

String literals can target either kind of text, but the destination matters:

```camp
string terminated = "ready";
const char[] counted = "ready";
wstring wide = "ready";
astring nativeCodePage = "ready";
```

Do not rely on hidden null termination for counted arrays. If a native API
needs a terminated pointer, create a terminated copy or accept a terminated
string type in the first place.

## `out`, `thrown`, And C-Style Results

`out` is caller-provided success storage:

```camp
export void getImageSize(Image* image, out int width, out int height);
```

The native shape is the familiar pointer-to-result pattern:

```c
void getImageSize(Image *image, int *width, int *height);
```

`thrown` is caller-provided error/status storage:

```camp
export int parsePort(const char[] text, thrown ParseError error);
```

That may lower like this:

```c
int parsePort(
	const char *text_elements,
	size_t text_length,
	ParseError *error);
```

Camp uses `try`, `catch`, and `throw` in source, but it does not require a C++
or managed exception runtime. The ABI still looks like ordinary C error
returning.

For APIs where the status itself should be the ABI return slot, use
`thrown(E)` as the return type:

```camp
export thrown(ParseError) tryParsePort(const char[] text, out int port);
```

A C-shaped view can put the status in the return slot and the success value in
caller storage:

```c
ParseError tryParsePort(
	const char *text_elements,
	size_t text_length,
	int *port);
```

That shape is useful when matching an existing C convention exactly.

## `within` And Allocator Context

`within allocator` is a source-level allocator context. At the ABI boundary it
is still an ordinary value that can be threaded through the call:

```camp
export escaped string readText(FileHandle handle, within allocator, thrown IoError error);
```

A C-shaped view can make the allocator explicit:

```c
char *readText(
	FileHandle handle,
	Allocator *allocator,
	IoError *error);
```

The caller decides the allocation context:

```camp
string text = FileHandle.readText(handle, within arena, catch error)
	finally delete;
```

The important ABI idea is that allocation policy is not hidden in a global
runtime. If an operation allocates across a boundary, the selected allocator is
visible in the source contract and available to the lowered call.

## Raw Function Pointers And Camp Callables

`fn` is a concrete function type. `fn*` is a raw function-address carrier.

```camp
newtype fn _cdecl int CompareBytes(const void* left, const void* right);

extern void qsort(
	void* base,
	nuint count,
	nuint size,
	CompareBytes compare);

extern void storeRawCallback(fn* value);
```

A `fn` value is callable with a known signature. A `fn*` value is not callable
by itself; it is just a raw function address. Cast back to a concrete callable
shape before calling, and use unsafe casts when the ABI has erased information
the compiler cannot prove.

Delegates and `once` callables are richer. They can carry context:

```camp
export void enumerateImages(
	ImageSet* set,
	escaped delegate bool(ImageInfo info) visit);
```

A native sketch looks like the usual callback plus context pair. Because the
Camp type is an anonymous `delegate`, the C function-pointer type can be
anonymous too:

```c
void enumerateImages(
	ImageSet *set,
	bool (*visit_call)(void *context, ImageInfo info),
	void *visit_context);
```

`once` has the same kind of physical ingredients, but a different source
contract: it is meant to be called exactly once. That distinction matters for
ownership and async completion.

## Interfaces Across The ABI

An interface is a named vtable contract. An `Interface*` value is an interface
pointer for a particular object or adapter.

```camp
export interface TextSink
{
	void write(const char[] text);
}

export void writeBanner(TextSink* sink)
{
	sink.write("ready");
}
```

A simplified C-shaped model is:

```c
typedef struct TextSink TextSink;
typedef struct TextSinkVTable TextSinkVTable;

struct TextSink {
	const TextSinkVTable *vtable;
};

struct TextSinkVTable {
	void (*write)(
		TextSink *context,
		const char *text_elements,
		size_t text_length);
};

void writeBanner(TextSink *sink);
```

The interface pointer supplies the context. The vtable supplies the slot
function. The slot function knows how to recover the concrete receiver when it
needs to.

Class implementations store durable interface slots inside the object, so a
class-backed interface pointer can live as long as the object. Struct
implementations use scoped adapters, so a struct-backed interface pointer is
for calls and locals whose lifetime is clearly bounded. That distinction is not
an implementation curiosity; it is what keeps the ABI explicit without forcing
every struct into heap boxing.

## Iterators Across The ABI

An iterator value is a protocol, not a hidden runtime collection. The public
shape carries state plus a function that advances the sequence.

```camp
export struct iter ImageInfo listImages(ImageSet* set);
```

For a struct iterator, the retained generator state is part of the exposed
shape. A C-facing sketch may look like:

```c
typedef struct ListImagesIter {
	ImageSet *set;
	size_t index;
	bool (*next)(struct ListImagesIter *state, ImageInfo *current);
	void (*destroy)(struct ListImagesIter *state);
} ListImagesIter;
```

The exact fields belong to the generated iterator state. The design
consequence is what matters: changing retained state can change the ABI for a
struct iterator.

A class iterator keeps the state private behind a class pointer:

```camp
export class iter ImageInfo streamImages(ImageSet* set);
```

```c
typedef struct StreamImagesIter StreamImagesIter;

bool StreamImagesIter_next(StreamImagesIter *state, ImageInfo *current);
void StreamImagesIter_destroy(StreamImagesIter *state);
```

Use a struct iterator when the iterator is small, transparent, and layout
stability is not a concern. Use a class iterator when the iterator state should
remain private or evolve without changing the public ABI.

## Async Across The ABI

Camp async is callback async with a source-level `await` surface.

```camp
export extern async ImageInfo loadImageInfoAsync(
	const char[] path,
	thrown ImageError error);
```

A native caller sees the completion callback:

```c
typedef void (*LoadImageInfoComplete)(
	void *context,
	ImageInfo result,
	ImageError error);

void loadImageInfoAsync(
	const char *path_elements,
	size_t path_length,
	LoadImageInfoComplete complete_call,
	void *complete_context);
```

The completion callback is a `once` callable in Camp terms. It receives at most
one ordinary success value plus an optional thrown slot if the async function is
awaitable. Camp's `await` operator supplies that completion and resumes the
async body later through the selected resumer.

For APIs that already have a native callback shape, you can expose both layers:

```camp
export extern void loadImageInfoNative(
	const char[] path,
	escaped once void(ImageInfo info, thrown ImageError error) complete);

export async ImageInfo loadImageInfoAsync(
	const char[] path,
	@awaitwith ImageScheduler* scheduler,
	thrown ImageError error)
{
	return await loadImageInfoNative(path);
}
```

That keeps C callers close to the callback ABI and Camp callers close to the
`await` surface.

## Generic Code At ABI Boundaries

Camp generics use type erasure. A generic body receives explicit capabilities
for the facts it needs:

```camp
export nuint byteLength<T: any>(const T[] values, sizeof(T))
{
	return values.length * sizeof(T);
}
```

The ABI needs the span components and the size capability:

```c
size_t byteLength(
	const void *values,
	size_t values_length,
	size_t sizeof_T);
```

When generic code needs a source-level type name, it asks for `typenameof(T)`.
When it dispatches through an interface constraint, it asks for
`vtableof(T: Interface)`.

```camp
export void writeAll<T: implements TextWritable>(
	const T[] values,
	TextSink* sink,
	sizeof(T),
	vtableof(T: TextWritable));
```

A C-shaped view carries the vtable capability as an ordinary parameter:

```c
void writeAll(
	const void *values,
	size_t values_length,
	TextSink *sink,
	size_t sizeof_T,
	const TextWritableVTable *vtableof_T_TextWritable);
```

Generics do not create a hidden reflection runtime. The ABI carries exactly the
facts the erased body asked for.

## Target-Conditioned Imports

Keep platform splits close to the native boundary:

```camp
#if WINDOWS
@symbol("Sleep")
extern _winapi void nativeSleep(uint milliseconds);
#elif POSIX
@symbol("usleep")
extern int nativeSleepMicros(uint microseconds);
#endif

void sleepMilliseconds(uint milliseconds)
{
#if WINDOWS
	nativeSleep(milliseconds);
#elif POSIX
	nativeSleepMicros(milliseconds * 1000);
#endif
}
```

Ordinary program code should call the wrapper. The wrapper is where call specs,
native names, integer widths, and platform conventions belong.

Use `@notsupported` when a function remains visible in the API but is not
available for a selected target:

```camp
#if NO_TIMERS
@notsupported("The current target does not support timers.")
#endif
export extern TimerHandle startTimer(
	nuint intervalMs,
	escaped delegate void(TimerHandle handle) callback);
```

Tooling and metadata can then explain the missing feature instead of pretending
the declaration never existed.

## Generated Headers And Metadata

Generated C headers describe the lowered ABI. Camp API output and metadata
describe the source API.

That difference is deliberate:

- an array remains `byte[]` in Camp API output, but pointer-plus-length in C;
- an async function remains `async` in metadata, but callback-shaped in C;
- a class remains opaque publicly, even though private generated code knows its
  fields;
- generated async frames, lambda contexts, iterator state helpers, and vtable
  thunks are not ordinary source declarations.

If you are writing Camp code, depend on the Camp API surface. If you are writing
C code against a Camp library, depend on the generated C header. If you are
writing tooling, use metadata for source concepts and emitted C artifacts for
ABI concepts.

## Interop Design Patterns

A good Camp interop layer usually has two tiers:

```camp
@symbol("native_read")
extern nint nativeRead(nint handle, void* buffer, nuint length, out IoError error);

void read(FileHandle handle, byte[] buffer, out nuint readCount, thrown IoError error)
{
	nint result = nativeRead((nint)handle, buffer.elements, buffer.length, out error);
	if (result < 0)
		throw error;

	readCount = (nuint)result;
	error = default;
}
```

The extern declaration matches the native ABI. The wrapper speaks Camp:
nominal handles, counted arrays, `out` success data, and `thrown` error flow.

Prefer wrappers when native APIs use:

- nullable pointer sentinels;
- ownership transfer;
- global error state;
- platform-specific call conventions;
- raw function pointers;
- zero-terminated strings;
- target-specific integer or pointer widths;
- callbacks with separate context pointers.

The point is not to hide C from Camp. The point is to make the boundary honest
in one place, so the rest of the program can use the clearer Camp shape.
