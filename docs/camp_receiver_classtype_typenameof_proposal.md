# Proposal: Receiver-Preserving Returns, Class-Relative Types, and Runtime Names

## Status

Draft proposal.

This proposal introduces three related language features:

1. `this` as a receiver-preserving method return type.
2. `classtype` as a class-relative self type inside class declarations.
3. `typenameof(...)` as a runtime string-producing intrinsic and explicit generic capability.

The features are independent, but they are designed to work together. They let APIs express receiver-preserving chains, class-relative factory results, and explicit runtime name capabilities without introducing hidden reflection objects or a general runtime type system.

## Goals

- Preserve the exact static receiver type across fluent/chaining methods.
- Allow class methods and instance methods to describe results relative to the type used at the call site.
- Provide a runtime `string` type-name intrinsic that follows Camp's existing ABI naming rules.
- Keep erased generic runtime support explicit, following the model already used by `sizeof(T)` and `vtableof(T: Interface)`.
- Avoid hidden metadata objects, runtime type objects, or dynamic reflection semantics.
- Keep ABI lowering predictable: every new source form has an ordinary lowered type or ordinary explicit parameter.

## Non-goals

This proposal does not introduce:

- runtime type objects;
- dynamic casts;
- hidden reflection metadata;
- automatic construction by name;
- field-level self types;
- callable `newtype` self-return signatures;
- interface self-return signatures;
- new ownership or allocation behavior.

---

# 1. `this` as a Return Type

## 1.1 Overview

An instance method may use plain `this` as its return type:

```camp
class Builder
{
	this append(const char[] text)
	{
		this.write(text);
		return this;
	}
}
```

A receiver extension method may also use `this` as its return type:

```camp
this logAndProceed(const MessageAccumulator* this)
{
	log(this);
	return this;
}
```

A method whose return type is `this` returns the receiver itself. At the call site, the returned expression has the exact same static type as the receiver expression used for the call.

That includes pointer shape, constness, lifetime annotations, and other receiver type qualifiers that participate in ordinary receiver typing.

Example:

```camp
MessageAccumulator* mutableAcc = ...;
const MessageAccumulator* constAcc = ...;

MessageAccumulator* a = mutableAcc.logAndProceed();
const MessageAccumulator* b = constAcc.logAndProceed();
```

Without a `this` return type, the method would need to declare one fixed return type, such as `const MessageAccumulator*`, and mutable callers would lose the more precise receiver type.

## 1.2 Valid declaration positions

Plain `this` may be used as the return type of an instance method of a concrete type.

This includes:

- methods declared inside `class` declarations;
- methods declared inside `struct` declarations;
- methods declared inside `enum` declarations, when enum methods are otherwise allowed;
- methods declared inside non-callable `newtype` declarations;
- out-of-scope receiver methods whose first parameter is named exactly `this`.

Examples:

```camp
struct Cursor
{
	nuint position;

	this advance(nuint count)
	{
		this.position += count;
		return this;
	}
}
```

```camp
newtype Counter: int
{
	this trace()
	{
		log((int)this);
		return this;
	}
}
```

```camp
this normalize(Rect* this)
{
	// extension-style receiver method
	return this;
}
```

## 1.3 Invalid declaration positions

Plain `this` may not be used as the return type of:

- free functions with no receiver;
- static methods;
- constructors;
- destructors;
- interface methods;
- callable `newtype` declarations;
- ordinary callable types such as `fn this(...)` or `delegate this(...)`.

Invalid:

```camp
this makeThing();                         // ERROR: no receiver
static this create();                     // ERROR: static methods have no receiver
newtype delegate this Formatter();        // ERROR: callable newtypes may not return `this`
interface IFluent { this next(); }        // ERROR: interface methods may not return `this`
```

## 1.4 `this` is not a type constructor

`this` as a return type is a special method return form. It is not a general type name.

Only plain `this` is allowed.

Invalid:

```camp
this* getPointer();          // ERROR
this[] getItems();           // ERROR
fn void(this) getCallback();  // ERROR
const this getConst();        // ERROR
```

Use `classtype` or an explicit type name when a composable type form is needed.

## 1.5 Body rules

A non-extern method that returns `this` may return only:

1. the receiver expression `this`; or
2. the result of an instance method call on `this` whose source return type is also `this`; or
3. a chain made only of such calls.

Valid:

```camp
this retain(Resource* this)
{
	return this;
}
```

```camp
this configure(Resource* this)
{
	return this.retain();
}
```

```camp
this prepare(Resource* this)
{
	return this.reset().retain().markReady();
}
```

Invalid:

```camp
this bad(Resource* this, Resource* other)
{
	return other; // ERROR
}
```

```camp
this bad(Resource* this)
{
	Resource* temp = this;
	return temp; // ERROR in this proposal
}
```

The second invalid example is rejected deliberately. The v1 rule is syntactic and provenance-free. It does not attempt to prove that a local variable still denotes the original receiver.

Parentheses around an otherwise valid expression do not change validity:

```camp
this ok(Resource* this)
{
	return (this);
}
```

## 1.6 Abstract, virtual, and override methods

Virtual and abstract methods may return `this`.

```camp
abstract class Node
{
	abstract this normalize();
}

virtual class Label: Node
{
	override this normalize()
	{
		return this;
	}
}
```

An override of a method that returns `this` must also declare `this` as its return type. The ordinary override rules still apply.

The exported virtual surface remains slot-based. A virtual slot that returns `this` has an ABI return type corresponding to the declaring receiver type. At source call sites, the result is still treated as the exact type of the receiver expression used for the call.

## 1.7 ABI lowering

In the ABI, a `this` return type is lowered as the receiver's ordinary lowered type.

For an explicit receiver method:

```camp
this retain(Resource* this);
```

conceptually lowers as:

```c
Resource* Resource_retain(Resource* this_value);
```

For a const pointer receiver:

```camp
this logAndProceed(const MessageAccumulator* this);
```

conceptually lowers as:

```c
const MessageAccumulator* MessageAccumulator_logAndProceed(
	const MessageAccumulator* this_value);
```

For an in-scope method, the compiler uses the same normalized receiver type it already uses for that method's ABI lowering.

The important source-level rule is that the call result tracks the call-site receiver expression, not merely the declaration's written receiver spelling.

## 1.8 Callable references

When a bound method reference is formed from a method that returns `this`, the special `this` return is resolved immediately using the bound receiver expression type.

```camp
MessageAccumulator* acc = ...;
auto f = acc.logAndProceed;
```

The inferred delegate return type is `MessageAccumulator*`. The delegate does not retain the special `this` return semantic. It has an ordinary concrete return type after binding.

If the same method is bound to a const receiver, the inferred delegate return type reflects that const receiver:

```camp
const MessageAccumulator* acc = ...;
auto f = acc.logAndProceed; // returns const MessageAccumulator*
```

Unbound method references and canonical flattened function references do not have a call-site receiver expression. For those references, the compiler resolves `this` to the method's ordinary declared receiver ABI type.

---

# 2. `classtype`

## 2.1 Overview

`classtype` is a reserved word. Within a `class` declaration, it names a class-relative type whose meaning depends on whether the enclosing class is open or sealed.

In a non-sealed class, `classtype` means:

> the enclosing class, or a type derived from the enclosing class.

In a sealed class, `classtype` means:

> the enclosing class exactly.

Unlike `this` as a return type, `classtype` is a type-like form. It may be composed with ordinary type syntax in allowed positions:

```camp
class Control
{
	classtype* getSelfLike();
	static classtype* create();
	void appendChild(classtype* child);
	iter classtype* descendants();
}
```

In the ABI, `classtype` is replaced with the enclosing class type.

At source call sites for non-sealed classes, `classtype` is rebound according to the type through which the method is called. In a sealed class, no derived call-site type can exist, so `classtype` is always the sealed enclosing class.

## 2.2 Valid scope

`classtype` is valid only inside a `class` declaration.

Valid in non-sealed classes:

```camp
class Control
{
	static classtype* create();
}
```

Valid in sealed classes:

```camp
sealed class FinalControl
{
	static classtype* create(); // `classtype` is exactly FinalControl
}
```

Invalid outside class scope:

```camp
classtype* globalValue; // ERROR

struct S
{
	classtype* value; // ERROR
}
```

## 2.3 Invalid storage positions

`classtype` may not be used in fields, static fields, or globals.

