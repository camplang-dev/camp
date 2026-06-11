# Camp LLM Code Guide

This guide describes Camp source accepted by the current compiler implementation. Do not infer missing features from C, C++, C#, or similar syntax.

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
| Arrays, optionals, delegates, and iterators are compiler-expanded forms. They have dot components such as `.elements`, `.length`, `.value`, `.specified`, `.call`, `.context`. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `tests/CCompile/expanded_return_iter_assignment.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Use `struct(T)` to materialize an expanded form when it must be a single stored value, especially arrays of expanded values. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Construction/destruction is explicit: `init T(...)`, `new T(...)`, `delete valueOrPointer`, and `finally delete expr`. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `tests/StdRun/pointer_new_array_finally_delete.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Operators.cs` |
| Error handling is explicit: thrown values use `thrown T` parameters, `throw value`, `try`/`catch`, and call-site `catch variable`. | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp`; `tests/Lowering/throw_try_finally.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs` |
| Camp is C ABI-oriented. `extern`, `export`, `public`, `@symbol`, generated C symbols, `void*`, `sizeof`, and flattened method names matter. | `CONFIRMED_BY_TEST` | `tests/CEmit/*.camp`; `tests/Api/*.camp`; `src/Camp.Compiler/CCodeEmitter.cs`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs` |

## 2. Lexical and Formatting Rules

| Form | Accepted | Confidence | Evidence |
|---|---|---|---|
| Identifier | ASCII letter or `_`, followed by ASCII letters, digits, or `_`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::IsIdentifierStart`, `IsIdentifierPart` |
| Attribute identifier | `@` followed by identifier start and identifier parts, e.g. `@range`, `@symbol("c_name")`. | `CONFIRMED_BY_TEST` | `tests/CCompile/slice_range_calls.camp`; `src/Camp.Compiler/CampTokenizer.cs::Tokenize` |
| Reserved words | Cannot be used as ordinary identifiers; includes `_`, `abstract`, `alias`, `any`, `astring`, `async`, `auto`, `bool`, `class`, `delegate`, `escaped`, `extern`, `fixed`, `fn`, `foreach`, `implements`, `init`, `interface`, `iter`, `newtype`, `once`, `overload`, `params`, `scoped`, `thrown`, `unscoped`, `vtableof`, `within`, etc. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.cs::ReservedWords`; `src/Camp.Compiler/CampParser.cs::IsKeyword` |
| Line comment | `//` to end of line. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Tokenize` |
| Block comment | `/* ... */`, tokenized line by line; can span lines. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadBlockCommentLine` |
| Strings | `"..."`, `'...'`, or `` `...` `` are tokenized as string-class literals; backslash escapes consume next char. Single-quoted string with exactly one decoded char is a character literal. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadString`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildLiteralExpression` |
| Invalid char literal | `'ab'` is rejected as a character literal because character literals must contain exactly one character. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildLiteralExpression` |
| Numbers | Decimal integers/floats; `0x`/`0X` hex accepts ASCII letters/digits; decimal literals may end with ASCII letter suffixes. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::ReadNumber` |
| Punctuation/operators | Single-character symbols include `~ ! % ^ & * ( ) + - = { } [ ] | ; : , . / < > ? $ #`. Multi-character operators are parsed from token sequences. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Punctuation`; `src/Camp.Compiler/CampParser.cs::ReadOperator` |
| Whitespace/newline | Horizontal whitespace and newlines are separate trivia tokens. Statements and declarations still normally require `;` or braces. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Tokenize`; `src/Camp.Compiler/CampParser.cs` |

Valid:

```camp
// line comment
/* block
   comment */
