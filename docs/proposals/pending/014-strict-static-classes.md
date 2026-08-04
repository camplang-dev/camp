# Strict Static Classes

Status: pending  
Proposal date: 2026-08-04  
Last updated date: 2026-08-04

## Summary

This proposal adds `static class` as a strict source-level container for static
members.

```camp
namespace Std;

static class Console
{
	public static void writeLine(const char[] text);
	public static TextReader getReader();
}
```

A static class is not an ordinary type. It can be used only as a member access
target:

```camp
Std::Console.writeLine("ready");
Console.writeLine("ready");
TextReader reader = Console.Reader;
```

Every member of a static class must be explicitly marked `static`.

## Motivation

Utility APIs often want member syntax:

```camp
Console.writeLine("Hello, world!");
Math.min(3, 4);
```

Modeling those APIs as ordinary classes creates type implications that are not
real:

- values of the container type;
- pointers to the container type;
- construction and destruction;
- inheritance;
- instance members;
- generated lifecycle helpers.

A strict static class keeps the member-call surface while removing those false
type concepts.

Static classes are also useful for static properties:

```camp
TextReader reader = Console.Reader;
```

That shape is preferable to forcing utility APIs to expose only explicit getter
function calls.

## Standard Library Console Migration

As part of this feature, the standard library `Console` surface should become a
static class:

```camp
namespace Std;

static class Console
{
	public static void writeLine(const char[] text);
}
```

`Console` should then be represented only as a static member container. Code may
continue to call:

```camp
Console.writeLine("Hello, world!");
```

after the ordinary standard-library namespace import is available.

The migration should remove any generated or source-visible ordinary type
behavior from `Console`, including construction, pointer usage, inheritance,
instance members, and lifecycle helpers.

## Declaration Rules

A static class declaration uses `static class`:

```camp
static class Console
{
	public static void writeLine(const char[] text);
}
```

The static class declaration itself may use only the `static` modifier.
Visibility is declared on members:

```camp
static class Console
{
	public static void writeLine(const char[] text);
	internal static void writeRaw(const char[] text);
}
```

Static classes may not declare type parameters:

```camp
static class Cache<T>
{
}
```

## Static Classes Are Not Types

A static class is a member container, not a type.

Invalid uses:

```camp
Console value;
Console* pointer;
new Console();
class Derived: Console { }
fn void(Console) callback;
```

`typenameof(Console)` may be allowed as a source-container query. If supported,
it must not imply that `Console` is a value type, reference type, or
constructible runtime type.

## Member Rules

Every static class member must be explicitly marked `static`:

```camp
static class Console
{
	public static void writeLine(const char[] text); // ok
	public void write(const char[] text);            // error
}
```

Allowed static class members:

- static methods;
- static property accessors;
- static fields;
- static inline constants.

Disallowed static class members and features:

- constructors;
- destructors;
- instance fields;
- instance methods;
- explicit `this` parameters;
- inheritance;
- interface implementation;
- generic static classes;
- virtual, override, abstract, or sealed members.

## Static Methods And `this`

A static method may not declare an explicit `this` parameter.

This validation should apply to every static method declaration:

```camp
class Image
{
	public static int getWidth(Image* this); // error
}

static class Console
{
	public static void close(Console this);  // error
}
```

A declaration marked `static` has no receiver. A parameter named `this` makes a
method receiver-bound. The combination is contradictory.

## Out-Of-Scope Static Class Members

Static class members may be declared out of scope:

```camp
namespace Std;

static class Console
{
	public static void writeLine(const char[] text);
}

public static void Console.writeError(const char[] text)
{
	Console.writeLine(text);
}
```

The qualified form is valid:

```camp
public static void Std::Console.writeError(const char[] text)
{
	Std::Console.writeLine(text);
}
```

Rules:

1. The qualifier before the member name must resolve to a static class.
2. The declaration must be marked `static`.
3. The declaration must not have an explicit `this` parameter.
4. The declaration follows the same member restrictions as in-body static class
   members.
5. The member's visibility is declared on the out-of-scope declaration.

