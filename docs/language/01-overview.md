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
