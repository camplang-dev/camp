# Camp Proposal: Default Interface Method Implementations and Optional Methods

**Status:** Draft proposal  
**Date:** 2026-06-23  
**Applies to:** Camp unified spec v17, especially interface semantics in §2.4 and callable/reference rules in §1.3, §1.4, and §3.4  
**Related guide impact:** `CAMP_LLM_CODE_GUIDE.md` interface, callable reference, metadata, and diagnostics guidance

## 1. Summary

This proposal adds a small syntax extension to interface method declarations:

```camp
interface IThing
{
	int doSomethingOpt(int c) = null;
	string getValue(int x) = getValueDefault;
}

string getValueDefault(IThing* this, int x)
{
	return "";
}
```

An interface method may now declare a **vtable initializer** after its signature.

- A method with no initializer remains required.
- A method initialized with a function is defaulted.
- A method initialized with `null` or `default` is optional.

The initializer determines the vtable slot value used when an implementing type omits that method. The slot itself always exists. The feature does not add interface method bodies, wrappers, dynamic lookup, metadata dispatch, or runtime fallback behavior.

Optional methods are ordinary nullable function-pointer slots. Calling an optional method through a null slot has the same failure mode as calling any other null function pointer. Camp does not insert a null check.

```camp
IThing* thing = getThing();

if (thing.doSomethingOpt != null)
{
	auto result = thing.doSomethingOpt(1);
}
```

## 2. Motivation

Camp interfaces are already ABI-visible nominal vtable contracts. A source interface method conceptually lowers to a plain function pointer field in the interface vtable, and that function pointer receives the interface-instance slot as its first argument.

That model makes optional and defaulted interface methods a natural extension. A default implementation is simply the vtable slot value used when the implementing type does not supply its own entry. An optional method is simply a null vtable slot.

This is useful for several cases:

- capability-style APIs where some operations are only available on some implementations;
- low-level interop surfaces where nullable callback slots are normal;
- lightweight versioning of interface-like contracts without forcing every implementer to write boilerplate;
- default helper behavior expressed as ordinary ABI-visible functions;
- avoiding secondary `tryGetX` / `supportsX` methods when the function pointer slot itself is the capability check.

The design intentionally keeps the risk visible. Optional methods are not safe optionals. They are nullable function pointers in interface vtables.

## 3. Design Goals

The feature should:

1. Preserve Camp's current explicit interface representation.
2. Keep interface vtable layout stable and ABI-visible.
3. Avoid hidden allocation, wrappers, delegates, bound receiver objects, and runtime metadata lookup.
4. Let optional methods be checked by comparing the vtable slot to `null`.
5. Let default implementations be ordinary static or out-of-scope functions.
6. Preserve nominal conformance.
7. Keep existing required-interface behavior for declarations that do not opt into this feature.

The feature should not:

1. Introduce interface method bodies.
2. Introduce C#-style runtime dispatch to interface bodies.
3. Make optional calls safe by default.
4. Infer that a method is optional from naming or from nullable return types.
5. Treat default implementations as instance methods of the implementing class.
6. Generate adapters merely to make an almost-compatible function fit.

## 4. Terminology

### Required interface method

An interface method without a vtable initializer is required.

```camp
interface IThing
{
	int required(int value);
}
```

A type implementing the interface must provide a matching implementation in the type body, unless an inherited implementation already satisfies the existing class/interface rules.

### Defaulted interface method

An interface method initialized with a function target is defaulted.

```camp
interface IThing
{
	string getValue(int x) = getValueDefault;
}
```

An implementing type may omit the method. If it omits the method, the generated implementation vtable uses the named default function as the slot value.

### Optional interface method

An interface method initialized with `null` or `default` is optional.

```camp
interface IThing
{
	int doSomethingOpt(int c) = null;
	int doAnotherThingOpt(int c) = default;
}
```

An implementing type may omit the method. If it omits the method, the generated implementation vtable stores a null function pointer for that slot.

`null` and `default` are semantically equivalent in this position. `null` is clearer when the intent is optionality.

## 5. Syntax

Interface method declarations gain an optional vtable initializer:

```camp
interface IExample
{
	ReturnType required(params);
	ReturnType optional(params) = null;
	ReturnType alsoOptional(params) = default;
	ReturnType defaulted(params) = defaultFunction;
}
```

