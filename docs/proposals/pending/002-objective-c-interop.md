# Objective-C Interop

Status: pending  
Proposal date: 2026-07-11  
Last updated date: 2026-07-11

Archive source: `archive/docs/proposals/camp_objc_interop_implementation_plan.md`

## Summary

Defines a staged plan for Objective-C syntax, selector semantics, call binding,
Objective-C source emission, static class messages, `this`, `classtype`,
protocol projection, native build integration, metadata, docs, and tests.

## Motivation

Camp targets can describe Objective-C support, and macOS interop benefits from
a source-level bridge to Objective-C classes, protocols, selectors, and build
artifacts.

## Proposed Surface

The archived plan covers Objective-C declarations, selector binding, message
emission, protocol projection, native build integration, and metadata.

## Current Implementation Status

Target metadata includes Objective-C capability hooks, but the current
documented language reference does not describe Objective-C source syntax as
current Camp behavior.

## Open Questions

- Exact source syntax to accept.
- How Objective-C selectors interact with ordinary Camp overloads.
- How Objective-C protocol projection appears in metadata and API headers.
- Whether Objective-C-specific generated code belongs in the C emitter or a
  separate emitter path.

## Acceptance Criteria

- Parser, binder, diagnostics, lowering, emission, build, metadata, and tests
  implement the accepted Objective-C surface.
- `docs/language`, `docs/compiler`, and `docs/semantics` document the accepted
  behavior.
- Target capabilities clearly distinguish Objective-C-capable and non-capable
  targets.

## Documentation Impact

Objective-C interop would update language interop docs, target/build docs,
C-emission semantics, metadata docs, and the LLM guide.

## Test Impact

Add parser, diagnostics, API, metadata, C emission, native build, and macOS
execution tests where target support exists.

## Uncertainty Introduced By Compiler Changes

Any compiler changes to target capabilities, class dispatch, `classtype`, or
native build emission MUST be reconciled with this proposal before
implementation.
