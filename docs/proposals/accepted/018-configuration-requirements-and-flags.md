# Configuration Requirements And Flags

## Status

Accepted.

## Proposal Date

2026-08-24

## Last Updated Date

2026-08-25

## Summary

This proposal replaces Camp's current source-level conditional preprocessing
model with semantic requirements over compiler-known configuration flags.

The proposal introduces:

- `@require(CONDITION)` as a declaration requirement attribute;
- standalone file metadata `@require(CONDITION);` as a file-level default
  requirement for top-level declarations;
- compiler intrinsic `configured(CONDITION)` as the only expression-level way to
  query whether a configuration expression is true;
- configuration flag declarations through `--declare` / `-d`;
- selected configuration values through `--configure` / `-c`;
- module-level dependency requirements through `--requires`;
- requirement policy selection through `--explicit-requires` and
  `--implicit-requires`;
- a base target that declares standard platform and capability flags before a
  final concrete target is selected;
- target-declared callspecs and typespecs with requirement expressions so
  platform-specific ABI syntax can be parsed before a final target is selected.

It removes:

- source-level `#if`, `#elif`, `#else`, and `#endif`;
- source-level conditional directives and source-local preprocessor symbol
  mutation directives;
- `@notsupported` as the normal target-availability mechanism.

Camp remains target-aware, but target awareness becomes semantic. Source files
parse as one coherent declaration graph. Declarations, fields, interface
members, virtual slots, generated declarations, API headers, metadata, and
future portable IR can carry requirements. Selected native emission still emits
only the declarations and layouts valid for the selected target.

The central rule is:

> A declaration with `@require(CONDITION)` is available only where the compiler
> can prove `CONDITION` is satisfied. Inside the declaration itself, the compiler
> treats `CONDITION` as satisfied.

Example:

```camp
@require(OS_WIN32)
void openWin32()
{
}

void f()
{
	if (configured(OS_WIN32))
		openWin32(); // valid

	openWin32(); // invalid
}
```

Configuration flags such as `OS_WIN32`, `SUBSYSTEM_POSIX`, `SUPPORTS_FILES`,
`DEBUG`, and `TEST_MODULE` do not enter ordinary source scope. They are
compiler-known configuration flags and are visible only inside:

- `@require(CONDITION)`;
- standalone file metadata `@require(CONDITION);`;
- `configured(CONDITION)`.

This prevents configuration flags from colliding with ordinary source names
while allowing target-aware flow analysis and portable metadata.

## Motivation

The current compiler has a preprocessing stage that removes inactive conditional
source before parsing. That model is simple for selected native builds, but it
creates long-term pressure:

- metadata and documentation describe only one selected target view;
- language-server behavior must select one target configuration instead of
  understanding the whole declaration graph;
- API headers cannot cleanly preserve target requirements for imported
  declarations;
- future portable IR cannot retain all target-specific declarations unless
  requirements are represented semantically;
- arbitrary token removal interacts badly with doc comments, attributes,
  generated declarations, iterators, async frames, vtables, constructors, and
  destructors.

The compiler already has a related semantic concept: test-only declarations
exist only in the test-module view and production declarations may not depend on
them. This proposal generalizes that kind of availability reasoning to named
configuration flags, then gives modules a way to publish the requirements their
consumers must satisfy.

The point is not to make Camp less target-aware. The point is to make target
awareness explicit, analyzable, serializable, and compatible with future
portable outputs.

## Goals

- Replace token-level conditional source removal with semantic requirements.
- Keep source declarations unique in each source scope.
- Keep configuration flags out of ordinary source lookup.
- Allow target-specific declarations to be represented in metadata and API
  surfaces.
- Allow target-specific callspec and typespec names to be represented
  semantically instead of hidden behind token-level preprocessing.
- Allow ordinary control flow to prove requirements before using a declaration.
- Support requirements on fields, interface members, virtual slots, overrides,
  generated iterator state, and generated async state.
- Support conditional interface conformance for portable types.
- Keep constructors and destructors simple by disallowing direct requirements on
  lifecycle declarations.
- Replace `@notsupported` with ordinary declaration requirements.
- Preserve selected native emission for the chosen target.
- Let reusable libraries publish module-level requirements.
- Let ordinary apps and desktop-focused libraries use ambient defaults while
  still emitting discovered requirements into API/metadata.

## Non-Goals

- Supporting a C-compatible arbitrary token preprocessor.
- Allowing conditionals to hide invalid Camp syntax.
- Allowing duplicate source declarations with the same identity because their
  requirements do not overlap.
- Allowing `@require` directly on constructors, destructors, individual
  parameters, individual generic parameters, base-class entries, or enum values.
- Redesigning the entire target system beyond the flag declaration/configuration
  and ABI-spec declaration model needed here.
- Adding arithmetic flag expressions or explicit one-of platform syntax.
- Changing `@test` discovery, test harness generation, or coverage semantics
  except where `@testonly` becomes requirement-based.

## Current Compiler Shape

Today, a `Compilation` carries target selection, profile information,
preprocessor symbols, source files, bindable trees, declaration expansion,
analysis/lowering results, owner maps, and emitted outputs.

The compiler currently distinguishes production and test-module declaration
participation:

- production participation excludes `@test`, `@testonly`, and generated
  declarations owned by test-only declarations;
- test-module participation includes production and test-only declarations.

The current preprocessing model runs before tokenization and parsing. It
consumes built-in symbols, profile symbols, target/variant symbols,
command-line or build-file preprocessor symbols, and the compiler-owned
`TEST_MODULE` symbol for test builds.

The current parser also receives callspec and typespec names from the selected
target. This means names such as `_winapi`, `_near`, or `_sysv` are recognized
only when the selected target reports them. Under this proposal, that selected
target-only parser view is replaced with a declared ABI-spec universe plus
semantic requirements on spec use.

This proposal changes that division:

- conditional source selection no longer removes tokens before parsing;
- configuration flags remain compiler-owned names outside ordinary lookup;
- declaration collection retains all syntactically valid declarations;
- declaration participation becomes requirement-aware rather than only
  test-aware;
- selected native emission still filters declarations, fields, vtable slots,
  generated declarations, and code paths for the selected configuration.

File-prelude `#within` and project-loading concerns such as `#build` remain
separate from this proposal. This proposal removes conditional source
directives, not all compiler pragmas.

## Terminology

| Term | Meaning |
|---|---|
| **configuration flag** | A compiler-known boolean configuration name such as `OS_WIN32`, `SUBSYSTEM_POSIX`, `SUPPORTS_FILES`, `DEBUG`, or `TEST_MODULE`. |
| **declared flag** | A flag name recognized by the compiler for this compilation graph. Declared flags have ambient values. |
| **ambient value** | The value assigned when a flag is declared. This value is used when no selected configuration value is supplied. |
| **configured value** | A selected value assigned to a declared flag by a target, variant, profile, project, or command line. A configured value may be true or false. |
| **unconfigured flag** | A declared flag with no selected configured value. Its ambient value is used. |
| **target-owned flag** | A flag declared or configured by the base target, concrete target, or target variant. It cannot be configured by the user through `--configure`. |
| **flag expression** | A boolean expression over declared flags using `!`, `&&`, `||`, `^`, and parentheses. |
| **requirement** | A flag expression that must be satisfied for a declaration, file, module, or dependency to be usable. |
| **effective requirement** | The requirement after applying containment, file metadata defaults, declaration-level attributes, and module-level requirements. |
| **requirement proof** | A flow fact proving that a flag expression is satisfied at a source location. |
| **selected configuration** | The actual flag values used for a selected native build, test run, coverage run, or AOT library build. |
| **portable configuration** | A symbolic configuration used for metadata/API/IR where final flag values may not all be known yet. |

Public documentation should use "configuration flag" and "requires." Semantic
documentation may also use "effective availability" internally where that maps
directly to compiler implementation.

## Configuration Flags

Configuration flags are boolean names known to the compiler before semantic
analysis. They can be declared by:

- the compiler;
- the base target;
- a concrete target;
- a profile;
- a project/build file;
- a referenced module/API/IR artifact;
- command-line declarations.

All flags used in `@require(...)`, standalone `@require(...);`, module
`--requires`, or `configured(...)` must be declared. Unknown flags are diagnostics.

Declared flags are not ordinary source identifiers.

Invalid:

```camp
if (OS_WIN32)
	Console.writeLine("Windows");
```

Valid:

```camp
if (configured(OS_WIN32))
	Console.writeLine("Windows");
```

This matters because the compiler may know a broad universe of flags from all
possible targets and referenced modules. If those flags entered ordinary source
scope, they could collide with variables, aliases, enum values, types, or
members.

## Flag Expressions

A flag expression is a boolean expression over declared flags using:

- `!`;
- `&&`;
- `||`;
- `^`;
- parentheses.

Examples:

```text
OS_WIN32
OS_WIN32 || SUBSYSTEM_POSIX
(OS_WIN32 || SUBSYSTEM_POSIX) && SUPPORTS_FILES
OS_WIN32 && !SUPPORTS_THREADS
OS_WIN32 ^ OS_DOS
```

Flag expressions are accepted only in:

- `@require(CONDITION)`;
- standalone file metadata `@require(CONDITION);`;
- module/build `--requires CONDITION`;
- compiler intrinsic `configured(CONDITION)`.

They are not ordinary Camp expressions. A flag name inside a flag expression
binds in the compiler-owned configuration namespace.

`^` is boolean exclusive-or. This proposal intentionally leaves arithmetic flag
expressions and explicit one-of syntax out. The first version only needs boolean
requirement logic.

## `configured(CONDITION)`

