# .NET Wrapper Generation For Camp Shared Libraries

Status: draft  
Proposal date: 2026-07-15  
Last updated date: 2026-07-15

## Summary

Add an optional compiler deliverable that generates a C#/.NET wrapper for a
native Camp shared library.

The Camp implementation remains a normal native shared library produced by the
existing C/native backend. The new wrapper generator reads the bound exported
API surface and metadata, emits C# facade source using `DllImport`, and can
optionally compile that facade into a managed wrapper assembly.

The generated .NET wrapper is a safe consumer-facing layer:

- functions become C# methods;
- exported classes become sealed C# wrapper classes backed by `IntPtr`;
- class constructors and destructors become C# constructors and `IDisposable`;
- getter/setter-shaped methods become properties where practical;
- enums and simple structs become C# enum/struct declarations;
- documentation comments become XML doc comments;
- the native shared library is copied beside the wrapper assembly when the
  wrapper is compiled.

This proposal is intentionally narrower than a managed-code backend. A future
proposal may reuse the same facade generator with a different implementation
binding underneath it. In this proposal, the implementation binding is always a
native shared library accessed through `DllImport`.

## Motivation

Camp can already produce C headers and native shared libraries, but .NET
consumers need a managed wrapper to call those libraries ergonomically.

Manually writing wrappers is repetitive and error-prone:

- C symbols must be spelled exactly;
- object ownership and destruction must be paired correctly;
- string and span conversions are easy to get wrong;
- thrown/status results should become idiomatic exceptions;
- documentation comments should not need to be copied by hand.

Camp already has enough source-level API metadata to generate most of this
wrapper automatically. This gives library authors a practical path to ship:

```text
native Camp shared library + generated .NET wrapper assembly
```

without waiting for a full managed-code backend.

## Command-Line Interface

Add a build option:

```text
--dotnet-wrapper source|assembly
```

The option is valid only when building a native shared library:

```sh
campc build src/*.camp --artifact shared --dotnet-wrapper source
campc build src/*.camp --artifact shared --dotnet-wrapper assembly
```

Invalid combinations report a diagnostic before native build work begins:

```sh
campc build app.camp --artifact exec --dotnet-wrapper source
campc build lib.camp --artifact static --dotnet-wrapper source
campc build lib.camp --artifact none --dotnet-wrapper source
```

The two modes are:

- `source`: generate wrapper C# source and a C# project file, but do not compile
  it.
- `assembly`: generate wrapper C# source/project, run `dotnet build`, and copy
  the native shared library beside the wrapper assembly output.

The option participates in ordinary command-line and `#build` processing.

## Output Layout

For a Camp library named `shapes`, the existing native shared-library output
continues to be produced:

```text
bin/<target>_shared_<profile>/
    libshapes.dylib / shapes.dll / libshapes.so
    shapes.h
    shapes_api.camp
    shapes_api.json
    build/
        shapes.c
        shapes_private.h
        ...
```

When `--dotnet-wrapper source` or `assembly` is enabled, the compiler also
creates:

```text
bin/<target>_shared_<profile>/
    dotnet/
        Shapes.Wrapper.csproj
        NativeMethods.g.cs
        Shapes.g.cs
```

When `--dotnet-wrapper assembly` is used, `dotnet build` also produces:

```text
bin/<target>_shared_<profile>/
    dotnet/
        bin/
            Debug/
                <tfm>/
                    Shapes.Wrapper.dll
                    libshapes.dylib / shapes.dll / libshapes.so
```

The native shared library is copied beside the wrapper assembly so a simple .NET
consumer can run without manually arranging the native library search path.

## Wrapper Project

The generated project is SDK-style.

The first implementation should default to a conservative target framework such
as `net8.0`, while keeping the generated C# source compatible with older .NET
patterns where practical:

- use `DllImport`, not `LibraryImport`;
- use `IntPtr`/`UIntPtr`, not `nint`/`nuint`, unless the selected wrapper target
  explicitly permits newer syntax;
- avoid source generators;
- avoid function pointer syntax in the public wrapper;
- use ordinary deterministic `IDisposable` for owned native objects.

Later work may add switches for wrapper target framework and namespace
customization. V1 uses:

- namespace from `export as`;
- otherwise, a sanitized artifact name;
- public wrapper types by default.

## Architecture

The wrapper generator should be designed around two layers:

1. **Facade model**: source-level exported Camp declarations mapped to C# names,
   properties, classes, interfaces, constructors, exceptions, and documentation.
2. **Implementation binding**: the mechanism used by facade methods to call the
   implementation.

For this proposal, the implementation binding is `DllImport` into the native
shared library.

The separation matters because a future managed-code implementation proposal
should be able to reuse the facade model and replace only the implementation
binding.

The generator should use the compiler model directly when it is running as part
of `campc build`. It may also consume emitted metadata internally where useful,
but the in-process compiler model remains the source of truth. The emitted
metadata should be complete enough that an external wrapper generator could be
written later.

## Native Method Layer

The generated `NativeMethods.g.cs` file contains internal declarations for
native entry points:

```csharp
using System;
using System.Runtime.InteropServices;

namespace Shapes;

internal static class NativeMethods
{
    [DllImport("shapes", EntryPoint = "add")]
    internal static extern int add(int a, int b);

    [DllImport("shapes", EntryPoint = "Gauge_create")]
    internal static extern IntPtr Gauge_create(int id, int reading);

    [DllImport("shapes", EntryPoint = "Gauge_destroy")]
    internal static extern void Gauge_destroy(IntPtr self);
}
```

`DllImport` library names should use the logical library name rather than a
platform-specific filename where possible. The copied native artifact supplies
the platform-specific extension/prefix.

The native method layer is internal. Public consumers should use the facade
types.

## Functions

Exported Camp functions with facade-safe signatures become public static C#
methods.

```camp
export int add(int a, int b);
```

generates:

```csharp
public static class ShapesNative
{
    public static int Add(int a, int b)
    {
        return NativeMethods.add(a, b);
    }
}
```

The containing class name may be based on the module/artifact name. If a future
namespace/member organization rule is added, it should preserve stable names
where possible.

Functions whose signatures cannot yet be safely represented in C# should either:

- be omitted from the wrapper with a generated comment/diagnostic; or
- be exposed only through a lower-level unsafe/raw wrapper if such a mode is
  later added.

V1 should prefer clear diagnostics during wrapper generation over silently
emitting broken C#.

## Enums

Exported Camp enums become C# enums.

The generated enum should preserve:

- exported enum name;
- exported member names;
- computed numeric values;
- explicit symbol overrides only where they matter to native binding.

```camp
export enum IoError
{
    NONE,
    NOT_FOUND
}
```

generates:

```csharp
public enum IoError
{
    None = 0,
    NotFound = 1,
}
```

The exact C# casing policy should be deterministic. If the generator changes
case for idiomatic C#, it should avoid collisions and have a fallback escaping
strategy.

## Classes And Ownership

Exported Camp classes become sealed C# wrapper classes backed by `IntPtr`.

```camp
export class Gauge
{
    export Gauge(int id, int reading);
    export int getReading();
    export void setReading(int value);
    export ~Gauge();
}
```

generates:

```csharp
public sealed class Gauge : IDisposable
{
    IntPtr handle;

    public Gauge(int id, int reading)
    {
        handle = NativeMethods.Gauge_create(id, reading);
    }

    public int Reading
    {
        get => NativeMethods.Gauge_getReading(handle);
        set => NativeMethods.Gauge_setReading(handle, value);
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.Gauge_destroy(handle);
            handle = IntPtr.Zero;
        }
    }
}
```

Rules:

- wrappers are `sealed` by default;
- wrappers use the Camp class name, not a `Handle` suffix;
- constructors and static factory methods return owning wrappers;
- wrappers created around borrowed pointers are internal/non-owning unless an API
  explicitly exposes borrowing;
- classes with destructors implement `IDisposable`;
- `Dispose()` calls the exported destroy function only when the wrapper owns the
  native object;
- finalizers are not generated by default;
- retain/release patterns are not special-cased in this proposal; methods named
  `retain`, `release`, or similar are ordinary generated wrapper methods;
- the wrapper does not infer thread affinity or schedule cleanup onto another
  thread. Users must call `Dispose()` in the context required by the library;
