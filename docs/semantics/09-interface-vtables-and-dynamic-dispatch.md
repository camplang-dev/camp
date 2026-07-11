# Interface VTables And Dynamic Dispatch

## Interface Shape

An interface shape is the set of inherited and declared callable slots visible
to implementers and callers. Shape includes required, defaulted, and optional
slots.

## Slot Function Types

Slot function types include receiver representation, parameter list, return
type, thrown slots, lifetimes, call specs, and generic substitutions. Override
and conformance checks require exact or rule-defined compatibility.

## Required Slots

Required slots must be supplied by implementing structs and classes.

## Defaulted Slots

Defaulted slots use a target implementation when an implementing type omits the
slot. The default target must match the slot function type.

## Optional Slots

Optional slots may be null in a vtable. Calls must account for optional-slot
rules rather than assuming a valid target function.

## Struct Conformance

Struct conformance binds methods directly against interface slots. Receiver
representation and addressability are part of compatibility.

## Class Conformance

Class conformance may include inheritance, virtual dispatch, and derived-class
conversion behavior. Extern class boundaries must preserve native ownership and
ABI rules.

## Interface Inheritance

Inherited slots participate in conformance and vtable generation. Name and
signature conflicts must be diagnosed.

## VTable Generation

VTables are generated from interface shape and implementation mapping. Emitted
names and slot order must be stable for ABI and tests.

## `vtableof`

`vtableof(T: Interface)` transports vtable capability for generic interface
dispatch. Lowering threads the capability through calls that require it.

## Interface Conversions

Conversions from structs/classes to interfaces build or reference compatible
interface carriers. Interface-to-interface and class/interface conversions must
respect inheritance and target representation.

## Diagnostics

Diagnostics should name the missing, mismatched, ambiguous, defaulted, or
optional slot and point to both the interface requirement and implementation
candidate when possible.