`configured(CONDITION)` is a compiler intrinsic expression. It evaluates a flag
expression as a compile-time boolean value in the current selected/proven
configuration.

Examples:

```camp
configured(OS_WIN32)
configured(OS_WIN32 || SUBSYSTEM_POSIX)
configured(SUPPORTS_FILES && !SUPPORTS_THREADS)
```

`configured(...)` may appear anywhere an ordinary boolean expression is accepted:

```camp
if (configured(OS_WIN32))
	useWin32();

while (configured(SUPPORTS_TIMERS) && pollTimer())
	readTimerEvent();

bool canUseFiles = configured(SUPPORTS_FILES);
```

In selected native emission, `configured(...)` can be constant-folded. During
analysis, it also contributes requirement flow facts.

Inside this block, `OS_WIN32` is proven true:

```camp
if (configured(OS_WIN32))
{
	openWin32();
}
```

Inside both the right-hand side of the loop condition and the loop body,
`SUPPORTS_TIMERS` is proven true:

```camp
while (configured(SUPPORTS_TIMERS) && pollTimer())
{
	readTimerEvent();
}
```

This is ordinary short-circuit flow. The right operand of `&&` is evaluated
only when the left operand is true, so the requirement proof is available while
analyzing `pollTimer()` as well as the loop body.

By contrast, a condition with multiple possible guarded branches does not prove
either branch-specific requirement in the body:

```camp
while ((configured(OS_WIN32) && pollWin32()) || (configured(SUBSYSTEM_POSIX) && pollPosix()))
{
	readEvent(); // neither OS_WIN32 nor SUBSYSTEM_POSIX is proven here
}
```

The compiler should not special-case only `if`. It should integrate
`configured(...)` with ordinary flow analysis wherever the existing flow system can
soundly carry facts.

The term `configured` in source means "this configuration expression is true in
the current selected/proven configuration," not merely "this flag name was
declared" and not merely "this flag has an explicitly configured value."
Documentation and diagnostics must keep these distinctions clear:

- `--declare APP_FEATURE` declares a flag with a false ambient value;
- `--declare APP_FEATURE=true` declares a flag with a true ambient value;
- `--configure APP_FEATURE=true` assigns a selected true value to a flag
  declared with `--declare`;
- `--configure APP_FEATURE=false` assigns a selected false value to a
  flag declared with `--declare`;
- `configured(SUPPORTS_FILES)` queries whether `SUPPORTS_FILES` is true in the
  current selected/proven configuration.

## `@require(CONDITION)`

`@require(CONDITION)` attaches a requirement to a declaration.

```camp
@require(OS_WIN32)
void openWin32();

@require((OS_WIN32 || SUBSYSTEM_POSIX) && SUPPORTS_FILES)
void openFile(const char[] path);

@require(TEST_MODULE)
void makeFixture();
```

The condition is a flag expression, not an ordinary Camp expression. It does not
use `configured(...)` inside the attribute.

Within the declaration, the compiler treats the requirement as proven:

```camp
@require(OS_WIN32)
void f()
{
	openWin32(); // valid without another guard
}
```

`@require` is valid on:

- top-level functions, variables, aliases, types, newtypes, callables, static
  classes, enums, and interfaces;
- ordinary methods, static methods, fields, and inline constants;
- interface methods;
- abstract methods;
- virtual methods;
- overrides.

`@require` is not valid directly on:

- constructors;
- destructors;
- individual parameters;
- individual generic parameters;
- individual base-class entries;
- enum values.

Requirements do not allow duplicate source declarations with the same identity.

Invalid:

```camp
@require(OS_WIN32)
void open();

@require(SUBSYSTEM_POSIX)
void open();
```

Use unique implementation names and a stable wrapper instead:

```camp
@require(OS_WIN32)
void openWindows()
{
}

@require(SUBSYSTEM_POSIX)
void openPosix()
{
}

void open()
{
	if (configured(OS_WIN32))
		openWindows();
	else if (configured(SUBSYSTEM_POSIX))
		openPosix();
}
```

The second guard is required. A plain `else` branch proves `!OS_WIN32`, but it
does not by itself prove `SUBSYSTEM_POSIX` at the call site.

## File-Level `@require`

Standalone file metadata can set the default requirement for top-level
declarations in a file:

```camp
namespace Std;

@require(SUBSYSTEM_POSIX);

extern int usleep(uint micros);
```

File-level metadata applies only to top-level declarations that do not provide
their own attribute of the same kind. A declaration-level `@require` replaces
the file-level `@require`; it does not combine with it.

Example:

```camp
@require(SUBSYSTEM_POSIX);

void commonPosix();

@require(SUBSYSTEM_POSIX && SUPPORTS_TIMERS)
void posixTimer();
```

`commonPosix` requires `SUBSYSTEM_POSIX`. `posixTimer` requires
`SUBSYSTEM_POSIX && SUPPORTS_TIMERS`, because it spells the full declaration
requirement.

This replacement behavior is not special to `@require`; it follows normal file
metadata rules. If a declaration needs to refine the file default, it must spell
the full requirement.

File-level metadata applies only to top-level declarations. It does not apply
directly to members nested inside a type. Member requirements still combine with
the requirement of the declaring type.

## Containment And Out-Of-Scope Declarations

Requirements combine through containment.

```camp
@require(OS_WIN32)
class WinHandle
{
	void close()
	{
	}

	@require(DEBUG)
	void debugDump()
	{
	}
}
```

`close` requires `OS_WIN32`. `debugDump` requires `OS_WIN32 && DEBUG`.

This containment rule applies only to declarations actually scoped inside the
containing declaration. Requirements of referenced types do not flow outward
into the declaration that references them.

For an out-of-scope method, the receiver is a formal `this` parameter. The
method must declare enough requirement to reference that receiver type:

```camp
@require(OS_WIN32)
class WinHandle
{
}

// invalid: WinHandle requires OS_WIN32
void close(WinHandle* this)
{
}

// valid
@require(OS_WIN32)
void close(WinHandle* this)
{
}
```

The same applies to parameters, return types, field types, generic constraints,
and other signature references:

```camp
@require(OS_WIN32)
struct WNDCLASS
{
}

// invalid
void registerClass(WNDCLASS* cls)
{
}

// valid
@require(OS_WIN32)
void registerClass(WNDCLASS* cls)
{
}
```

The compiler should diagnose the invalid declaration because the declaration is
available everywhere while its signature references a Windows-only type.

## Constructors And Destructors

Do not allow `@require` on constructors or destructors.

The rule remains one lifecycle surface per type. If a type has a requirement,
its constructors and destructors inherit that requirement from the type.

Valid:

```camp
@require(OS_WIN32)
class WinResource
{
	WinResource()
	{
	}

	~WinResource()
	{
	}
}
```

Invalid:

```camp
class Resource
{
	@require(OS_WIN32)
	Resource()
	{
	}
}
```

Invalid:

```camp
class Resource
{
	@require(OS_WIN32)
	~Resource()
	{
	}
}
```

Constructor-specific requirements are complicated because constructors can
generate fields and lifecycle helpers. Destructor-specific requirements are
worse because they change generated delete/destroy behavior and may change which
cleanup path exists at a delete site.

A portable destructor that needs platform-specific cleanup should use ordinary
flow:

```camp
class Resource
{
	~Resource()
	{
		if (configured(OS_WIN32))
			releaseWin32();
		else if (configured(SUBSYSTEM_POSIX))
			releasePosix();
	}
}
```

## Signatures, Parameters, And Generic Constraints

Do not allow `@require` directly on individual parameters or generic parameters.

Instead, the containing declaration must be available wherever its signature
requires conditionally available declarations.

Valid:

```camp
@require(OS_WIN32)
class WindowHandle
{
}

@require(OS_WIN32)
void show(WindowHandle* handle)
{
}
```

Invalid:

```camp
@require(OS_WIN32)
class WindowHandle
{
}

void show(WindowHandle* handle)
{
}
```

The same rule applies to generic constraints:

```camp
@require(OS_WIN32)
interface IWinHandle
{
}

@require(OS_WIN32)
void useHandle<T: IWinHandle>(T value)
{
}
```

If a generic constraint references a conditionally available interface, the
generic declaration's effective requirement must prove that interface is
available.

## Base Classes

A class may derive from a conditionally available base only when the class's
effective requirement proves the base class is available.

Valid:

```camp
@require(OS_WIN32)
class WinBase
{
}

@require(OS_WIN32)
class WinDerived: WinBase
{
}
```

Invalid:

```camp
@require(OS_WIN32)
class WinBase
{
}

class Derived: WinBase
{
}
```

Do not allow `@require` directly on one base-class entry. Base classes affect
layout, construction, destruction, and inherited member shape.

## Interface Conformance

A type may implement a conditionally available interface while remaining more
broadly available. The conformance edge exists only where the interface is
available.

Valid:

```camp
@require(OS_WIN32)
interface IWinDrawable
{
	void draw();
}

class Control: IWinDrawable
{
	void draw(): IWinDrawable
	{
	}
}
```

`Control` is available everywhere. Its `IWinDrawable` conformance requires
`OS_WIN32`.

Methods implementing interface members must still use normal interface
ascription. If the implementing method's signature references conditionally
available types, the implementing method or declaring type must have sufficient
requirement:

```camp
@require(OS_WIN32)
struct WNDCLASS
{
}

@require(OS_WIN32)
interface IWinDrawable
{
	void draw(WNDCLASS* cls);
}

class Control: IWinDrawable
{
	// invalid: the method signature references WNDCLASS
	void draw(WNDCLASS* cls): IWinDrawable
	{
	}
}
```

Valid:

```camp
class Control: IWinDrawable
{
	@require(OS_WIN32)
	void draw(WNDCLASS* cls): IWinDrawable
	{
	}
}
```

Interface members may also have their own requirements:

```camp
interface IPlatformControl
{
	void common();

	@require(OS_WIN32)
	void hwnd();
}

class Control: IPlatformControl
{
	void common(): IPlatformControl
	{
	}

	@require(OS_WIN32)
	void hwnd(): IPlatformControl
	{
	}
}
```

For every configuration where an interface member is available, an implementing
method must exist, must ascribe to the interface, and must have a requirement at
least as broad as the interface member.

## Abstract Methods

Abstract method completeness is requirement-aware.

For every configuration where a non-abstract derived type is available, every
available abstract base member must be implemented by an available override.

Valid:

```camp
abstract class Base
{
	@require(OS_WIN32)
	abstract void winMethod();
}

class Derived: Base
{
	@require(OS_WIN32)
	override void winMethod()
	{
	}
}
```

An unconditional abstract method cannot be satisfied by a Windows-only override
unless the derived type is abstract or itself Windows-only.

## Virtual Methods, Overrides, And Vtables

Virtual methods may have requirements.

```camp
class Base
{
	@require(OS_WIN32)
	virtual void draw();
}
```

The virtual slot requires `OS_WIN32`. A selected native vtable contains that
slot only when `OS_WIN32` is true. Portable metadata and future portable IR
preserve the slot requirement.

Overrides must be at least as available as the method they override.

Valid:

```camp
class Derived: Base
{
	@require(OS_WIN32)
	override void draw()
	{
	}
}
```

Also valid:

```camp
class Derived: Base
{
	override void draw()
	{
		if (configured(OS_WIN32))
		{
			// no base call required
		}
	}
}
```

The second override is available everywhere, so it is broad enough to satisfy
the Windows-only slot. Overrides are not directly callable as ordinary methods;
they exist to fill virtual dispatch slots. In a non-Windows selected build,
there is no base slot for `draw`, so the override relationship has no emitted
vtable entry and native output can prune the helper if it is otherwise unused.

Invalid:

```camp
class Base
{
	virtual void draw();
}

class Derived: Base
{
	@require(OS_WIN32)
	override void draw()
	{
	}
}
```

The base slot is available everywhere, but the override requires `OS_WIN32`.
A concrete derived type would fail to provide the required slot in non-Windows
configurations.

Base calls are ordinary requirement-sensitive accesses:

```camp
class Base
{
	@require(OS_WIN32)
	virtual void draw();
}

class Derived: Base
{
	override void draw()
	{
		base.draw(); // invalid: this method is broader than the base slot
	}
}
```

Fix by narrowing the override or guarding the call:

```camp
class Derived: Base
{
	@require(OS_WIN32)
	override void draw()
	{
		base.draw();
	}
}
```

Vtable generation rules:

- a virtual slot exists under the requirement of the virtual method that
  introduced it;
- an override must be at least as available as the slot it implements;
- selected native vtables include only selected slots;
- generated vtable storage and initialization inherit type and slot
  requirements;
- portable metadata and future portable IR preserve slot requirements.

## Fields And Layout

Fields may have requirements.

```camp
struct NativeHandle
{
	@require(OS_WIN32)
	nuint handle;

	@require(SUBSYSTEM_POSIX)
	int fd;
}
```

Selected native output includes only selected fields. Portable metadata and
future portable IR preserve field requirements.

Conditional fields are required because source-authored layouts are not the only
layouts the compiler creates. Iterator state and async frame generation may need
conditionally available fields when a captured value exists only under a proven
configuration condition.

The compiler already has to resolve final layout for target facts such as
pointer size and ABI. Field requirements become another input to selected
layout.

## Enum Values

Do not allow `@require` on individual enum values.

Invalid:

```camp
enum PlatformEvent
{
	COMMON,
	@require(OS_WIN32) WINDOW_MESSAGE
}
```

Enum values are constants with ordering and carrier values. Conditional enum
values create switch-coverage, ABI, numbering, and documentation footguns. If an
enum is platform-specific, put the requirement on the enum declaration:

```camp
@require(OS_WIN32)
enum WinEvent
{
	WINDOW_MESSAGE
}
```

## Requirement-Aware Flow Exhaustiveness

The compiler should use declaration requirements as assumptions for flow
analysis inside the declaration.

This should be valid:

```camp
@require(OS_WIN32 || SUBSYSTEM_POSIX)
nint getFileHandle()
{
	if (configured(OS_WIN32))
		return getFileHandleWin32();
	else if (configured(SUBSYSTEM_POSIX))
		return getFileHandlePosix();
}
```

The declaration requirement proves `OS_WIN32 || SUBSYSTEM_POSIX` for the
entire method body. The first branch handles the `OS_WIN32` case. The
second branch explicitly proves `SUBSYSTEM_POSIX`, which is required to call
`getFileHandlePosix()`.

After those two guarded branches, the compiler should know there is no remaining
reachable configuration state:

```text
required:  OS_WIN32 || SUBSYSTEM_POSIX
handled:   OS_WIN32
handled:   !OS_WIN32 && SUBSYSTEM_POSIX
remaining: none
```

Therefore the method satisfies "all paths return a value" without requiring an
unreachable tail:

```camp
else
	not_reachable;
```

The second guard remains necessary. This is not valid:

```camp
@require(OS_WIN32 || SUBSYSTEM_POSIX)
nint getFileHandle()
{
	if (configured(OS_WIN32))
		return getFileHandleWin32();
	else
		return getFileHandlePosix(); // invalid: SUBSYSTEM_POSIX is not proven
}
```

In the `else` branch, the compiler knows `!OS_WIN32`, but it should not
silently infer the callee's requirement. The source must prove the callee's
requirement at the call site. The explicit `else if (configured(SUBSYSTEM_POSIX))`
does that.

The first implementation should at least support simple disjunction coverage for
returning, throwing, or otherwise terminating branches. Broader boolean
exhaustiveness can be improved later.

## Conditional Constants

This proposal allows configuration-dependent constants where the compiler can
preserve a single source declaration.

### Aliases

Ordinary alias syntax remains narrow:

```camp
alias A = B;
```

The right-hand side is an alias target name. In the current compiler this is
parsed as the existing qualified-name alias target syntax, such as `B` or
`SomeNamespace::B`, and then resolved using the existing alias target rules.
This proposal does not make alias targets arbitrary expressions.

The proposal adds a deliberate conditional alias-list form:

```camp
alias TSTRING =
	configured(UNICODE): wstring,
	astring;
```

The same form works for callable aliases:

```camp
alias CreateDirectory =
	configured(UNICODE): CreateDirectoryW,
	CreateDirectoryA;
```

Rules:

- an alias may still name a single target;
- an alias may instead provide a comma-separated list of candidate targets;
- every candidate except the final fallback candidate must begin with
  `configured(FLAGS):`;
- `FLAGS` is a flag expression;
- candidates are evaluated in source order;
- the first candidate whose `configured(...)` guard is true/proven true is
  selected;
- the final unguarded candidate is selected if no earlier guard is true;
- guards do not have to be mutually exclusive because source order resolves
  overlap.

Example with overlapping conditions:

```camp
alias NativePath =
	configured(OS_WIN64): Win64Path,
	configured(OS_WIN32): Win32Path,
	PortablePath;
```

On 64-bit Windows, `OS_WIN64` and `OS_WIN32` may both be true. The alias selects
`Win64Path` because it appears first.

The grammar is intentionally limited. Informally:

```text
alias-declaration :=
    alias IDENTIFIER = alias-target ;
  | alias IDENTIFIER = guarded-alias-target-list ;

guarded-alias-target-list :=
    guarded-alias-target , guarded-alias-target-list
  | alias-target

guarded-alias-target :=
    configured(FLAG_EXPRESSION) : alias-target
```

`alias-target` means the alias target syntax accepted today: the existing
name/qualified-name form that resolves to a valid alias target under current
alias semantics. `configured(...)` may contain a flag expression. No other
expressions are allowed in an alias declaration.

Invalid:

```camp
alias Bad =
	configured(OS_WIN32): string,
	Console.writeLine;
```

The problem is not that this mixes target kinds in an arbitrary expression. The
problem is simpler: `Console.writeLine` uses member-access syntax, which is not
valid alias target syntax. This proposal does not broaden alias targets beyond
adding guarded selection.

### Metadata Attribute Arguments

Attributes that currently accept string literals should parse ordinary
expressions, but this proposal only permits a deliberately small compile-time
string expression subset, and only permits that subset semantically for
`@symbol`.

The primary target is `@symbol`:

```camp
alias TSTRING =
	configured(UNICODE): wstring,
	astring;

@symbol(
	configured(UNICODE) ? "OpenFileW" :
	configured(OS_WIN32) ? "OpenFileA" :
	"OpenFile")
extern HANDLE OpenFile(TSTRING* filename);
```

This keeps one source declaration while allowing ABI spelling to vary by
configuration.

The parser should accept the syntax in other string-valued attributes so the
grammar does not need an attribute-specific branch. Semantic validation should
reject those uses for now:

```camp
@category(configured(OS_WIN32) ? "Windows" : "Portable")
public void f();
```

This is invalid in the first version because `@category` is not `@symbol`. A
good diagnostic should explain that conditional string metadata is currently
supported only for `@symbol`.

For `@symbol`, semantic validation should reject any parsed expression that is
not made only from:

- string literals;
- the ternary conditional operator;
- `configured(FLAG_EXPRESSION)` as the ternary condition.

Each ternary arm must itself produce a string. This is intentionally a semantic
restriction, not a parser restriction, so future proposals can allow additional
constant-expression forms without reshaping the grammar.

