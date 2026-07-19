# Default-Parameter Source Capture Intrinsics

## Proposal Date

2026-07-19

## Last Updated

2026-07-19

## Status

Pending.

## Summary

This proposal adds two compiler intrinsics that may appear only in default
parameter expressions:

```camp
caller(sourceline)
caller(sourcefile)
caller(propertyname)
caller(functionname)
caller(qualifiedname)

sourceof(argumentName)
```

These intrinsics let an API capture call-site source information without making
every caller pass file names, line numbers, caller names, property keys, or
assertion expression text manually.

The feature is intentionally narrow. It is part of default argument insertion.
A source capture intrinsic that appears outside a default parameter expression
is invalid.

## Motivation

### Logging

Logging APIs commonly want source location and caller identity:

```camp
void logInfo(
	string message,
	string file = caller(sourcefile),
	uint line = caller(sourceline),
	string where = caller(qualifiedname));
```

Callers can write the useful call:

```camp
logInfo("loaded configuration");
```

The compiler supplies the file, line, and caller name from the call site.

### Tests And Assertions

Assertion helpers need both source location and the text of the expression that
failed:

```camp
void assert(
	bool condition,
	string expression = sourceof(condition),
	string file = caller(sourcefile),
	uint line = caller(sourceline),
	string where = caller(qualifiedname));
```

Then a test can write:

```camp
assert(output == expected);
```

and the assertion helper receives `"output == expected"` as the expression
text, plus the source file and line of the call.

### Property Bags

Property-bag helpers often use the property name as a key:

```camp
string Name
{
	get => getPropertyBagValue();
	set => setPropertyBagValue(value);
}

string getPropertyBagValue(string key = caller(propertyname));
void setPropertyBagValue(string value, string key = caller(propertyname));
```

When these helpers are called from a property accessor body, the compiler can
supply the property name. When they are called from a non-property body, the
compiler cannot supply the key, so the caller must provide it explicitly.

## Syntax

### `caller(...)`

`caller(...)` is recognized only as a contextual compiler intrinsic in default
parameter expressions.

The valid selectors are:

```camp
caller(sourceline)
caller(sourcefile)
caller(propertyname)
caller(functionname)
caller(qualifiedname)
```

The selector inside `caller(...)` is an intrinsic selector, not an ordinary
identifier resolved by lookup. The call must contain exactly one selector.

`caller` is not a reserved word. If `caller` appears without function-call
syntax, it follows ordinary lookup:

```camp
inline string caller = "manual";

void writeLog(string owner = caller)
{
}
```

In that example, `caller` is an ordinary inline constant. There is no conflict
with the intrinsic form because the intrinsic is recognized only as
`caller(selector)` in a default parameter expression.

### `sourceof(...)`

`sourceof(...)` is recognized only as a compiler intrinsic in default parameter
expressions.

The only valid syntax is:

```camp
sourceof(argumentName)
```

`argumentName` must be a single identifier that names a parameter in the same
signature according to the ordinary default-parameter binding rules. Arbitrary
expressions are invalid:

```camp
void assert(bool condition, string text = sourceof(condition)); // valid
void check(int left, int right, string text = sourceof(left + right)); // invalid
```

## Placement Rules

The new intrinsics may appear in any function signature position that already
permits default parameter values. This includes ordinary functions, methods,
interface methods, callable newtype declarations, and ascribed implementation
surfaces where default values are permitted.

They may not appear outside default parameter expressions:

```camp
void log(string file = caller(sourcefile)); // valid

string file = caller(sourcefile); // invalid

void f()
{
	string name = caller(functionname); // invalid
}
```

The feature does not add default-value support to signatures that do not
otherwise allow defaults.

## Caller Capture Values

### `caller(sourceline)`

Type: `uint`.

Supplies the 1-based source line of the call expression that caused the default
argument to be inserted. Conceptually, this is the line where a debugger would
step for the call.

For multi-line calls, the value is still the line of the call expression, not a
column or argument-specific range.

### `caller(sourcefile)`

Type: `string`.

Supplies the source file path of the call expression.

The path is relative by default. When a build flag requests absolute source
paths, the value is an absolute path using the current target/host path rules.

For builds driven by a `.campbuild` file, the relative path is relative to the
directory containing that `.campbuild` file. This matches the existing rule that
relative source patterns and path-valued options in a build file are rebased to
the build file directory.

For package builds and loose source-file builds, the compiler request must
choose a deterministic relative base and use it consistently for diagnostics,
metadata tests, generated C, and logs.

### `caller(propertyname)`

Type: `string`.

Supplies the property name of the caller when the call appears inside a Camp
property accessor body.

