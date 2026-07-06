# Camp LLM Code Guide

This guide describes Camp source accepted by the current compiler implementation. Do not infer missing features from C, C++, C#, or similar syntax.

This guide is based on the following source documents. Evidence citations use these aliases:

| Alias | Source |
|---|---|
| `spec` | `docs/camp_unified_spec_v24.md` |
| `scheduler_design` | `docs/camp_async_scheduler_design_v7.md` |

Confidence labels:

| Label | Meaning |
|---|---|
| `CONFIRMED_BY_TEST` | Passing or failing checked-in test demonstrates the rule. |
| `CONFIRMED_BY_COMPILER_CODE` | Parser/binder/analyzer/lowerer explicitly implements or rejects the rule. |
| `INFERRED_FROM_IMPLEMENTATION` | Rule follows from implementation behavior but is not directly tested. |
| `SPEC_ONLY_OR_UNVERIFIED` | Mentioned in docs/spec only; avoid generating unless also confirmed elsewhere. |

## 1. Core Principles

| Rule | Confidence | Evidence |
|---|---|---|
| Do not generate ordinary overloads. Same invoker names are allowed only as explicit `overload` selector families; mixing ordinary and overload declarations is rejected. | `CONFIRMED_BY_TEST` | `tests/CCompile/overload_basic.camp`; `tests/Diagnostics/overload_invalid.expected.txt`; `src/Camp.Compiler/BindableNodeAnalyzer.Overloads.cs::ValidateOverloadFamily` |
| Use explicit pointer syntax: `T*`, `void*`, `const T*`. Member access uses `.` for values and pointers; no `->` token exists. | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `tests/CCompile/array_this_component_access.camp`; `src/Camp.Compiler/CampTokenizer.cs::Punctuation`; `src/Camp.Compiler/CampParser.cs::TryParsePostfixPart` |
| Use `struct` for value-like aggregate storage and `class` for pointer-allocated object patterns. `struct` methods cannot be virtual/override/sealed/abstract. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeStructImplementations`; `tests/CCompile/struct_interface_indirect.camp` |
| Use `fixed struct` only for structs; `fixed class` is invalid. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyStructDeclarators`; `ApplyClassDeclarators` |
| Classes may be `virtual`, `abstract`, or `sealed`. Virtual methods require virtual/abstract classes; abstract methods require abstract classes. | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateClassVirtualMethods` |
| `extern class` is a foreign opaque escaped pointer type. Do not declare instance fields, virtual/abstract/sealed modifiers, non-extern constructors/destructors, by-value storage, or arrays of direct extern-class values. Static fields and non-extern helper methods are allowed. An extern class may list imported interface contracts; Camp imports the generated interface accessor surface rather than generating fields. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/extern_class_invalid.camp`; `tests/CCompile/extern_class_opaque_helpers.camp`; `tests/CCompile/extern_class_inheritance_lifecycle.camp`; `tests/CCompile/project_reference_consumes_exported_interface_accessors_and_vtables.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateExternClass` |
| Arrays, optionals, delegates, and iterators are compiler-expanded forms. They have dot components such as `.elements`, `.length`, `.value`, `.specified`, `.call`, `.context`. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `tests/CCompile/expanded_return_iter_assignment.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| `T[]` is a span-style expanded array value. `fixed T[n]` declarations create fixed-size inline array storage. Fixed-size arrays expose `.elements` and constant `.length`, can view-convert to `T[]`, and are not copyable values. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_span_views.camp`; `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8`, `1.6.9` |
| `inline` declarations are typed compile-time constants, not storage. Use them for scalar, enum, pointer-null, fn-null, and string-like constants that should emit as typed C macros. Do not take their address or assign to them. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/inline_constants_syntax_invalid.camp`; `tests/CCompile/inline_constants_fixed_enums.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.InlineConstants.cs`; `spec::5.1.8` |
| Camp enums have a fixed integer representation. Omitted enum underlying type means `uint`; generated C uses an underlying typedef plus typed member macros, not native C `enum`. | `CONFIRMED_BY_TEST` | `tests/CCompile/enum_value_c_symbols.camp`; `tests/CCompile/inline_constants_fixed_enums.camp`; `src/Camp.Compiler/CCodeEmitter.cs::WriteEnumDefinition`; `spec::1.10` |
| `T: any` is the erased non-copying generic constraint. `T: copyable` is required when generic code copies, assigns, stores, moves, returns, or otherwise transports `T` values by value; erased copying also needs `sizeof(T)` when the lowering requires a byte size. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::1.12.11`, `6.1.6`, `6.2.6` |
| `typenameof(T)` is a compile-time type-name capability for type operands only. It is not expression reflection. Generic use requires a `typenameof(T)` capability parameter. | `CONFIRMED_BY_TEST` | `tests/StdRun/typenameof_runtime.camp`; `tests/Diagnostics/typenameof_invalid*.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.NameOf.cs`; `spec::3.2.6` |
| `classtype` is valid only inside class declarations. Use it in method signatures and locals when a class-relative type should rebind at the call site; do not use it in fields, globals, callable newtypes, structs, interfaces, or enum declarations. `typenameof(classtype)` is valid only as a default parameter value. | `CONFIRMED_BY_TEST` | `tests/StdRun/classtype_runtime.camp`; `tests/Diagnostics/classtype_invalid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ClassType.cs`; `spec::2.2.5` |
| Plain `this` may be used as a receiver-preserving return type for instance or extension receiver methods. It is not a general type form: do not generate `this*`, `this[]`, callable slots returning `this`, fields, globals, or parameters. A method returning `this` should return `this` or a chain of `this`-returning calls on `this`. | `CONFIRMED_BY_TEST` | `tests/StdRun/this_return_runtime.camp`; `tests/Diagnostics/this_return_invalid.camp`; `tests/Diagnostics/this_return_body_invalid.camp`; `spec::1.4.3` |
| Namespace names should use PascalCase: `using Std;`, `using Std::Math;`, `export as Std;`, `export as Std::Math;`. Avoid lowercase namespace segments in generated Camp examples. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::5.1` |
| Callable `newtype` ascription gives a function or method's natural callable reference form a named callable contract without changing direct calls, overloads, unbound references, generated symbols, callable lowering, or ABI representation. Receiverless declarations ascribe `fn`; receiver-bearing methods ascribe context-carrying `delegate` or `iter`. Explicit callable `this` qualifiers are part of that contract. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/callable_ascription_invalid.camp`; `tests/CCompile/callable_this_ascription*.camp`; `spec::1.4.16`, `1.11`, `3.4.4` |
| Conversion intent is explicit. Use ordinary casts for nominal/value boundaries, `(unsafe T)value` only for direct casts that break a protected contract, raw fences such as `void*`, `fn*`, or `untyped` when erasing type family information, and reconstruction for arrays, delegates, optionals, or generic constructed values whose inner type shape changes. | `CONFIRMED_BY_TEST` | `tests/CCompile/conversion_policy_stage*.camp`; `tests/Diagnostics/conversion_*invalid*.camp`; `tests/CCompile/conversion_multivalue_stage5.camp`; `spec::1.12` |
| Use `struct(T)` to materialize an expanded form when it must be a single stored value, especially arrays of expanded values. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Construction/destruction is explicit: `init T(...)`, `new T(...)`, `delete valueOrPointer`, and `finally delete expr`. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `tests/StdRun/pointer_new_array_finally_delete.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Operators.cs` |
| Error handling is explicit: thrown values use `thrown T` parameters, `throw value`, `try`/`catch`, and call-site `catch variable`. | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp`; `tests/Lowering/throw_try_finally.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs` |
| Camp is C ABI-oriented. `extern`, `export`, `public`, `@symbol`, generated C symbols, `void*`, `sizeof`, and flattened method names matter. | `CONFIRMED_BY_TEST` | `tests/CEmit/*.camp`; `tests/Api/*.camp`; `src/Camp.Compiler/CCodeEmitter.cs`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs` |

## 2. Lexical and Formatting Rules

| Form | Accepted | Confidence | Evidence |
|---|---|---|---|
| Identifier | ASCII letter or `_`, followed by ASCII letters, digits, or `_`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::IsIdentifierStart`, `IsIdentifierPart` |
| Attribute identifier | `@` followed by identifier start and identifier parts, e.g. `@range`, `@symbol("c_name")`. | `CONFIRMED_BY_TEST` | `tests/CCompile/slice_range_calls.camp`; `src/Camp.Compiler/CampTokenizer.cs::Tokenize` |
| Reserved words | Cannot be used as ordinary identifiers; includes `_`, `abstract`, `alias`, `any`, `astring`, `async`, `auto`, `await`, `bool`, `class`, `classtype`, `constof`, `copyable`, `delegate`, `escaped`, `extern`, `fixed`, `fn`, `foreach`, `implements`, `init`, `inline`, `interface`, `iter`, `newtype`, `once`, `overload`, `params`, `postpone`, `scoped`, `thrown`, `typenameof`, `unsafe`, `unscoped`, `vtableof`, `within`, etc. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.cs::ReservedWords`; `src/Camp.Compiler/CampParser.cs::IsKeyword`; `spec::6.1.6` |
| Line comment | `//` to end of line. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Tokenize` |
| Block comment | `/* ... */`, tokenized line by line; can span lines. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadBlockCommentLine` |
| Strings | `"..."`, `'...'`, or `` `...` `` are tokenized as string-class literals; backslash escapes consume next char. Single-quoted string with exactly one decoded char is a character literal. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadString`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildLiteralExpression` |
| Invalid char literal | `'ab'` is rejected as a character literal because character literals must contain exactly one character. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildLiteralExpression` |
| Numbers | Decimal integers/floats; `0x`/`0X` hex accepts ASCII letters/digits; decimal literals may end with ASCII letter suffixes. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadNumber` |
| Punctuation/operators | Single-character symbols include `~ ! % ^ & * ( ) + - = { } [ ] | ; : , . / < > ? $ #`. Multi-character operators are parsed from token sequences. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Punctuation`; `src/Camp.Compiler/CampParser.cs::ReadOperator` |
| Whitespace/newline | Horizontal whitespace and newlines are separate trivia tokens. Statements and declarations still normally require `;` or braces. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Tokenize`; `src/Camp.Compiler/CampParser.cs` |
| Preprocessor directives | `#define`, `#undef`, `#if`, `#elif`, `#else`, `#endif`, and prelude-only `#build`/`#within` are recognized. `#build` examples may show compiler option fragments, but those option names are tooling behavior, not language syntax. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs`; `spec::5.1.12`; `docs/camp_declarations_statements_grammar.txt::preprocessor-directive` |

Valid:

```camp
// line comment
/* block
   comment */
