# Objective-C Interop Implementation Plan

## Summary

Implement Objective-C interop as a staged compiler/backend feature centered on
`-emit objc`, imported Objective-C declarations, Objective-C message emission,
and generated peer objects for ordinary Camp classes that project to Objective-C
classes or protocols.

Execution tests are macOS-only and should stay headless by default. Use
Foundation/runtime-style smoke programs for execution, and use AppKit/Cocoa only
for compile/link smoke tests unless a later test explicitly requires a GUI
session.

The proposal text mentions `nameof(...)`, but current Camp uses
`typenameof(...)`. This feature does not add a new name intrinsic.

## Stage 1: Syntax, Model, And Early Diagnostics

- Add `objc` as a valid `--emit` mode while keeping `c99` as the default.
- Make the parser permissive about target specifiers/call specs before any type
  keyword. It should parse arbitrary specifier identifiers before `struct`,
  `class`, `interface`, `enum`, `newtype`, `params`, and future type keywords.
- Do not special-case `_objc` in the grammar. Parse it as an ordinary target
  specifier and classify it later in the bindable/AST construction path.
- Reject invalid type-specifier placements when building the AST/bindable model,
  not in grammar parsing. This includes permissive grammar shapes such as
  `_objc struct`, `_stdcall newtype`, or arbitrary specifiers on type keywords
  that do not semantically allow them.
- Permit scoped bodies on any `extern class`, not only `extern _objc class`.
  Every member inside an `extern class` body must be explicitly `extern`.
- Add `_objc` as a built-in compiler-recognized specifier for:
  - `extern _objc class`;
  - `extern _objc interface`;
  - Objective-C selector-callable methods.
- Represent `_objc class` and `_objc interface` distinctly from ordinary Camp
  classes/interfaces. `_objc interface` is an Objective-C protocol, not a Camp
  vtable interface.
- Treat `_objc class` and `_objc interface` values as escaped for lifetime
  purposes.
- Reject:
  - `_objc struct`, `_objc enum`, `_objc newtype`, and `_objc params`;
  - non-extern `_objc class` in v1;
  - `_objc` features under `-emit c99` when they are reachable for emission;
  - non-extern members in any `extern class` body.
- Tests:
  - AST/declaration goldens for permissive parsing, `_objc class`, scoped
    `extern class`, scoped `extern _objc class`, `_objc interface`, and `_objc`
    methods.
  - Diagnostics for invalid specifier/type combinations, missing member
    `extern`, non-extern `_objc class`, and reachable `_objc` under C99.
  - Lifetime diagnostics/proofs showing `_objc` object/protocol values behave as
    escaped values.

## Stage 2: Selector Semantics And Call Binding

- Infer Objective-C selectors from Camp method names and parameter names:
  - receiver `this` is not part of the selector;
  - `run(T* this)` -> `run`;
  - `setTitle(T* this, NSString* title)` -> `setTitle:`;
  - `drawWithExpansionFrame(T* this, NSRect frame, NSView* inView)` ->
    `drawWithExpansionFrame:inView:`.
- Treat `@symbol("selector:")` on `_objc` methods as selector override, not as a
  C symbol override.
- Require named arguments for selector pieces after the first colon.
- Permit selector overloads only for `_objc` methods on the same receiver when
  their resolved selectors differ. Reject same receiver plus same selector.
- Reject mixing `_objc` and non-`_objc` methods with the same Camp method name on
  the same receiver family.
- Enforce Objective-C ABI-compatible method signatures:
  - allow scalar primitives, enums, pointer/newtype pointer forms, ordinary C
    structs by value, `_objc class*`, `_objc interface*`, `this` returns, and
    `classtype*` static returns;
  - reject expanded arrays, optionals, delegates, once, iter, async, async iter,
    thrown/within parameters, generic selector parameters, type parameters as
    selector values, fixed arrays by value, Camp interface pointers, and default
    parameter values.
- Tests:
  - Selector inference, `@symbol`, named selector argument enforcement, selector
    duplicate rules, and `_objc`/non-`_objc` conflict diagnostics.
  - Signature restriction diagnostics for each forbidden Camp-only shape.

## Stage 3: Objective-C Source Emission

- Add an Objective-C emitter path that writes `.m` source files and reuses C
  lowering/declaration infrastructure where practical.
- Emit minimal imports only:
  - `#import <objc/objc.h>`;
  - C runtime/stdint headers already required by Camp lowering;
  - no Cocoa/Foundation/AppKit umbrella headers.
- Emit local Objective-C declarations from Camp `_objc` declarations:
  - `@class` for imported classes with no known methods;
  - local `@interface` for imported classes with scoped method declarations;
  - `@protocol` declarations for `_objc interface`.
- Emit a local root object stub with `Class isa;` when generated subclasses need
  local root layout and no real Foundation header is imported.
- Lower `_objc` calls to Objective-C message syntax.
- Lower `_objc interface*` as Objective-C protocol-object types such as
  `id<Protocol>`.
- Lower `null` as `nil` in Objective-C object-pointer message contexts.
- Tests:
  - ObjCEmit goldens for `.m` output, local declarations, protocol-object
    types, message syntax, selector pieces, and `nil`.
  - ObjCCompile tests on macOS only. Non-mac hosts should skip native
    Objective-C compile tests while still running emission goldens.

## Stage 4: Static Class Messages, `this`, `classtype`, And Lifecycle Boundaries