Portable metadata and API headers should preserve conditional `@symbol`
expressions when needed. Selected native emission evaluates them to the
selected value.

## Command-Line And Build Roles

The configuration arguments in this section are available from both command-line
invocation and build-file configuration. In other words, `--declare`,
`--configure`, `--requires`, `--explicit-requires`, and `--implicit-requires`
should be accepted through both `#build` and `.campbuild` using the same
semantics.

### `--declare` / `-d`

`--declare` introduces a configuration flag name into the compiler's recognized
flag universe and assigns its ambient value. Flags declared with `--declare`
are command-line-configurable flags.

Forms:

```text
-d APP_FEATURE
--declare APP_FEATURE=true
-d APP_FEATURE=false
```

Rules:

- `--declare FLAG` is equivalent to `--declare FLAG=false`;
- `--declare FLAG=false` declares `FLAG` with a false ambient value;
- `--declare FLAG=true` declares `FLAG` with a true ambient value;
- a flag may be declared only once in the combined configuration universe;
- all flags used in source or module requirements must be declared.

Packages, standard library builds, and portable libraries should mostly use
`--declare` to say which flags exist. Because the combined configuration
universe has no duplicate flag declarations, package-specific flags should use
a package prefix to avoid collisions:

```text
--declare ANSITERM_VT100
--declare JSONVIEW_COLOR=true
```

### `--configure` / `-c`

`--configure` assigns the selected value of a declared flag.

Forms:

```text
--configure APP_FEATURE
--configure APP_FEATURE=true
--configure APP_FEATURE=false
-c APP_FEATURE=true
```

Rules:

- `--configure FLAG` is equivalent to `--configure FLAG=true`;
- `--configure FLAG=false` assigns a selected false value;
- `--configure` may only name flags declared with `--declare`;
- a flag may be configured only once;
- `--configure` may not name flags declared by the base target, concrete
  target, or target variant;
- if a target or target variant configures a flag, command-line and project
  configuration may not configure that flag again;
- if no selected configuration value is supplied, the flag's ambient value is
  used.

`--configure` is for user or library flags intentionally configurable by the
final build. It may configure flags declared by the current module or by
referenced modules, API headers, or portable metadata, provided those flags were
declared with `--declare` and have not already been configured. This is how an
executable can select its own build options, or select dependency options
published by referenced modules.

`--configure` must not be used to spoof target facts. Flags such as `OS_WIN32`,
`OS_LINUX`, `OS_MACOSX`, `SUBSYSTEM_POSIX`, and `SUPPORTS_FILES` are
target-owned because they are declared by the base target. Users select a target
that configures those values.

This preserves the current target-owned restriction: source should not pretend
to be compiled for a different target by manually supplying target facts.

### `--requires`

`--requires` publishes a module-level requirement.

Forms:

```text
--requires OS_WIN32
--requires "OS_WIN32 && SUPPORTS_FILES"
```

Rules:

- the current module can only be used by consumers whose selected/proven
  configuration satisfies the expression;
- the expression may mention flags declared by the current module, referenced
  modules, the base target, or selected target;
- the requirement is emitted into metadata/API/IR so consuming modules can check
  it;
- module-level requirements are assumed true while analyzing the module.

This is the right shape for a Windows-only library:

```text
campc build windowslib.campbuild --requires OS_WIN32
```

Then source files do not need `@require(OS_WIN32)` everywhere. The module as a
whole has that requirement.

If `app` references `windowslib`, `app` must satisfy `OS_WIN32`. In practice
that means selecting a target that configures `OS_WIN32=true`.

### `--explicit-requires` And `--implicit-requires`

`--explicit-requires` and `--implicit-requires` control how the compiler handles
ambient true defaults when source uses declarations that have requirements.

Defaults:

- reusable libraries default to `--explicit-requires`;
- test/cover builds of libraries default to `--explicit-requires`;
- executables default to `--implicit-requires`.

Under `--explicit-requires`, using a declaration that requires a flag is valid
only when the source context explicitly proves or carries that requirement:

```camp
// FileSystem.writeFile requires SUPPORTS_FILES.

public void save()
{
	FileSystem.writeFile("out.txt", "text"); // invalid under explicit-requires
}
```

Fix by making the requirement part of the library's contract:

```camp
@require(SUPPORTS_FILES)
public void save()
{
	FileSystem.writeFile("out.txt", "text");
}
```

or by publishing a module-level requirement:

```text
--requires SUPPORTS_FILES
```

Under `--implicit-requires`, ambient true defaults may satisfy the call. If the
compiler discovers that the module used required features without spelling those
requirements explicitly, it records those discovered requirements in API
headers/metadata.

For example, if a module uses file and timer APIs under `--implicit-requires`,
and those requirements were not already attached to specific declarations, the
generated API header can begin with:

```camp
@require(SUPPORTS_FILES && SUPPORTS_TIMERS);
```

This is appropriate for desktop-focused libraries and ordinary applications. If
an executable targets WASI and `SUPPORTS_FILES=false`, unguarded file API use should
produce a build error. The fix for an executable is usually to stop using files
or choose a target that supports them, not to publish a requirement.

For portable libraries that want to support capability-limited targets,
`--explicit-requires` forces the author to choose between:

- requiring the capability for the whole module; or
- limiting the requirement to specific entrypoints so the rest of the module
  remains usable on targets without that capability.

## Library Builds Versus Final Deliverables

The conceptual split is:

- libraries declare flags and publish requirements;
- final deliverables select targets and configure only flags explicitly declared
  with `--declare`.

For a source library or portable IR library, the compiler may not know final
operating system, subsystem, pointer size, calling convention, filesystem
support, thread support, or timer support. Those may be known only when the
library is consumed by an executable or AOT native library build.

For a final executable, test, coverage run, or AOT native library, the compiler
needs selected values. Native C emission cannot leave platform facts symbolic.

Model:

```text
source package / portable IR:
  declare known flags
  publish requirements
  preserve conditional declarations

final native build:
  load declared flags
  select target/profile
  receive target-owned values from the selected target
  configure command-line-declared final values
  validate requirements
  emit selected native output
```

## Base Target And Standard Flags

A base target should declare the standard configuration flag universe before a
final concrete target has been selected.

Its job is to:

1. declare standard platform and capability flags;
2. provide defaults where defaults are meaningful;
3. avoid pretending a final platform has been selected when it has not.

Flags declared by the base target are target-owned even when their selected
value is only the ambient value. They cannot be configured from the command
line. A user-configurable flag must be introduced explicitly with `--declare`.

Sketch:

```ini
[declare]
OS_DOS=false
OS_WIN16=false
OS_WIN32=false
OS_WIN64=false
OS_LINUX=false
OS_MACOSX=false
OS_WASI=false
RUNTIME_EMSCRIPTEN=false

SUBSYSTEM_POSIX=false
SUBSYSTEM_DARWIN=false

ARCH_X86=false
ARCH_X64=false
ARCH_ARM64=false
ARCH_WASM32=false

SUPPORTS_FILES=true
SUPPORTS_TIMERS=true
SUPPORTS_THREADS=true
SUPPORTS_NETWORK=true

# Reserved for future use.
# SUPPORTS_ENVIRONMENT=true
# SUPPORTS_PROCESSES=true
# SUPPORTS_TERMINAL=true

# Reserved for future use.
# COMPILER_GCC=false
# COMPILER_CLANG=false
# COMPILER_MSVC=false
```

Concrete targets configure selected target-owned values:

```ini
# msvc-windows-x64.target
[configure]
OS_WIN32=true
OS_WIN64=true
ARCH_X64=true
```

```ini
# gcc-linux-x64.target
[configure]
OS_LINUX=true
SUBSYSTEM_POSIX=true
ARCH_X64=true
```

```ini
# clang-macos-x64.target
[configure]
OS_MACOSX=true
SUBSYSTEM_POSIX=true
SUBSYSTEM_DARWIN=true
ARCH_X64=true
```

```ini
# clang-macos-arm64.target
[configure]
OS_MACOSX=true
SUBSYSTEM_POSIX=true
SUBSYSTEM_DARWIN=true
ARCH_ARM64=true
```

```ini
# gcc-emscripten-wasm.target
[configure]
RUNTIME_EMSCRIPTEN=true
ARCH_WASM32=true
SUPPORTS_FILES=false
SUPPORTS_THREADS=false
```

Defaulting `SUPPORTS_*` to `true` is convenient and appropriate for ordinary
full-platform targets. Requirement policy controls whether relying on those
defaults is accepted silently and exported as an implicit module requirement, or
rejected until the author spells the requirement explicitly.

The standard flag set should remain small and stable. It should include facts
that materially affect compile-time shape, link behavior, ABI, or standard
library availability. It should avoid details such as Linux distro or specific
modern Windows version unless the standard library actually needs that
compile-time distinction.

`SUPPORTS_*` describes the environment capability, not whether a standard
library file exists.

## Target Migration, Variants, Callspecs, And Typespecs

The existing target files currently use preprocessor-style symbols such as
`WINDOWS`, `LINUX`, `MACOS`, `POSIX`, `X86`, `X64`, `ARM64`, `WASM`, `WASI`, `NO_FILES`,
`NO_TIMERS`, `UNICODE`, `_UNICODE`, `GCC`, `CLANG`, and `MSVC`.

This proposal migrates the source-visible platform symbols to standardized
configuration flags. The intended first target migration is:

| Current target symbol | Proposed target configuration |
|---|---|
| `WINDOWS` | No direct replacement; configure `OS_WIN16`, `OS_WIN32`, or `OS_WIN64` according to the target. |
| `LINUX` | `OS_LINUX` |
| `MACOS` | `OS_MACOSX` |
| `WASI` | `OS_WASI` |
| `EMSCRIPTEN` | `RUNTIME_EMSCRIPTEN` |
| `POSIX` | `SUBSYSTEM_POSIX` |
| `WIN16` | `OS_WIN16` |
| `WIN32` | `OS_WIN32` and `ARCH_X86` |
| `WIN64` | `OS_WIN32`, `OS_WIN64`, and `ARCH_X64` |
| `X86` | `ARCH_X86` |
| `X64` | `ARCH_X64` |
| `ARM64` | `ARCH_ARM64` |
| `WASM` | `ARCH_WASM32` |
| `NO_FILES` | `SUPPORTS_FILES=false` |
| `NO_TIMERS` | `SUPPORTS_TIMERS=false` |
| `UNICODE` | `UNICODE` |
| `_UNICODE` | C preprocessor symbol only, not a Camp configuration flag |

Target files should use `[configure]` and variant-specific
`[configure:<variant>]` sections for selected configuration values.

The Windows flags intentionally follow the familiar `_WIN16`, `_WIN32`, and
`_WIN64` C macro family:

- `OS_WIN16` means a Windows module with a segmented, 16-bit memory model;
- `OS_WIN32` means a Windows module with a flat memory model. This includes
  Windows NT, Windows CE, Win32S, Windows 95, 32-bit Windows, and 64-bit
  Windows;
- `OS_WIN64` means a Windows module with a 64-bit memory model. This includes
  both Intel and ARM Windows targets.

Most Windows API declarations should require `OS_WIN32`. A declaration that is
only valid for 64-bit Windows should require `OS_WIN64`. A declaration that is
only valid for the flat 32-bit Windows memory model can require
`OS_WIN32 && !OS_WIN64`. A declaration that is only valid for 32-bit x86 Windows
can require `OS_WIN32 && ARCH_X86`.

`ARCH_X86` and `ARCH_X64` are architecture facts, not Windows API-family facts.
Windows targets may also use other architecture flags in the future. `ARCH_ARM64`
is part of the standard set immediately so a future `clang-macos-arm64` target
can use it without another flag migration.

`OS_DOS` is included as a standard operating-system flag for future pure DOS
targets, but none of the current checked-in targets configure it.

The compiler/toolchain names should not normally be source-visible platform
requirements. Existing `GCC`, `CLANG`, and `MSVC` target symbols should become
target/toolchain capabilities used by the compiler. The base target should
reserve commented-out `COMPILER_GCC`, `COMPILER_CLANG`, and `COMPILER_MSVC`
flags, but they should remain inactive until a concrete source-level use case
appears. Do not keep accidental compiler-name symbols as part of the ordinary
source flag surface.

Likewise, `C99` is better treated as a target capability or C emission fact,
not as a source-visible configuration flag unless a concrete source-level use
case appears.

`UNICODE` is the important exception. It is a source-visible, target-owned
configuration flag because Windows API headers and wrappers need to choose
aliases and symbols based on the selected character-width variant:

```camp
alias TSTRING =
	configured(UNICODE): wstring,
	string;

@symbol(configured(UNICODE) ? "CreateWindowExW" : "CreateWindowExA")
@require(OS_WIN32)
extern _winapi HWND CreateWindowEx(TSTRING className);
```

Users should not set `UNICODE` with `--configure`. They select a target variant,
and the target owns the selected value. The target may still pass both
`/DUNICODE` and `/D_UNICODE` or equivalent C compiler flags to native C
compilation. `_UNICODE` does not need to enter Camp's configuration flag
namespace.

### Variants

Variants and configuration flags remain separate concepts:

- a variant is a target-selection overlay that can change native spelling,
  compiler flags, default typespecs, conversions, cache identity, or other
  target facts;
- a configuration flag is a semantic boolean that source can require or query
  through `@require` and `configured(...)`.

A variant may configure target-owned configuration flags when source needs to
observe the selection. Do not automatically expose every variant name as a
configuration flag.

For example, the Windows `charwidth=ansi unicode*` variant should configure
`UNICODE=true` only for the `unicode` variant. The variant still controls C
compiler configures independently:

```ini
[variant]
charwidth=ansi unicode*

[configure:unicode]
UNICODE=true

[profile.DEBUG:unicode]
cflags=/Zi /DUNICODE /D_UNICODE
```

The `memorymodel` variant on the 16-bit MSVC target should remain a target
variant because it controls pointer defaults, conversions, and native ABI
layout. It should not automatically produce user-facing flags such as
`MEMORYMODEL_HUGE` unless source needs to query that fact. Most memory-model
effects are represented through selected target facts such as default data and
function pointer typespecs.

### Callspecs And Typespecs

The existing parser receives the selected target's known callspec and typespec
names. That is not sufficient once conditional source is semantic rather than
token-level. Source may contain a platform-specific spec in a declaration that
is unavailable for the selected target, and that source still must parse.

Therefore the base target should declare the known callspec and typespec
universe with requirement expressions:

```ini
[declare.callspec]
_cdecl=true
_stdcall=OS_WIN16 || OS_WIN32
_fastcall=OS_WIN16 || OS_WIN32
_thiscall=OS_WIN32
_pascal=OS_DOS || OS_WIN16
_winapi=OS_WIN16 || OS_WIN32
_sysv=SUBSYSTEM_POSIX && (ARCH_X86 || ARCH_X64)
_msabi=SUBSYSTEM_POSIX && ARCH_X64

[declare.typespec]
_near=OS_DOS || OS_WIN16 || OS_WIN32
_far=OS_DOS || OS_WIN16 || OS_WIN32
_huge=OS_DOS || OS_WIN16 || OS_WIN32
```

The `_near`, `_far`, and `_huge` typespecs remain source-addressable for the
Windows-family targets. On 16-bit Windows they lower to the selected native
spelling such as `__near`, `__far`, or `__huge`. On Win32 and Win64 they are
valid but lower to an empty spelling. This lets shared Windows API headers
parse and bind without forcing every modern Windows declaration to remove old
memory-model annotations.

The value in `[declare.callspec]` or `[declare.typespec]` is a requirement
expression. It means "this spec name exists in the Camp syntax universe, but
using it requires this condition." `true` means the spec name is always
available as source syntax. This shape intentionally keeps declaration and
availability together for ABI specs; callspecs and typespecs do not have
boolean selected values in the way configuration flags do.

Concrete targets still provide selected native spellings:

```ini
# gcc-linux-x64.ini
[configure]
OS_LINUX=true
SUBSYSTEM_POSIX=true
ARCH_X64=true

[callspec]
_cdecl=
_sysv=__attribute__((sysv_abi))
_msabi=__attribute__((ms_abi))
```

```ini
# msvc-windows-x86.ini
[configure]
OS_WIN32=true
ARCH_X86=true

[callspec]
_cdecl=__cdecl
_stdcall=__stdcall
_fastcall=__fastcall
_thiscall=__thiscall
_winapi=__stdcall

[typespec]
_near=
_far=
_huge=
```

```ini
# msvc-win16-x86.ini
[configure]
OS_WIN16=true
ARCH_X86=true

[callspec]
_cdecl=__cdecl
_stdcall=__stdcall
_fastcall=__fastcall
_pascal=__pascal
_winapi=__stdcall

[typespec]
_near=__near
_far=__far
_huge=__huge
```

Using a callspec or typespec imposes its declared requirement on the declaration
or type expression that uses it. The requirement may be satisfied by an
enclosing declaration requirement or by local flow facts at the point where the
type expression appears. For example:

```camp
@require(OS_WIN32)
extern _winapi int MessageBoxW(nint hwnd, wstring text, wstring caption, uint type);
```

is valid because `_winapi` requires `OS_WIN32`, and the declaration carries
that requirement.

Invalid:

```camp
extern _winapi int MessageBoxW(nint hwnd, wstring text, wstring caption, uint type);
```

The declaration is available everywhere, but `_winapi` is not.

Likewise:

```camp
@require(OS_WIN32)
const char* _near value;
```

is valid, while using `_near` in an unconditional declaration is invalid because
`_near` requires a DOS or Windows-family target.

The same rule applies inside method bodies:

```camp
void f()
{
	if (configured(OS_WIN16) && tryGetValue(out const char* _near someResult))
	{
		useNearResult(someResult);
	}
}
```

The `_near` typespec is valid in the `out` declaration because the left side of
the `&&` proves `OS_WIN16` while analyzing the right side. The variable is valid
inside the guarded block according to normal source scoping and flow rules.

The rule also applies to callspecs in type expressions:

```camp
fn* fnx = ...;
if (configured(OS_WIN32) && ((fn _stdcall bool(int value))fnx)(123))
{
	useWin32Callback();
}
```

The exact safety requirements for the cast are governed by the existing cast
semantics. The availability point is that `_stdcall` is a valid callspec in the
function type expression because `configured(OS_WIN32)` proves its requirement
before the cast expression is analyzed.

Selected native emission must also validate that every selected callspec and
typespec has a selected native spelling or selected target rule. It is valid for
a target to declare a spec in the base universe while not providing a native
spelling, as long as no selected declaration uses that spec for that target.

The test-only `test-abi-slot-compatible` target may continue to declare its
synthetic specs (`_small`, `_large`, `last`, `Item`, `callme`) in the target
itself. Those should not become standard base-target specs because they exist
only for ABI-slot tests:

```ini
[declare.typespec]
_small=true
_large=true
last=true
Item=true

[declare.callspec]
callme=true
```

