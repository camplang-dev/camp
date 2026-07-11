# Binding, Analysis, And Lowering Pipeline

## Parse Model

`CampTokenizer` produces tokens and `CampParser` produces syntax nodes. Parser
diagnostics should be attached to the tightest useful syntax range. Syntax nodes
preserve ranges used later by binding, diagnostics, dumps, and LSP features.

## Bindable Node Model

`BindableNode` and its expression, statement, and declaration subtypes form the
semantic tree. The builder translates parsed syntax into bindable nodes while
retaining source syntax references for diagnostics and serialization.

## Analysis Scopes

Analysis scopes contain declarations, generic parameters, receiver facts,
lifetime anchors, imports, and symbols visible to the current declaration or
body. Scope lookup must respect namespace imports, type scopes, generic scopes,
and body-local declarations.

## Declaration Passes

Declaration analysis is multi-pass. Passes collect declarations, bind types,
validate signatures, expand generated forms, check export visibility, and
prepare lowered declarations. Keep pass boundaries explicit so later features do
not accidentally depend on partially analyzed state.

## Body Analysis

Body analysis resolves locals, expressions, statements, calls, overloads,
target typing, conversion checks, lifetime facts, and control-flow-sensitive
rules. Body analysis also collects async await sites and generator yield
surfaces.

## Expansion

Expansion creates compiler-visible declarations and components for arrays,
delegates, iterators, async callables, grouped params, thrown slots, and helper
surfaces. Expansion should preserve source-facing API shape while making lowered
forms explicit enough for emission.

## Lowering

Lowering rewrites high-level bindable nodes into simpler Camp-like forms used
by the C emitter and dump serializers. Lowering covers instance calls, operator
rewrites, lambdas, interfaces, slices, size/name/vtable expressions, statements,
exceptions, and params.

## Emission

Emission serializes lowered declarations and bodies to C, API headers, metadata
JSON, XML dumps, or Camp-like lowering dumps. Emission must not invent semantics
that analysis did not validate.

## Generated Declaration Factory

Generated declarations should be created through `GeneratedDeclarationFactory`
or established helper APIs so names, symbols, visibility, provenance, and
attributes remain consistent.

## Provenance And Source Ranges

Generated and lowered nodes should retain enough provenance to report useful
diagnostics and map language-service results back to source. When a generated
node has no direct source token, diagnostics should point to the source
declaration or expression that caused generation.
