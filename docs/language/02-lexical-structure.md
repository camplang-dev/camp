# Lexical Structure

## Files, Whitespace, And Comments

Camp source is plain text. Whitespace separates tokens and is otherwise
insignificant except inside strings and character literals. Line comments use
`//`. Block comments use `/* ... */`.

Documentation comments use `///` or `/** ... */` and are described in
[Attributes And Doc Comments](20-attributes-and-doc-comments.md).

## Identifiers And Keywords

Identifiers name declarations, parameters, locals, fields, enum values, labels,
and namespaces. Identifiers consist of letters, digits, and underscores, with a
letter or underscore at the start.

Keywords include declaration words such as `struct`, `class`, `interface`,
`enum`, `newtype`, `alias`, `export`, `public`, `extern`, `static`, `virtual`,
`override`, `abstract`, `sealed`, `fixed`, and `inline`; type and qualifier
words such as `const`, `constof`, `volatile`, `escaped`, `scoped`, `unscoped`,
`fn`, `delegate`, `once`, `iter`, `async`, `thrown`, `this`, and `classtype`;
and statement/expression words such as `if`, `else`, `while`, `do`, `for`,
`foreach`, `switch`, `case`, `default`, `return`, `yield`, `break`, `continue`,
`goto`, `try`, `catch`, `finally`, `throw`, `await`, `postpone`, `new`, `init`,
`delete`, `within`, `sizeof`, `vtableof`, `typenameof`, and `symbolof`.

## Literals

Camp has numeric, string, character, boolean, and null literals.

```camp
int count = 42;
bool ready = true;
const char[] label = "ready";
char marker = 'R';
void* missing = null;
```

Numeric literal interpretation is target-aware for primitive widths and follows
the conversion rules described in
[Primitive Values And Literals](06-primitive-values-and-literals.md).

## Strings And Characters

String literals are constant text. With no stronger target type, a string
literal has the primitive `string` type. A literal can also target compatible
const character-pointer, const counted-character-array, and fixed
character-array storage forms.

Character literals represent character values. Character literal typing follows
the destination type where one is available.

String literal storage and Unicode details are covered with arrays and strings.

## Preprocessor Directives

Preprocessor directives start with `#` and appear in compilation units.

```camp
#define DEBUG_LOGGING
#if DEBUG_LOGGING
export void writeDebug(const char[] message);
#endif

#build --target gcc-linux-x64
#within explicit
```

`#define`, `#undef`, `#if`, `#elif`, `#else`, and `#endif` control source
inclusion. `#within` selects the source-level default allocation policy.
`#build` supplies build arguments consumed by compiler tooling.

## Grammar Notation

The archived grammar uses angle brackets for rule references, uppercase names
for terminal rules, lower-snake-case for nonterminal rules, `?` for optional
terms, `*` for zero or more terms, `|` for alternatives, square brackets for
optional sequences, and round parentheses for grouping.

## Token Boundaries And Trivia

Comments and whitespace are trivia except where they separate tokens or appear
inside literal text. Documentation comments are recognized as trivia before
parsing, then translated to metadata attributes on the following declaration or
declaration child. A non-doc-comment token between the documentation block and
the declaration breaks the attachment.

Identifiers are case-sensitive. Namespace qualification uses `::`, member
access uses `.`, and index/call syntax uses `[]` and `()`. Target specifier
names and call specifier names are identifiers, but their validity is checked
against the selected target rather than the core keyword list.

## Literal Regions In Documentation Comments

Inside documentation comments, inline code spans and fenced code blocks are
literal regions. The doc-comment parser does not resolve symbol links, child
targets, or doc attributes inside those regions. This matters because code
examples often contain text such as `[Name]`, `@summary`, or `- value:` that
should remain literal.

## Conditional Compilation Model

The preprocessor controls which source tokens are parsed. A declaration hidden
by `#if` is not available to name lookup, metadata, API headers, or generated
code for that compilation. Target-owned defines, command-line defines, and
source defines all contribute to the active condition set.

Keep conditional compilation coarse. Prefer target-conditioned wrappers around
small interop declarations instead of scattering target checks through ordinary
logic.

## Reserved And Target-Sensitive Names

Some names are rejected because they collide with generated C, target runtime,
or compiler-reserved surfaces. A name that is harmless in pure Camp can still
be invalid for a selected target if it cannot be emitted safely. Use meaningful
source names and `@symbol` only when an external ABI requires a specific native
spelling.

## Source File Shape

A Camp source file can contain:

- file-prelude directives such as `#build` and `#within`;
- conditional compilation directives;
- `using` declarations;
- `export as`;
- declarations.

`using` and `export as` are file/module-level source declarations. They are not
runtime statements. Normal declarations can follow them in the same file.

```camp
#build --target clang-macos-x64
#within explicit

export as Sample::Text;

using Std;

export int main()
{
	Console.writeLine("ready");
	return 0;
}
```

`#build` directives are consumed by compiler tooling. `#within` changes the
allocation-context policy for the physical source file where it appears.

## Attribute Tokens

Attributes begin with `@` and are tokenized as attribute identifiers.

```camp
@symbol("native_name")
extern void nativeCall();
```

The attribute name is not an ordinary expression name at that point in the
grammar. Attribute arguments are parsed according to the attribute form and can
include metadata-only expressions such as `symbolof(Name)` where supported.

## Operators And Punctuation

Camp uses familiar C-family punctuation with a few deliberate differences:

| Spelling | Role |
|---|---|
| `::` | Namespace qualification. |
| `.` | Member access for values and pointers. |
| `[]` | Indexing, array types, and fixed-size array types. |
| `()` | Calls, grouping, and parameter lists. |
| `{}` | Blocks, type bodies, enum bodies, and initializer lists. |
| `:` | Base/interface lists, callable ascription, enum underlying type. |
| `@` | Attributes and attribute-like parameter annotations. |
| `#` | Preprocessor and build directives. |

There is no pointer-member `->` operator. Member access always uses `.`.

## Numeric Literal Lexing

Numeric tokens are read before semantic typing. Decimal and hexadecimal integer
spellings are accepted by the lexer, and a decimal point or exponent-style
suffix can make a literal floating-point-shaped for semantic analysis.

```camp
int count = 42;
uint mask = 0x00FF00FF;
double ratio = 0.5;
```

The lexer does not decide the final type of the literal. Binding uses the
destination type when one is available and then applies the primitive literal
conversion rules.

## String Escapes

String and character literals can contain escape sequences. The lexer treats an
escaped character as part of the literal rather than as the end delimiter.

```camp
const char[] line = "first\nsecond";
char quote = '\'';
```

Escape interpretation belongs to literal binding and target text encoding. A
string literal cannot span a physical newline.

## The Discard Name

`_` is reserved as a discard in expression and argument contexts.

```camp
parsePort(text, out _);
readFile(path, catch _);
```

Do not use `_` as a normal declaration name. The discard can receive a value
but cannot be read.

## Conditional Branches And Errors

Inactive preprocessor branches are not parsed as Camp code. They still need to
be lexically balanced enough for the preprocessor to find matching directive
boundaries, but declarations inside inactive branches do not participate in
name lookup, metadata, ABI output, or diagnostics for ordinary Camp semantics.

Keep inactive branches understandable. They are still source code that future
readers and tools must maintain.