int value_1 = 0x10;
char c = 'x';
const char[] text = "hello";
```

Invalid:

```camp
char bad = 'xy';     // character literal must contain exactly one character
int class = 1;       // reserved word
```

## 3. Declarations

### Declaration Overview

| Declaration | Syntax | Confidence | Evidence |
|---|---|---|---|
| Function | `[export|public|extern|async] ReturnType name<T...>(params) bodyOr;` | `CONFIRMED_BY_TEST` | `tests/CEmit/basic_functions.camp`; `src/Camp.Compiler/CampParser.cs::ParseMemberDeclaration` |
| Expression-bodied function | `ReturnType name(params) => expr;` | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildFunctionBody` |
| Method | Declared inside `struct`, `class`, `enum`, `newtype`, or `params` body. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildStructDefinition`, `BuildClassDefinition`, `AddMethodOnlyScope` |
| Static method | `static ReturnType name(params) { ... }` inside type only. | `CONFIRMED_BY_TEST` | `tests/CCompile/overload_basic.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFunctionDeclarators` |
| Global variable | `[export|public|extern] Type name [= expr];` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildVariableDefinition` |
| Field | `Type name;` or `static Type name;` inside `struct`/`class`; fields cannot be `export`, `public`, `extern`, `virtual`, `override`, `sealed`, `abstract`, `async`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFieldDeclarators` |
| Struct | `[export|public|extern|fixed] struct Name[: bases] { ... }` | `CONFIRMED_BY_TEST` | `tests/Ast/basic_struct.camp`; `tests/CCompile/struct_interface_indirect.camp` |
| Class | `[export|public|extern|virtual|abstract|sealed|escaped] class Name[: baseOrInterfaces] { ... }` | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyClassDeclarators` |
| Interface | `[export|public|extern] interface Name[: interfaces] { methodSigs; }` | `CONFIRMED_BY_TEST` | `tests/CCompile/struct_interface_indirect.camp`; `tests/CCompile/overload_interface.camp` |
| Enum | `[export|public|extern] enum Name[: underlyingType] { A = 0, B }` | `CONFIRMED_BY_TEST` | `tests/CCompile/thrown_parameter_forwarding.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildEnumDefinition` |
| Newtype value | `newtype Name: numericOrPointerType;` | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildNewtypeDefinition`, `IsValidValueNewtypeUnderlying` |
| Newtype callable/iter | `newtype fn Ret Name(params);` or `newtype iter T Name(params);` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_return_iter_assignment.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildNewtypeDefinition` |
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
| Optional expanded form | `T?` | Components: `value: T`, `specified: bool`; direct `T??` rejected. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Function pointer | `fn Ret(params)` | Not expanded as params component. `null` can convert to `fn`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildCallableTypeReference`; `CanImplicitlyConvert` |
| Delegate | `delegate Ret(params)` | Expanded components: `call`, `context`. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddDelegatePendingComponents` |
| Once | `once Ret(params)` | Parsed/analyzed callable kind; expanded-form type. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypePrefix`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::IsExpandedFormType` |
| Async callable | `async Ret(params)` | Parsed/analyzed callable kind; async lowering is partial. Prefer avoiding unless tests require it. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildCallableTypeReference`; `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::IsAwaitable` |
| Iterator protocol | `iter T` or `iter(params)` | Expanded components: `call`, `context`; result slots can include one `thrown` after yielded slots. | `CONFIRMED_BY_TEST` | `tests/Ast/iter_type_protocol.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::ValidateIteratorType` |
| Generator return | `struct iter T f()` or `class iter T f()` | Function return modifier for generated iterator state. | `CONFIRMED_BY_TEST` | `tests/Lowering/iterator_generator_multiple_yields.camp`; `src/Camp.Compiler/BindableNodeBuilder.cs::GetIteratorKind` |
| Materialized expanded form | `struct(T[])`, `struct(T?)`, `struct(delegate Ret(...))`, `struct(iter T)` | Only valid for expanded array/optional/delegate/iter forms. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Thrown return form | `thrown(T)` | Parsed/analyzed as a type; flow treats return type `thrown(E)` as rethrow-compatible. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypePrefix`; `src/Camp.Compiler/BindableNodeAnalyzer.Flow.cs::GetFunctionThrownType` |
| Type declarators | `const T`, `volatile T`, `escaped T`, `scoped T`, `scoped(anchor) T`, `unscoped T`, `unscoped(anchor) T` | Prefix and postfix forms parse, but target-specific specifiers must appear after type forms. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypeDeclarator`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildDeclaratorTypeReference`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::ValidateTargetTypeSpec` |

Valid:

```camp
const char[] name;
int? maybe;
delegate int(int value) transform;
fn void(void* context) callback;
struct(int[]) storedArray;
scoped const int[] slice;
unscoped(owner) char* data;
```

Invalid:

```camp
int?? nested;      // optional values may not directly contain optional
int?[] values;    // arrays of expanded values are rejected; use struct(int?)[]
struct(int) x;    // struct(T) requires expanded array/optional/delegate/iter
params(int) p;    // params(T) type syntax is no longer supported
```

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
| Member access | `target.member` for values and pointers. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `tests/CCompile/overload_property_and_base.camp` |
| Property getter/setter | `obj.Value`, `obj.Value = x`, `obj.Value[arg]` map to `getValue`/`setValue` methods. | `CONFIRMED_BY_TEST` | `tests/Lowering/overload_property_and_base.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Expressions.cs::TryRewritePropertySetterAssignment` |
| Indexing | `arr[i]`, `ptr[i]`, `obj[indexArgs]` | `CONFIRMED_BY_TEST` | `tests/CCompile/array_literal_indexing.camp`; `tests/CCompile/slice_property_getter.camp` |
| Range | `start..end`, `..end`, `start..`, `..`; from-end uses prefix `^`. | `CONFIRMED_BY_TEST` | `tests/CCompile/array_range_index.camp`; `src/Camp.Compiler/CampParser.cs::ParseRangeOrAssignmentExpression` |
| Cast | `(Type)expr`, `(struct)expr`, `(class)expr`, `(params)expr` parsed; generate normal `(Type)expr` unless tests require special cast keyword. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::TryParseCastExpression` |
| Construction | `init T(args)`, `new T(args)`, `within (allocator) new T(args)`, array new `new T[count]`. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `tests/StdRun/within_new_array_expression.camp`; `src/Camp.Compiler/CampParser.cs::TryParseConstructionExpression` |
| Lambda | `(params) => expr` or `(params) => { ... }` parsed. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::TryParseLambdaExpression`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildLambdaExpression` |
| Await/postpone | `await expr`, `postpone expr` parse as unary prefix operators. Generate cautiously; async semantics are partially implemented. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::TryParseUnaryPrefix`; `src/Camp.Compiler/BindableNodeBuilder.Expressions.cs::BuildUnaryExpression`; `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.cs` |
| `sizeof` | `sizeof(T)` expression or hidden parameter `sizeof(T)` in parameter list. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_sizeof_field.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.SizeOf.cs` |
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
| Yield | `yield expr;` in iterator generators. | `CONFIRMED_BY_TEST` | `tests/Lowering/iterator_generator_multiple_yields.camp` |
| Throw | `throw expr;` | `CONFIRMED_BY_TEST` | `tests/Lowering/throw_try_finally.camp` |
| Try/catch/finally | `try { ... } catch (Err e) { ... } finally { ... }` | `CONFIRMED_BY_TEST` | `tests/CCompile/catch_argument_variable.camp`; `tests/Lowering/throw_try_finally.camp` |
| Within | `within (allocator) stmt` | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Statements.cs::RewriteStatement` |
| Delete | `delete expr;` | `CONFIRMED_BY_TEST` | `tests/CCompile/delete_string.camp`; `tests/CCompile/lifecycle_allocator.camp` |
| Finally delete expression | `finally delete expr` as unary expression. | `CONFIRMED_BY_TEST` | `tests/StdRun/pointer_new_array_finally_delete.camp`; `src/Camp.Compiler/CampParser.cs::ParseUnaryExpression` |
| Goto/labels | `label:`, `goto label;` parse/build. Avoid unless required. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.Statements.cs::BuildGotoStatement`, `BuildStatement` |

