# Overview

## What Camp Is

Camp is a statically typed language for systems and library code that compiles
through C and native toolchains. It has source-level concepts for native
interop, explicit ownership-sensitive allocation, callbacks, iterators,
interfaces, generics, and async control flow.

Camp source is organized as declarations in `.camp` files. The compiler can
emit C, native artifacts, API headers, metadata JSON, and several inspection
forms. Those compiler surfaces are described in the compiler supplement.

## Language Goals

Camp aims to make native code explicit without making routine library code
needlessly ceremonial. Its main goals are:

- preserve predictable native representation and interop;
- make allocation contexts and lifetime intent visible in signatures;
- support structured high-level surfaces such as interfaces, generics,
  iterators, lambdas, and async callbacks;
- emit portable C where the selected target permits it;
- expose enough metadata for tooling and documentation generation.

## Language Non-Goals

Camp is not a managed runtime language. It does not assume a garbage collector,
reflection runtime, global package resolver, or universal binary target. Camp is
also not trying to hide target-specific ABI details when those details are part
of the source contract.

## Who Camp Is For

Camp is for programmers who need native artifacts and precise interop while
still wanting modern source-level tools. It is especially suited to libraries,
runtime components, bindings, and code where C ABI boundaries matter.

## Who Camp Is Not For

Camp is not the easiest choice for quick scripts, purely managed applications,
or projects that prefer implicit memory management and a large standard runtime
over explicit native control.

## Novel Aspects

Camp treats expanded forms, such as arrays, delegates, iterators, thrown slots,
and async callbacks, as source-level abstractions with precise lowered shapes.
It combines lifetime annotations, allocator parameters, and target-specific type
specifiers in ordinary declarations, so APIs can describe native constraints
directly.

The language also makes metadata a first-class compiler output. Documentation
comments, symbol links, visibility, and source declarations can be emitted for
tools without requiring a runtime reflection system.

## Influences And Borrowed Ideas

Camp borrows C's native compilation and interop mindset, C++'s concern for
lifecycle and value layout, C#'s comfortable declaration and property style,
Rust's attention to lifetimes and explicit safety boundaries, and Swift-like
clarity around modern call syntax. Camp combines these ideas around C emission
and target-driven ABI control rather than a single built-in runtime.

## Source Files And Compilation Units

A compilation unit contains preprocessor directives, `using` declarations,
`export as` declarations, and ordinary declarations. Top-level declarations may
define functions, types, aliases, variables, and constants.

Build-related directives such as `#build` are recognized in source files, but
their option semantics belong to the compiler supplement.

## A Minimal Program

```camp
export int main()
{
	return 0;
}
```

An exported or public `main` lets the compiler infer an executable artifact for
ordinary build commands. Library code usually exports functions, types, and API
surface instead.

## Documentation Map

Use this language reference for source syntax and semantics. Use the compiler
supplement for `campc`, build files, packages, targets, artifacts, and metadata
JSON. Use the semantic supplements for compiler-writer details such as lowering
and conversion classification.

## Syntax Used In Examples

Examples prefer complete Camp fragments with meaningful names. When an example
is only a declaration fragment, the surrounding context is stated in prose.

Examples that import or qualify `Std`, `Std::Math`, or `Std::Time` are using
standard-library APIs. Some fragments use standard-library names such as
`Console`, `FileHandle`, or `List` unqualified; read those as if the surrounding
file had the appropriate `using Std;` or selected import. Other names, such as
small `Buffer`, `Reader`, `NativeHandle`, or `EventLoop` types, are local
teaching examples unless the text says otherwise. Treat those as application
code you could write, not as a promise that the standard library contains that
exact type or helper.

## Core Design Commitments

Camp's design is built around a few commitments that show up throughout the
language reference.

First, source contracts should describe the important native contract. If an
API depends on an allocator, lifetime, target calling convention, interface
vtable, thrown slot, or generic size capability, that fact should appear in the
signature rather than being hidden in comments or runtime state.

Second, high-level forms should have understandable lowered shapes. Arrays are
counted views. Interfaces are vtable contracts. Delegates have context. Async is
callback-shaped. Iterators have protocol state. Users do not need to write the
lowered ABI in normal code, but they should be able to reason about ownership,
cost, and interop from the source model.

Third, nominal boundaries matter. Structs, classes, interfaces, enums, and
newtypes are not interchangeable just because their storage might look similar.
Explicit conversions, casts, adapters, or constructors are the way to cross
boundaries.

Fourth, target-specific behavior belongs at target boundaries. Camp can express
call specs, type specs, primitive widths, framework links, shared libraries, and
native declarations, but ordinary code should keep those details local to the
API that needs them.

## Reading Order

The reference is ordered so later chapters can build on earlier concepts:

1. Lexical structure, names, and declarations establish source shape.
2. Type, primitive, pointer, and callable chapters define values and signatures.
3. Expressions, statements, and lifecycle chapters explain executable code.
4. Interfaces, newtypes, arrays, errors, lifetimes, generics, iterators, and
   async cover the larger language features.
5. Attributes and standard-library/interop chapters explain documentation,
   metadata, and common boundary surfaces.

When a feature depends on a later chapter, the earlier chapter gives only the
minimum needed context and links forward.

## What Counts As User-Facing Detail

This reference includes ABI and representation information when it affects how
a Camp programmer designs or calls an API. For example, interface vtable
storage, struct adapter lifetimes, array carrier components, `within`
allocation, and async frame retention are user-facing because they affect
correct source code.

Compiler-only algorithms, exact generated helper names, pass ordering, and C
emitter implementation choices belong in `docs/semantics` unless the source
programmer must understand them to write correct code.
