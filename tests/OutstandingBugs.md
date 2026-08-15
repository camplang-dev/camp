# Outstanding Bugs

Next bug number: BUG-079.

## BUG-078: Generic indexed accessor with scalar literal can lower the literal as `void*`

Status: open

Observed while adding a retained-allocator std container test. A generic indexed
accessor such as `map.Item[7]`, where the receiver is a generic container whose
key type is `int`, can lower the scalar literal as a `void*` compound literal
instead of an `int` temporary. The generated C fails to compile with an
incompatible integer-to-pointer conversion.

General repro:

1. Create a generic type with an indexed getter that takes an `in T` or
   equivalent erased generic value.
2. Instantiate it with a scalar type such as `int`.
3. Access the indexed getter with a scalar literal at the call site.
4. Compile to C.

Expected behavior: the literal should be materialized as the concrete scalar
type required by the instantiated generic slot.

Current workaround: assign the literal to a local variable of the target type
and pass that local to the indexed accessor.