This is not a receiver-style extension method. There is no static class value
and no receiver.

## Emission And Metadata

Static classes emit member functions or storage for their members. They do not
emit:

- type layouts;
- constructors;
- destructors;
- create/delete helpers;
- vtables;
- instance receiver plumbing.

API headers and metadata should represent static classes as source containers
with static members, not as ordinary class/type declarations.

## Diagnostics

Required diagnostics include:

- invalid modifier on a static class declaration;
- generic static class declaration;
- static class used in a type position;
- construction or allocation of a static class;
- inheritance from a static class;
- interface implementation by a static class;
- non-static member inside a static class;
- constructor or destructor inside a static class;
- instance field inside a static class;
- `virtual`, `override`, `abstract`, or `sealed` member inside a static class;
- explicit `this` parameter on a static method;
- out-of-scope static class member declaration whose qualifier does not resolve
  to a static class;
- out-of-scope static class member declaration missing `static`.

Diagnostic examples:

```text
Static class 'Console' is not a type and cannot be used in a type position.
```

```text
Static method 'getWidth' cannot declare a 'this' parameter.
```

```text
Out-of-scope member 'Std::Console.writeError' must be declared static because
'Std::Console' is a static class.
```

## Compiler Impact

Implementation areas:

- parser:
  - parse `static class`;
  - parse qualified out-of-scope member declarations such as
    `static void Std::Console.writeError();`;
- declaration model:
  - represent static classes as source containers distinct from type
    declarations;
- binding:
  - bind static class names in member-access contexts;
  - reject static classes in type positions;
  - support in-body static class member lookup;
  - support out-of-scope static class member binding;
  - reject `this` parameters on static methods;
- C emission:
  - emit only static class members;
  - suppress type layout, lifecycle, receiver, and vtable emission for static
    classes;
- metadata/API output:
  - add a static-container representation or equivalent structured marker;
  - include static class members according to visibility;
  - keep static classes distinct from ordinary types for tools.

## Test Plan

### Parser Tests

- parse `static class`;
- reject invalid static class modifiers;
- parse static class methods, property accessors, fields, and inline constants;
- parse out-of-scope static class members;
- parse qualified out-of-scope static class members.

### Semantic Tests

- static class cannot be generic;
- static class cannot be used as a type;
- static class cannot be constructed;
- static class cannot be inherited;
- static class cannot implement interfaces;
- every static class member must be static;
- static class cannot contain constructors or destructors;
- static class cannot contain instance fields or instance methods;
- static class cannot contain virtual/override/abstract/sealed members;
- static methods cannot declare explicit `this`;
- out-of-scope static class members bind to the static class container;
- qualified out-of-scope static class members bind through namespace
  qualification.
- `Std::Console` is a static class and still supports `Console.writeLine(...)`
  through the ordinary standard-library import path.

### Emission Tests

- static class methods emit callable functions;
- static class fields and inline constants emit in the expected static storage
  shape;
- static classes emit no type layout;
- static classes emit no lifecycle helpers;
- static classes emit no receiver plumbing;
- out-of-scope static class members emit as members of the static container.
- `Console` emits no ordinary class layout or lifecycle helpers after migration.

### API And Metadata Tests

- API headers include visible static classes and visible static members;
- metadata identifies static classes as source containers;
- metadata does not report static classes as ordinary constructible types;
- private/internal/public/export filtering works for static class members;
- out-of-scope static class members appear under the static class container.
- standard-library API/metadata represents `Console` as a static class.

## Documentation Plan

Update living documentation only:

- language guide:
  - static class declaration syntax;
  - allowed and rejected static class members;
  - static class member calls and property syntax;
  - out-of-scope static class member declarations;
  - `this` prohibition on static methods;
  - `Console` as the standard static class example;
- semantics supplements:
  - static class binding;
  - static class emission;
  - static class metadata/API representation;
  - diagnostics for invalid static class usage;
- compiler docs:
  - metadata JSON updates if static classes require a new declaration kind or
    structured field.

Do not update accepted or rejected completed proposals and do not update prior
release notes as part of this work. Those files are historical records.