A compact grammar sketch:

```text
InterfaceMethodDeclaration
    := InterfaceMethodSignature InterfaceMethodVTableInitializer? ';'

InterfaceMethodVTableInitializer
    := '=' InterfaceMethodVTableInitializerValue

InterfaceMethodVTableInitializerValue
    := 'null'
     | 'default'
     | RestrictedFunctionReference
```

`RestrictedFunctionReference` is a compile-time function reference expression. It may name:

- a free function;
- an out-of-scope receiver function;
- a static method;
- a namespace-qualified function or static method.

Examples:

```camp
interface IThing
{
	string a(int x) = getValueDefault;
	string b(int x) = Defaults.getValue;
	string c(int x) = MyLibrary::Defaults.getValue;
}
```

This proposal does not allow arbitrary expressions in the initializer. These are invalid:

```camp
interface IThing
{
	string a(int x) = () => "";              // ERROR: lambda
	string b(int x) = someObject.getValue;   // ERROR: bound method
	string c(int x) = createDefault();       // ERROR: call expression
	string d(int x) = someDelegate;          // ERROR: delegate value
}
```

## 6. Slot Function Type

For an interface method declared directly in interface `I`:

```camp
interface I
{
	R m(P1 p1, P2 p2);
}
```

the source-level vtable slot function type is:

```camp
fn R(I* this, P1 p1, P2 p2)
```

The lowered shape continues to follow the existing interface representation. Since source `I*` is the interface-instance pointer form, the lowered first parameter has the interface slot pointer shape:

```camp
fn R(I** ctx, lowered(P1), lowered(P2))
```

The first parameter belongs to the vtable slot. It is not written in the interface method declaration itself.

## 7. Default Implementation Target Matching

A default implementation target must match the interface slot function type exactly.

For:

```camp
interface IThing
{
	string getValue(int x) = getValueDefault;
}
```

the target must have the source-level function type:

```camp
fn string(IThing* this, int x)
```

A valid out-of-scope receiver function:

```camp
string getValueDefault(IThing* this, int x)
{
	return "";
}
```

A valid static method:

```camp
class IThingDefaults
{
	static string getValue(IThing* thing, int x)
	{
		return "";
	}
}

interface IThing
{
	string getValue(int x) = IThingDefaults.getValue;
}
```

A valid namespace-qualified target reference:

```camp
interface IThing
{
	string getValue(int x) = MyDefaults::getValue;
}
```

Here `MyDefaults::getValue` must resolve to a visible free function, out-of-scope receiver function, or static method with the exact slot signature. The namespace-qualified reference does not change the matching rule.

Invalid targets:

```camp
string missingReceiver(int x)
{
	return "";
}

interface IThing
{
	string getValue(int x) = missingReceiver; // ERROR
}
```

```camp
class ThingImpl
{
	string instanceDefault(int x)
	{
		return "";
	}
}

interface IThing
{
	string getValue(int x) = ThingImpl.instanceDefault; // ERROR: instance method
}
```

```camp
string wrongReceiver(ConcreteThing* this, int x)
{
	return "";
}

interface IThing
{
	string getValue(int x) = wrongReceiver; // ERROR: receiver is not IThing*
}
```

The check is exact after normal Camp signature normalization:

- return type must match exactly;
- parameter count must match exactly after adding the interface receiver slot;
- parameter types must match exactly;
- `const`, `in`, `out`, `within`, `thrown`, lifetime annotations, and target-specific callable/type specifiers must match according to the same rules used for interface method contract matching;
- expanded parameter forms must match as source-level forms;
- generic parameters and hidden `sizeof(T)` / `vtableof(T: I)` parameters must match when present;
- default argument values on the target do not participate in matching;
- ordinary parameter names do not need to match, except that an out-of-scope receiver method must use `this` because that is how Camp declares receiver methods.

No adapter function is generated merely to satisfy this check. If the target's natural function reference is not exactly compatible with the vtable slot, the interface declaration is invalid.

## 8. Conformance Rules

When a type declares that it implements an interface, each interface method is classified as required, defaulted, or optional.

| Interface declaration | Implementer may omit? | Vtable slot when omitted |
|---|---:|---|
| `R m(P...);` | no | invalid conformance |
| `R m(P...) = target;` | yes | `target` |
| `R m(P...) = null;` | yes | `null` |
| `R m(P...) = default;` | yes | `null` |

