# Proposal: Camp `inline` Constants and Fixed-Representation Enums

Status: proposed compiler-facing behavior.

Goal: let Camp emit typed constants as C macros without addressable storage/linkage, and emit Camp enums with precise underlying sizes on all C targets.

## 1. Inline constants

### Syntax

```camp
[export|public] inline Type NAME = constant_expression;
[export|public] inline const Type NAME = constant_expression;
```

Rules:

- `inline` is reserved and must appear before the type.
- `const` is part of the type, so `inline const char* NAME = ...;` is valid.
- `const inline T NAME = ...;` is invalid; recover and diagnose: write `inline const T NAME = ...`.
- `extern inline` is invalid.
- The initializer is required.
- File-scope and type-scope declarations are supported.
- Local inline declarations are not supported.

### Semantics

An `inline` declaration introduces a typed compile-time constant binding. It has no storage, no address, no linkage, no assignable location, and no `out` identity.

```camp
inline uint MAX_PLAYERS = 8;
uint* p = &MAX_PLAYERS; // ERROR
MAX_PLAYERS = 4;        // ERROR
```

`const` does not change inline semantics.

## 2. Inline initializer evaluation

The compiler must fully evaluate the initializer using Camp semantics and emit the precomputed value. Do not emit a C re-expression of the initializer.

Allowed in inline initializers:

- literals;
- `default`;
- enum values;
- visible inline constants;
- casts among allowed constant types;
- `sizeof(ConcreteType)` where already valid;
- string literals for the string targets listed below.

Forbidden in inline initializers:

- function calls;
- function symbols, except `null`/`default` for `fn` constants;
- ordinary variables;
- `const` variables;
- non-inline globals;
- addressable storage;
- cyclic inline dependencies.

```camp
inline uint A = 10;
inline uint B = A + 5; // OK, value 15

const uint C = 1;
inline uint D = C;     // ERROR: `C` has storage

inline uint E = f();   // ERROR
```

Ordinary variables, including `const` variables, may reference inline constants:

```camp
inline uint A = 10;
const uint B = A; // OK
```

## 3. Eligible inline types

Check eligibility after alias resolution.

Allowed inline types:

| Category | Allowed values |
|---|---|
| scalar primitives | constant value or `default` |
| enum types | constant enum value or `default` |
| value `newtype` over scalar/pointer representation | constant value or `default` |
| pointer types | only `null` or `default` |
| pointer to ordinary aggregate type | only `null` or `default` |
| `fn` types | only `null` or `default` |
| `string`, `wstring`, `astring` | string literal, `null`, or `default` |
| `const char*`, `const wchar*`, `const achar*`, or equivalent alias | string literal, `null`, or `default` |

Examples:

```camp
newtype HWND: nint;
inline HWND HWND_BROADCAST = (HWND)-1;

inline Widget* NO_WIDGET = null;
inline fn void() NO_CALLBACK = default;
inline string APP_NAME = "Camp";
inline const char* RAW_NAME = "Camp";
```

Forbidden inline types or values:

- aggregate inline values, including `default` aggregate values;
- fixed-size array values;
- compiler-expanded forms;
- materialized compiler-expanded forms;
- pointers to fixed-size arrays;
- pointers to compiler-expanded or materialized compiler-expanded forms;
- mutable string literal destinations.

```camp
inline Point ORIGIN = { 0, 0 };         // ERROR
inline Point DEFAULT_POINT = default;   // ERROR
inline Point* NO_POINT = null;          // OK

inline byte[16] BLOCK = default;        // ERROR
inline byte[16]* BLOCK_PTR = null;      // ERROR
inline int? MAYBE = default;            // ERROR
inline struct(int?)* P = null;          // ERROR

inline char* BAD = "Camp";             // ERROR
inline char[] ALSO_BAD = "Camp";       // ERROR
```

## 4. Emission placement

Every inline constant that survives to C is emitted as a typed C `#define`. Generated C uses the macro symbol.

| Camp visibility | C emission location |
|---|---|
| `export inline` | public header |
| `public inline` | generated private header |
| private file-scope inline | top of the generated `.c` for the defining file |

Emit the macro after any typedef/declaration needed by the macro cast type.

Example:

```camp
inline uint MAX_DEVICES = 10;
export const uint MIN_PLAYERS = 1;
export inline uint MAX_PLAYERS = MAX_DEVICES - 1;
```

Public header:

```c
extern const uint32_t MIN_PLAYERS;
#define MAX_PLAYERS ((uint32_t)9u)
```

Generated `.c`:

```c
#define MAX_DEVICES ((uint32_t)10u)
const uint32_t MIN_PLAYERS = 1u;
```

`export const` is an addressable storage object and must not additionally emit a macro. Inline macros are C expression macros; they are not required to work in C `#if`.

## 5. Symbols and collisions

`@symbol("Name")` is allowed on any inline constant and overrides the full generated macro symbol.

Namespaces are erased and do not affect generated symbols.

For type-scoped inline constants, the default symbol is:

```text
ContainingTypeSymbol_MEMBER
```

The containing type's `@symbol` affects the default prefix. The inline constant's own `@symbol` overrides the whole macro name.

```camp
@symbol("HWND")
newtype WindowHandle: nint
{
    @symbol("HWND_BROADCAST") inline WindowHandle BROADCAST = (WindowHandle)-1;
    inline WindowHandle NO_WINDOW = default;
}
```

```c
typedef intptr_t HWND;
#define HWND_BROADCAST ((HWND)(intptr_t)-1)
#define HWND_NO_WINDOW ((HWND)0)
```