- Camp virtuality is not represented as C# virtuality. The native library has
  already implemented Camp dispatch. The wrapper simply calls exported entry
  points.

A future debug option may generate a finalizer that only reports a leak when a
wrapper is collected with a non-zero handle. Such a finalizer must not call
native destroy/release methods.

## Properties

Getter/setter-compatible Camp methods become C# properties where practical.

```camp
export int getReading();
export void setReading(int value);
```

generates:

```csharp
public int Reading { get; set; }
```

Readonly getters generate get-only properties:

```camp
export WidgetKind getKind();
```

generates:

```csharp
public WidgetKind Kind { get; }
```

If a getter/setter pair cannot be safely paired because of mismatched types,
indexer shape, overload ambiguity, or unsupported marshaling, the generator
should leave them as methods or report a wrapper-generation diagnostic.

## Structs

Simple exported Camp structs become `[StructLayout(LayoutKind.Sequential)]` C#
structs when their fields are facade-safe.

Facade-safe fields include primitive numeric types, enums, and other simple
structs that can be represented directly.

Structs with pointer-bearing fields, callbacks, non-copyable fields, unsupported
fixed arrays, or target-specific layout concerns should be deferred or exposed
through raw/internal forms until a safe representation is designed.

## Interfaces

Camp interfaces can be represented as C# interfaces where the class facade can
provide an ordinary managed wrapper surface.

For example:

```camp
export interface IMeasurable
{
    int getMeasurement();
}

export class Gauge: IMeasurable
{
    int getMeasurement(): IMeasurable;
}
```

can generate:

```csharp
public interface IMeasurable
{
    int Measurement { get; }
}

public sealed class Gauge : IMeasurable
{
    public int Measurement { get; }
}
```

Raw native Camp interface pointers are vtable-shaped native values. Public C#
consumers should not need to construct or inspect them directly.

Initial support may limit interface wrappers to class implementations that are
visible in metadata and can be represented through existing class wrappers.

## Strings And Spans

String and span marshaling should be conservative.

Initial supported cases:

- `string` / `const char[]` input that does not escape through the return type:
  encode a temporary UTF-8 buffer and pass pointer + length components;
- returned owning/copied strings where the native API clearly returns a copy or
  where the wrapper can immediately copy from pointer + length;
- simple `char[]`/`byte[]` input buffers using pinned arrays where safe.

Cases needing more design:

- returned borrowed spans tied to input or receiver lifetime;
- mutable span outputs;
- allocator-returned strings requiring explicit free;
- wide strings and target-dependent character width;
- fixed arrays in public struct fields.

The generator should not flatten lifetime-sensitive Camp views into `string` if
that would hide important borrowing semantics. When in doubt, emit a diagnostic
or defer the member.

## Thrown Values And Exceptions

Camp `thrown` parameters should become C# exceptions in the facade.

For status enums or status newtypes, generate exception classes:

```csharp
public sealed class IoErrorException : Exception
{
    public IoErrorException(IoError value)
        : base(value.ToString())
    {
        Value = value;
    }

    public IoError Value { get; }
}
```

The native method declaration keeps the thrown/out parameter shape. The facade
checks the status value after the call and throws the generated exception when
the value is non-default.

This mapping should be staged after basic class/function wrappers if necessary.

## Delegates And Callbacks

Callback support is important but should not be part of the smallest wrapper
slice unless the signature is very simple.

Eventually, C# delegates should be convertible to Camp escaped delegate
parameters by:

- storing the managed delegate in a `GCHandle`;
- passing a native context pointer and trampoline function to Camp;
- ensuring the handle remains alive while Camp may call it;
- freeing the handle when the Camp delegate context is destroyed.

This should be implemented carefully because lifetime and cleanup mistakes can
lead to callbacks into collected managed objects.

## Documentation Comments

Generated facade source should include XML doc comments derived from Camp doc
metadata:

```csharp
/// <summary>
/// Returns the current reading.
/// </summary>
public int Reading { get; }
```

Supported doc metadata should include summaries, parameter docs, returns docs,
remarks, examples, deprecation, and see-symbol links where they can be
represented cleanly in XML documentation comments.