The parser should be initialized from the declared callspec/typespec universe,
not merely the selected target's active `[callspec]` and `[typespec]` sections.
Diagnostics may use the leading underscore convention as a hint. For example,
if `_fsr` is not declared as a typespec, the diagnostic can say "`_fsr` is not a
known typespec." The parser must not assume every leading-underscore identifier
is a target spec.

### Proposed Target Shapes

The first migration of the current checked-in targets should look like this at
the source-visible flag level:

```ini
# base/c99.ini
[declare]
OS_DOS=false
OS_WIN16=false
OS_WIN32=false
OS_WIN64=false
OS_LINUX=false
OS_MACOSX=false
OS_WASI=false
RUNTIME_EMSCRIPTEN=false

SUBSYSTEM_POSIX=false
SUBSYSTEM_DARWIN=false

ARCH_X86=false
ARCH_X64=false
ARCH_ARM64=false
ARCH_WASM32=false

SUPPORTS_FILES=true
SUPPORTS_TIMERS=true
SUPPORTS_THREADS=true
SUPPORTS_NETWORK=true

UNICODE=false

# Reserved for future use.
# SUPPORTS_ENVIRONMENT=true
# SUPPORTS_PROCESSES=true
# SUPPORTS_TERMINAL=true

# Reserved for future use.
# COMPILER_GCC=false
# COMPILER_CLANG=false
# COMPILER_MSVC=false
```

```ini
# base/c99.ini, continued
[declare.callspec]
_cdecl=true
_stdcall=OS_WIN16 || OS_WIN32
_fastcall=OS_WIN16 || OS_WIN32
_thiscall=OS_WIN32
_pascal=OS_DOS || OS_WIN16
_winapi=OS_WIN16 || OS_WIN32
_sysv=SUBSYSTEM_POSIX && (ARCH_X86 || ARCH_X64)
_msabi=SUBSYSTEM_POSIX && ARCH_X64

[declare.typespec]
_near=OS_DOS || OS_WIN16 || OS_WIN32
_far=OS_DOS || OS_WIN16 || OS_WIN32
_huge=OS_DOS || OS_WIN16 || OS_WIN32

[callspec]
_cdecl=
```

```ini
# gcc-linux-x64.ini
[configure]
OS_LINUX=true
SUBSYSTEM_POSIX=true
ARCH_X64=true
```

```ini
# gcc-linux-x86.ini
[configure]
OS_LINUX=true
SUBSYSTEM_POSIX=true
ARCH_X86=true
```

```ini
# clang-macos-x64.ini
[configure]
OS_MACOSX=true
SUBSYSTEM_POSIX=true
SUBSYSTEM_DARWIN=true
ARCH_X64=true
```

```ini
# clang-macos-arm64.ini
[configure]
OS_MACOSX=true
SUBSYSTEM_POSIX=true
SUBSYSTEM_DARWIN=true
ARCH_ARM64=true
```

```ini
# msvc-windows-x64.ini
[configure]
OS_WIN32=true
OS_WIN64=true
ARCH_X64=true

[typespec]
_near=
_far=
_huge=

[variant]
charwidth=ansi unicode*

[configure:unicode]
UNICODE=true
```

```ini
# msvc-windows-x86.ini
[configure]
OS_WIN32=true
ARCH_X86=true

[typespec]
_near=
_far=
_huge=

[variant]
charwidth=ansi unicode*

[configure:unicode]
UNICODE=true
```

```ini
# msvc-win16-x86.ini
[configure]
OS_WIN16=true
ARCH_X86=true
```

```ini
# wasm32-wasi.ini
[configure]
OS_WASI=true
ARCH_WASM32=true
SUPPORTS_FILES=false
SUPPORTS_TIMERS=false
SUPPORTS_THREADS=false
```

```ini
# wasm32-emscripten.ini
[configure]
ARCH_WASM32=true
RUNTIME_EMSCRIPTEN=true
SUPPORTS_FILES=false
SUPPORTS_TIMERS=false
SUPPORTS_THREADS=false
```

Whether Emscripten should also configure `OS_WASI` is deliberately not assumed.
The checked-in target currently distinguishes `WASI` and `EMSCRIPTEN`, and the
new flags should preserve that distinction. If the standard library needs a
shared "web/wasm hosted environment" condition later, introduce it deliberately
rather than overloading `OS_WASI`.

## Capability Requirements

Standard library declarations should use requirements to describe real platform
capabilities.

Example file API requirement:

```camp
@require((OS_WIN32 || SUBSYSTEM_POSIX) && SUPPORTS_FILES);
```

This says the declarations in the file require:

- an implementation path using Win32 or POSIX APIs; and
- actual filesystem support.

If a program targets Emscripten/WASI and the target configures:

```ini
[configure]
SUPPORTS_FILES=false
```

then unguarded use of file APIs fails:

```camp
void f()
{
	FileSystem.deleteFile("out.txt"); // error when SUPPORTS_FILES is false
}
```

The user can guard locally:

```camp
void f()
{
	if (configured(SUPPORTS_FILES))
		FileSystem.deleteFile("out.txt");
}
```

or require support on the containing declaration:

```camp
@require(SUPPORTS_FILES)
void f()
{
	FileSystem.deleteFile("out.txt");
}
```

or require support for the whole module:

```text
--requires SUPPORTS_FILES
```

## Referenced Modules And Requirement Propagation

When module `A` references module `B`, the compiler loads:

- `B`'s declared flags;
- `B`'s module-level requirements;
- requirements on `B`'s exported declarations;
- selected values from the current final target/build, if any.

Then it validates:

1. The current compilation knows every flag used by `B`.
2. If `A` imports/links `B` as a whole, `A`'s selected/proven configuration
   satisfies `B`'s module-level requirement.
3. If `A` references a specific declaration from `B`, the source context in `A`
   satisfies that declaration's requirement.
4. If `A` is itself a reusable library under `--explicit-requires`, it must
   publish an equal or stronger module requirement or put the requirement on the
   relevant declarations.
5. If `A` is built under `--implicit-requires`, the compiler may satisfy the use
   from ambient true defaults and write discovered unspecified requirements into
   `A`'s API header/metadata.

Example:

```camp
public void save()
{
	FileSystem.writeFile("out.txt", "text");
}
```

If `FileSystem.writeFile` requires `SUPPORTS_FILES`, then under
`--explicit-requires` this public declaration must either carry:

```camp
@require(SUPPORTS_FILES)
public void save()
{
	FileSystem.writeFile("out.txt", "text");
}
```

or the module must publish:

```text
--requires SUPPORTS_FILES
```

Under `--implicit-requires`, the compiler may rely on an ambient true default,
but the discovered requirement becomes part of the generated API/metadata
contract.

## Replacing `@testonly`

`@testonly` should become either a compatibility spelling for or a lowering to:

```camp
@require(TEST_MODULE)
```

The current test-module participation rule remains:

- production builds exclude test declarations and test-only declarations;
- test-module builds include production and test-only declarations;
- production declarations cannot depend on test-only declarations.

`TEST_MODULE` remains compiler-owned. Manually declaring or defining it should
not simulate a real test build because test mode also controls harness
generation, manifest output, coverage runner behavior, and related compiler
workflow.

The proposal does not require removing `@test`. `@test` continues to mean "test
function and test-discovery candidate." It implies test-module availability for
the function it marks.

## Replacing `@notsupported`

`@notsupported` exists because a declaration may appear in metadata/API output
while being unavailable for a target. `@require` handles that more directly.

Current-style pattern:

```camp
#if NO_TIMERS
@notsupported("The current target does not support timers.")
#endif
export TimerHandle startTimer(nuint intervalMs);
```

Proposed pattern:

```camp
@require(SUPPORTS_TIMERS)
export TimerHandle startTimer(nuint intervalMs);
```

Metadata can preserve that `startTimer` requires `SUPPORTS_TIMERS`.
Documentation can present that requirement.

If a future feature needs a declaration to be visible but intentionally rejected
for reasons other than configuration requirements, that should be separate. It
should not keep `@notsupported` alive as the normal target-availability
mechanism.

## Generated Declarations

Generated declarations must inherit requirements from the source declaration or
source expression that caused them.

Examples:

- iterator state structs generated for a conditionally available iterator;
- iterator move/current helper methods;
- async frame structs and resumption helpers;
- generated lambda context types and invoke helpers;
- interface vtable declarations and generated adapter helpers;
- generated lifecycle helpers associated with conditionally available fields or
  methods;
- test harness declarations generated from `@test` declarations.

The compiler should avoid symbol-name heuristics. Requirements should be carried
through explicit generated-declaration provenance, the same general policy
already used for test-only ownership.

## Metadata, API Headers, And Emission

### Metadata

Metadata should preserve requirements for modules, declarations, fields,
methods, virtual slots, interface members, and generated declarations when the
metadata view is not selected-native-only.

Documentation generators can then report that a declaration requires
`OS_WIN32`, `SUPPORTS_FILES`, `TEST_MODULE`, or any other flag expression.

### Camp API Headers

Camp API headers should preserve:

- declared flags relevant to the API;
- module-level requirements;
- declaration requirements;
- file-level requirements;
- requirements discovered under `--implicit-requires`.

A consuming compiler should reject calls or type references unless the consuming
source context proves the imported declaration's requirement.

API headers must not emit inactive selected-target declarations as if they were
unconditional.

### Native C Emission

Selected native C emission should:

- emit only declarations whose effective requirements are true for the selected
  configuration;
- emit only selected fields;
- emit only selected virtual slots and vtable initializers;
- emit only selected generated declarations;
- constant-fold `configured(...)`;
- evaluate conditional aliases and metadata strings to selected values where C
  output requires a single selected spelling;
- reject any selected code path that references a declaration whose requirement
  is not satisfied.