Invalid:

```camp
class Control
{
	classtype* parent;              // ERROR
	static classtype* sharedValue;  // ERROR
}
```

This restriction avoids self-type storage whose safe assignment rules would be too narrow to be useful. In practice, the only universally safe value for such storage would often be `this`, which is not a useful field relationship.

`classtype` may still be used in local variables, casts, and method bodies:

```camp
class Control
{
	classtype* getThis()
	{
		classtype* value = this;
		return value;
	}
}
```

## 2.4 Invalid type-declaration positions

`classtype` may not appear in:

- interface declarations;
- callable `newtype` declarations;
- value `newtype` underlying types;
- enum declarations outside method bodies;
- struct declarations outside method bodies;
- top-level aliases.

Invalid:

```camp
class Control
{
	newtype delegate void Callback(classtype* value); // ERROR
}
```

A method may still return or accept ordinary callable types that were declared without `classtype`.

## 2.5 Source meaning at call sites

Given:

```camp
class Control
{
	static classtype* create();
	classtype* getParent();
	void addChild(classtype* child);
}

class Button: Control
{
}
```

A call through `Control` treats `classtype` as `Control`:

```camp
Control* c = Control.create();
Control* p = c.getParent();
c.addChild(c);
```

A call through `Button` treats `classtype` as `Button`:

```camp
Button* b = Button.create();
Button* p = b.getParent();
b.addChild(b);
```

The binding is based on the static type used at the call site:

- for instance methods, the receiver expression's static type;
- for static methods, the type name used before `.` in the static call.

No runtime type check is implied.

## 2.6 ABI meaning

In lowered ABI signatures, `classtype` is replaced by the enclosing class type.

Source:

```camp
class Control
{
	static classtype* create();
	void addChild(classtype* child);
}
```

Conceptual ABI shape:

```c
Control* Control_create();
void Control_addChild(Control* this_value, Control* child);
```

A call through a derived type may require casts in the lowered code because the source result is more precise than the ABI result:

```camp
Button* b = Button.create();
```

Conceptual lowering:

```c
Button* b = (Button*)Control_create();
```

The cast is justified by the source-level `classtype` contract. Implementations that return a value not satisfying that contract are invalid by source semantics, or unsafe if they rely on an explicit cast.

## 2.7 Conversions involving `classtype`

A value whose type contains `classtype` may implicitly widen to the enclosing class type.

```camp
class Control
{
	void use(classtype* item)
	{
		Control* controlValue = item; // OK
	}
}
```

The reverse direction is not implicit:

```camp
class Control
{
	classtype* bad(Control* item)
	{
		return item; // ERROR
	}
}
```

An explicit cast is allowed:

```camp
class Control
{
	classtype* risky(Control* item)
	{
		return (classtype*)item; // OK, unsafe assertion by programmer
	}
}
```

The special safe value for an instance method is `this`:

```camp
class Control
{
	classtype* getThis()
	{
		return this; // OK
	}
}
```

## 2.8 Static methods

A static method may use `classtype` in its signature.

```camp
class Control
{
	static classtype* create(string name = typenameof(classtype));
}

class Button: Control
{
}

Button* button = Button.create();
```

At the call site, `Button.create()` treats `classtype` as `Button` and inserts a default value based on `Button` for `typenameof(classtype)`.

The method implementation itself does not automatically know the derived static call target unless the information is supplied through an ordinary parameter or explicit special capability. In the example above, the ordinary `name` parameter carries that information.

A static method implementation that returns `classtype*` must return a value that satisfies the class-relative contract. It may return a value obtained from a factory, or it may use an explicit cast when the implementation can guarantee correctness:

```camp
class Control
{
	static classtype* create(string name = typenameof(classtype))
	{
		Control* value = Registry.create(name);
		return (classtype*)value;
	}
}
```

## 2.9 Sealed classes

In a sealed class, `classtype` denotes the enclosing class exactly because no derived class can exist.

```camp
sealed class FinalControl
{
	static classtype* create()
	{
		return new FinalControl(); // OK: classtype* is exactly FinalControl*
	}
}
```

This exact-type rule is especially useful when a sealed class implements an abstract or virtual result-position `classtype` contract declared by a base class. The override body may return a newly created instance of the sealed class without an explicit cast.

