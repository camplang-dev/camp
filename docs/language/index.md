# Camp Language Reference

This directory contains the version 1 Camp language reference. It describes the
source language for people writing Camp code. Compiler command-line, build,
package, metadata, and compiler-writer details live in sibling documentation
areas.

## [1. Overview](01-overview.md)

- What Camp Is
- Language Goals
- Language Non-Goals
- Who Camp Is For
- Who Camp Is Not For
- Novel Aspects
- Influences And Borrowed Ideas
- Source Files And Compilation Units
- A Minimal Program
- Documentation Map
- Syntax Used In Examples
- Core Design Commitments
- Reading Order
- What Counts As User-Facing Detail

## [2. Lexical Structure](02-lexical-structure.md)

- Files, Whitespace, And Comments
- Identifiers And Keywords
- Literals
- Strings And Characters
- Preprocessor Directives
- Grammar Notation
- Token Boundaries And Trivia
- Literal Regions In Documentation Comments
- Conditional Compilation Model
- Reserved And Target-Sensitive Names
- Source File Shape
- Attribute Tokens
- Operators And Punctuation
- Numeric Literal Lexing
- String Escapes
- The Discard Name
- Conditional Branches And Errors

## [3. Namespaces, Imports, And Visibility](03-namespaces-imports-visibility.md)

- Qualified Names
- `using`
- `export as`
- Visibility Keywords
- Source Symbols And ABI Symbols
- Name Lookup Summary
- Default Visibility
- Exported Members Of Non-Exported Types
- Foreign Definitions
- Aliases
- Generated Views
- Namespace Qualification
- Selected Imports
- Import Aliases
- Export Namespaces
- Visibility And Headers
- Private Helpers Behind Exported APIs
- Alias Boundaries
- Extern Visibility
- Name Lookup Pitfalls

## [4. Declarations](04-declarations.md)

- Declaration Forms
- Declaration Grammar Summary
- Type Declarations
- Member Declarations
- Modifiers
- Declaration Children
- Declaration Order And Generated Declarations
- File-Level Declarations
- Variables And Constants
- Function-Like Declarations
- Bodies And Semicolons
- Generic Declarations
- Receiver Declarations
- Interface Implementation Markers
- Declaration Names And Symbols
- Invalid Combinations
- Generated Surface Is Not Extra Source Authority

## [5. Type System Overview](05-type-system-overview.md)

- Type Categories
- Type Spelling
- Value, Reference, And Expanded Forms
- Type Qualifiers
- Target Specifiers And Call Specifiers
- When Types Are Nominal
- Expanded Forms
- Storage, View, And Ownership
- Source Type Versus ABI Shape
- Primitive And Library Type Names
- Pointer And View Types
- Value-Like And Identity-Like Types
- Copyability And Fixed Storage
- Materialized Expanded Forms
- Callable Type Families
- Interface Type Family
- Type Equality And Compatibility
- Metadata Type Spelling

## [6. Primitive Values And Literals](06-primitive-values-and-literals.md)

- Primitive Type Table
- Integer Types
- Natural Integer Types
- Floating-Point Types
- Boolean Values
- Character Types
- Primitive String Types
- Numeric Literals
- String Literals
- Character Literals
- `void`
- `default`
- `null`
- `untyped`
- Primitive Defaults And Status Values
- Literal Portability

## [7. Pointers, Qualifiers, And Conversions](07-pointers-qualifiers-and-conversions.md)

- Pointer Types
- `const`, `volatile`, And `constof`
- Target Type Specifiers
- Implicit And Explicit Conversions
- `unsafe` Casts
- Raw Carriers
- Conversion Limits And Reconstruction
- Class, Interface, And Newtype Boundaries
- Arrays, Optionals, And Expanded Forms
- Diagnostics To Expect

## [8. Functions And Callables](08-functions-and-callables.md)

- Function Declarations
- Parameters And Arguments
- Default Arguments
- Named Arguments
- Trailing `out` Result Binding
- Receiver Parameters
- Callable Types
- Delegates, `once`, And Function Pointers
- Callable Newtypes
- Lambdas
- Method References
- Overloads And Callable Ascription
- Async And Iterator Callable Surfaces

## [9. Expressions And Operators](09-expressions-and-operators.md)

- Expression Typing
- Evaluation Model
- Operator Precedence
- Boolean Operators
- Arithmetic And Bitwise Operators
- Null Coalescing
- Assignment
- Names And Member Access
- Expanded Components
- Calls
- Overload Selector Parameters
- Method References
- Property Access
- Indexing
- Ranges And Slices
- Casts
- Construction Expressions
- Initializer Lists
- `sizeof`, `typenameof`, `vtableof`, And `symbolof`
- `await`, `postpone`, And `throw`
- `within` Expressions
- Expression-Level Cleanup
- Target Typing And `auto`

## [10. Statements And Control Flow](10-statements-and-control-flow.md)

- Blocks And Scope
- Local Declarations
- Fixed Storage Locals
- Expression Statements
- `if`
- `while` And `do`
- `for`
- `switch`, `case`, And `default`
- `break` And `continue`
- Labels And `goto`
- `return`
- Trailing `out` Result Binding
- `yield`
- `foreach`
- `try`, `catch`, And `finally`
- `throw`
- `delete`
- `within` Statements
- Async Control Flow
- Statement Conditions
- Discards

## [11. Structs, Classes, And Lifecycle](11-structs-classes-and-lifecycle.md)