## 6. Compiler-Expanded Forms

| Source type | Components | Component access | Confidence | Evidence |
|---|---|---|---|---|
| `T[]` | `elements: T*`, `length: nuint` | `arr.elements`, `arr.length` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddArrayPendingComponents` |
| `T?` | `value: T`, `specified: bool` | `opt.value`, `opt.specified` | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddOptionalPendingComponents` |
| `delegate R(P...)` | `call: fn R(void*, P...)`, `context: void*` | `del.call`, `del.context`; `del(args...)` rewrites to call. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddDelegatePendingComponents`; `Lowering.Expressions.cs::TryRewriteDelegateInvocation` |
| `iter T` | `call: fn bool(void*, T*)`, `context: void*` | `it.call`, `it.context`; callable protocol. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_return_iter_assignment.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs::AddIteratorPendingComponents` |

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
| `fixed struct` is accepted; `fixed class` is rejected. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyStructDeclarators`, `ApplyClassDeclarators` |
| A class/struct may list one base class plus interfaces after `:`. More than one base class is rejected. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeClassOrStructBaseTypes` |
| Interfaces may derive only from interfaces. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::AnalyzeInterfaceBaseTypes` |
| Classes deriving from virtual/abstract classes must be `virtual`, `abstract`, or `sealed`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateClassVirtualMethods` |
| Override/sealed methods must match inherited virtual/abstract signatures. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateOverrideMethod` |
| Base method calls use `base.method(args)`. | `CONFIRMED_BY_TEST` | `tests/CCompile/virtual_interface_abi.camp`; `tests/Lowering/overload_property_and_base.camp` |
| `init T(...)` initializes storage; `new T(...)` allocates and initializes; `delete` invokes generated lifecycle cleanup/deallocation. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateInitNewMethod`, `CreateDeleteMethod`, `CreateDestroyMethod` |
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
| Type declarators `scoped`, `escaped`, and `unscoped` are implemented. `scoped(...)` and `unscoped(...)` can carry anchor identifiers. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampParser.cs::ParseTypeDeclarator`; `src/Camp.Compiler/BindableNodeBuilder.cs::BuildDeclaratorTypeReference` |
| Lifetime conversion is ordered: source lifetime must be at least target lifetime under the implementation's `Scoped < Unscoped < Escaped` enum. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeShapes.cs::LifetimeKind`, `QualifiersCanConvert` |
| `escaped` class declarator marks classes only; `escaped struct`/`escaped interface` are invalid. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyClassDeclarators`, `ApplyStructDeclarators`, `ApplyNonStructTypeDeclarators` |
| `within Allocator* allocator` may appear as a parameter modifier, and bare `within name` can be an implicit within parameter form. | `CONFIRMED_BY_TEST` | `tests/CCompile/lifecycle_allocator.camp`; `src/Camp.Compiler/CampParser.cs::ParseParameter` |
| `within (allocator) statement` or `within (allocator) new/init T(...)` supplies current allocator context. | `CONFIRMED_BY_TEST` | `tests/StdRun/within_new_array_expression.camp`; `src/Camp.Compiler/CampParser.cs::TryParseConstructionExpression` |
| Allocation requires accessible `malloc(nuint)`/`free(void*)` or allocator methods `alloc(nuint)`/`free(void*)`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.LoweringHelpers.cs::CreateAllocCallFromByteSize`, `CreateFreeCall` |

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
| Generic constraints use colon syntax: `<T: any>` or `<T: implements Interface>` depending on parsed `implements` keyword. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_erasure.camp`; `tests/Lowering/vtableof_generic_dispatch.camp`; `src/Camp.Compiler/CampParser.cs::ParseGenericParameter` |
| Generic parameter names must be unique. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Declarations.cs::AnalyzeGenericParameters` |
| Generic values constrained to `any` must be passed by reference. | `CONFIRMED_BY_TEST` | `tests/Diagnostics/overload_invalid.expected.txt`; `tests/CCompile/generic_erasure.camp` |
| `sizeof(T)` hidden parameter support is implemented for generic allocation/layout. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_new_sizeof_field.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.SizeOf.cs` |
| `vtableof(T: Interface)` requires generic parameter `T` to be constrained to implement that interface. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.VTableOf.cs::ValidateVTableOfRequest` |
| Generic types and functions lower by substitution/erasure patterns visible in C compile tests. | `CONFIRMED_BY_TEST` | `tests/CCompile/generic_erasure.camp`; `tests/CCompile/generic_scalar_erasure.camp`; `tests/CCompile/generic_iterator_param_erasure.camp` |