Doc comments belong on public facade declarations. `NativeMethods.g.cs` may stay
undocumented.

## Metadata Requirements

Wrapper generation needs source-level metadata/API facts:

- exported declarations only;
- exported C/native symbol names;
- module/export namespace;
- type kinds: enum, struct, class, interface, newtype;
- class constructors/destructors/create/destroy surface;
- method names, property names, property index/value parameters;
- parameter names, types, default values, thrown/out/within roles;
- enum numeric values;
- struct fields and field types;
- interface implementation relationships;
- doc metadata attributes.

Some target ABI facts still come from the selected native target rather than
metadata alone:

- native library file name and logical import name;
- primitive widths;
- bool representation;
- pointer width;
- string element width and encoding policy;
- calling convention if non-default.

So the generator is best described as:

```text
compiler model + metadata/API facts + selected native target ABI facts
```

not metadata in isolation.

## Staged Implementation Plan

Each stage should end with targeted tests, a full local test-suite pass, and a
commit. Cross-platform wrapper tests can be added after the local path is stable.

### Stage 1: CLI, Deliverable Planning, And Source Skeleton

- Add `--dotnet-wrapper source|assembly`.
- Validate that it is used only with `--artifact shared`.
- Add wrapper output directory planning under the shared artifact directory.
- Generate a minimal `.csproj`, `NativeMethods.g.cs`, and facade `.g.cs` for an
  empty/export-minimal library.
- Do not run `dotnet build` yet unless `assembly` is requested and later stages
  support it.
- Completion criteria:
  - invalid artifact combinations report clear diagnostics;
  - `source` mode creates deterministic wrapper files;
  - existing native shared-library output is unchanged.

### Stage 2: Functions, Enums, And Primitive Type Mapping

- Generate `DllImport` declarations for exported primitive functions.
- Generate public static facade methods for those functions.
- Generate C# enums with numeric values.
- Map primitive integer, bool, floating, pointer, and enum types.
- Use `IntPtr`/`UIntPtr` for native pointer-sized values where older framework
  compatibility matters.
- Completion criteria:
  - C# wrapper source compiles for primitive exported functions;
  - generated facade can call a native shared library function from a C# test
    project;
  - enum values match Camp metadata.

### Stage 3: Wrapper Assembly Build

- Implement `--dotnet-wrapper assembly`.
- Run `dotnet build` for the generated wrapper project.
- Copy the native shared library beside the wrapper assembly output.
- Surface dotnet build failures as compiler diagnostics with useful command
  context.
- Completion criteria:
  - local test builds a Camp shared library, wrapper assembly, and C# consumer;
  - C# consumer runs and loads the copied native library.

### Stage 4: Class Wrappers, Constructors, Destructors, And Properties

- Generate sealed C# wrappers for exported Camp classes.
- Map exported constructors/static create surfaces to C# constructors/factories.
- Generate `IDisposable` for classes with destructors.
- Generate property wrappers from getter/setter-shaped methods.
- Hide allocator and `within allocator` parameters from public facade
  signatures.
- Completion criteria:
  - C# consumer constructs, uses properties/methods, and disposes a Camp class;
  - generated wrappers do not include finalizers by default;
  - ownership is not duplicated for borrowed wrappers;
  - virtual Camp methods appear as ordinary facade methods.

### Stage 5: Simple Structs And Interface Facades

- Generate simple sequential C# structs for facade-safe Camp structs.
- Generate C# interfaces for facade-safe Camp interfaces.
- Make class wrappers implement generated C# interfaces when metadata shows
  exported implementations.
- Completion criteria:
  - struct by-value function calls work through the wrapper;
  - class wrapper can be used through a generated C# interface.

### Stage 6: Strings, Spans, And Arrays

- Add conservative input string marshaling.
- Add returned string copying for safe cases.
- Add pinned byte/char array input for simple span parameters where safe.
- Reject or defer lifetime-sensitive borrowed output views with precise
  diagnostics.
- Completion criteria:
  - C# `string` input reaches a Camp `string`/`const char[]` parameter;
  - safe returned string is copied into C# `string`;
  - unsupported string/span signatures do not generate broken C#.

