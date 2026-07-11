# Interfaces And Dispatch

## Interface Declarations

Interfaces declare callable surface that types can implement.

```camp
export interface Writer
{
	void write(const char[] text);
}
```

Interface members describe required, defaulted, or optional slots.

## Implementing Interfaces

Structs and classes can implement interfaces by providing compatible members.
The implementation must satisfy the receiver, parameter, return, lifetime, and
visibility rules for each required slot.

```camp
export struct ConsoleWriter: Writer
{
	void write(const char[] this, const char[] text);
}
```

## Interface Inheritance

Interfaces may inherit from other interfaces. An implementing type must satisfy
the complete inherited surface.

## Required, Defaulted, And Optional Methods

A required method must be implemented by each conforming type. A defaulted
method uses a named default target when the type does not provide an override.
An optional method permits a missing slot and requires callers to respect the
optional call rules.

Default and optional methods allow interface evolution without forcing every
implementation to duplicate shared behavior.

## Interface Calls

Calls through an interface value dispatch through the interface slot. The
compiler validates that the call is legal for the slot, including optional-slot
rules.

```camp
writer.write("saved");
```

## Interface Conversions

Values of implementing struct or class types can convert to compatible
interface views where the language defines the conversion. Derived class and
extern class boundaries follow the object model and target ABI rules.

## `vtableof`

`vtableof(T: Interface)` supplies a vtable capability for generic code that
needs interface dispatch on a type parameter.

```camp
export void render<T: implements Writer>(T* writer, vtableof(T: Writer));
```

The exact vtable layout is a compiler-writer detail.