```camp
abstract class Shape
{
	abstract classtype* clone();
}

abstract class BoxBase: Shape
{
	// Still abstract; it does not know the final concrete classtype.
}

sealed class Box: BoxBase
{
	override classtype* clone()
	{
		return new Box(); // OK: in Box, classtype* is exactly Box*
	}
}
```

A call still uses the static receiver type to determine the source-visible result type:

```camp
Box* box = new Box();
Box* boxCopy = box.clone();

Shape* shape = box;
Shape* shapeCopy = shape.clone();
```

The runtime object returned by `shape.clone()` may be a `Box`, but the static result type is `Shape*` because the receiver expression has static type `Shape*`.

## 2.10 Virtual and abstract methods

`classtype` is covariant. It is intuitive in result positions, but misleading in input positions of virtual or abstract methods.

For this reason, a virtual or abstract method may use `classtype` only in result positions:

- the return type;
- direct `out` parameter types.

Valid:

```camp
abstract class Node
{
	abstract classtype* clone();
	abstract void getPeer(out classtype* peer);
}
```

Invalid:

```camp
abstract class Node
{
	abstract void compareTo(classtype* other); // ERROR
}
```

The invalid declaration would imply that every override receives the most-derived override type. That is not true for calls through the original virtual slot. A caller using the base static type may pass any value compatible with the original declaring class.

When an override implements a virtual method whose result uses `classtype`, it may keep `classtype` in the result position:

```camp
abstract class Node
{
	abstract classtype* clone();
}

class ButtonNode: Node
{
	override classtype* clone()
	{
		return this;
	}
}
```

A sealed override may also return a newly created instance of the sealed class without an explicit cast, because `classtype` is exact in sealed class scope:

```camp
sealed class LeafNode: Node
{
	override classtype* clone()
	{
		return new LeafNode();
	}
}
```

The virtual ABI slot returns the declaring class type. Source call sites still see a class-relative result according to the static receiver type.

For v1, `classtype` may not appear inside nested callable types in virtual or abstract method signatures. This avoids variance analysis through delegate, iterator, async, or function type positions.

Invalid:

```camp
abstract class Node
{
	abstract void visit(delegate void(classtype* item) visitor); // ERROR
}
```

## 2.11 Examples

### Fluent class-relative factory

```camp
class Widget
{
	static classtype* create(string typeName = typenameof(classtype))
	{
		Widget* value = WidgetRegistry.create(typeName);
		return (classtype*)value;
	}
}

class Button: Widget
{
}

Button* button = Button.create();
```

### Non-virtual same-family input

```camp
class MenuItem
{
	void copyPresentationFrom(classtype* other)
	{
		MenuItem* menuValue = other;
		this.copyBasePresentation(menuValue);
	}
}
```

Because the method is non-virtual, `classtype*` in the input parameter is bound by the receiver type used at the call site.

---

# 3. Runtime `typenameof(...)`

## 3.1 Overview

`typenameof` is a reserved word and compiler intrinsic. It produces a runtime `string` containing the Camp type-name contribution that the compiler uses for flattened method-name prefixes and overload symbols.

```camp
string a = typenameof(uint);     // "UInt"
string b = typenameof(int[]);    // "IntArray"
string c = typenameof(MyType);   // "MyType"
```

The result type is `string`: a pointer to zero-terminated UTF-8 constant data.

The returned string is not owned by the caller and must not be deleted.

`typenameof(...)` is not a reflection feature. It does not produce a type object, method descriptor, metadata handle, or dynamic runtime value. Its operand is resolved at compile time and must be a type form, type alias, generic type parameter, qualified type name, or `classtype` in the special default-parameter position described below.

## 3.2 Relationship to metadata `symbolof(...)`

Camp already uses `symbolof(...)` in metadata attribute arguments. That existing `symbolof(...)` form remains metadata-only.

This proposal does not change metadata `symbolof(...)`.

This proposal introduces a separate runtime intrinsic named `typenameof(...)`.

`typenameof(...)` always produces a `string` value in ordinary expression and special-parameter contexts. It never produces a metadata symbol reference.

## 3.3 `@symbol` is ignored

`typenameof(...)` ignores `@symbol` attributes.