This preserves selected native build behavior while allowing richer compiler
metadata before emission.

## Diagnostics

Diagnostics should use requirement and configuration-flag terminology, not
preprocessor terminology.

Suggested diagnostics:

```text
Unknown configuration flag 'OS_WNI32'. Declare the flag before using it in
@require(...) or configured(...).
```

```text
Configuration flag 'OS_WIN32' is not an ordinary source identifier. Use
configured(OS_WIN32) to query its value.
```

```text
Declaration 'deleteFile' requires SUPPORTS_FILES, but this context does not
prove SUPPORTS_FILES.
```

```text
Declaration 'registerClass' is available unconditionally, but parameter type
'WNDCLASS' requires OS_WIN32. Add @require(OS_WIN32) to the declaration or
avoid the conditional type in its signature.
```

```text
Referenced module 'Win32Forms' requires OS_WIN32, but the selected target does
not satisfy OS_WIN32.
```

```text
Public declaration 'save' calls 'FileSystem.writeFile', which requires
SUPPORTS_FILES. Add @require(SUPPORTS_FILES) to 'save' or add a module-level
--requires SUPPORTS_FILES.
```

In implicit mode, that case is not an error; the compiler adds the discovered
requirement to generated API metadata.

```text
Cannot configure unknown configuration flag 'SUPPORTS_FILE'. Declare the flag first
or check the spelling.
```

```text
Cannot configure target-owned configuration flag 'OS_WIN32'. Select a target that
configures this flag instead.
```

```text
@require is not valid on constructors. Put @require on the containing type
instead.
```

```text
@require is not valid on enum values. Put @require on the enum declaration
instead.
```

```text
Override 'draw' requires OS_WIN32, but the overridden slot is available
unconditionally. A concrete type must provide every available virtual slot in
every configuration where the type is available.
```

```text
#if is not supported in Camp source. Use @require on declarations and
configured(...) inside ordinary control-flow expressions.
```

Diagnostics for declaration-level `@require` in a file with file-level
`@require` should avoid implying automatic combination. If useful:

```text
This declaration has its own @require, which replaces the file-level @require.
Spell the full requirement if both are required.
```

## Documentation Impact

If implemented, update only living documentation. Do not update accepted,
rejected, or superseded historical proposals except for mechanical lifecycle
movement.

### Language Guide

The language guide should stay focused on everyday usage. It should not lead
with vtables, metadata, generated state, or portable IR details.

Recommended updates:

- `docs/language/03-declarations-and-program-shape.md`
  - Explain file metadata `@require(CONDITION);` as a file-level default for
    top-level declarations.
  - Explain declaration-level replacement of file metadata attributes.

- `docs/language/10-names-imports-visibility-and-symbols.md`
  - Explain that configuration flags are not ordinary source names.
  - Show `configured(OS_WIN32)`, not bare `OS_WIN32`.

- `docs/language/11-enums-newtypes-and-inline-constants.md`
  - Mention that enum values cannot be individually conditional.
  - Add a small conditional alias example if conditional aliases are implemented
    in the same work.

- `docs/language/12-interfaces-and-dynamic-dispatch.md`
  - Add user-facing examples for platform-specific interface members and
    platform-specific interface conformance.
  - Avoid detailed vtable mechanics.

- `docs/language/17-native-interop-and-abi-boundaries.md`
  - Replace conditional preprocessor examples with `@require` declarations and
    `if (configured(...))` wrapper bodies.
  - Replace `@notsupported` guidance with declaration requirements.
  - Show conditional `@symbol` for APIs such as `OpenFileW` / `OpenFileA`.
  - Explain that platform-specific callspecs and typespecs carry requirements
    and must be used only in declarations or contexts that prove those
    requirements.

- `docs/language/18-expressions-statements-and-operators-reference.md`
  - Add `configured(CONDITION)` as a compiler intrinsic expression.

- `docs/language/19-attributes-documentation-comments-and-metadata-hints.md`
  - Add `@require`.
  - Remove or deprecate `@notsupported`.
  - Replace `@testonly` guidance with `@require(TEST_MODULE)` or document
    compatibility sugar if retained.
  - Document file metadata replacement behavior.

### Semantics Documentation

The semantic docs should be complete and canonical.

Recommended updates:

- `docs/semantics/01-binding-analysis-and-lowering-pipeline.md`
  - Replace early conditional preprocessing with configuration flag declaration,
    requirement parsing, and requirement-aware binding.
  - Generalize declaration participation from test-only ownership to effective
    requirements.
  - Document generated-declaration requirement provenance.

- `docs/semantics/05-lifetime-analysis-and-flow-facts.md`
  - Add requirement flow facts from `configured(...)`.
  - Describe propagation through supported control-flow constructs.
  - Document requirement-aware exhaustiveness.

- `docs/semantics/08-async-resumption-lowering.md`
  - Document requirement propagation into async frames and generated resumption
    helpers.

- `docs/semantics/09-interface-vtables-and-dynamic-dispatch.md`
  - Document conditional interface members, conditional conformance,
    requirement-aware abstract completeness, conditional virtual slots, override
    requirements, and selected vtable emission.

- `docs/semantics/10-construction-destruction-and-allocation.md`
  - Document that `@require` is invalid on constructors and destructors, and
    lifecycle methods inherit containing type requirements.

- `docs/semantics/11-metadata-api-surface-and-symbols.md`
  - Replace `@notsupported` with requirement metadata.
  - Document conditional `@symbol` expressions.
  - Define API header preservation of declarations, module requirements, and
    implicit discovered requirements.

- `docs/semantics/12-target-capabilities-and-c-emission.md`
  - Document declared flags, configured values, target-owned flags, base target,
    selected native emission, and C output filtering.
  - Document target-declared callspecs and typespecs, their requirement
    expressions, parser visibility, and selected native spelling validation.

- `docs/semantics/13-diagnostics-source-ranges-and-error-quality.md`
  - Add diagnostics for unknown flags, bare flag names, invalid `@require`
    placement, unavailable declarations, invalid `--configure`, and requirement
    policy failures.

- `docs/semantics/14-core-expression-statement-and-access-semantics.md`
  - Add `configured(...)` expression binding/evaluation and requirement-sensitive
    access checks.

### Compiler Tooling Documentation

Recommended updates:

- `docs/compiler/01-campc-command-line.md`
  - Document `--declare`, `--configure`, `--requires`, `--explicit-requires`, and
    `--implicit-requires`.
  - Explain target-owned flags and `TEST_MODULE`.

- `docs/compiler/02-build-files-and-pragmas.md`
  - Remove conditional preprocessor guidance.
  - Keep `#build` and `#within` separate.
  - Document how build files pass declaration/configuration/requirement policy.

- `docs/compiler/04-targets-and-native-builds.md`
  - Explain base target, standard flags, target-owned configurations, and selected
    native views.
  - Document `[declare.callspec]` and `[declare.typespec]`, including the
    migration from current target symbols to standard source-visible flags.

- `docs/compiler/06-metadata-json.md`
  - Add requirement metadata fields for modules, declarations, fields, virtual
    slots, interface members, and generated declarations.

- `docs/compiler/07-dumps-diagnostics-and-introspection.md`
  - Include requirements in relevant dumps.

- `docs/compiler/08-language-server-and-editor-tooling.md`
  - Explain that LSP can reason over the full requirement-preserving declaration
    graph.

- `docs/camp-llm-coding-guide.md`
  - Replace old conditional examples with `@require` and `configured(...)`.
  - Emphasize that configuration flags are not ordinary identifiers.
  - Explain file metadata replacement behavior.

## Test Surface

Testing should update existing conditional-preprocessor tests and add focused
coverage for requirement semantics. Dense tests are preferred where they cover
related cases without unnecessarily increasing suite time.

### Existing Test Updates

Update tests that currently assume:

- inactive conditional branches are stripped before parsing;
- bare preprocessor symbols are valid in method bodies;
- source-local preprocessor symbol mutation directives can introduce or remove
  source-local symbols;
- `@notsupported` is the target-availability mechanism;
- `@testonly` is the only test-only helper spelling;
- production metadata lacks declarations from inactive targets.

Where a test is mostly about selected native output, convert it to:

- `@require(...)` on declarations or fields;
- `if (configured(...))` in method bodies;
- selected C output assertions that unavailable declarations are not emitted.

### Configuration Flag Tests

Cover:

- declared flags accepted in `@require`, `--requires`, and `configured`;
- unknown flags diagnosed;
- bare flag names rejected in ordinary source expressions;
- flags do not collide with ordinary variables/types/aliases of the same name;
- duplicate flag declarations across packages rejected;
- flags declared by the base target, concrete target, or target variant
  rejected by `--configure`;
- flags declared with `--declare` by the current module accepted by
  `--configure`;
- flags declared with `--declare` by referenced modules, API headers, or
  portable metadata accepted by `--configure`;
- a second configuration of the same command-line-configurable flag rejected.

### Target, Variant, Callspec, And Typespec Tests

Cover:

- migration of existing target symbols to standard source-visible flags;
- Windows-family flags matching `_WIN16`, `_WIN32`, and `_WIN64` conventions:
  `OS_WIN32` true for flat-memory Windows, including 64-bit Windows,
  `OS_WIN64` true only for 64-bit Windows, and `OS_WIN16` true only for
  segmented 16-bit Windows;
- Windows `charwidth` variant configuring `UNICODE` for Camp source while still
  providing both `UNICODE` and `_UNICODE` for native C where appropriate;
- variants not automatically becoming configuration flags;
- parser recognizing declared callspecs/typespecs even when the selected target
  does not provide an active native spelling;