### Stage 7: Thrown Values And Exceptions

- Generate exception classes for enum/status thrown types.
- Generate facade checks for thrown parameters.
- Map successful calls without throwing.
- Completion criteria:
  - C# consumer catches generated exception type;
  - exception exposes original Camp status value;
  - non-error calls do not throw.

### Stage 8: Documentation Comments And Polish

- Emit XML doc comments on public facade declarations from Camp doc metadata.
- Improve C# naming/casing and collision escaping.
- Add deterministic file ordering and formatting.
- Add wrapper-generation diagnostics for unsupported exported members.
- Completion criteria:
  - generated XML comments compile cleanly;
  - unsupported members are reported or intentionally omitted with traceable
    diagnostics;
  - wrapper source remains deterministic across builds.

### Stage 9: Callback Support

- Support simple C# delegate-to-Camp escaped delegate conversion.
- Generate trampoline/context glue.
- Keep managed delegates alive with `GCHandle`.
- Free handles when Camp delegate contexts are destroyed.
- Completion criteria:
  - C# lambda can be passed to Camp and invoked by native Camp code;
  - cleanup path releases the managed handle.

### Stage 10: Final Audit And Future-Reuse Boundary

- Review generated facade model for reuse by future implementation bindings.
- Ensure all native-wrapper-specific code is isolated in the `DllImport`
  implementation binding.
- Re-check metadata/API requirements.
- Add final local full-suite and wrapper integration validation.
- Completion criteria:
  - facade generator is not hard-wired to `DllImport` except at the binding
    layer;
  - local full suite passes;
  - representative wrapper examples cover functions, enums, classes, structs,
    interfaces, strings, docs, and thrown values.

## Test Surface

Recommended tests:

- CLI validation for `--dotnet-wrapper`;
- source-mode output file creation;
- assembly-mode build and native-library copy;
- primitive function call from C# consumer;
- enum numeric value match;
- class constructor/property/method/dispose from C# consumer;
- simple struct by-value call;
- generated C# interface implemented by class wrapper;
- input string marshaling;
- safe returned string marshaling;
- thrown enum to generated exception;
- XML doc comment generation;
- unsupported signature diagnostics;
- deterministic wrapper source output.

Tests should be dense. Prefer a few C# consumer projects that exercise multiple
wrapper surfaces over many tiny one-feature tests.

## Non-Goals

This proposal does not implement:

- compiling Camp implementation code to managed IL;
- NuGet package generation;
- source-generated `LibraryImport`;
- generic managed type imports;
- full callback/event interop in the initial stages;
- async/`Task` bridge;
- C# subclasses overriding Camp virtual methods;
- complete facade coverage for every Camp signature shape in v1.

## Risks

- Metadata may be missing details needed by a high-quality wrapper. The
  generator should improve metadata/API output as needed rather than guessing.
- String/span lifetime rules can be flattened incorrectly. Conservative copying
  and clear diagnostics are safer than clever wrappers.
- Class ownership must be explicit. Accidental double-destroy or leaked native
  objects would be painful for consumers.
- Without finalizers, forgotten `Dispose()` calls leak native resources. This is
  preferable to calling Camp destructors from the CLR finalizer thread, which
  may be unsafe for many Camp libraries.
- The wrapper does not yet understand thread affinity. Consumers must dispose
  objects on a thread/context that is valid for the native library.
- Native library loading differs across platforms. Copying the native library
  beside the wrapper assembly is a good first step but may not solve every
  deployment shape.
- C# name casing can create collisions. The generator needs deterministic
  escaping.
- `DllImport` signatures must match the native ABI exactly. Incorrect bool,
  enum, struct, or calling-convention mapping can fail at runtime rather than
  compile time.

## Open Follow-Up Design Items

- Wrapper target framework option.
- Wrapper namespace override option.
- Public/internal wrapper visibility option for source-only output.
- Raw/unsafe wrapper mode for advanced consumers.
- Optional leak-check-only finalizer mode.
- Exact casing policy for C# names.
- How to expose borrowed handles safely.
- How to represent mutable spans and out buffers.
- How to package native libraries for RID-specific .NET deployment later.