It returns the Camp/compiler type-name contribution, not a backend-facing symbol override.

```camp
class Control
{
	@symbol("ControlValue")
	export int getValue();
}

string name = typenameof(Control); // "Control"
```

This is intentional. `typenameof(...)` is for Camp type names and generated-prefix type names. Metadata links and backend-facing symbol names remain separate concepts.

## 3.4 Operand forms

The operand of `typenameof(...)` must be a resolved type form. It is not evaluated as a runtime expression.

Valid operands include:

- primitive type forms;
- named type declarations;
- aliases;
- qualified type names;
- generic type parameters when the required name capability is known;
- composed type forms such as arrays when the compiler can determine their type-name contribution;
- `classtype` only in default parameter value position;
- aliases to aliases, after alias resolution.

Invalid operands include variables, expressions, functions, methods, fields, member accesses, enum values, and compiler-expanded component accesses.

```camp
void sample(int local)
{
	string a = typenameof(local);       // ERROR: local is not a type
	string b = typenameof(this.value);  // ERROR: member access is not a type
}
```

## 3.5 Type-form names

`typenameof(...)` returns the type-name contribution the compiler uses when that type participates in generated receiver or overload symbols.

Examples:

```camp
typenameof(uint)       // "UInt"
typenameof(char[])     // "CharArray"
typenameof(int[])      // "IntArray"
typenameof(const int*) // "Int"
```

Pointer, `const`, lifetime, and similar declarators do not contribute additional punctuation or spelling to the result.

Named callable newtypes use their declared name:

```camp
newtype delegate char CharReader();

string name = typenameof(CharReader); // "CharReader"
```

Aliases are resolved before name calculation. If an alias resolves to a primitive or other named target, `typenameof(alias)` returns the resolved target name, not the alias name.

```camp
alias TSTRING = wstring;

string name = typenameof(TSTRING); // "WString"
```

The alias rule follows Camp's alias model: aliases introduce alternate source names, not new runtime or ABI identities.

## 3.6 `classtype` operands

`typenameof(classtype)` is invalid in ordinary expression contexts.

```camp
class Control
{
	void sample()
	{
		string name = typenameof(classtype); // ERROR
	}
}
```

`typenameof(classtype)` is allowed only as a default parameter value. In that position, the default is inserted at the call site after ordinary `classtype` binding.

```camp
class Factory
{
	static classtype* create(string typeName = typenameof(classtype))
	{
		return (classtype*)Registry.create(typeName);
	}
}

class ButtonFactory: Factory
{
}

ButtonFactory* factory = ButtonFactory.create();
```

The call behaves as though it supplied the name explicitly:

```camp
ButtonFactory* factory = ButtonFactory.create(typeName: "ButtonFactory");
```

`typenameof(classtype)` is not a general way for a static method body to discover the derived static call target. If the method needs that name, it must receive it through an ordinary parameter, usually by using `typenameof(classtype)` as that parameter's default value.

## 3.7 Runtime expression use

When the type operand is known at the use site, `typenameof(...)` may appear as an ordinary expression:

```camp
void logUIntName()
{
	string name = typenameof(uint);
	log(name);
}
```

The compiler emits a pointer to static zero-terminated string data.

Conceptual C lowering:

```c
static const char __camp_name_UInt[] = "UInt";
const char* name = __camp_name_UInt;
```

## 3.8 Default parameter values

When `typenameof(...)` appears in a default parameter value, the default is inserted at the call site after ordinary generic substitution and `classtype` binding.

```camp
void create<T: any>(string typeName = typenameof(T));
```

A concrete caller may omit the argument:

```camp
create<int>(); // supplies "Int"
```

If the caller cannot determine the required name, the default is unavailable and the argument must be supplied explicitly.

```camp
void makeOuter<T: any>()
{
	create<T>(); // ERROR if T's name is not known here
}
```

Manual supply remains possible:

```camp
void makeOuter<T: any>(string knownName)
{
	create<T>(knownName);
}
```

## 3.9 Erased generics and special parameter form

Like `sizeof(T)` and `vtableof(T: Interface)`, erased generic code may request `typenameof(T)` explicitly as a special parameter.

```camp
void inspect<T: any>(T* value, typenameof(T))
{
	log(typenameof(T));
}
```