If the implementing type provides a matching implementation method, that implementation is used instead of the default or null initializer.

```camp
interface IThing
{
	string getValue(int x) = getValueDefault;
}

string getValueDefault(IThing* this, int x)
{
	return "default";
}

class BasicThing: IThing
{
	// OK: getValue omitted; vtable slot uses getValueDefault.
}

class CustomThing: IThing
{
	string getValue(int x)
	{
		return "custom";
	}
}
```

There is no syntax in this proposal for an implementing type to explicitly re-null a defaulted method. If the type declares a matching implementation, the implementation is used. If the type omits the method, the interface initializer is used.

## 9. Interface Calls and Optional Slot Checks

Calling through an interface pointer continues to use the existing interface-call model.

```camp
IThing* thing = getThing();
string value = thing.getValue(10);
```

A call does not insert a null check, even when the interface method is optional.

```camp
IThing* thing = getThing();
int result = thing.doSomethingOpt(1); // allowed; crashes if slot is null
```

The programmer may explicitly test the slot:

```camp
if (thing.doSomethingOpt != null)
{
	int result = thing.doSomethingOpt(1);
}
```

The condition is an ordinary boolean comparison. Camp still does not treat pointers or function pointers as implicit booleans.

```camp
if (thing.doSomethingOpt) // ERROR: not a bool condition
{
}
```

### 9.1 Method reference expression on an interface pointer

When `thing` has type `IThing*`, and `doSomethingOpt` names an interface method, the expression:

```camp
thing.doSomethingOpt
```

outside call syntax reads the current vtable slot function pointer. Its source-level type is:

```camp
fn int(IThing* this, int c)
```

That function pointer can be compared to `null`.

If the function pointer is stored in a local, the receiver must be supplied explicitly when calling the local:

```camp
fn int(IThing* this, int c) slot = thing.doSomethingOpt;

if (slot != null)
{
	int result = slot(thing, 1);
}
```

The direct member-call form remains special interface-call syntax and supplies the interface receiver automatically:

```camp
thing.doSomethingOpt(1);
```

### 9.2 Bare interface vtable values

The same slot check is valid through a bare interface vtable value.

```camp
IThing vt = SomeThing_IThing;
IThing* thing = getThing();

if (vt.doSomethingOpt != null)
{
	int result = vt.doSomethingOpt(thing, 1);
}
```

This follows the existing rule that calling through bare `IFoo` requires the interface-instance pointer to be supplied explicitly.

## 10. Lowering Examples

### 10.1 Interface declaration

Source:

```camp
interface IThing
{
	int doSomethingOpt(int c) = null;
	string getValue(int x) = getValueDefault;
}

string getValueDefault(IThing* this, int x)
{
	return "";
}
```

Conceptual vtable shape:

```camp
struct IThing
{
	fn int(IThing** ctx, int c) doSomethingOpt;
	fn string(IThing** ctx, int x) getValue;
}
```

The shape is the same as it would be without initializers. The initializer affects only the value placed in an implementation vtable when an implementation method is omitted.

### 10.2 Class omits both methods

Source:

```camp
class BasicThing: IThing
{
}
```

Conceptual generated vtable:

```camp
IThing BasicThing_IThing =
{
	.doSomethingOpt = null,
	.getValue = getValueDefault,
};
```

### 10.3 Class overrides defaulted method

Source:

```camp
class CustomThing: IThing
{
	string getValue(int x)
	{
		return "custom";
	}
}
```

Conceptual generated vtable:

```camp
IThing CustomThing_IThing =
{
	.doSomethingOpt = null,
	.getValue = CustomThing_getValue,
};
```

If the interface slot requires a fixup thunk because of the class layout, the existing interface thunk rules apply for concrete implementation methods. Default implementation targets do not need class fixup thunks because they already receive the interface-instance pointer.

### 10.4 Class implements optional method

Source:

```camp
class ActionThing: IThing
{
	int doSomethingOpt(int c)
	{
		return c + 1;
	}
}
```

Conceptual generated vtable:

```camp
IThing ActionThing_IThing =
{
	.doSomethingOpt = ActionThing_doSomethingOpt,
	.getValue = getValueDefault,
};
```

### 10.5 Struct implementation

Source:

```camp
struct SliceThing: IThing
{
	int doSomethingOpt(int c)
	{
		return c * 2;
	}
}
```

The existing scoped indirect interface adapter model is preserved. The generated struct adapter vtable uses the concrete struct implementation for `doSomethingOpt` and the interface default function for `getValue`.

```camp
IThing SliceThing_IThing =
{
	.doSomethingOpt = SliceThing_IThing_doSomethingOpt,
	.getValue = getValueDefault,
};
```

If the struct omits `doSomethingOpt`, the adapter vtable stores `null` for that slot.

## 11. Interface Inheritance

Inherited interface slots keep their initializers.

```camp
interface IReadable
{
	nuint read(byte[] buffer);
	bool getEndOfFile() = defaultEndOfFile;
}

bool defaultEndOfFile(IReadable* this)
{
	return false;
}

interface ISeekable: IReadable
{
	void seek(nuint position) = null;
}
```

A type implementing `ISeekable` must still implement `read`, because `read` is required. It may omit `getEndOfFile` and `seek`.

The receiver type of an inherited default target remains the interface that declared the slot. In this example, `defaultEndOfFile` receives `IReadable*`, not `ISeekable*`.

Flattened vtable layout is unchanged. Optional and defaulted inherited slots occupy the same positions they would occupy if they were required.

Diamond inheritance follows the existing interface inheritance rule: the same base contract is not duplicated. The initializer belongs to the unique inherited slot.

This proposal does not add a new redeclaration mechanism for changing the initializer of an inherited method. If Camp later supports explicit interface slot redeclaration, that feature should define whether a derived interface may replace a base initializer.

## 12. Generic Interfaces and Generic Methods

Defaulted and optional methods are valid in generic interfaces and on generic interface methods, subject to exact signature matching.

Example:

```camp
interface IFormatter<T: any>
{
	nuint format(in T value, char[] buffer, sizeof(T)) = formatDefault<T>;
}

nuint formatDefault<T: any>(IFormatter<T>* this, in T value, char[] buffer, sizeof(T))
{
	return 0;
}
```

The default target must have a generic parameter list and hidden generic support parameters compatible with the slot being initialized. This proposal does not add partial generic inference or adapter generation for default implementation targets.

For generic interface methods, the target must match the method's generic parameter list in the same declaration-relative way.

```camp
interface ITransformer
{
	TOut transform<TIn: any, TOut: any>(in TIn value, sizeof(TIn), sizeof(TOut)) = transformDefault;
}
```

A target that cannot be resolved to the exact generic slot shape is invalid.

## 13. Interaction with `vtableof(T: Interface)`

Generic dispatch through `vtableof(T: Interface)` uses the same vtable object that ordinary interface dispatch uses.

That means optional and defaulted entries naturally travel through `vtableof`.

- If an implementation supplies the method, the vtable entry points to that implementation path.
- If the interface method is defaulted and the implementation omits it, the vtable entry points to the default function.
- If the interface method is optional and the implementation omits it, the vtable entry is null.

`T: implements IFoo` remains a nominal constraint. It does not imply that every slot in `IFoo` is non-null. Code that requires a non-null operation should use a stricter interface or check the slot.

## 14. Interaction with Default Arguments

Default arguments on the interface method belong to the interface-call surface.

```camp
interface IThing
{
	string getValue(int x = 0) = getValueDefault;
}

string getValueDefault(IThing* this, int x)
{
	return "";
}

void sample(IThing* thing)
{
	string value = thing.getValue(); // uses interface method default argument
}
```

Default arguments on the default implementation target do not affect calls through the interface.

```camp
string getValueDefault(IThing* this, int x = 123)
{
	return "";
}
```

The target's default value is irrelevant to the interface slot. The target must still be callable with the exact slot argument list.

## 15. Interaction with Lifetimes and Receiver Qualifiers

Interface method receiver lifetime annotations and ordinary parameter lifetime annotations remain part of the interface contract.

If an interface method has receiver annotations, the default implementation target must match the resulting slot signature.

```camp
interface IThing
{
	string getName(scoped this) = getNameDefault;
}

string getNameDefault(scoped IThing* this)
{
	return "";
}
```

If the exact spelling above does not match Camp's final receiver-annotation syntax for out-of-scope interface receiver methods, the compiler should enforce the same normalized qualifier set it already uses for interface implementation compatibility.