- Bind inherited static `_objc` class methods so the call-site class object is
  the emitted Objective-C message receiver.
- Apply existing `classtype*` rules to class-relative Objective-C factories such
  as `NSObject.alloc()` called as `NSButton.alloc()`.
- Apply existing `this` return-type rules to receiver-preserving Objective-C
  instance methods such as `objcInit`, `retain`, and `autorelease`.
- Reject Camp lifecycle operations on `_objc class` object types:
  - `new NSButton()`;
  - `init NSButton()`;
  - `delete objcObject`;
  - `finally delete objcObject`.
- Tests:
  - ObjCEmit/CCompile for `NSButton.alloc().objcInit()` emitting `[NSButton alloc]`
    and `init`.
  - Diagnostics for Camp lifecycle misuse on Objective-C object types.
  - Type tests for widened receiver `this` return behavior.

## Stage 5: Objective-C Protocol Projection For Camp Classes

- For an ordinary Camp class that lists one `_objc class` plus zero or more
  `_objc interfaces`, generate:
  - hidden `_objc_peer` field;
  - deterministic peer class name such as `TypeName_ObjCPeer`;
  - peer `@interface` and `@implementation`;
  - `-initWithCamp:` storing the Camp object pointer;
  - selector methods that forward Objective-C calls to Camp methods.
- Reserve `_objc_peer`; reject user fields/members that collide with it.
- Generate borrowed projection helpers:
  - `TypeName_op_NSObject`;
  - `TypeName_op_NSApplicationDelegate`;
  - exported when the source class is exported.
- Insert projections when a Camp class pointer is passed where an Objective-C
  class/protocol pointer is expected.
- Inject peer creation in typed construction scaffolding before the user
  constructor body.
- Inject peer release in the owning destructor path after the user destructor
  body.
- In v1, reject ordinary Camp base-class inheritance combined with Objective-C
  projection. If ordinary Camp interface conformance conflicts with projection
  lowering, reject mixed conformance with a clear diagnostic and defer it.
- Tests:
  - ObjCEmit for generated peer class, forwarding selector, projection helper,
    and inserted projection conversion.
  - ObjCCompile for projected class with required and optional protocol methods.
  - Diagnostics for missing required protocol methods, `_objc_peer` collision,
    unsupported mixed base layout, and optional methods not emitted when absent.

## Stage 6: Native Build And macOS Execution Coverage

- Teach native build/test infrastructure that `objc` emission produces `.m`
  files compiled as Objective-C.
- Require a target that supports Objective-C compilation for `-emit objc`;
  initially this should be `clang-macos-x64`.
- Compile generated Objective-C in manual retain/release mode for v1.
- Use existing `--framework/-f` support for Foundation/Cocoa/AppKit link tests.
- Add test categories or case options for:
  - `ObjCEmit`: text golden, all hosts;
  - `ObjCCompile`: native Objective-C compile/link, macOS-only;
  - `ObjCRun`: native execution, macOS-only and headless.
- Execution tests should verify:
  - basic allocation/init/release style message calls;
  - static class messages;
  - selector return values;
  - peer forwarding from Objective-C into Camp.
- Avoid GUI/AppKit runtime tests in the first pass. Use AppKit/Cocoa only for
  compile/link smoke tests unless a later requirement needs a GUI session.
- Run:
  - `dotnet build src/camplang.sln`;
  - targeted Objective-C tests;
  - full `dotnet vstest src/Camp.Compiler.TestRunner/bin/Debug/net8.0/Camp.Compiler.TestRunner.dll`.

## Stage 7: API, Metadata, Docs, And Final Polish

- Preserve `_objc` target-specifier information in Camp API output.
- Preserve `@symbol` selector values for `_objc` methods.
- Represent `_objc interface` metadata as Objective-C protocol declarations, not
  Camp vtable interfaces.
- Omit generated peer classes/helpers from source-level metadata by default,
  consistent with existing generated-helper policy, unless needed as stubs.
- Update all programmer-facing docs:
  - unified spec;
  - declarations/statements grammar;
  - expressions grammar if message-call or type syntax text requires it;
  - LLM coding guide;
  - Sublime syntax;
  - metadata supplement document.
- Documentation must describe:
  - permissive target-specifier parsing versus semantic rejection;
  - `extern class` scoped bodies and member `extern` requirement;
  - `_objc class` / `_objc interface` escaped lifetime behavior;
  - selector inference and named selector pieces;
  - Objective-C lifecycle separation from Camp `new`/`delete`;
  - framework linking as a native build concern, not header inclusion.
- Add final diagnostic polish for:
  - unsupported emit mode/target;
  - reachable `_objc` under C99;
  - invalid selector signatures;
  - missing named selector pieces;
  - Objective-C lifecycle misuse.

## Assumptions

- `_objc` remains a compiler-recognized specifier and is not added to target INI
  files.
- The grammar remains permissive; AST/bindable construction and semantic
  analysis decide whether a specifier is legal in a given position.
- Scoped bodies are valid on all `extern class` declarations, but every member
  in such a body must explicitly be `extern`.
- `_objc class` and `_objc interface` values are escaped for lifetime analysis.
- Objective-C blocks, categories, class extensions, runtime class registration,
  Swift annotations, lightweight generics, and ARC are out of scope for v1.
- Generated `.m` files do not include Cocoa/Foundation/AppKit umbrella headers.
- macOS execution tests are headless; GUI behavior is covered only by compile or
  link smoke tests unless a later requirement says otherwise.