- callspec/typespec use imposing the declared requirement on the containing
  declaration or type expression;
- selected native emission rejecting a selected use of a spec with no selected
  native spelling;
- selected native emission ignoring unavailable declarations that contain
  unsupported platform-specific specs;
- local flow facts allowing guarded use of callspecs and typespecs in method
  body type expressions, including `out` declarations and function type casts;
- `_near`, `_far`, and `_huge` lowering to native spellings on Win16 and empty
  spellings on Win32/Win64;
- diagnostics for unknown likely-spec names such as mistyped leading-underscore
  typespecs;
- test-only ABI specs remaining local to the test target rather than becoming
  standard base-target specs.

### `configured(...)` Tests

Cover:

- constant folding in selected native output;
- use in `if`, `while`, conditional expressions, and inline constants;
- `&&`, `||`, `^`, `!`, and parentheses;
- flow facts allowing calls in guarded regions;
- left-to-right `&&` flow facts proving requirements in the right operand;
- disjunctive guarded loop conditions not proving either branch-specific
  requirement in the loop body;
- failed access outside guarded regions;
- `else if (configured(...))` proving requirements where plain `else` does not.

### `@require` Declaration Tests

Cover:

- top-level functions/types/aliases/variables;
- methods/static methods/fields/inline constants;
- member containment under a required type;
- out-of-scope methods requiring their own `@require` when receiver type is
  conditional;
- parameter/return/generic constraint references to required declarations;
- invalid `@require` placement on constructors, destructors, parameters,
  generic parameters, base-class entries, and enum values.

### File Metadata Tests

Cover:

- standalone `@require(SUBSYSTEM_POSIX);` applying to top-level declarations;
- declaration-level `@require(SUBSYSTEM_POSIX && SUPPORTS_TIMERS)` replacing,
  not combining with, the file-level value;
- file-level metadata not applying directly to nested members;
- member-level requirements still combining with containing type requirements;
- diagnostics or dumps making replacement behavior clear.

### Module Requirement And Policy Tests

Cover:

- `--requires` emitted into API headers/metadata;
- consuming modules satisfying module requirements;
- consuming modules rejected when selected target does not satisfy module
  requirements;
- `--explicit-requires` rejecting public library declarations that use required
  APIs without carrying/publishing requirements;
- `--implicit-requires` accepting the same shape and emitting discovered API
  requirements;
- executables defaulting to implicit mode;
- libraries and library test/cover builds defaulting to explicit mode.

### Requirement-Aware Exhaustiveness Tests

Cover:

- `@require(A || B)` with guarded `return` branches for `A` and `B` satisfying
  all-paths-return;
- plain `else` not proving `B` for a call requiring `B`;
- equivalent terminating branches using `throw`, `return`, or other existing
  no-fallthrough constructs;
- negative cases where the handled branches do not cover the declaration
  requirement.

### Interface, Abstract, And Virtual Tests

Cover:

- conditionally available interface declarations implemented by broader types;
- conditional interface members and required ascribed implementations;
- method signatures requiring conditional types;
- abstract completeness under conditional members;
- virtual slot requirements;
- override requirements at least as broad as the slot;
- base calls requiring requirement proof;
- selected vtable emission with selected slots only.

### Field And Generated-State Tests

Cover:

- conditional struct/class fields affecting selected layout;
- selected C output omitting unavailable fields;
- iterator generated state inheriting requirements;
- async frame generated state inheriting requirements;
- generated lambda/helper declarations inheriting source requirements.

### Conditional Constant And Attribute Tests

Cover:

- alias selection between types;
- alias selection between callables, if supported;
- mixed alias target kinds rejected;
- conditional `@symbol` evaluated for selected native output;
- conditional `@symbol` preserved in metadata/API output where required;
- conditional `@symbol` expressions rejecting non-string branches or
  unsupported expression forms;
- conditional string syntax in other string-valued attributes parsed but
  rejected by semantic validation.

### Metadata And API Header Tests

Cover:

- metadata preserving module and declaration requirements;
- API header emission preserving `@require` conditions;
- consuming an API header with required declarations;
- selected production metadata excluding test-module-only declarations;
- test-module metadata including `@require(TEST_MODULE)` declarations;
- implicit discovered requirements serialized into API metadata.

### Native Emission Tests

Cover:

- unavailable declarations omitted from selected C;
- unavailable fields omitted from selected C structs;
- unavailable vtable slots omitted from selected vtables;
- selected conditional aliases and symbols emitted correctly;
- no unresolved references remain after selected requirement filtering.

### Diagnostics Tests

Cover diagnostic quality for:

- unknown configuration flags;
- bare configuration flag names;
- invalid `@require` placement;
- unavailable declaration calls;
- signatures referencing unavailable types;
- invalid user `--configure` of flags declared by the base target, concrete
  target, or target variant;
- valid user `--configure` of flags declared by referenced modules, API
  headers, or portable metadata;
- explicit-requires failures;
- override requirement narrower than the overridden slot;
- missing conditional interface implementation;
- plain `else` branch that does not prove the alternate requirement.

## Implementation Notes

This proposal should be implemented as a semantic feature, not as another
preprocessor.

Suggested broad compiler changes:

1. Create a compiler-owned configuration flag namespace.
2. Load the base target before source analysis and merge concrete target,
   profile, project/build, referenced module, and command-line flag
   declarations.
3. Track each declared flag's ambient value and any selected configured value,
   including whether the selected value came from a target or target variant.
4. Validate `--configure` only against flags declared with `--declare` and with
   no prior configured value.
5. Add target parsing/loading support for `[declare.callspec]` and
   `[declare.typespec]` requirement expressions.
6. Initialize the parser from the declared callspec/typespec universe, not only
   the selected target's native spellings.
7. Validate callspec/typespec usage against the requirements declared by the
   target.
8. Remove conditional source filtering from the parser path.
9. Parse `@require` attributes and standalone file metadata.
10. Parse and bind `configured(CONDITION)` as a compiler intrinsic.
11. Add effective requirements to modules, declarations, fields, interface
   members, virtual slots, generated declarations, and relevant metadata nodes.
12. Extend lookup/access validation to check requirements as well as visibility.
13. Extend flow analysis to propagate `configured(...)` facts and initial
    declaration/module requirements.
14. Add first-version requirement-aware exhaustiveness for simple disjunction
    coverage.
15. Update declaration validation for signatures, constraints, base classes,
    interface conformance, abstract completeness, overrides, and invalid
    placement.
16. Implement explicit/implicit requirement policy and discovered requirement
    serialization.
17. Update lowering and selected native emission to filter unavailable
    declarations, validate selected callspec/typespec native spellings, and
    evaluate selected conditional constants.
18. Update metadata and API emission to preserve requirements.
19. Migrate current test-only and not-supported logic onto requirements where
    appropriate.
20. Migrate current target files from legacy source-visible symbols to the
    standard flag names described above.

## Resolved Design Details

The following details are part of this proposal rather than deferred open
questions:

- `--declare`, `--configure`, `--requires`, `--explicit-requires`, and
  `--implicit-requires` are available through both `#build` and `.campbuild`.
- Metadata should use a simple explicit representation for declared flags,
  declared ABI specs, module requirements, declaration requirements, and
  discovered implicit requirements. The implementation plan may choose exact
  field names, but the data must be complete enough for API documentation and
  downstream consumers.
- Camp API headers should preserve source-level requirement spelling:
  `@require(...)`, guarded `alias` lists, and conditional `@symbol` expressions.
- `RUNTIME_EMSCRIPTEN` is the Emscripten runtime flag.
- `SUPPORTS_ENVIRONMENT`, `SUPPORTS_PROCESSES`, and `SUPPORTS_TERMINAL` are
  reserved in the base target as commented-out flags until the standard library
  needs them.
- `COMPILER_GCC`, `COMPILER_CLANG`, and `COMPILER_MSVC` are reserved in the
  base target as commented-out flags, but compiler-name facts remain target
  capabilities unless source-level ABI branching becomes necessary.
- The first implementation should support requirement-aware exhaustiveness for
  simple disjunction coverage and guarded terminating branches; broader boolean
  reasoning can improve later.
- Conditional string constant syntax is parsed for string-valued attributes,
  but only `@symbol` may compile with that syntax in the first version.

## Readiness Criteria

This proposal is ready to move to pending when reviewers agree on:

- using `@require` as the declaration/file requirement attribute;
- using "configuration flag" terminology;
- requiring `configured(...)` for all expression-level configuration checks;
- treating configuration flags as compiler-owned names outside ordinary source
  lookup;
- removing source-level conditional directives;
- removing source-local preprocessor symbol mutation directives;
- replacing `@notsupported` with requirements;
- file-level `@require` replacement behavior;
- no duplicate declaration identities based on non-overlapping requirements;
- allowing conditional interface conformance for portable types;
- disallowing `@require` on constructors, destructors, parameters, generic
  parameters, base-class entries, and enum values;
- introducing `--declare`, `--configure`, `--requires`, `--explicit-requires`, and
  `--implicit-requires`;
- forbidding `--configure` from configuring target-owned flags while allowing
  it for current-module and dependency flags declared with `--declare`;
- introducing a base target and standard configuration flags;
- migrating existing target symbols to `OS_*`, `SUBSYSTEM_*`, `ARCH_*`,
  `SUPPORTS_*`, and `UNICODE`;
- keeping variants separate from configuration flags while allowing variants to
  configure source-visible flags deliberately;
- declaring callspecs and typespecs in the target universe with requirement
  expressions;
- initializing parsing from declared specs rather than selected native specs;
- serializing discovered implicit requirements into API/metadata output;
- the documentation and test areas listed above.