Across the ABI, this is an ordinary `string` parameter supplied by the caller.

Conceptual lowering:

```c
void inspect(void* value, const char* T_name)
{
	log(T_name);
}
```

The special parameter is not automatically available merely because a method is generic. It must be requested when erased generic code needs it.

Invalid:

```camp
void inspect<T: any>(T* value)
{
	log(typenameof(T)); // ERROR: no typenameof(T) capability is available
}
```

`typenameof(T[])` with an erased `T` is also invalid unless the exact name capability for that type form is available. A `typenameof(T)` capability does not automatically provide `typenameof(T[])`.

Invalid:

```camp
void inspectArrayName<T: any>(typenameof(T))
{
	log(typenameof(T[])); // ERROR: typenameof(T[]) was not requested and is not known
}
```

Valid:

```camp
void inspectArrayName<T: any>(typenameof(T[]))
{
	log(typenameof(T[]));
}
```

A generic caller may rely on a default only when the name is known at that call site. If the caller itself has only an erased generic `T` and no matching `typenameof(...)` capability, it must either request the capability or pass an explicit string.

## 3.10 Constructor storage for generic types

When a generic class constructor requests `typenameof(T)`, the compiler may store the supplied string in hidden instance state, exactly like constructor-requested `sizeof(T)` or `vtableof(T: Interface)` capabilities.

```camp
class TypeBox<T: any>
{
	TypeBox(typenameof(T))
	{
	}

	string getTypeName()
	{
		return typenameof(T);
	}
}
```

Conceptual lowered storage:

```c
struct TypeBox
{
	const char* __typenameof_T;
};

void TypeBox_init(TypeBox* this_value, const char* T_name)
{
	this_value->__typenameof_T = T_name;
}

const char* TypeBox_getTypeName(TypeBox* this_value)
{
	return this_value->__typenameof_T;
}
```

If the constructor does not request `typenameof(T)`, later instance methods of the generic class may not use `typenameof(T)` unless they receive it through their own method parameter list.

Invalid:

```camp
class TypeBox<T: any>
{
	TypeBox()
	{
	}

	string getTypeName()
	{
		return typenameof(T); // ERROR: constructor did not request capability
	}
}
```

## 3.11 Generic methods

A standalone generic method or an instance method of a generic type may request `typenameof(T)` in its own parameter list.

```camp
class TypeBox<T: any>
{
	string describe(typenameof(T))
	{
		return typenameof(T);
	}
}
```

No hidden field is created for a method-level request. The value is an ordinary ABI parameter for that call.

## 3.12 Interaction with ordinary named parameters

A named string parameter with a default `typenameof(T)` is ordinary source syntax and does not create a special capability:

```camp
void create<T: any>(string typeName = typenameof(T))
{
	log(typeName);
}
```

Inside the body, use the named parameter `typeName`.

By contrast, a special parameter makes the expression `typenameof(T)` itself available:

```camp
void create<T: any>(typenameof(T))
{
	log(typenameof(T));
}
```

Both forms lower to ordinary string parameters. The difference is source binding and hidden-storage behavior for generic type constructors.

---

# 4. Combined Examples

## 4.1 Receiver-preserving fluent API

```camp
class QueryBuilder
{
	this where(const char[] clause)
	{
		this.appendWhere(clause);
		return this;
	}

	this orderBy(const char[] clause)
	{
		this.appendOrderBy(clause);
		return this;
	}
}

QueryBuilder* builder = new QueryBuilder();
builder.where("active = 1").orderBy("name");
```

If the receiver is const or scoped, the returned value preserves that same receiver type.

## 4.2 Class-relative creation

```camp
class View
{
	static classtype* create(string typeName = typenameof(classtype))
	{
		View* value = ViewRegistry.create(typeName);
		return (classtype*)value;
	}
}

class Button: View
{
}

Button* button = Button.create();
```

Conceptual lowering:

```c
View* raw = View_create("Button");
Button* button = (Button*)raw;
```

## 4.3 Explicit generic name capability

```camp
void logType<T: any>(T* value, typenameof(T))
{
	log(typenameof(T));
}
```

A concrete caller supplies the name automatically:

```camp
int value = 0;
logType<int>(&value); // supplies "Int"
```