The rule for determining the property name is exactly the existing Camp
property accessor rule:

- `getX` is the getter for property `X`;
- `setX` is the setter for property `X`;
- indexed property access follows the existing accessor and `@index` rules;
- a method is not a property merely because its name resembles an accessor
  unless Camp's property accessor rules recognize it as one.

If the call does not appear inside a property accessor body, this value is not
supplied. When a caller omits an argument whose default is
`caller(propertyname)` and the value is not supplied, the call is invalid unless
the caller provides the argument explicitly.

### `caller(functionname)`

Type: `string`.

Supplies the visible source name of the caller function. The same rule applies
to top-level functions, instance methods, static methods, out-of-scope member
declarations, interface methods, overrides, and generator declarations.

The name includes the overload suffix.

For constructors, the value is `"create"`. For destructors, the value is
`"destroy"`.

For overrides and generators, the value is the visible source name of the
function. Native symbols and generated helper names are irrelevant.

### `caller(qualifiedname)`

Type: `string`.

Supplies the visible source name of the caller function qualified with its
source namespace and containing type where present.

The format is:

```text
[Namespace::][Type.]functionName
```

Examples:

```text
parse
Json::parse
Json::Writer.writeString
```

The function name portion includes the overload suffix.

For constructors, the function name portion is `create`. For destructors, the
function name portion is `destroy`.

For overrides and generators, the function name portion is the visible source
name of the function. Native symbols and generated helper names are irrelevant.

## `sourceof(argumentName)`

`sourceof(argumentName)` produces a `string`.

The compiler uses the caller-written source text for the argument supplied to
`argumentName` at the call site.

Example:

```camp
void assert(bool condition, string expression = sourceof(condition));

assert(value.count == 3);
```

The default for `expression` is:

```camp
"value.count == 3"
```

If the caller did not supply an argument for `argumentName`, the value produced
by `sourceof(argumentName)` is the empty string:

```camp
void record(int value = 10, string expression = sourceof(value));

record(3); // expression == "3"
record();  // expression == ""
```

This rule preserves the meaning of `sourceof`: it captures what the caller
explicitly wrote. If the caller wrote nothing for that parameter, the captured
source text is empty.

### Source Text Normalization

The captured source text is normalized as follows:

- comments are removed;
- newlines are replaced with a single space;
- repeated whitespace outside string and character literals is collapsed to a
  single space;
- leading and trailing whitespace is removed;
- string and character literal spelling is preserved;
- escape sequences are preserved as written;
- whitespace is preserved where removing it would merge tokens.

The captured text is Camp source text before lowering. If the caller uses
property syntax, named arguments, index/range syntax, omitted trailing `out`
binding, arrays, delegates, optionals, or another expanded source form, the
captured string is the source spelling the caller wrote, not a generated helper
call or lowered ABI component list.

## Not Supplied Semantics

Some caller capture expressions cannot always produce a value. This proposal
uses the term **not supplied** for that case.

When a default parameter expression evaluates to not supplied, the compiler
does not insert an argument for that parameter. The call is valid only if the
caller supplied an explicit argument through another source form accepted by
ordinary call binding.

The main expected case is:

```camp
string getPropertyBagValue(string key = caller(propertyname));
```

When called from a property accessor body, `key` is supplied by the compiler.
When called from an ordinary function body, `key` is not supplied and the caller
must pass it explicitly:

```camp
string value = getPropertyBagValue("Name");
```

`sourceof(argumentName)` does not use not supplied when the referenced argument
was omitted. It supplies the empty string instead.

## Default Arguments, Callable Newtypes, And Interfaces

The capture intrinsics follow the same rules as other default parameter values.
They may appear anywhere default values are permitted, and the callable surface
used for the call determines which defaults are applied.

For direct calls to a concrete function or method, the concrete signature's
defaults are used.

For calls through an interface view, the interface callable surface determines
the defaults that apply to the call.

For callable newtypes and ascribed implementations, the existing callable
newtype and ascription rules determine which callable surface is being invoked
and therefore which defaults are applied.

Specifying equivalent defaults at an implementation or ascription site is
optional unless required by the existing callable/default rules. This proposal
does not add a separate rule for copying, inheriting, or merging source capture
defaults across related callable surfaces.

## Binding And Lowering

Default argument insertion must happen after the callable target and supplied
arguments are bound, and before callable lowering flattens the call.

The source capture insertion order is:

1. Resolve the callable and bind supplied arguments, including named arguments.
2. Determine which parameter defaults are needed.
3. Substitute caller capture defaults using the original source call site.
4. Substitute `sourceof(argumentName)` using the caller-written source text for
   the bound argument, or `""` when that argument was omitted.