The default target may not weaken lifetime requirements or strengthen caller obligations relative to the interface method contract.

## 16. Metadata Impact

Metadata should preserve the fact that an interface method has a vtable initializer.

A suggested function metadata extension:

```json
{
  "name": "doSomethingOpt",
  "returnType": "int",
  "parameters": [
    { "name": "c", "type": "int" }
  ],
  "interfaceSlotInitializer": {
    "kind": "null",
    "source": "null"
  }
}
```

For a default function target:

```json
{
  "name": "getValue",
  "returnType": "string",
  "parameters": [
    { "name": "x", "type": "int" }
  ],
  "interfaceSlotInitializer": {
    "kind": "function",
    "source": "getValueDefault",
    "target": "getValueDefault",
    "targetRef": "function:MyModule::getValueDefault",
    "targetSymbol": "IThing_getValueDefault"
  }
}
```

For `= default`, metadata may preserve the source spelling while normalizing the semantic kind to null:

```json
{
  "interfaceSlotInitializer": {
    "kind": "null",
    "source": "default"
  }
}
```

Consumers should not treat `interfaceSlotInitializer.kind == "null"` as an optional return value or as a `T?` optional. It describes the vtable slot initializer only.

## 17. Diagnostics

The compiler should report an error when:

1. A type omits a required interface method.
2. An interface method initializer is used on a non-interface declaration.
3. An interface constructor or destructor uses this initializer syntax.
4. The initializer expression is not `null`, `default`, or a restricted function reference.
5. A default target resolves to an overload group rather than one exact function.
6. A default target is a bound method, instance method, delegate value, lambda, call expression, or other context-carrying callable value.
7. A default target's signature does not exactly match the interface slot function type.
8. A default target requires an adapter, conversion, thunk, or default argument insertion to match.
9. A generic default target cannot be matched exactly to the generic interface slot.
10. A programmer attempts to use an optional slot as a boolean condition without comparison.

Examples:

```camp
interface IThing
{
	int required();
	int optional() = null;
}

class MissingRequired: IThing
{
	// ERROR: required omitted.
	// OK: optional omitted.
}
```

```camp
int defaultImpl(int x)
{
	return x;
}

interface IThing
{
	int optional(int x) = defaultImpl; // ERROR: missing IThing* receiver
}
```

```camp
interface IThing
{
	int optional(int x) = null;
}

void sample(IThing* thing)
{
	if (thing.optional) // ERROR: condition is not bool
	{
	}
}
```

A compiler may optionally warn on direct calls to methods whose declaration initializer is `null` or `default`, but this proposal does not require such a warning. Mandatory flow-sensitive null checking is explicitly out of scope.

## 18. Implementation Sketch

### Parser

- Extend interface method parsing to accept `= initializer` before the terminating semicolon.
- Permit this only for ordinary interface method signatures.
- Reject it for interface fields, constructors, destructors, type declarations, or non-interface methods.
- Parse the initializer as a restricted function-reference expression or as the keywords `null` / `default`.

### Binding

- Store the optional vtable initializer on the bound interface method declaration.
- Resolve `null` and `default` as null slot initializers.
- Resolve function-reference initializers in the interface declaration context.
- Allow forward references to the same degree ordinary function references allow them.

### Semantic analysis

- Compute the source-level slot function type by adding the declaring interface pointer as the first parameter.
- Validate that a function target's natural unbound callable reference exactly matches that slot type.
- Reject context-carrying callable values.
- Preserve existing interface method contract checks for concrete implementations.

### Interface conformance

For each slot:

1. If the implementing type supplies a matching method, use the existing concrete implementation path.
2. Otherwise, if the interface slot has a function initializer, use that function pointer.
3. Otherwise, if the interface slot has a null/default initializer, use null.
4. Otherwise, report a missing required member diagnostic.

### Lowering and C emission

- Vtable shape is unchanged.
- Vtable field initializers may now be null or a default function symbol.
- Concrete implementation entries continue to use direct method pointers or fixup thunks as required by the existing class/struct interface lowering rules.
- Default implementation entries should not use concrete class fixup thunks because they already receive the interface-instance pointer.

### Member access and invocation