An erased generic caller must also have the capability:

```camp
void outer<T: any>(T* value, typenameof(T))
{
	logType<T>(value); // OK: outer can forward typenameof(T)
}
```

Without that capability, the call is invalid unless an explicit string is supplied through an ordinary parameter form.

## 4.4 Virtual result-only self type

```camp
abstract class DocumentNode
{
	abstract classtype* clone();
	abstract void getOwner(out classtype* owner);
}

class HeadingNode: DocumentNode
{
	override classtype* clone()
	{
		return this;
	}

	override void getOwner(out classtype* owner)
	{
		owner = this;
	}
}
```

A sealed implementation may return a new instance of the sealed class:

```camp
abstract class Shape
{
	abstract classtype* clone();
}

abstract class BoxBase: Shape
{
	// No override here; this class is still abstract.
}

sealed class Box: BoxBase
{
	override classtype* clone()
	{
		return new Box();
	}
}

Box* b = new Box();
Box* copyOfBox = b.clone();

Shape* shape = b;
Shape* copyOfShape = shape.clone();
```

Invalid virtual input position:

```camp
abstract class DocumentNode
{
	abstract bool sameFamilyAs(classtype* other); // ERROR
}
```

Use the declaring class name when the method accepts any value in the base family:

```camp
abstract class DocumentNode
{
	abstract bool sameFamilyAs(DocumentNode* other);
}
```

---

# 5. Diagnostics Summary

The compiler should report errors for:

- `this` return type on free functions, static methods, constructors, destructors, interface methods, or callable newtypes;
- any composed form of `this`, such as `this*`, `this[]`, or `delegate void(this)`;
- returning any expression other than `this` or a valid chain of `this`-returning calls from a non-extern `this`-returning method;
- `classtype` outside a class declaration;
- `classtype` in fields, static fields, globals, aliases, callable newtypes, interfaces, structs, or enum declarations outside method bodies;
- `classtype` in non-result positions of virtual or abstract method signatures;
- `classtype` inside nested callable types in virtual or abstract method signatures;
- implicit conversion from the enclosing class type to `classtype` except for the special `this` value;
- `typenameof(...)` with an ambiguous, unresolved, or non-type operand;
- `typenameof(classtype)` outside default parameter value position;
- `typenameof(T)` in erased generic code when no `typenameof(T)` capability is available;
- `typenameof(T[])` or another generic type form in erased generic code when no matching `typenameof(...)` capability is available;
- omitted default arguments using `typenameof(T)` or `typenameof(classtype)` when the caller cannot determine the name.

---

# 6. Compatibility

`this` is already a language keyword in receiver contexts. This proposal adds a new return-type meaning in method return-type position.

`classtype` is a full reserved word. Existing uses of `classtype` as an ordinary identifier become invalid.

`typenameof` is a full reserved word introduced by this proposal. Existing uses of `typenameof` as an ordinary identifier become invalid. Existing metadata-only `symbolof(...)` behavior remains unchanged and separate from `typenameof(...)`.

---

# 7. Rationale

## 7.1 Why `this` return type is separate from `classtype`

`this` means the returned value is the receiver itself. It preserves the exact receiver expression type, including constness and lifetime.

`classtype` means a class-relative type. It can describe values in the same class family, but it does not mean the value is the receiver itself.

These are different contracts and should remain separate.

## 7.2 Why `classtype` is not allowed in fields

A field whose type is relative to an unknown derived class has unclear assignment rules and little practical value. The only universally safe assignment in an instance would usually be `this`, which does not justify a stored field.

For class-relative relationships in storage, use the declaring class type explicitly and cast where a stronger relationship is known.

## 7.3 Why virtual input positions reject `classtype`

`classtype` is covariant. Virtual dispatch accepts calls through the original declaring class. A derived override cannot assume that a `classtype*` input parameter is an instance of the derived override class.

For virtual methods, use `classtype` only for values flowing out of the call. Use the declaring class name for input parameters.

## 7.4 Why `typenameof(T)` is explicit in erased generics

Camp does not hide generic runtime support behind metadata objects. When erased generic code needs size, vtable, or name information, it requests that information explicitly.

This keeps the ABI visible and allows foreign code to call generic-lowered surfaces by passing ordinary values.