int value_1 = 0x10;
char c = 'x';
const char[] text = "hello";
```

Build-prelude example:

```camp
// Comments may appear before build directives.
#build --target clang-macos-x64
#build --artifact shared
#within explicit

export as MyLibrary;
```

Treat specific `#build` option names as examples of the current `campc`
tooling. Do not describe them as normative language grammar. `#within explicit`
or `#within implicit` is a file-local compiler policy directive for source-level
allocation checks.

Invalid:

```camp
char bad = 'xy';     // character literal must contain exactly one character
int class = 1;       // reserved word
```

## 3. Declarations

### Declaration Overview

| Declaration | Syntax | Confidence | Evidence |
|---|---|---|---|
| Function | `[export|public|extern|async] ReturnType name<T...>(params) [: CallableNewtype] bodyOr;` | `CONFIRMED_BY_TEST` for ordinary functions; callable ascription is `SPEC_ONLY_OR_UNVERIFIED` | `tests/CEmit/basic_functions.camp`; `src/Camp.Compiler/CampParser.cs::ParseMemberDeclaration`; `spec::1.4.16` |
| Expression-bodied function | `ReturnType name(params) [: CallableNewtype] => expr;` | `CONFIRMED_BY_TEST` for ordinary expression bodies; callable ascription is `SPEC_ONLY_OR_UNVERIFIED` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildFunctionBody`; `spec::1.4.16` |
| Method | Declared inside `struct`, `class`, `enum`, `newtype`, or `params` body. May use `: CallableNewtype` after the parameter list when the declaration family matches. If the callable newtype declares explicit callable `this`, omitted method `this` inherits those qualifiers. | `CONFIRMED_BY_COMPILER_CODE` for ordinary methods; callable ascription is `SPEC_ONLY_OR_UNVERIFIED` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildStructDefinition`, `BuildClassDefinition`, `AddMethodOnlyScope`; `spec::1.4.16` |
| Static method | `static ReturnType name(params) [: CallableNewtype] { ... }` inside type only; static methods are receiverless for callable ascription. | `CONFIRMED_BY_TEST` for ordinary static methods; callable ascription is `SPEC_ONLY_OR_UNVERIFIED` | `tests/CCompile/overload_basic.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFunctionDeclarators`; `spec::1.4.16` |
| Global variable | `[export|public|extern] Type name [= expr];`; fixed-size array storage uses `fixed T[n] name [= initializer];`; inline constants use `[export|public] inline Type NAME = constant;` and emit no storage. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_storage.camp`; `tests/CCompile/inline_constants_fixed_enums.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildVariableDefinition`; `spec::1.6.8`, `5.1.8` |
| Field | `Type name;` or `static Type name;` inside `struct`/`class`; fixed-size array fields use `fixed T[n] name;`; type-scoped constants use `inline Type NAME = constant;`; `escaped` is valid on fields to require escaped pointer-bearing assignments. `export`, `public`, and `extern` are valid only on explicit `static` fields, while `inline` is normalized to static constant behavior. Instance fields cannot use those visibility/import modifiers and cannot be `virtual`, `override`, `sealed`, `abstract`, or `async`. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_storage.camp`; `tests/CCompile/inline_constants_fixed_enums.camp`; `tests/CCompile/lifetime_escaped_fields.camp`; `tests/Api/static_type_fields.camp`; `tests/CCompile/extern_class_opaque_helpers.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFieldDeclarators`; `spec::1.6.8`, `2.2.1`, `4.1`, `5.1.8` |
| Struct | `[export|public|extern|fixed] struct Name[: bases] { ... }` | `CONFIRMED_BY_TEST` | `tests/Ast/basic_struct.camp`; `tests/CCompile/struct_interface_indirect.camp` |
| Class | `[export|public|extern|virtual|abstract|sealed|escaped] class Name[: baseOrInterfaces] { ... }`; if `extern`, it may inherit only from another `extern class`, may import interface contracts, and may not be virtual/abstract/sealed. | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `tests/Diagnostics/extern_class_invalid.camp`; `tests/CCompile/project_reference_consumes_exported_interface_accessors_and_vtables.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyClassDeclarators`; `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeClassOrStructBaseTypes` |
| Interface | `[export|public|extern] interface Name[: interfaces] { methodSigs; }`. Ordinary interface methods may be required, optional with `= null`/`= default`, or defaulted with `= functionName`; optional/defaulted slots may be omitted by implementers. | `CONFIRMED_BY_TEST` | `tests/CCompile/struct_interface_indirect.camp`; `tests/CCompile/overload_interface.camp`; `tests/CCompile/default_interface_methods.camp` |
| Enum | `[export|public|extern] enum Name[: underlyingType] { A = 0, B }`; omitted underlying type means `uint`; C output is a typedef plus typed value macros. `@symbol` is valid on enum types and members. | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp`; `tests/CCompile/enum_value_c_symbols.camp`; `tests/CCompile/inline_constants_fixed_enums.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildEnumDefinition`; `src/Camp.Compiler/CCodeEmitter.cs::WriteEnumDefinition` |
| Newtype value | `newtype Name: numericOrPointerType;` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildNewtypeDefinition`, `IsValidValueNewtypeUnderlying` |
| Newtype callable | `newtype fn Ret Name(params);`, `newtype delegate Ret Name([qualifiers this,] params);`, or `newtype iter T Name([qualifiers this]);` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_return_iter_assignment.camp`; `tests/CCompile/callable_this_*.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildNewtypeDefinition`; `spec::1.11` |
| Alias | `[export|public] alias Name = Qualified::Target;` | `CONFIRMED_BY_TEST` | `tests/Api/alias_api.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::AddAliasDeclaration` |
| Using | `using Namespace;`, `using Namespace as Alias;`, `using Namespace { A, B };` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseUsingImportExportDeclaration`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildUsingDeclaration` |
| Export namespace | `export as Namespace;` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseExportImportExportDeclaration`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildImportExportDeclaration` |

Modifier order is parser-permissive for repeated declarator keywords before the declaration keyword, but generation should use stable order:

```camp
[export|public] extern virtual class Name { }
[export|public] extern fixed struct Name { }
[export|public] extern static int method() { return 0; }
```

Never combine `export` and `public`; the binder rejects multiple visibility declarators.

Valid:

```camp
export class Resource
{
	export Resource() {}
	export ~Resource() {}
}

virtual class Base
{
	virtual int value() { return 1; }
}

sealed class Derived: Base
{
	override int value() { return base.value() + 1; }
}
```

Invalid:

```camp
public export int f() { return 0; } // cannot combine visibilities
static int f() { return 0; }        // static is not valid on global method
struct S { virtual void f() {} }    // struct methods may not be virtual
interface I { int x; }              // interface cannot contain fields
```

### Constructors And Destructors

| Rule | Confidence | Evidence |
|---|---|---|
| Constructor name must match containing type and has no return type. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ValidateLifecycleMember` |
| Destructor syntax is `~TypeName(...)`; destructor has no return type. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ValidateLifecycleMember` |
| Destructor may declare at most one optional `within` parameter. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ValidateDestructorParameters` |
| Interface constructors/destructors must declare a `within` parameter. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ValidateInterfaceConstructorParameters`, `ValidateDestructorParameters` |

### Callable Newtype Ascription

A function-like declaration may place one callable `newtype` name after the parameter list. The ascription names the declaration's natural callable reference form; it does not change direct calls, overload resolution, generated symbols, ABI lowering, default-argument insertion, or bound/unbound reference classification.

Rules:

- Receiverless declarations, including free functions and static methods, may ascribe only callable `newtype fn`.
- Receiver-bearing declarations may ascribe only accepted context-carrying callable newtypes, currently `delegate` or `iter`.
- The target must be a named callable `newtype`, not an anonymous callable type.
- A concrete declaration may have at most one callable ascription.
- For receiver-bearing methods, the ascription applies to the bound reference form such as `date.format`; `Date.format` and `Date_format` remain ordinary anonymous `fn` references.
- If the ascribed callable newtype declares an explicit callable `this` parameter, those qualifiers are part of the callable contract.
- If an ascribed instance method omits explicit `this`, it inherits the callable newtype's explicit callable `this` qualifiers for receiver checking and body analysis.
- If both the ascribed callable newtype and the method declare explicit `this`, their normalized qualifier sets must match.
- In an `escaped class`, an ascribed instance method must make `escaped this` explicit either on the callable newtype or on the method declaration.

Valid:

```camp
newtype fn bool IntParser(const char[] text, out int value);

bool tryParseInt(const char[] text, out int value) : IntParser
{
	return false;
}
```

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	int year;

	nuint format(char[] buffer = default) : CharFormatter
	{
		return (nuint)this.year;
	}
}
```

Invalid:

```camp
newtype delegate bool Parser(const char[] text, out int value);
bool tryParseInt(const char[] text, out int value) : Parser { return false; } // receiverless cannot ascribe delegate
```

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