- Preserve existing interface-call lowering for `iface.method(args)`.
- Add or confirm interface slot member-reference behavior for `iface.method` without `()`.
- The slot reference reads the current vtable function pointer and can be compared to `null`.
- If the slot reference is stored as a `fn`, later calls to that `fn` must pass the interface receiver explicitly.

## 19. Test Plan

Suggested tests:

1. Parse `interface I { int f() = null; }`.
2. Parse `interface I { int f() = default; }`.
3. Parse `interface I { int f() = fDefault; }`.
4. Reject initializer syntax outside interface method declarations.
5. Reject initializer syntax on interface constructors and destructors.
6. Accept a class that omits an optional method.
7. Accept a class that omits a defaulted method.
8. Reject a class that omits a required method.
9. Verify a provided concrete implementation overrides the default initializer.
10. Verify a provided concrete implementation overrides a null optional slot.
11. Verify generated vtable entries contain null for omitted optional methods.
12. Verify generated vtable entries contain the default function for omitted defaulted methods.
13. Verify struct interface adapters use the default/null entry when the struct omits the method.
14. Verify inherited interface slots preserve their default/null initializer.
15. Verify `thing.optional != null` compiles.
16. Verify `if (thing.optional)` is rejected.
17. Verify `thing.optional(1)` compiles without an inserted null check.
18. Verify storing `fn R(I* this, P...) slot = thing.optional;` and calling `slot(thing, args...)` works.
19. Reject default target with missing interface receiver.
20. Reject default target with wrong receiver type.
21. Reject bound instance method target.
22. Reject delegate/lambda/call-expression initializer.
23. Reject ambiguous overload target.
24. Verify default arguments on the interface method still apply to interface calls.
25. Verify default arguments on the target function do not affect interface calls.
26. Verify metadata JSON includes the interface slot initializer.

## 20. Backward Compatibility

This feature is backward compatible with existing Camp source.

Existing interface methods without initializers remain required. Existing implementations remain valid or invalid under the same rules as before, except where a source file opts into the new initializer syntax.

The ABI shape of an interface does not change merely because a method becomes optional or defaulted. The same vtable slot exists in the same position. Only the slot value chosen for a particular implementation pair changes.

## 21. Rationale

### Why `= target` instead of method bodies?

Camp interfaces are nominal vtable shapes, not abstract classes and not hidden runtime objects. A vtable initializer expresses the actual mechanism directly: the slot receives a function pointer value.

```camp
string getValue(int x) = getValueDefault;
```

is more ABI-honest than:

```camp
string getValue(int x)
{
	return "";
}
```

because the latter suggests a method body living inside the interface, while the actual mechanism is a function pointer stored in implementation vtables.

### Why allow `null`?

Optional vtable slots are a common low-level representation. Camp already exposes function pointers and explicit null comparisons. Allowing `= null` makes that ABI shape visible without inventing a separate optional-method system.

### Why also allow `default`?

`default` is Camp's ordinary all-default value. For a function pointer slot, the all-default value is null. Accepting `default` makes the initializer consistent with other default-value contexts, while `null` remains the recommended spelling for optional methods.

### Why not require a null check before calls?

Camp generally does not turn raw pointer operations into checked operations. Optional interface methods are intentionally as risky as nullable function pointers. The surface makes the risk obvious and the check straightforward:

```camp
if (thing.method != null)
{
	thing.method();
}
```

A future linter may warn about unchecked calls to optional slots, but that is not a core language requirement.

## 22. Proposed Spec Edit Summary

Replace the current §2.4.8 rule that every declared interface entry is required with a new section titled:

```text
2.4.8 Required, defaulted, and optional interface entries
```

The replacement section should state:

- interface methods without initializers are required;
- interface methods with function initializers are defaulted;
- interface methods with `null` or `default` initializers are optional;
- the vtable slot always exists;
- the initializer controls the slot value only when the implementing type omits the method;
- default implementation targets must be exact static or out-of-scope function targets;
- optional calls perform no implicit null check.

Add a short note to the interface call/member-reference section explaining that member access to an interface method without invocation reads the vtable slot function pointer and may be compared to `null`.

Add a short note to the LLM code guide interface section:

```text
Interface methods may declare vtable initializers: `= null`, `= default`, or `= functionTarget`. Missing implementations are valid only for methods with such initializers. `null`/`default` make the slot optional. Calls through optional slots do not check for null; compare `iface.method != null` first when needed.
```