- Structs
- Classes
- Fields And Static Fields
- Methods And Receivers
- Constructors
- Destructors
- `init`, `new`, And `delete`
- Abstract, Virtual, Override, Sealed, And Extern Types
- Object Layout And ABI Surface
- Common Lifecycle Patterns

## [12. Interfaces And Dispatch](12-interfaces-and-dispatch.md)

- Interface Declarations
- Interface Values And ABI Model
- Implementing Interfaces
- Required, Defaulted, And Optional Methods
- Interface Inheritance
- Lifetime And Constness In Interface Contracts
- Class Implementation Of Interfaces
- Struct Implementation Of Interfaces
- Interface Constructors And Destructors
- Interface Calls And Optional Slot Checks
- Interface Conversions
- `vtableof`
- Metadata And API Surface
- Cost Model
- Mental Model

## [13. Enums, Newtypes, And Inline Constants](13-enums-newtypes-and-inline-constants.md)

- Enums
- Enum Underlying Types
- Enum Values
- Enums In Error And Status APIs
- Enum ABI Representation
- Enum Symbols And Metadata
- `newtype`
- Value Newtypes
- Callable Newtypes
- Callable Ascription
- Newtype Members
- Newtype `this`
- Newtype ABI Representation
- Inline Constants
- Inline Constant Types
- Type-Scope Inline Constants
- Inline Constants And Symbols
- Choosing Between The Three

## [14. Arrays, Slices, Optionals, And Strings](14-arrays-slices-optionals-and-strings.md)

- Array Values
- Array Carrier Versus Element Type
- Fixed-Size Arrays
- Array Literals
- Indexing
- Slices And Ranges
- Optional Values
- String Literals
- Counted Text
- Strings, UTF-8, And Character Helpers
- Arrays In Generic Code
- Arrays, Lifetimes, And Ownership
- Standard Array And String Helpers

## [15. Errors, Thrown, And Cleanup](15-errors-thrown-and-cleanup.md)

- Error Values
- `thrown` Parameters
- `thrown(E)` Return Form
- `throw`
- Propagation
- Catch Arguments
- `try` And `catch`
- `finally` Blocks
- `finally` Cleanup Statements
- Expression-Level Cleanup
- `finally delete`
- Cleanup And Ownership
- Thrown Flow Through Iterators
- Thrown Flow Through Async
- ABI Shape
- Choosing Error Shapes
- Diagnostics To Expect

## [16. Lifetimes, Allocation, And `within`](16-lifetimes-allocation-and-within.md)

- Lifetime Annotations
- Default Lifetimes
- `escaped`, `scoped`, And `unscoped`
- Lifetime Anchors
- Allocators And `within`
- Source-Level Allocation
- Safe Lifetime Casts
- Slots And Values
- Aggregate And Container Rule
- Returns, Yields, And Captures
- Async And Suspension
- Common Pitfalls

## [17. Generics And Type Capabilities](17-generics-and-type-capabilities.md)

- Generic Type And Function Declarations
- Constraints
- `T: any`
- `T: copyable`
- Interface Constraints
- `sizeof(T)`, `typenameof(T)`, And `vtableof(T: Interface)`
- Generic Construction And Destruction
- Generic Static Members
- Generic Interfaces And Generic Methods
- Arrays, Optionals, Delegates, And Strings In Generic Code
- Lifetimes And Async In Generic Code
- Common Generic Design Patterns

## [18. Iterators, `foreach`, And Generators](18-iterators-foreach-and-generators.md)

- Iterator Type Forms
- Plain Iterator Values
- Generator Declarations
- Generator Parameters
- Failing Iterators
- `yield`
- Iterator Protocol
- `foreach`
- `foreach` And Thrown Flow
- Manual Iteration
- Iterator Cleanup
- Arrays And `foreach`
- Iterator Values As Callables
- Generic Iterators
- Iterators And Expanded Values
- `struct iter` Versus `class iter`
- Common Pitfalls

## [19. Async, Await, And Postpone](19-async-await-and-postpone.md)

- Async Callable Shape
- Signature Rewrite
- Completion Callbacks
- `await`
- Await Error Handling
- Resumer Selection
- `resumeAsync`
- `@awaitwith`
- `@noawait`
- Async State Machines
- Async Frames And Allocation
- Calling Async Functions Without `await`
- `once`
- `postpone`
- Async Lambdas And Callable Values
- Lifetimes Across Suspension
- Structural Async And Interop

## [20. Attributes And Doc Comments](20-attributes-and-doc-comments.md)

- Attribute Syntax
- Attribute Attachment
- Source And ABI Attributes
- Metadata Attributes
- Documentation Comment Forms
- Lowering To Metadata Attributes
- Child Documentation Targets
- Documenting Members And Enum Values
- Symbol Links
- `symbolof`
- Literal Regions
- Examples
- Deprecation
- Attribute Arguments And Source Text
- Metadata JSON Relationship
- Authoring Conventions

## [21. Standard Library And Interop](21-standard-library-and-interop.md)

- Standard Library Availability
- What Is Language Versus Library
- Allocation Library Surface
- Arrays
- Strings And Counted Text
- Formatting
- Console
- Streams
- Files
- Collections
- Math
- Time And Timing
- Native Interop With `extern`
- Extern Classes
- Source Symbols And Native Symbols
- Call Specs
- Type Specs
- Raw Function Pointers
- Arrays And Native Boundaries
- Strings And Native Boundaries
- Pointers And Ownership At Native Boundaries
- Target-Conditioned Code
- Generated Headers And Metadata
- Interop Design Guidelines
