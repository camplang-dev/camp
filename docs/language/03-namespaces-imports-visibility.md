# Namespaces, Imports, And Visibility

## Qualified Names

Camp uses `::` to qualify names.

```camp
Std::Console.writeLine("hello");
```

A qualified name resolves through visible declarations, imports, and namespace
exports. The last identifier is the local declaration name.

## `using`

`using` imports a namespace. It may import a namespace directly, import it with
an alias, or import selected names.

```camp
using Std;
using Platform::Windows as Win;
using Math::{min, max};
```

Imports affect source lookup. They do not change the exported namespace of the
current module.

## `export as`

`export as` gives the module an exported namespace.

```camp
export as Samples::Text;
```

Exported declarations are emitted under that namespace in API views and
metadata.

## Visibility Keywords

`export` declares API surface intended for external consumption. `public`
declares public source surface that is visible beyond private implementation
scope. Declarations with neither keyword are private to the compilation scope
where they are visible.

`extern` declares a symbol implemented outside Camp. It is commonly used for C
functions, native types, and interop boundaries.

## Source Symbols And ABI Symbols

The source name is the Camp name used in code. The ABI symbol is the emitted
native name. Most declarations use a derived symbol. `@symbol` overrides the
native symbol when an API must bind to a specific external name.

```camp
@symbol("puts")
extern int cPuts(const char* text);
```

The metadata and compiler supplements describe emitted API and metadata naming.

## Name Lookup Summary

Name lookup starts from local declarations, parameters, receiver members, type
members, imports, and visible namespace declarations. Generic parameters and
lifetime anchors participate in the scopes where their declaration makes them
available. Ambiguous or missing names are compiler errors.