An accessible inline constant reserves its Camp binding name and its generated C macro symbol. No visible declaration, type, field, enum value, parameter, local, generated helper, or expanded component may collide with those names in any generated C view where the inline is accessible.

```camp
inline uint COUNT = 10;
void f(uint COUNT) {} // ERROR
```

Apply the collision rule before C emission.

After prefixing and `@symbol` override, an inline constant's generated symbol must not end with `_H_`.

```camp
inline uint CONFIG_H_ = 1;                    // ERROR
@symbol("CONFIG_H_") inline uint CONFIG = 1; // ERROR
```

## 6. Type-scope inlines

Inline constants may appear in `struct`, `class`, and `newtype` bodies. They are type-associated constants, not instance fields, and do not affect layout.

```camp
struct Buffer
{
    inline uint DEFAULT_CAPACITY = 16;
}
```

```c
#define Buffer_DEFAULT_CAPACITY ((uint32_t)16u)
```

Inside generic type declarations, the initializer must be valid without depending on generic type parameters. Erased generics do not create per-instantiation inline constants.

## 7. Camp preprocessor separation

Camp preprocessor symbols and inline constants are separate. Camp conditional compilation uses boolean defined/not-defined symbols only; inline constants are not visible to `#if`/`#elif` evaluation.

Camp preprocessing happens before C emission. Generated C `#define`s for inline constants do not feed back into Camp preprocessing. The backend does not need to output Camp preprocessor condition macros.

## 8. Enum representation

Enum syntax remains:

```camp
[export|public|extern] enum Name[: underlyingType]
{
    VALUE,
    OTHER = expression
}
```

If `underlyingType` is omitted, use `uint`.

Allowed underlying types:

```text
sbyte byte short ushort int uint long ulong nint nuint
```

`nint` and `nuint` are target-sized as usual. Range checking is target-aware.

Evaluate enum member expressions using Camp semantics, then range-check against the underlying type. Out-of-range values are invalid. Negative values for unsigned underlying types are invalid.

```camp
enum Small: byte { A = 255, B } // ERROR: B is 256
enum U: uint { BAD = -1 }       // ERROR
```

C emission must not use native C enums. Emit a typedef of the underlying representation plus typed member macros.

```camp
export enum DifficultyLevel: ushort
{
    EASY,
    HARD = EASY + 100
}
```

```c
typedef uint16_t DifficultyLevel;
#define DifficultyLevel_EASY ((DifficultyLevel)0u)
#define DifficultyLevel_HARD ((DifficultyLevel)100u)
```

## 9. Enum `@symbol`

`@symbol` is allowed on enum types and enum members.

- Type `@symbol` determines the C typedef name.
- Type `@symbol` also determines the default enum member macro prefix.
- Member `@symbol` overrides the full generated member macro symbol.

```camp
@symbol("Difficulty")
export enum DifficultyLevel: ushort
{
    @symbol("DIFFICULTY_EASY") EASY,
    HARD = EASY + 100
}
```

```c
typedef uint16_t Difficulty;
#define DIFFICULTY_EASY ((Difficulty)0u)
#define Difficulty_HARD ((Difficulty)100u)
```

Enum typedef symbols and enum member macro symbols participate in the same generated-symbol collision checks as inline constants.

## 10. Metadata JSON

For inline declarations, add:

```json
"inline": true
```

Inline declarations must include the precomputed value:

```json
{
  "kind": "variable",
  "name": "MAX_PLAYERS",
  "type": "uint",
  "visibility": "export",
  "inline": true,
  "value": 9
}
```

Use JSON `null` for pointer/`fn` null/default values. Use the string content for string literal values.

Do not emit source initializer text for inline constants.

Do not emit `value` for ordinary variables, including `const` variables.

Enum values continue to include their computed numeric `value`.

## 11. Required diagnostic coverage

Diagnose: bad `inline`/`const` order, `extern inline`, missing initializer, local inline declaration, invalid inline type category, invalid pointer/`fn` initializer, forbidden initializer reference, dependency cycle, generated-symbol collision, generated inline symbol ending in `_H_`, out-of-range enum value, negative value for unsigned enum, and enum/inline `@symbol` collision.

## 12. Combined acceptance example

```camp
inline uint MAX_DEVICES = 10;
public inline uint INTERNAL_LIMIT = MAX_DEVICES * 2;
export inline uint MAX_PLAYERS = MAX_DEVICES - 1;
export const uint MIN_PLAYERS = 1;

newtype HWND: nint;
inline HWND HWND_BROADCAST = (HWND)-1;

inline Widget* NO_WIDGET = null;
inline fn void() NO_CALLBACK = default;
inline string APP_NAME = "Camp";

struct Limits { inline uint DEFAULT_CAPACITY = 16; }

@symbol("Difficulty")
export enum DifficultyLevel: ushort
{
    @symbol("DIFFICULTY_EASY") EASY,
    HARD = EASY + 100
}
```

Representative C surfaces:

```c
/* public header */
extern const uint32_t MIN_PLAYERS;
#define MAX_PLAYERS ((uint32_t)9u)
typedef uint16_t Difficulty;
#define DIFFICULTY_EASY ((Difficulty)0u)
#define Difficulty_HARD ((Difficulty)100u)

/* private header */
#define INTERNAL_LIMIT ((uint32_t)20u)

/* generated .c */
#define MAX_DEVICES ((uint32_t)10u)
#define HWND_BROADCAST ((HWND)(intptr_t)-1)
#define NO_WIDGET ((Widget*)0)
#define NO_CALLBACK ((void (*)(void))0)
#define APP_NAME ((const char*)"Camp")
#define Limits_DEFAULT_CAPACITY ((uint32_t)16u)
const uint32_t MIN_PLAYERS = 1u;
```