Valid:

```camp
class Box<T>
{
	Box() {}
}

void accept<T: any>(T* value)
{
}

int main()
{
	int value = 0;
	accept<int>(&value);
	auto box = new Box<string>();
	return box != null ? 0 : 1;
}
```

Invalid:

```camp
void accept<T: any>(T value) {} // generic any values must be by reference
```

## 11. Lowering Patterns Useful For LLMs

| Source pattern | Lowering/ABI pattern | Confidence | Evidence |
|---|---|---|---|
| Instance methods | Rewritten to C-style calls with receiver passed explicitly; extension symbols include flattened receiver type fragments. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.InstanceCalls.cs`; `src/Camp.Compiler/BindableNodeAnalyzer.TypeShapes.cs::BuildExtensionFunctionSymbol` |
| Static methods | Rewritten from `Type.method()` to direct symbol calls. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.Lowering.Expressions.cs::TryRewriteStaticMemberInvocation` |
| Overload selectors | Full callable name is invoker plus flattened selector type fragment, e.g. `writeInt`, `writeString`. | `CONFIRMED_BY_TEST` | `tests/CCompile/overload_basic.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.Overloads.cs::PrecomputeOverloadCallableName` |
| Constructors | User constructor produces generated `#init_new` and `create`/allocation helpers. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateInitNewMethod`, `CreateCreateMethod` |
| Destructors | User destructor produces generated delete/destroy helpers. | `INFERRED_FROM_IMPLEMENTATION` | `src/Camp.Compiler/BindableNodeAnalyzer.Expansion.cs::CreateDeleteMethod`, `CreateDestroyMethod` |
| Arrays | `T[]` lowers as components/pair: `T* elements`, `nuint length`. | `CONFIRMED_BY_TEST` | `tests/CEmit/primitive_flattened_symbols.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Optionals | `T?` lowers as value plus bool specified. | `CONFIRMED_BY_TEST` | `tests/CCompile/expanded_forms.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
| Delegates | `delegate` lowers as call pointer taking context first plus context pointer. | `CONFIRMED_BY_TEST` | `tests/CCompile/direct_function_delegate_thunk.camp`; `src/Camp.Compiler/BindableNodeAnalyzer.ParamsComponents.cs` |
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

### Namespace Export

```camp
export as MyLibrary;

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
| Using `->`. | Always use `.` for member access. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/CampTokenizer.cs::Punctuation`; no parser rule for `->` |
| Arrays of expanded values: `int?[]`, `char[][]` where element is expanded. | `struct(int?)[]`, `struct(char[])[]`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Direct nested optional `T??`. | `struct(T?)?` if nesting is required. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.TypeBinding.cs::AnalyzeType` |
| Interface fields or method bodies. | Interface method signatures only. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildInterfaceDefinition`, `BuildInterfaceFunctionDefinition` |
| Nested type declarations in structs/classes/interfaces/enums/newtypes/params. | Declare types at top level. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs` type builders |
| Bodyless non-extern methods outside interfaces/abstract methods. | Add body or mark `extern`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildFunctionDefinition` |
| `static` global functions/variables. | Omit `static` at top level. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::ApplyFunctionDeclarators`, `ApplyVariableDeclarators` |
| Virtual methods in non-virtual/non-abstract classes. | Mark class `virtual`/`abstract` or remove `virtual`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateClassVirtualMethods` |
| Abstract method with body. | Use `virtual` with body or `abstract` with semicolon. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.DeclarationValidation.cs::ValidateFunctionModifiers` |
| `foreach` over non-array/non-iterator source. | Use `T[]`, `iter T`, or iterator state with `bool next(T* outValue)`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType` |
| Async foreach. | Avoid; implementation reports async iterator foreach not implemented. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeAnalyzer.MethodBody.Semantics.cs::GetForeachElementType` |
| User-defined `params` declarations or `params(T)` type syntax. | Built-in arrays/options/delegates/iter or `struct(T)`. | `CONFIRMED_BY_COMPILER_CODE` | `src/Camp.Compiler/BindableNodeBuilder.cs::BuildTypeDefinition`; `BuildTypeReference` |

