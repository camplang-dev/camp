# Attributes And Doc Comments

## Attribute Syntax

Attributes use `@name` with optional arguments and attach to declarations or
declaration children.

```camp
@symbol("native_add")
extern int add(int left, int right);
```

Attribute arguments may include strings, values, named arguments, and metadata
symbol expressions where supported.

## Metadata Attributes

Metadata attributes describe declarations for tools. Common documentation
attributes include `@summary`, `@remarks`, `@returns`, `@example`, `@see`, and
`@deprecated`.

Unknown documentation attributes are compiler errors when they appear in doc
comments.

## Documentation Comments

Line documentation comments use `///`. Block documentation comments use
`/** ... */`. A documentation block attaches to the immediately following
declaration or declaration child.

```camp
/// Adds two values.
///
/// - left: First value.
/// - right: Second value.
/// @returns The sum.
export int add(int left, int right);
```

Plain text lowers to `@summary` metadata.

## Child Documentation Targets

A doc-comment line beginning with `- name:` targets a child of the attached
declaration. Children include type parameters, function parameters, receiver
parameters, fields, methods, and enum values depending on the declaration.

## Symbol Links

`[Symbol]` inside doc text creates a documentation link. The compiler resolves
the symbol and emits a metadata symbol reference.

```camp
/// Converts a [UserId] to text.
export const char[] format(UserId id);
```

`symbolof(Name)` is valid only inside metadata attribute arguments.

## Literal Regions

Inline code spans and fenced code blocks are literal regions. Symbol links,
child targets, and doc attributes are not parsed inside literal regions.

````camp
/// Example:
/// ```camp
/// writeLine("ready");
/// ```
export void showExample();
````

## Direct Attribute Authoring

Documentation metadata can be written directly when comments are not the best
source form.

```camp
@summary("Writes one line.")
export void writeLine(const char[] value);
```

## Relationship To Metadata JSON

Doc comments lower to metadata attributes during binding. Metadata JSON
serialization is described in the compiler supplement. Language users only need
to know the source attachment and authoring rules.