5. Continue ordinary callable lowering, including instance calls, delegate
   calls, interface dispatch, thunks, `within`, `thrown`, `out`, `sizeof`,
   `typenameof`, and `vtableof`.

Generated thunks that exist only to preserve callable default behavior must
preserve source call-site provenance. The values supplied by these intrinsics
must describe the user's source call, not the thunk or lowered helper.

## Diagnostics

The compiler should report focused diagnostics for invalid uses:

- source capture intrinsic used outside a default parameter expression;
- unknown `caller(...)` selector;
- `caller(...)` with zero arguments or more than one argument;
- `caller(...)` selector that is not one of the supported intrinsic selectors;
- `sourceof(...)` with anything other than a single parameter name;
- `sourceof(argumentName)` where `argumentName` does not bind to a valid
  parameter in the same signature;
- omitted argument whose default evaluates to not supplied.

Diagnostics should point at the intrinsic call or selector that caused the
problem. For omitted arguments whose default is not supplied, diagnostics should
point at the call expression and name the parameter that still needs an
explicit argument.

## Metadata JSON

Metadata must preserve source capture defaults as source-level default
expressions on parameter records. They are not evaluated when metadata is
emitted, because package consumers need the defaults to capture their own call
sites.

The existing `defaultValue` field should preserve the source spelling:

```json
{
  "name": "line",
  "type": "uint",
  "defaultValue": "caller(sourceline)"
}
```

For tools that should not parse Camp expression text, parameter metadata should
also include a structured default expression record for these intrinsics:

```json
{
  "name": "line",
  "type": "uint",
  "defaultValue": "caller(sourceline)",
  "defaultExpression": {
    "kind": "caller",
    "selector": "sourceline"
  }
}
```

For `sourceof(argumentName)`:

```json
{
  "name": "expression",
  "type": "string",
  "defaultValue": "sourceof(condition)",
  "defaultExpression": {
    "kind": "sourceof",
    "argument": "condition"
  }
}
```

The structured `defaultExpression` record is source-level metadata. It is not a
lowered ABI artifact, and it does not contain the file, line, function name, or
source text captured for any particular call. Those values are computed by the
downstream compilation that actually inserts the default argument.

Ordinary default values may continue to use `defaultValue` without a structured
`defaultExpression` record unless another metadata feature gives them one.

## API Header Persistence

Camp API headers must preserve these defaults in source form:

```camp
public void assert(
	bool condition,
	string expression = sourceof(condition),
	string file = caller(sourcefile),
	uint line = caller(sourceline),
	string where = caller(qualifiedname));
```

A downstream project that imports this API header must see the same default
parameter expressions and apply them at its own call sites.

API header generation must not replace source capture defaults with values from
the library's build. For example, `caller(sourcefile)` in an exported API must
not become the file that declared the exported function. It remains
`caller(sourcefile)` so the consumer call site is captured later.

## Test Surface

Implementation should include tests for:

- `caller(sourceline)` in ordinary function calls;
- `caller(sourcefile)` relative path behavior from a `.campbuild`;
- `caller(sourcefile)` absolute path behavior under the build flag;
- `caller(functionname)` for top-level functions, instance methods, static
  methods, out-of-scope members, constructors, destructors, overrides,
  generators, and overloads;
- `caller(qualifiedname)` for namespaced functions and type members;
- `caller(propertyname)` from getters and setters;
- omitted `caller(propertyname)` outside property bodies producing a missing
  argument diagnostic;
- `sourceof(argumentName)` for positional and named supplied arguments;
- `sourceof(argumentName)` when the referenced argument was omitted, producing
  an empty string;
- rejection outside default parameter expressions;
- rejection of unknown `caller(...)` selectors;
- rejection of arbitrary `sourceof(...)` expressions;
- metadata JSON for `caller(...)` and `sourceof(...)` defaults;
- API header preservation across package or project references;
- callable reference/default thunk behavior;
- interface-view call versus concrete implementation call;
- callable-newtype/ascribed call behavior.

## Acceptance Criteria

The feature is complete when:

- every intrinsic is accepted only in default parameter expressions;
- `caller` remains contextual and does not block ordinary declarations or
  lookup of the name `caller`;
- call-site substitution happens before callable lowering;
- `caller(propertyname)` is not supplied outside property accessor bodies;
- `sourceof(argumentName)` accepts only a single parameter name;
- source text normalization is deterministic;
- metadata JSON preserves both source spelling and structured intrinsic data;
- API headers preserve source capture defaults for downstream call-site
  substitution;
- diagnostics are source-ranged and stable enough for tests.
