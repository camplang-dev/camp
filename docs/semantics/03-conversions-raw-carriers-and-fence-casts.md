# Conversions, Raw Carriers, And Fence Casts

## Conversion Levels

The conversion classifier uses these levels: implicit, explicit, unsafe,
fence-required, reconstruct-required, and forbidden. An implicit conversion
needs no cast. An explicit conversion uses `(T)value`. An unsafe conversion
requires `(unsafe T)value`. Fence-required conversions erase type information
through a raw carrier before recovery. Reconstruct-required conversions are not
casts.

## Target Specifier Domains

Targets define type-spec domains and conversion policies. A value conversion at
a pointer or natural-integer boundary does not rewrite type constructors such as
arrays, generics, or callable signatures.

## Raw Carrier Families

`void*` erases data pointee type and data-pointer family. `fn*` erases concrete
function signature. `nint` and `nuint` are natural integer carriers. `untyped`
erases raw scalar carrier identity.

## Const And Volatile Overrides

Removing `const` or `volatile` requires an unsafe cast even after a raw carrier
fence. The classifier must preserve these overrides so a fence does not hide
qualifier removal.

## Pointer Family Rules

Data pointer conversions preserve physical indirection depth unless an unsafe
or forbidden classifier result says otherwise. Struct, class, interface,
primitive, and value-newtype pointer families each have their own compatibility
rules.

## Callable Rules

Direct callable conversions must preserve compatible signatures and call specs.
`fn*` fences erase signature identity but do not make an arbitrary value
callable. Delegate and other multivalue callable values require reconstruction
when their component shape changes.

## Array And Optional Rules

Array carrier conversions do not rewrite element type. Optional conversions
operate on the optional value shape and payload compatibility; they do not
permit arbitrary payload reinterpretation.

## Generic Constructed Types

Generic argument conversion is not a value conversion. A constructed type with
one argument is not implicitly rewritten to another constructed type merely
because the arguments are individually convertible.

## Diagnostics And Warnings

Diagnostics should name the required action: add an explicit cast, use
`unsafe`, pass through a raw carrier, reconstruct the value, or change the API.
Warnings should remain warnings only where the classifier explicitly permits
the conversion.

## Test Matrix

Key coverage lives in `tests/CCompile/conversion_*`,
`tests/Diagnostics/conversion_*`, `tests/Api/conversion_policy_stage3.camp`,
and target conversion policy tests.