## 14. LLM Generation Checklist

Before emitting Camp code:

1. Pick only compiler-confirmed features; do not borrow syntax from C/C++/C#.
2. Use explicit pointer types (`T*`) and dot member access (`ptr.member`), never `->`.
3. Avoid ordinary overloads. If same invoker name is needed, make a valid `overload` family.
4. Keep expanded forms in mind:
   - `T[]` has `.elements` and `.length`.
   - `T?` has `.value` and `.specified`.
   - `delegate`/`iter` have `.call` and `.context`.
5. Do not create arrays of expanded values. Use `struct(T)` materialization.
6. For classes with virtual methods, mark the class `virtual` or `abstract`; derived virtual-class children must be `virtual`, `abstract`, or `sealed`.
7. Interfaces contain signatures only. Implement every interface method exactly.
8. Constructors/destructors must match the containing type name and have no return type.
9. Use `init T(...)` for existing storage and `new T(...)` for allocation. Pair owned values/pointers with `delete` or `finally delete`.
10. Use `within (allocator)` when constructors/destructors declare `within` allocator parameters.
11. For thrown errors, declare `thrown E error`, call with `catch error`, or catch/rethrow explicitly.
12. Use `foreach (T item in arrayOrIterator)` only for arrays, iterator protocols, or iterator states with `next`.
13. Prefer top-level type declarations; do not nest types.
14. Avoid reserved words and generated component-name collisions such as `items`, `items_length`, `callback_context`.
15. If exporting C ABI, use `export`, `public`, `extern`, and optionally `@symbol("name")`; verify generated symbol names if overloads or methods are involved.
