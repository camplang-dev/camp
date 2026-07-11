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

String literals produce counted text values that can be used with array and
string APIs. Character literals represent character values, while string
literal storage and Unicode details are covered with arrays and strings.

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