struct Date
{
	nuint format(escaped this, char[] buffer = default) : CharFormatter { return 0; } // explicit this mismatch
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/Diagnostics/callable_ascription_invalid.camp`; `tests/CCompile/callable_this_ascription*.camp`; `spec::1.4.16`.

### Lambdas And Escaped Delegates

Lambdas are target-typed callable expressions. Use them only where a target
callable type is known from an assignment, parameter, return, cast, or callable
newtype ascription. A lambda argument cannot select an overload family by
itself; call the typed overload entry explicitly.

Rules:

- A non-capturing lambda can target `fn` or `delegate`.
- A capturing lambda must target `delegate`.
- A scoped capturing delegate stores pointers to declaration-scope values.
- An escaped capturing delegate allocates context storage, copies captured
  values into it, and treats those captured values as read-only inside the
  lambda body.
- Escaped lambdas may capture pointer-bearing values only when those values are
  proven escaped. Non-escaped `this` cannot be captured by an escaped lambda.
- An escaped delegate context is an ordinary pointer. If the expression creates
  a delegate context that the caller owns, delete `del.context` when done.
- `within (allocator)` affects escaped lambda context allocation in the same
  way it affects `new`.
- Nested scoped lambdas inside escaped lambdas are supported; the scoped nested
  context may point at the surrounding escaped lambda context while the outer
  lambda invocation is active.
- Target-typed lambda parameters inherit `constof(anchor)` from the target
  callable. Explicit lambda parameter types are checked with the ordinary
  callable signature variance rules. `constof` anchors inside a lambda name the
  lambda's own parameters or explicit callable `this`, not enclosing function
  parameters or captured locals.

Valid:

```camp
escaped delegate int() makeCounter(int start)
{
	return () => start;
}

int use()
{
	auto counter = makeCounter(7);
	int value = counter();
	delete counter.context;
	return value;
}
```

```camp
int apply(escaped delegate int(int) callback, int value)
{
	return callback(value);
}

int run(int seed)
{
	return apply(value =>
	{
		int doubled = value * 2;
		return doubled + seed;
	}, 3);
}
```

Invalid:

```camp
fn int() bad()
{
	int value = 1;
	return () => value; // capturing lambdas require delegate targets
}

void choose(overload escaped delegate bool(int) predicate) {}
void choose(overload fn bool(int) predicate) {}

void call()
{
	choose(value => true); // lambda cannot select an overload family
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CCompile/lambda_escaped_delegate_invocation.camp`; `tests/CCompile/lambda_nested_scoped_escaped.camp`; `tests/Diagnostics/lambda_escaped_capture_invalid.camp`; `tests/Diagnostics/lambda_escaped_overload_invalid.camp`.

### Namespace Naming Convention

Use PascalCase namespace segments in generated Camp source.

Valid:

```camp
export as Std;
export as Std::Math;

using Std;
using Std::Math;
```

Avoid:

```camp
export as std;       // avoid lowercase namespace segment
export as std::math; // avoid lowercase namespace segments
using std;
```

Confidence: `SPEC_ONLY_OR_UNVERIFIED`; evidence: `spec::5.1`.

## 4. Types

### Primitive Types

`void`, `bool`, `string`, `wstring`, `astring`, `byte`, `sbyte`, `ushort`, `short`, `uint`, `int`, `ulong`, `long`, `nuint`, `nint`, `float`, `double`, `char`, `wchar`, `achar`, `uchar`, `untyped`.

Confidence: `CONFIRMED_BY_COMPILER_CODE`  
Evidence: `src/Camp.Compiler/BindableNode.cs::PrimitiveType`; `src/Camp.Compiler/BindableNodeBuilder.cs::TryGetPrimitiveType`; `src/Camp.Compiler/BindableNodeAnalyzer.cs::GetPrimitiveTypeName`.

### Type Forms

| Type form | Syntax | Notes | Confidence | Evidence |
|---|---|---|---|---|
| Named | `Name`, `Ns::Name` | Qualifiers use `::`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseQualifiedNameType` |
| Generic | `Name<T, U>` | Generic args in `<...>`. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_constructed.camp` |
| Pointer | `T*` | Explicit pointer type. | `CONFIRMED_BY_TEST` | Many tests, e.g. `tests/CCompile/lifecycle_allocator.camp` |
| Array expanded form | `T[]` | Components: `elements: T*`, `length: nuint`. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddArrayPendingComponents` |
| Fixed-size array type | `T[n]`; declarations that create storage use `fixed T[n] name` | Inline storage for exactly `n` elements. Not a `T[]` and not compiler-expanded. Exposes `.elements` and constant `.length`; converts to `T[]`; direct value copy is invalid. `T[n]*` is a pointer to one fixed array object. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_span_views.camp`; `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8` |
| Optional expanded form | `T?` | Components: `value: T`, `specified: bool`; direct `T??` rejected. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Function pointer | `fn Ret(params)` | Not expanded as params component. `null` can convert to `fn`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildCallableTypeReference`; `CanImplicitlyConvert` |
| Raw function pointer | `fn*` or `fn* _far` | Raw function-pointer carrier. It erases a concrete callable signature but is not callable until explicitly cast back to a concrete `fn` type. It is separate from `void*`, which is the data-pointer fence. | `CONFIRMED_BY_TEST` | `tests/CCompile/conversion_callable_stage4.camp`; `tests/Diagnostics/conversion_callable_stage4_invalid.camp`; `spec::1.12.1` |
| Delegate | `delegate Ret(params)` or `delegate Ret(qualifiers this, params)` | Expanded components: `call`, `context`. Explicit callable `this` qualifies the hidden context parameter. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddDelegatePendingComponents` |
| Once | `once Ret(params)` | Parsed/analyzed callable kind; expanded-form type. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypePrefix`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::IsExpandedFormType` |
| Async callable | `async Ret(params)` | Source-level async callable kind. Async lowers to callback-shaped ABI functions with a final `once void(...)` completion. Awaitable completions may have at most one non-error result and may have one `thrown` slot. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_phase11_integration_runtime.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildCallableTypeReference`; `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::IsAwaitable` |
| Iterator protocol | `iter T`, `iter(T)`, `iter(T, thrown E)`, or context-qualified callable/`newtype iter` forms such as `newtype iter char Reader(const this);` | Expanded components: `call`, `context`; exactly one yielded type, optionally followed by one `thrown` type. Explicit callable `this` qualifies the hidden context parameter. | `CONFIRMED_BY_TEST` | `tests/Ast/iter_type_protocol.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::ValidateIteratorType` |
| Generator return | `struct iter T f()` or `class iter T f()` | Function return modifier for generated iterator state. | `CONFIRMED_BY_TEST` | `tests/Lowering/iterator_generator_multiple_yields.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::GetIteratorKind` |
| Materialized expanded form | `struct(T[])`, `struct(T?)`, `struct(delegate Ret(...))`, `struct(iter T)` | Only valid for expanded array/optional/delegate/iter forms. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Thrown return form | `thrown(T)` | Parsed/analyzed as a type; flow treats return type `thrown(E)` as rethrow-compatible. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypePrefix`; `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs::GetFunctionThrownType` |
| Type declarators | `const T`, `constof(anchor) T`, `volatile T`, `escaped T`, `scoped T`, `scoped(anchor) T`, `unscoped T`, `unscoped(anchor) T` | Prefix and postfix forms parse, but target-specific specifiers must appear after type forms. `constof(anchor)` is source-level dependent constness: anchors are validated, callees see ordinary `const`, call sites substitute the anchor actual's constness, produced returns/`out` values need anchor provenance or an explicit `constof(anchor)` cast, Camp API/metadata preserve the spelling, and C erases it to ordinary `const`. Storage conversion allows mutable-to-`constof`, `constof`-to-ordinary-`const`, and same-anchor `constof`, but not ordinary-`const`-to-`constof`, `constof`-to-mutable, or different anchors without an explicit cast. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypeDeclarator`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildDeclaratorTypeReference`; `src/Camp.Compiler/BindableNodeAnalyzer.ConstOf.cs`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::ValidateTargetTypeSpec` |

Valid:

```camp
const char[] name;
fixed byte[32] scratch;
byte[32]* scratchPtr;
int? maybe;
delegate int(int value) transform;
delegate nuint(const this, char[] buffer) formatter;
fn void(void* context) callback;
struct(int[]) storedArray;
scoped const int[] slice;
unscoped(owner) char* data;
```

Invalid:

```camp
int?? nested;           // optional values may not directly contain optional
int?[] values;         // arrays of expanded values are rejected; use struct(int?)[]
byte[32] scratch;      // fixed-size array storage requires `fixed byte[32] scratch`
void f(byte[32] data); // fixed-size arrays are not passed by value; use byte[32]* or byte[]
struct(int) x;         // struct(T) requires expanded array/optional/delegate/iter
params(int) p;         // params(T) type syntax is no longer supported
```

### Async, Await, Resumers, And Postpone

| Rule | Guidance | Confidence | Evidence |
|---|---|---|---|
| Async ABI | `async T f(...)` is source-level sugar for a callback-shaped ABI function returning `void` with a final omitted-at-source `once void(...)` completion callback. Do not invent task objects. | `CONFIRMED_BY_TEST` | `tests/CEmit/async_noawait_no_frame.camp`; `tests/StdRun/async_noawait_runtime.camp`; `spec::5.3` |
| Awaitable shape | `await` requires the final omitted parameter to be `once void(...)`, with no `out` parameters, at most one `thrown` parameter, and at most one non-error success parameter. Multi-result await/deconstruction is not supported. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/async_await_stage6_invalid.camp`; `tests/Diagnostics/async_await_stage12_invalid.camp`; `tests/StdRun/async_phase11_integration_runtime.camp` |
| Await catch | Awaited thrown completions use ordinary catch arguments: `await op(catch err)` or `await op(catch _)`. If not caught, the error rethrows through the containing async function's completion error path. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_phase11_integration_runtime.camp`; `src/Camp.Compiler/CCodeEmitter.cs::WriteAsyncFrameAwaitErrorHandling` |
| Resumer selection | A concrete async body that can suspend uses either one ordinary parameter marked `@awaitwith` or, for receiver methods, `this` when the receiver type provides exactly one compatible `resumeAsync(...)`. Free/static async functions need `@awaitwith` unless marked `@noawait`. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_tail_await_runtime.camp`; `tests/Diagnostics/async_stage34_scheduler_invalid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Declarations.cs::ValidateAsyncResumer` |
| Resumer shape | A selected resumer must provide exactly one compatible `resumeAsync`: `void resumeAsync(escaped once void() continuation)`, `void resumeAsync(once void(escaped this) continuation)`, or parameterless `async void resumeAsync()`. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_tail_await_runtime.camp`; `tests/Diagnostics/async_stage34_scheduler_invalid.camp` |
| `@awaitwith` | `@awaitwith` is a source attribute on an ordinary runtime parameter of a concrete async definition. It is not a callable type modifier and does not change ABI, overload identity, or callable compatibility. | `CONFIRMED_BY_TEST` | `tests/Ast/async_stage12_surface.camp`; `tests/Metadata/async_resumption_stage1_attributes.camp` |
| `@noawait` | `@noawait` is valid only on concrete async definitions with Camp bodies. It forbids `await` in the body and lets a non-suspending async definition omit a resumer. It does not change async ABI shape. | `CONFIRMED_BY_TEST` | `tests/CEmit/async_noawait_no_frame.camp`; `tests/Diagnostics/async_resumption_stage1_noawait_body_invalid.camp` |
| Async frame storage | Compiler-generated async frames use an escaped `within` allocator when present, then fallback `malloc/free`. Source `new`/`delete` inside async bodies still use ordinary allocation rules. Resumers do not allocate or free frames. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_phase11_integration_runtime.camp`; `src/Camp.Compiler/CCodeEmitter.cs::WriteAsyncFrameAllocation` |
| Stack array allocation | `init T[n]` array allocation expressions are invalid inside async bodies. Use fixed storage where legal or explicit allocation. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/async_stage8_invalid.camp`; `spec::5.3` |
| `once` ownership | `once` guarantees one call; it does not intrinsically free context. Deletion belongs to the producer. Escaped generated once-lambda and `postpone` contexts self-delete; ordinary received once values do not. | `CONFIRMED_BY_TEST` | `tests/StdRun/once_lambda_runtime.camp`; `tests/StdRun/postpone_once_runtime.camp`; `scheduler_design::2` |
| Postpone and resumers | `postpone` treats an `@awaitwith` parameter as an ordinary source parameter slot: supplied values are captured, omitted slots become parameters of the returned `once` delegate. | `CONFIRMED_BY_TEST` | `tests/Ast/async_stage12_surface.camp`; `spec::5.3.12` |
| Lambda `context` | The hidden lambda context parameter is named `context`. Treat it as a special cleanup name, not a normal variable. `delete context` is valid only for escaped ordinary delegate lambdas with generated owned context. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/lambda_context_invalid.camp`; `tests/CCompile/lambda_escaped_context_ownership.camp`; `spec::5.3.13` |
| Async iterators | `async iter` and `await foreach` remain deferred. Do not generate async iterator bodies or rely on async stream helpers beyond declared/provisional surfaces. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType`; `spec::5.4` |

### Fixed-Size Arrays

`T[n]` names a fixed-size array type with inline storage for exactly `n` elements. It is distinct from `T[]`, which is a span-like expanded value with `elements` and `length` components.

Storage declarations require the `fixed` marker:

```camp
fixed byte[32] buffer = [1, 2, 3];
fixed char[8] name = "camp";
```

Invalid:

```camp
byte[32] buffer = [1, 2, 3]; // missing fixed marker
```

Direct fixed-size array storage is allowed only for locals, globals, and struct/class fields. Direct fixed-size array value types are not used as ordinary parameters, returns, callable slots, or value `newtype` underlyings.

```camp
int sum(byte[32] data);              // ERROR: fixed array by value
fn byte[32]();                       // ERROR: fixed array return by value
newtype Block: byte[32];             // ERROR

int sum(byte[32]* data);             // OK: pointer to one fixed array object
fn byte[32]*();                      // OK: function returns a pointer
```

A fixed-size array is a fixed value. It may be written into, indexed, sliced, addressed, and viewed as `T[]`, but it may not be copied as a value.

```camp
fixed byte[4] a = [1, 2];
fixed byte[4] b = [3, 4];

a = [5, 6];       // OK: target-typed initializer pattern, default-fills rest
a = default;      // OK: zero/default-fills storage
a = b;            // ERROR: fixed-array value copy
byte[] span = a;  // OK: view conversion to T[]
a = span;         // ERROR: T[] does not convert to fixed array storage
```

String literals may initialize or overwrite compatible fixed-size character arrays: `char[n]`, `wchar[n]`, and `achar[n]`. If the literal exactly fills the destination, no terminator beyond capacity is appended. If space remains, remaining elements are zero-filled.

```camp
fixed char[4] a = "abc";  // a,b,c,0
fixed char[3] b = "abc";  // a,b,c, no extra terminator
fixed achar[8] c = "ok";
fixed wchar[8] w = "ok";
```

A fixed-size array exposes `.elements` and `.length`. `.length` is the constant bound. `sizeof(T[n])` is valid and is `n * sizeof(T)`, so `sizeof(int[8]) == 32`.

```camp
fixed int[8] values;
nuint n = values.length;     // 8
int* p = values.elements;
nuint bytes = sizeof(int[8]); // 32
```

`&array` has type `T[n]*`, not `T*` and not `T[]*`.

```camp
fixed byte[32] data;
byte[32]* whole = &data;
byte* first = data.elements;
byte[] span = data;
```

A pointer to a fixed-size array points to one fixed array object. Indexing the pointer indexes fixed array objects, not elements. Dereference explicitly before using fixed-array indexing, slicing, `.elements`, `.length`, or span conversion.

```camp
int sum(int[8]* values)
{
	int a = values[0];      // ERROR: values[0] has type int[8]
	int b = (*values)[0];   // OK
	int c = values[0][0];   // OK but less clear
	int[] s = (*values)[0..2];
	return b + c + (int)s.length;
}
```

Nested fixed-size arrays compose by applying the same rule at each level:

```camp
fixed byte[8][8] matrix;     // 8x8 bytes
byte[8][8]* matrixPtr;       // pointer to one 8x8 matrix
fixed byte[8]*[8] rowPtrs;   // fixed array of 8 pointers to byte[8]
fixed byte*[8][8] ptrMatrix; // 8x8 matrix of byte pointers
```

A copyable struct may contain fixed-size array fields only when the fixed-array storage is aggregate-copyable. Copying the containing struct copies the fixed-array field as part of the enclosing aggregate copy, but direct fixed-array value copy remains invalid.

```camp
struct Packet
{
	fixed byte[32] data;
}

Packet p1;
Packet p2;

p1 = p2;           // OK if Packet is copyable
p1.data = p2.data; // ERROR
p1.data = [1, 2];  // OK: initializer-pattern write
```

Confidence: `SPEC_ONLY_OR_UNVERIFIED`; evidence: `spec::1.6.8`, `1.12.4`, `2.2.1`, `3.9.5`.

## 5. Expressions And Statements

### Expressions

| Form | Syntax | Confidence | Evidence |
|---|---|---|---|
| Variable declaration | `Type name;`, `Type name = expr;`, `auto name = expr;`, `auto (a, b) = expr;` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseDeclarationTarget` |
| Assignment | `=`, compound assignment operators parsed as assignment expressions. | `CONFIRMED_BY_TEST` | `tests/Lowering/default_arguments.camp`; `src/Camp.Compiler/CampParser.cs::ParseAssignmentExpression` |
| Calls | `f()`, `f(a, b)`, `Type.staticMethod()` | `CONFIRMED_BY_TEST` | `tests/CCompile/overload_basic.camp` |
| Named arguments | `f(name: value)` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseArgument`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.ParamsExpressions.cs` |
| Out argument | `f(out target)` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseArgument`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildArgumentExpression` |
| Catch argument | `f(catch error)` | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp` |
| Within argument | `f(within allocator)` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseArgument`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Expressions.cs::AddImplicitWithinArgument` |
| Member access | `target.member` for values and pointers. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `tests/Lowering/overload_property_and_base.camp` |
| Function/method reference | `functionName`, `target.method`, `Type.method`, or canonical flattened symbols such as `Type_method` without `()` refer to callable declarations or values. A matching callable ascription gives only the matching natural reference form the named callable newtype instead of the ordinary anonymous callable type. Explicit callable `this` qualifiers are enforced for bound method conversions. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `tests/CCompile/callable_this_*.camp`; `spec::1.4.16`, `3.4.4` |
| Property getter/setter | `obj.Value`, `obj.Value = x`, `obj.Value[arg]` map to `getValue`/`setValue` methods. Type-owned `getX` methods with no explicit receiver are analyzed as `const this`; explicit `this` overrides that default, and typed extension receivers keep their written qualifiers. | `CONFIRMED_BY_TEST` | `tests/Lowering/overload_property_and_base.camp`; `tests/CCompile/property_getter_implicit_const_this.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Expressions.cs::TryRewritePropertySetterAssignment` |
| Indexing | `arr[i]`, `ptr[i]`, `obj[indexArgs]` | `CONFIRMED_BY_TEST` | `tests/CCompile/array_literal_indexing.camp`; `tests/CCompile/slice_property_getter.camp` |
| Fixed-size array operations | `fixed T[n] a`, `a[i]`, `a[start..end]`, `a.elements`, `a.length`, `&a`, `(*p)[i]`, `sizeof(T[n])`; no fixed-array value copy. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_span_views.camp`; `tests/CCompile/fixed_array_pointer_receivers.camp`; `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8`, `3.9.5` |
| Range | `start..end`, `..end`, `start..`, `..`; from-end uses prefix `^`. | `CONFIRMED_BY_TEST` | `tests/CCompile/array_range_index.camp`; `src/Camp.Compiler/CampParser.cs::ParseRangeOrAssignmentExpression` |
| Cast | `(Type)expr`, `(unsafe Type)expr`, `(struct)expr`, `(class)expr`, `(params)expr`, and lifetime casts such as `(escaped)expr`. `unsafe` is only for direct casts that require acknowledgement; when changing array element types, delegate signatures, optional payloads that need reconstruction, or generic arguments, rebuild the value instead. | `CONFIRMED_BY_TEST` | `tests/CCompile/conversion_policy_stage*.camp`; `tests/Diagnostics/conversion_*invalid*.camp`; `src/Camp.Compiler/CampParser.cs::TryParseCastExpression`; `spec::1.12` |
| Construction | `init T(args)`, `new T(args)`, `within (allocator) new T(args)`, array allocation `new T[count]`, and stack-array allocation `init T[count]`. `init T[n]` returns `T[]`, not `T[n]`; it is invalid inside generator and async bodies. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `tests/StdRun/within_new_array_expression.camp`; `tests/Diagnostics/async_stage8_invalid.camp`; `src/Camp.Compiler/CampParser.cs::TryParseConstructionExpression`; `spec::4.4.1`, `5.2.4`, `5.3` |
| Lambda | `(params) => expr` or `(params) => { ... }`; target-types to `fn` or `delegate`. Capturing scoped delegates store pointers to declaration-scope values. Escaped delegates allocate context storage and copy captures by value. | `CONFIRMED_BY_TEST` | `tests/CCompile/lambda_fn_stage1.camp`; `tests/CCompile/lambda_scoped_capture_stage3.camp`; `tests/CCompile/lambda_escaped_hardening_matrix.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Lambdas.cs` |
| Await/postpone | `await call`, `postpone call` are unary prefix operators whose operand must be a method-call expression or chain ending in a method call. `await` is valid only in async bodies and omits the final `once` completion slot. `postpone` performs partial application and returns `once`. | `CONFIRMED_BY_TEST` | `tests/StdRun/async_phase11_integration_runtime.camp`; `tests/StdRun/postpone_once_runtime.camp`; `src/Camp.Compiler/CampParser.cs::TryParseUnaryPrefix`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildUnaryExpression`; `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.cs` |
| `sizeof` | `sizeof(T)` expression or hidden parameter `sizeof(T)` in parameter list. `sizeof(int[8])` is valid and equals 32. Generic copy of `T` requires `T: copyable` plus `sizeof(T)` when erased lowering needs the size. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_sizeof_field.camp`; `tests/CCompile/fixed_array_const_lengths.camp`; `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.SizeOf.cs`; `spec::1.6.8`, `6.2.6` |
| `vtableof` | `vtableof(T: Interface)` expression or hidden parameter. | `CONFIRMED_BY_TEST` | `tests/Ast/future_syntax_surface.camp`; `tests/Lowering/vtableof_generic_dispatch.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.VTableOf.cs` |

### Statements

| Statement | Syntax | Confidence | Evidence |
|---|---|---|---|
| Block | `{ statements }` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseBlockStatement` |
| If/else | `if (cond) stmt else stmt`; `else if` attaches as nested if. | `CONFIRMED_BY_TEST` | `tests/Lowering/else_if_chain.camp` |
| While | `while (cond) stmt` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Statements.cs::BuildWhileStatement` |
| Do while | `do stmt while (cond);` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Statements.cs::AttachDoWhileCondition` |
| For | `for (init; cond; step) stmt` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Statements.cs::BuildForStatement` |
| Foreach | `foreach (Type value in source) stmt`; `await foreach` is represented internally but several async cases report not implemented. | `CONFIRMED_BY_TEST` | `tests/CCompile/foreach_array_sum.camp`; `tests/Lowering/foreach_iterator.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType` |
| Switch | `switch (expr) { case C: ... default: ... }` | `CONFIRMED_BY_TEST` | `tests/CCompile/switch_statement.camp` |
| Return | `return;`, `return expr;` | `CONFIRMED_BY_TEST` | Many tests |
| Yield | `yield expr;` in iterator generators. Iterators yield one value; use a named struct if each item needs multiple fields. | `CONFIRMED_BY_TEST` | `tests/Lowering/iterator_generator_multiple_yields.camp` |
| Throw | `throw expr;` | `CONFIRMED_BY_TEST` | `tests/Lowering/throw_try_finally.camp` |
| Try/catch/finally | `try { ... } catch (Err e) { ... } finally { ... }` | `CONFIRMED_BY_TEST` | `tests/CCompile/catch_argument_variable.camp`; `tests/Lowering/throw_try_finally.camp` |
| Within | `within (allocator) stmt` | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::RewriteStatement` |
| Delete | `delete expr;` | `CONFIRMED_BY_TEST` | `tests/CCompile/delete_string.camp`; `tests/CCompile/lifecycle_allocator.camp` |
| Finally delete expression | `finally delete expr` as unary expression. | `CONFIRMED_BY_TEST` | `tests/StdRun/pointer_new_array_finally_delete.camp`; `src/Camp.Compiler/CampParser.cs::ParseUnaryExpression` |
| Goto/labels | `label:`, `goto label;` parse/build. Avoid unless required. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Statements.cs::BuildGotoStatement`, `BuildStatement` |

## 6. Compiler-Expanded Forms

Fixed-size arrays are not compiler-expanded forms. They are inline storage values that synthesize `.elements` and `.length` only for array-like operations and span conversion.

| Source type | Components | Component access | Confidence | Evidence |
|---|---|---|---|---|
| `T[]` | `elements: T*`, `length: nuint` | `arr.elements`, `arr.length` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddArrayPendingComponents` |
| `T?` | `value: T`, `specified: bool` | `opt.value`, `opt.specified` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddOptionalPendingComponents` |
| `delegate R(P...)` | `call: fn R(void*, P...)`, `context: void*` | `del.call`, `del.context`; `del(args...)` rewrites to call. Explicit callable `this` qualifiers describe the hidden context. Escaped delegate contexts are ordinary owned pointers; delete `del.context` when you own one. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `tests/CCompile/lambda_escaped_delegate_invocation.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddDelegatePendingComponents`; `Lowering.Expressions.cs::TryRewriteDelegateInvocation` |
| `iter T` | `call: fn bool(void*, T*)`, `context: void*` | `it.call`, `it.context`; callable protocol. Explicit callable `this` qualifiers describe the hidden context. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_return_iter_assignment.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddIteratorPendingComponents` |

Rules:

| Rule | Confidence | Evidence |
|---|---|---|
| Expanded-form parameters/local declarations may synthesize multiple underlying component symbols; avoid names that collide with generated component names. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Declarations.cs::ValidateExpandedParameterNames`; `ValidateDuplicateTopLevelSymbols` |
| Assignment to expanded forms is lowered component-wise. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::TryRewriteParamsAssignment` |
| Return of expanded forms is lowered through component/out-style handling. | `CONFIRMED_BY_TEST` | `tests/CEmit/expanded_return_argument.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::TryRewriteExpandedReturn` |
| Default arguments after expanded arguments are supported. | `CONFIRMED_BY_TEST` | `tests/CCompile/default_argument_after_expanded_arg.camp` |
| Arrays of expanded values are rejected; use `struct(T)` materialization. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |

Valid:

```camp
int apply(delegate int(int value) transform, int? maybe, const char[] name)
{
	if (maybe.specified)
		return transform(maybe.value) + (int)name.length;
	return (int)name.length;
}

struct(int?)[] materializedOptions;
```

Invalid:

```camp
int?[] rawOptions;     // use struct(int?)[]
int?? nested;          // use struct(int?)? if materialized nesting is required
```

## 7. Object Model

| Rule | Confidence | Evidence |
|---|---|---|
| `struct` is a value aggregate and can implement interfaces. | `CONFIRMED_BY_TEST` | `tests/CCompile/struct_interface_indirect.camp` |
| `class` participates in pointer/object allocation patterns; `new Class()` returns pointer-like values in tests. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_constructed.camp`; `tests/CCompile/lifecycle_allocator.camp` |
| `extern class` is opaque foreign escaped storage. Use only pointer values such as `Native*`. Do not use direct locals/fields/params/returns, arrays of direct values, `init`, hidden layout, or generated constructors. Imported interface contracts are allowed and mean the foreign type exposes the generated `getInterfaceName` accessor surface. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/extern_class_invalid.camp`; `tests/Diagnostics/extern_class_lifecycle_invalid.camp`; `tests/CCompile/extern_class_inheritance_lifecycle.camp`; `tests/CCompile/project_reference_consumes_exported_interface_accessors_and_vtables.camp` |
| `fixed struct` is accepted; `fixed class` is rejected. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyStructDeclarators`, `ApplyClassDeclarators` |
| A class/struct may list one base class plus interfaces after `:`. More than one base class is rejected. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeClassOrStructBaseTypes` |
| An `extern class` may inherit only from an `extern class`; an ordinary class may inherit only from an ordinary class. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/extern_class_invalid.camp`; `tests/CCompile/extern_class_inheritance_lifecycle.camp` |
| Interfaces may derive only from interfaces. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeInterfaceBaseTypes` |
| Classes deriving from virtual/abstract classes must be `virtual`, `abstract`, or `sealed`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateClassVirtualMethods` |
| Override/sealed methods must match inherited virtual/abstract signatures. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateOverrideMethod` |
| Base method calls use `base.method(args)`. | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `tests/Lowering/overload_property_and_base.camp` |
| `init T(...)` initializes storage; `new T(...)` allocates and initializes; `delete` invokes lifecycle cleanup/deallocation. For `extern class`, `new` requires an extern constructor/create surface and `delete` requires an extern destructor; no `op_initnew`, allocation/free, layout, or implicit base constructor logic is generated. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `tests/CCompile/extern_class_inheritance_lifecycle.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateInitNewMethod`, `CreateCreateMethod`, `CreateDeleteMethod`, `CreateDestroyMethod` |
| `within (allocator)` supplies allocator context to lifecycle methods with `within` parameters. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::RewriteStatement` |

Valid:

```camp
class Allocator
{
	void* alloc(nuint size) { return null; }
	void free(void* ptr) {}
}

struct Buffer
{
	Buffer(int length, within Allocator* allocator) {}
	~Buffer(within Allocator* allocator) {}
	int* item;
}

int main()
{
	Allocator* arena = null;
	within (arena)
	{
		Buffer local = init Buffer(2);
		delete local;
	}
	return 0;
}
```

## 8. Error Handling

| Rule | Confidence | Evidence |
|---|---|---|
| A throwing function usually declares a `thrown E error` parameter. | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp` |
| `throw value;` must be caught or rethrown by compatible thrown result/parameter. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs::HandleThrownValue` |
| Calls to throwing functions can pass `catch error` to capture the error value. | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp`; `src/Camp.Compiler/CampParser.cs::ParseArgument` |
| `try { } catch (Err e) { } finally { }` is supported; catch condition is a declaration target. | `CONFIRMED_BY_TEST` | `tests/CCompile/catch_argument_variable.camp`; `src/Camp.Compiler/CampParser.cs::ParseCatchStatementCondition` |
| Returning from a throwing function clears thrown parameter state during lowering. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::PrependThrownParameterClear` |
| `thrown(E)` return type is recognized by flow as a rethrow-compatible function return form. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs::GetFunctionThrownType` |

Valid:

```camp
enum Err
{
	OK = 0,
	BAD
}

int mightThrow(thrown Err error)
{
	throw BAD;
}

int main()
{
	Err error = default;
	int value = mightThrow(catch error);
	return error == Err.BAD ? value : 1;
}
```

Avoid:

```camp
int bad()
{
	throw BAD; // no compatible catch/rethrow context
}
```

## 9. Lifetimes And Allocators

| Rule | Confidence | Evidence |
|---|---|---|
| Type declarators `scoped`, `escaped`, `unscoped`, and `constof` are implemented. `scoped(...)`, `unscoped(...)`, and `constof(...)` carry anchor identifiers. `scoped`/`unscoped` describe lifetime; `constof` describes caller-visible constness and should not be substituted by lifetime rules. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypeDeclarator`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildDeclaratorTypeReference`; `src/Camp.Compiler/BindableNodeAnalyzer.ConstOf.cs` |
| Callable signature compatibility treats `constof` outputs covariantly and inputs contravariantly for ordinary callable assignment/ascription/interface checks. Virtual and abstract overrides remain exact. | `CONFIRMED_BY_TEST` | `tests/CCompile/constof_signature_variance.camp`; `tests/Diagnostics/constof_signature_variance_invalid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Callables.cs::CallableShapesCompatibleWithConstOfVariance` |
| Lifetime conversion is ordered: source lifetime must be at least target lifetime under the implementation's `Scoped < Unscoped < Escaped` enum. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeShapes.cs::LifetimeKind`, `QualifiersCanConvert` |
| `escaped` class and interface declarators are valid; `escaped struct` is invalid. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyClassDeclarators`, `ApplyStructDeclarators`, `ApplyNonStructTypeDeclarators` |
| `escaped` fields are valid and require assigned pointer-bearing values to satisfy escaped storage. `scoped`/`unscoped` fields, locals, and globals are invalid. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_escaped_fields.camp`; `tests/Diagnostics/lifetime_annotation_placement_invalid.camp`; `tests/Diagnostics/lifetime_escaped_field_assignment_invalid.camp` |
| Explicit lifetime casts are available: `(scoped)value`, `(escaped)value`, `(unscoped(anchor))value`, and combined type/lifetime casts such as `(escaped string)value`. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/lifetime_cast_invalid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.LifetimeFacts.cs` |
| In an `escaped class`, an ascribed instance method must preserve the escaped receiver contract explicitly: the callable newtype declares `escaped this`, or the method declares `escaped this`. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16`, `4.2.3` |
| `within Allocator* allocator` may appear as a parameter modifier, and bare `within name` can be an implicit within parameter form. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/CampParser.cs::ParseParameter` |
| `within (allocator) statement` or `within (allocator) new/init T(...)` supplies current allocator context. `within (default)` masks any surrounding allocator and intentionally uses fallback `malloc/free`. | `CONFIRMED_BY_TEST` | `tests/StdRun/within_new_array_expression.camp`; `tests/CCompile/within_default_allocator.camp`; `src/Camp.Compiler/CampParser.cs::TryParseConstructionExpression` |
| `#within explicit` requires source-level `new`, pointer-form `delete`, and pointer-storage `finally delete` in that file to use `within (allocator)`, a routine `within` parameter, or `within (default)`. `#within implicit` allows fallback allocation without an explicit source context. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/within_allocation_policy.camp`; `tests/Diagnostics/within_directive_errors.camp`; `src/Camp.Compiler/CompilerDriver.cs::TryReadWithinAllocationPolicy` |
| Allocation requires accessible `malloc(nuint)`/`free(void*)` or allocator methods `alloc(nuint)`/`free(void*)`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.LoweringHelpers.cs::CreateAllocCallFromByteSize`, `CreateFreeCall` |

Keep lifetime signatures sparse. Do not write `unscoped(this)` merely to restate an instance-method default. Use explicit annotations when an API allocates escaped storage, returns a borrow tied to a specific anchor, stores a pointer-bearing value into a receiver/container, or requires an escaped field/context. Use explicit lifetime casts only as local assertions after the code has an actual reason the compiler cannot prove.

Preferred pattern:

```camp
class Allocator
{
	void* alloc(nuint size) { return null; }
	void free(void* ptr) {}
}

void use(within Allocator* allocator)
{
	int* data = new int[4];
	delete data;
}
```

## 10. Generics

| Rule | Confidence | Evidence |
|---|---|---|
| Generic declarations use `<T>` after type/function name. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_layout.camp`; `tests/CCompile/generic_new_constructed.camp` |
| Generic constraints use colon syntax: `<T: any>`, `<T: copyable>`, or `<T: implements Interface>` depending on parsed `implements` keyword. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_erasure.camp`; `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Lowering/vtableof_generic_dispatch.camp`; `src/Camp.Compiler/CampParser.cs::ParseGenericParameter`; `spec::6.1.6`, `6.2.6` |
| Generic parameter names must be unique. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Declarations.cs::AnalyzeGenericParameters` |
| Generic values constrained to `any` must be passed by reference and are non-copying. A `T: any` body may not copy, assign, return, store, move, or otherwise transport `T` values by value. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/overload_invalid.expected.txt`; `tests/CCompile/generic_erasure.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.5` |
| `T: copyable` is stronger than `T: any`. It excludes direct class types, fixed structs, and fixed-size array value types. Pointer types remain copyable, including pointers to classes, fixed structs, and fixed-size arrays. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.1.6`, `6.2.6` |
| A `T: copyable` type argument may satisfy a callee that requires the same `T: any`. A `T: any` type argument may not satisfy a callee or type that requires `T: copyable`. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.6` |
| `sizeof(T)` hidden parameter support is implemented for generic allocation/layout. Under `T: any`, `sizeof(T)` permits enumeration, pointer indexing, size-based allocation, and default-fill, but never permits copying `T`. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_sizeof_field.camp`; `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.SizeOf.cs`; `spec::6.2.7` |
| A generic operation that copies a `T` value requires both `T: copyable` and an available `sizeof(T)` parameter when erased lowering needs the storage size. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.6`, `6.2.7` |
| `vtableof(T: Interface)` requires generic parameter `T` to be constrained to implement that interface. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.VTableOf.cs::ValidateVTableOfRequest` |
| Generic types and functions lower by substitution/erasure patterns visible in C compile tests. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_erasure.camp`; `tests/CCompile/generic_scalar_erasure.camp`; `tests/CCompile/generic_iterator_param_erasure.camp` |

Use `T: any` for non-copying algorithms that observe, address, enumerate, compare through a provided callback, or default-fill storage. Use `T: copyable` for containers and algorithms that copy, move, store, compact, swap, return, or assign `T` values.

Valid `T: any` patterns:

```camp
void accept<T: any>(T* value)
{
}

T* elementAt<T: any>(T[] values, @index nuint index, sizeof(T))
{
	return values.addressOf(index);
}
```

Invalid `T: any` copies:

```camp
void badCopy<T: any>(T* dst, T* src, sizeof(T))
{
	*dst = *src; // ERROR: T: any is never copyable, even with sizeof(T)
}

void badArrayCopy<T: any>(T[] dst, T[] src, sizeof(T))
{
	dst[0] = src[0]; // ERROR: copies T value
}
```

Valid `T: copyable` copy pattern:

```camp
void copyOne<T: copyable>(T* dst, T* src, sizeof(T))
{
	*dst = *src;
}
```

Invalid `T: copyable` copy without size:

```camp
void badCopyOne<T: copyable>(T* dst, T* src)
{
	*dst = *src; // ERROR when erased lowering needs sizeof(T)
}
```

Fixed values and pointer values:

```camp
fixed struct ParserState
{
	nuint position;
}

class Box<T: any>
{
	T* ptr;
}

class List<T: copyable>
{
	T[] items;
}

Box<ParserState> stateBox;     // OK: non-copying storage pointer pattern
List<ParserState> badStates;   // ERROR: fixed struct is not copyable
List<ParserState*> statePtrs;  // OK: pointer value is copyable

Box<byte[32]> blockBox;        // OK: fixed-size array as T under non-copying any
List<byte[32]> badBlocks;      // ERROR: fixed-size array value is not copyable
List<byte[32]*> blockPtrs;     // OK: pointer value is copyable
```

Constraint flow is one-way:

```camp
nint findIndex<T: any>(T[] values, delegate bool(const T* value) predicate, sizeof(T));

void useCopyable<T: copyable>(T[] values, sizeof(T))
{
	auto i = findIndex<T>(values, isMatch, sizeof(T)); // OK: copyable also satisfies any
}

void useAny<T: any>(T[] values, sizeof(T))
{
	List<T> list; // ERROR: List<T: copyable> cannot accept a merely-any T
}
```

Generic standard-library types and methods that store, copy, move, compact, swap, or return `T` values should be declared with `T: copyable`, not `T: any`. For example, `List<T: any>` is wrong if the list owns contiguous storage and moves elements; write `List<T: copyable>`.

## 11. Lowering Patterns Useful For LLMs

| Source pattern | Lowering/ABI pattern | Confidence | Evidence |
|---|---|---|---|
| Instance methods | Rewritten to C-style calls with receiver passed explicitly; extension symbols include flattened receiver type fragments. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.InstanceCalls.cs`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeShapes.cs::BuildExtensionFunctionSymbol` |
| Static methods | Rewritten from `Type.method()` to direct symbol calls. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Expressions.cs::TryRewriteStaticMemberInvocation` |
| Overload selectors | Full callable name is invoker plus flattened selector type fragment, e.g. `writeInt`, `writeString`. | `CONFIRMED_BY_TEST` | `tests/CCompile/overload_basic.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Overloads.cs::PrecomputeOverloadCallableName` |
| Constructors | Ordinary user constructors produce generated `#init_new` and `create`/allocation helpers. Extern-class constructors are extern create surfaces only; do not expect or emit `op_initnew`. | `CONFIRMED_BY_TEST` | `tests/CCompile/extern_class_inheritance_lifecycle.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateInitNewMethod`, `CreateCreateMethod` |
| Destructors | Ordinary user destructors produce generated delete/destroy helpers. Extern-class destructors are extern delete surfaces only; do not expect generated destroy/free logic. | `CONFIRMED_BY_TEST` | `tests/CCompile/extern_class_opaque_helpers.camp`; `tests/CCompile/extern_class_inheritance_lifecycle.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateDeleteMethod`, `CreateDestroyMethod` |
| Arrays | `T[]` lowers as components/pair: `T* elements`, `nuint length`. | `CONFIRMED_BY_TEST` | `tests/CEmit/primitive_flattened_symbols.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Fixed-size arrays | `fixed T[n]` lowers to inline C array storage where C supports it, e.g. `uint8_t data[32]`. `T[n]*` lowers to pointer-to-array, e.g. `uint8_t (*p)[32]`. `T[n]` view-converts to `T[]` by synthesizing `{ elements, n }`. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_storage.camp`; `tests/CCompile/fixed_array_pointer_receivers.camp`; `tests/CCompile/fixed_array_span_views.camp`; `spec::1.6.8` |
| Optionals | `T?` lowers as value plus bool specified. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Delegates | `delegate` lowers as call pointer taking context first plus context pointer. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Callable ascription | Uses the same ABI representation as the ascribed callable newtype's underlying form. It does not add wrappers, adapter thunks, hidden allocations, null contexts, direct-call rewrites, or generated symbols. Explicit callable `this` qualifiers affect type checking, not lowering. | `CONFIRMED_BY_TEST` | `tests/CCompile/callable_this*.camp`; `tests/CCompile/lambda_escaped_target_typing.camp`; `spec::1.4.16`, `1.11` |
| Interfaces | Interface implementation generates vtable storage, thunks, and indirect structs for structs. | `CONFIRMED_BY_TEST` | `tests/CEmit/interface_vtable_exports.camp`; `tests/CCompile/struct_interface_indirect.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs` |
| Iterators | `struct iter`/`class iter` generators lower to state plus `next` protocol. | `CONFIRMED_BY_TEST` | `tests/Lowering/iterator_generator_*.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.Iterators.cs` |

## 12. Canonical Code Idioms

### Simple Function

```camp
int add(int left, int right)
{
	return left + right;
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CEmit/basic_functions.camp`.

### Struct With Constructor

```camp
struct Point
{
	Point(int x, int y)
	{
		this.x = x;
		this.y = y;
	}

	int x;
	int y;
}
```

Confidence: `CONFIRMED_BY_COMPILER_CODE`; evidence: `src/Camp.Compiler/BindableNodeBuilder.cs::BuildStructDefinition`, `ValidateLifecycleMember`.

### Class With Destructor

```camp
class Holder
{
	Holder() {}
	~Holder() {}
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/Api/exported_lifecycle_api.camp`.

### Array Parameter

```camp
int lengthOf(const char[] text)
{
	return (int)text.length;
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CCompile/default_argument_after_expanded_arg.camp`.

### Fixed-Size Array Storage

```camp
struct Packet
{
	fixed byte[32] data;
	nuint length;
}

void initialize(Packet* packet)
{
	packet.data = [1, 2, 3, 4];
	packet.length = 4;

	byte[] view = packet.data;
	byte[32]* whole = &packet.data;
}
```

Confidence: `SPEC_ONLY_OR_UNVERIFIED`; evidence: `spec::1.6.8`.

### Optional Parameter

```camp
int readOrDefault(int? value)
{
	return value.specified ? value.value : 0;
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CCompile/expanded_forms.camp`.

### Delegate Callback

```camp
void invoke(delegate int(in int left, in int right) comparer)
{
}

int compare(in int left, in int right) => 0;
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CCompile/direct_function_delegate_thunk.camp`.

### Callable Newtype Ascription and CharFormatter

```camp
newtype delegate nuint CharFormatter(const this, char[] buffer = default);

string copyString(CharFormatter this, within allocator)
{
	char[] buffer = new char[this()];
	this(buffer);
	return buffer.elements;
}

struct Date
{
	int year;

	nuint format(char[] buffer = default) : CharFormatter
	{
		return 1; // includes trailing null terminator
	}
}
```

The `const this` on `CharFormatter` is inherited by `Date.format`, so the method body is checked as a const receiver even though the method does not rewrite `const this` explicitly.

Confidence: `SPEC_ONLY_OR_UNVERIFIED`; evidence: `spec::1.4.16`.

### Thrown Error

```camp
enum Err { OK = 0, BAD }

int mightThrow(thrown Err error)
{
	throw BAD;
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/CCompile/thrown_parameter_forwarding.camp`.

### Property Getter/Setter

```camp
class Item
{
	void setValue(overload int value) {}
	int getValue(overload int fallback) => fallback;
}

void use(Item* item)
{
	item.Value = 1;
	int v = item.Value[2];
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/Lowering/overload_property_and_base.camp`.

### Init/New/Finally Delete

```camp
int main()
{
	int* values = new int[4];
	finally delete values;
	return 0;
}
```

Confidence: `CONFIRMED_BY_TEST`; evidence: `tests/StdRun/pointer_new_array_finally_delete.camp`.

### Generic Copyable Container

```camp
class List<T: copyable>
{
	T[] items;
	nuint count;

	void add(in T value, sizeof(T))
	{
		this.items[this.count] = value;
		this.count++;
	}
}
```

Use `T: any` only for non-copying generic APIs. Copying under `T: copyable` still needs `sizeof(T)` when erased lowering requires the size.

Confidence: `SPEC_ONLY_OR_UNVERIFIED`; evidence: `spec::6.2.6`.

### Namespace Export

```camp
export as Std::Math;

export int answer()
{
	return 42;
}
```

Confidence: `CONFIRMED_BY_COMPILER_CODE`; evidence: `src/Camp.Compiler/CampParser.cs::ParseExportImportExportDeclaration`.

## 13. Anti-Patterns

| Avoid | Use Instead | Confidence | Evidence |
|---|---|---|---|
| Ordinary same-name overloads by signature. | Explicit `overload` selector family, same selector parameter name and first non-this parameter position. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/overload_invalid.expected.txt` |
| Mixing ordinary and `overload` declarations in one family. | Make every declaration in family use `overload`, or make all names unique. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/overload_invalid.expected.txt` |
| More than one overload selector. | One `overload` parameter only. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Overloads.cs::AnalyzeOverloadDeclaration` |
| `overload` on constructors/destructors, thrown params, defaulted params, or non-first ordinary param. | Put `overload` on first ordinary non-this value/out/in parameter without default. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/overload_invalid.camp`; `BindableNodeAnalyzer.Overloads.cs::AnalyzeOverloadDeclaration` |
| Receiverless declarations ascribing `delegate`, `iter`, or another context-carrying callable `newtype`. | Use a named callable `newtype fn`, or make the declaration receiver-bearing when context is part of the callable contract. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| Receiver-bearing methods ascribing `fn`. | Use a named context-carrying callable `newtype` such as `delegate` or `iter`. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| Anonymous callable type after an ascription colon. | Use a named callable `newtype`, e.g. `: CharFormatter`, not `: delegate nuint(...)`. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| Explicit method `this` qualifiers that disagree with the ascribed callable newtype's explicit callable `this`. | Omit method `this` to inherit callable qualifiers, or write matching qualifiers exactly. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| Ascribed methods in `escaped class` without explicit `escaped this` on either the callable newtype or method. | Put `escaped this` on the callable newtype when the callable contract requires escaped context, or explicitly write `escaped this` on the method. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16`, `4.2.3` |
| Expecting callable ascription to affect direct calls, overload groups, `Date.format`, or `Date_format`. | Ascription affects only the matching natural callable reference form, such as `date.format` for receiver-bearing delegate/iter ascription. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| `CharFormatter` copy helpers that allocate `formatter() + 1` characters. | `CharFormatter` returns the required character count including the trailing null terminator; allocate exactly `new char[formatter()]`. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::1.4.16` |
| Calling a `fn*` value directly. | Cast back to a concrete function type first: `((fn int(int))raw)(value)`. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/conversion_callable_stage4_invalid.camp`; `spec::1.12` |
| Rewriting array element types, delegate signatures, optional payloads requiring reconstruction, or generic arguments with a cast. | Reconstruct the value with explicit components or a typed helper. Casts do not tunnel through arrays, delegates, optionals that need reconstruction, or constructed generic arguments. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/conversion_multivalue_stage5_invalid.camp`; `spec::1.12.6` |
| Using `->`. | Always use `.` for member access. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Punctuation`; no parser rule for `->` |
| Arrays of expanded values: `int?[]`, `char[][]` where element is expanded. | `struct(int?)[]`, `struct(char[])[]`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Declaring fixed-size array storage without `fixed`: `byte[32] data;`. | Use `fixed byte[32] data;`. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8` |
| Passing or returning fixed-size arrays by value: `void f(byte[32] data)`, `fn byte[32]()`. | Use `byte[32]*` for whole-array storage or `byte[]` for a span view. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8` |
| Copying fixed-size arrays directly: `a = b`, `return packet.data`, `auto copy = packet.data`. | Write an initializer pattern into known storage (`a = [1, 2]`, `a = default`, `a = "text"`) or pass a pointer/span. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8` |
| Treating `T[n]*` as `T*`: `int x = values[0];` when `values: int[8]*`. | Dereference the whole fixed array first: `(*values)[0]`, or explicitly use `values[0][0]`. | `CONFIRMED_BY_TEST` | `tests/CCompile/fixed_array_pointer_receivers.camp`; `tests/Diagnostics/fixed_array_invalid.camp`; `spec::1.6.8`, `3.9.5` |
| Direct nested optional `T??`. | `struct(T?)?` if nesting is required. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Interface fields or method bodies. | Interface method signatures only. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildInterfaceDefinition`, `BuildInterfaceFunctionDefinition` |
| Nested type declarations in structs/classes/interfaces/enums/newtypes/params. | Declare types at top level. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs` type builders |
| Bodyless non-extern methods outside interfaces/abstract methods. | Add body or mark `extern`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildFunctionDefinition` |
| `static` global functions/variables. | Omit `static` at top level. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFunctionDeclarators`, `ApplyVariableDeclarators` |
| Virtual methods in non-virtual/non-abstract classes. | Mark class `virtual`/`abstract` or remove `virtual`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateClassVirtualMethods` |
| Abstract method with body. | Use `virtual` with body or `abstract` with semicolon. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateFunctionModifiers` |
| `foreach` over non-array/non-iterator source. | Use `T[]`, `iter T`, or iterator state with `bool next(T* current)` and optional trailing `thrown E`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType` |
| Async foreach or `async iter` bodies. | Avoid; async iterators and `await foreach` are reserved/deferred and diagnostics report them as not implemented. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType`; `spec::5.4` |
| Generic containers that copy/move/store `T` declared as `T: any`, e.g. `class List<T: any>`. | Use `T: copyable` and require `sizeof(T)` where erased copying needs a size. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.6`, `8.4` |
| Copying `T` under `T: any`, even with `sizeof(T)`. | Use `T: copyable` plus `sizeof(T)` when copying is required; use `T: any` only for non-copying access/enumeration/default-fill. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.5`, `6.2.7` |
| Passing a `T: any` type parameter to a generic type or method requiring `T: copyable`. | Declare the caller with `T: copyable` if it needs copyable APIs. A `T: copyable` may flow to `T: any`, not the reverse. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifetime_generic_boundaries_valid.camp`; `tests/Diagnostics/lifetime_generic_boundaries_invalid.camp`; `spec::6.2.6` |
| Lowercase namespace names such as `export as std::math;` or `using std;`. | Use PascalCase namespace segments: `export as Std::Math;`, `using Std;`. | `SPEC_ONLY_OR_UNVERIFIED` | `spec::5.1` |
| User-defined `params` declarations or `params(T)` type syntax. | Built-in arrays/options/delegates/iter or `struct(T)`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildTypeDefinition`; `BuildTypeReference` |

## 14. LLM Generation Checklist

Before emitting Camp code:

1. Pick only compiler-confirmed features unless the task explicitly targets spec-level design work; do not borrow syntax from C/C++/C#.
2. Use explicit pointer types (`T*`) and dot member access (`ptr.member`), never `->`.
3. Use PascalCase namespace segments: `using Std;`, `using Std::Math;`, `export as Std::Math;`.
4. Avoid ordinary overloads. If same invoker name is needed, make a valid `overload` family.
5. Keep expanded forms in mind:
   - `T[]` has `.elements` and `.length` and is a span-like value.
   - `T?` has `.value` and `.specified`.
   - `delegate`/`iter` have `.call` and `.context`.
6. Keep fixed-size arrays separate from spans:
   - Declare storage as `fixed T[n] name`.
   - Use `T[n]*` for a pointer to one fixed array object.
   - Use `T[]` for a span view.
   - Do not copy fixed arrays by value.
7. Fixed-size arrays may be initialized or overwritten only by target-typed initializer patterns, compatible string literals (`char[n]`, `wchar[n]`, `achar[n]`), or `default`; do not write `a = b` for fixed arrays.
8. For a pointer to a fixed-size array, explicitly dereference before element indexing or slicing: `(*p)[0]`, not `p[0]`.
9. `sizeof(T[n])` is valid; `sizeof(int[8])` is 32. Generic copying needs `T: copyable` plus `sizeof(T)` when erased lowering needs size.
10. Use `T: any` only for non-copying generic code. Use `T: copyable` for lists, buffers, sorting, copying, moving, compacting, returning, assigning, or storing `T` values.
11. A `T: copyable` argument can satisfy `T: any`; a merely `T: any` argument cannot satisfy `T: copyable`.
12. `sizeof(T)` under `T: any` permits enumeration, pointer indexing, size-based allocation, and default-fill, but never `T` value copying.
13. Use callable `newtype` ascription only when the declaration family matches: receiverless declarations use `fn`; receiver-bearing methods use `delegate` or `iter`.
14. For context-carrying callable newtypes, explicit callable `this` qualifiers are part of the contract. An ascribed method may omit `this` and inherit them; if it writes explicit `this`, the qualifiers must match.
15. In an `escaped class`, preserve an ascribed method's escaped receiver contract explicitly with `escaped this` on the callable newtype or on the method.
16. Do not rely on callable ascription to change direct calls, overload groups, unbound method references, generated symbols, default-argument insertion, or ABI lowering.
17. Use lambdas only where a callable target type is known. If a lambda has captures, target a `delegate`, not `fn`.
18. Do not use lambda arguments to select an overload family; call the typed overload entry explicitly.
19. For escaped delegate lambdas, capture only escaped pointer-bearing values, treat captured values as read-only, and delete owned `delegate.context` pointers when done.
20. Use preprocessor directives intentionally:
   - Put `#build` and `#within` only in the file prelude, before ordinary declarations and imports.
   - Use `#define`/`#if` for conditional compilation, not runtime branching.
   - When documenting or generating examples with `#build`, say that the shown flags are compiler-tooling examples rather than language-specified syntax.
   - Use `#within explicit` for files that should make heap allocation/deallocation choices visible, and `within (default)` when fallback allocation is intentional.
21. Use `within (allocator)` around escaped lambda creation when the context should be allocated through that allocator.
22. For `CharFormatter`, the returned required count includes the trailing null terminator; allocate `formatter()` characters, not `formatter() + 1`.
23. Do not create arrays of expanded values. Use `struct(T)` materialization.
24. For classes with virtual methods, mark the class `virtual` or `abstract`; derived virtual-class children must be `virtual`, `abstract`, or `sealed`.
25. Interfaces contain signatures only. Implement every interface method exactly.
26. Constructors/destructors must match the containing type name and have no return type.
27. Use `init T(...)` for existing storage and `new T(...)` for allocation. Pair owned values/pointers with `delete` or `finally delete`.
28. For `extern class`, write pointer-oriented helper APIs. Constructors/destructors must be `extern`; ordinary methods may be Camp-side helpers. Never generate instance fields, `op_initnew`, direct value storage, or arrays of direct extern-class values. Interface contracts may be listed only to import the foreign type's generated interface accessor surface.
29. In generator bodies, do not use `init T[n]` stack-array allocation; declare `fixed T[n]` storage instead when fixed state storage is needed.
30. Use `within (allocator)` when constructors/destructors declare `within` allocator parameters.
31. Pointer-form `delete` is valid only when the selected allocator `free` method or fallback `free(...)` accepts the pointer under ordinary type/lifetime conversion rules.
32. For thrown errors, declare `thrown E error`, call with `catch error`, or catch/rethrow explicitly.
33. Use `foreach (T item in arrayOrIterator)` only for arrays, iterator protocols, or iterator states with `next`.
34. Prefer top-level type declarations; do not nest types.
35. Avoid reserved words and generated component-name collisions such as `items`, `items_length`, `callback_context`.
36. If exporting C ABI, use `export`, `public`, `extern`, and optionally `@symbol("name")`; verify generated symbol names if overloads or methods are involved.
