# Camp Language Guide

This guide is organized for a first read, then for return visits. The early
topics introduce Camp through ordinary programs and API shapes. The later
topics become progressively more reference-oriented, ending with native
interop, expressions/statements/operators, and metadata attributes.

## 1. [Camp In One Page](01-camp-in-one-page.md)

1. A Tiny Program
2. What Camp Is Trying To Be
3. Familiar Ground
4. What Camp Makes Explicit
5. ABI Boundaries Are First-Class
6. What Camp Leaves Out
7. What Camp Borrows
8. Where Camp Asks For Care
9. When Camp Is Probably The Wrong Tool
10. The Road Ahead

## 2. [Your First Camp Program](02-your-first-camp-program.md)

1. A Complete File
2. Imports Bring Names Into View
3. `main` Is An Ordinary Function At The Boundary
4. Local Values And Calls
5. Splitting Work Into Helpers
6. What The Compiler Sees
7. A Slightly Less Tiny Program
8. What You Have Now

## 3. [Declarations And Program Shape](03-declarations-and-program-shape.md)

1. A File Is Not A Script
2. Top-Level Declarations
3. Functions
4. Bodies And Semicolons
5. Type Declarations
6. Members Inside Types
7. Visibility And Exported Shape
8. Static Members And Inline Constants
9. Generic Declarations At A Glance
10. Choosing The Right Declaration Form

## 4. [Everyday Types: Values, Text, Arrays, And Optionals](04-everyday-types-values-text-arrays-and-optionals.md)

1. A Small Program With Everyday Types
2. Values, Views, And Storage
3. Primitive Scalars
4. Text: `string` And Counted Character Views
5. Arrays Are Counted Views
6. Custom Indexing And Slicing
7. Fixed-Size Arrays Store Inline
8. Allocated And Initialized Arrays
9. Optional Values
10. `default`, `null`, And Literals
11. Choosing The Right Everyday Type

## 5. [Structs, Classes, And Object Lifetimes](05-structs-classes-and-object-lifetimes.md)

1. A Record And An Object
2. Structs As Plain Values
3. Fixed Structs
4. Classes As Identity-Bearing Objects
5. Fields, Static Members, And Inline Constants
6. Methods And Receivers
7. Constructors And Initialization
8. Object Initializers After Construction
9. Destructors And Cleanup
10. `new`, `init`, And `delete` In Context
11. Virtual, Abstract, Override, And Sealed
12. Extern Classes And Native Objects
13. Class-Relative `classtype`
14. Layout, Opacity, And Exported APIs
15. Common Lifecycle Patterns

## 6. [Functions, Methods, And Callables](06-functions-methods-and-callables.md)

1. Function Declarations
2. Parameters And Arguments
3. Default Arguments
4. `out` Parameters
5. `thrown` Parameters And `catch` Arguments
6. Intentional Discards
7. `within` Parameters And Allocation Context
8. Methods And Receiver Calls
9. Property Accessors
10. Receiver-Preserving `this` Returns
11. Callable Type Families
12. `fn` Values
13. Delegates And Captured Context
14. `once` Callbacks
15. Callable Newtypes And Ascription
16. Lambdas
17. Method References
18. Overloads And Overload Selectors
19. Async And Iterator Surfaces At A Glance
20. Choosing The Right Callable Form

## 7. [Lifetimes, Allocation, And `within`](07-lifetimes-allocation-and-within.md)

1. The Shape Of The Problem
2. The Lifetime Vocabulary
3. Anchors
4. `escaped`
5. `scoped`
6. `unscoped(anchor)`
7. Allocation Contexts With `within`
8. `new`, `delete`, And Matching Contexts
9. Captures And Callback Lifetimes
10. Aggregates And Retained Fields
11. Lifetime Casts
12. Common Fixes

## 8. [Pointers, Constness, And Conversion Boundaries](08-pointers-constness-and-conversion-boundaries.md)

1. Pointer Values
2. Pointer Depth
3. `const` And `volatile`
4. `constof(anchor)`
5. Dependent Constness In Parameters
6. Target Type Specifiers
7. Conversion Levels
8. Ordinary Explicit Casts
9. `unsafe` Casts
10. Raw Data Carriers
11. Raw Function Carriers
12. `untyped`
13. Reconstruct Instead Of Cast
14. Class, Interface, And Newtype Boundaries
15. Examples Worth Remembering

## 9. [Errors, Cleanup, And Ownership Flow](09-errors-cleanup-and-ownership-flow.md)

1. Error Values
2. `thrown` Slots
3. `throw`
4. Propagation
5. Catch Arguments
6. `try` And `catch`
7. `finally` Blocks
8. Scope Cleanup
9. Expression Cleanup
10. `thrown(E)` Return Form
11. Choosing An Error Shape
12. Ownership Patterns

## 10. [Names, Imports, Visibility, And Symbols](10-names-imports-visibility-and-symbols.md)

1. Qualified Names
2. `using`
3. `namespace`
4. Private, `internal`, `public`, And `export`
5. Exported Types And ABI Shape
6. Exported Functions
7. `extern`
8. `@symbol`
9. Symbol Rules
10. Aliases
11. Public Headers And Private Headers
12. Organizing A Small Library

## 11. [Enums, Newtypes, And Inline Constants](11-enums-newtypes-and-inline-constants.md)

1. A Small API Shape
2. Enums For Named Choices
3. Underlying Types And Numbering
4. Status And Error Enums
5. Enum ABI And Symbols
6. Value Newtypes
7. Newtype Methods
8. Callable Newtypes As API Names
9. Inline Constants
10. Type-Scoped Constants
11. Inline Constant Symbols
12. Small Names, Stronger APIs

## 12. [Interfaces And Dynamic Dispatch](12-interfaces-and-dynamic-dispatch.md)

1. A First Interface
2. `Interface` And `Interface*`
3. How An Interface Call Works
4. Class Implementations
5. Struct Implementations
6. Required, Optional, And Defaulted Slots
7. Interface Inheritance
8. Constness And Lifetimes
9. Interface Conversions
10. `vtableof` At A Glance
11. Designing Interface APIs

## 13. [Generics And Capabilities](13-generics-and-capabilities.md)

1. The Shape Of A Generic Declaration
2. Checked Once, Not Per Instantiation
3. Constraints At A Glance
4. Representation Generics
5. Type Erasure With `T: any`
6. `in T` As Erased Value Transport
7. `T*` Means Storage Form
8. Copyable Erased Values
9. `sizeof(T)`
10. `typenameof(T)`
11. Interface Constraints And `vtableof`
12. Generic Interfaces And Generic Methods
13. Arrays And Other Expanded Forms
14. Generic Construction And Cleanup
15. Static Members
16. Designing Generic APIs

## 14. [Iterators, `foreach`, And Generated Sequences](14-iterators-foreach-and-generated-sequences.md)

1. The Everyday Shape
2. Iterator Type Forms
3. Plain Iterator Values
4. Generator Declarations
5. What Calling A Generator Does
6. `yield`
7. Failing Iterators
8. `foreach`
9. The Iterator Protocol
10. Manual Iteration
11. Cleanup
12. ABI Shape For `struct iter`
13. ABI Shape For `class iter`
14. Iterator Values As Callables
15. Arrays And `foreach`
16. Generic Iterators
17. Iterators And Expanded Values
18. Choosing `struct iter` Or `class iter`
19. Common Mistakes

## 15. [Async, Await, And Deferred Calls](15-async-await-and-deferred-calls.md)

1. A Small Awaited Function
2. The Shape Of An Async API
3. A Real Flow: Load, Parse, Display
4. Awaitable Result Shapes
5. Error Handling
6. Resuming In The Right Place
7. What A Resumer Provides
8. Writing Async Bodies
9. Lifetimes Across `await`
10. Cleanup In Async Code
11. Manual Async Calls
12. `once` And Completion Ownership
13. `postpone`
14. Async At ABI Boundaries
15. What Async Does Not Add
16. Designing Async APIs

## 16. [The Standard Library In Practice](16-the-standard-library-in-practice.md)

1. Importing `Std`
2. Language Forms Versus Library APIs
3. Console I/O
4. Allocation And `within`
5. Arrays
6. Text And Strings
7. Formatting
8. Files
9. Streams
10. Lists
11. Hash Maps And Hash Sets
12. Math Helpers
13. Time
14. Timing, Timers, And Atomics
15. Reading The Library

## 17. [Native Interop And ABI Boundaries](17-native-interop-and-abi-boundaries.md)

1. A First Native Wrapper
2. Source Surface And Native Shape
3. Exported Functions
4. Structs And Classes Across The Boundary
5. Extern Classes And Opaque Native Values
6. Symbols And Native Spelling
7. Call Specs And Type Specs
8. Newtypes, Enums, And Inline Constants
9. Arrays And Counted Text
10. Zero-Terminated Strings
11. `out`, `thrown`, And C-Style Results
12. `within` And Allocator Context
13. Raw Function Pointers And Camp Callables
14. Interfaces Across The ABI
15. Iterators Across The ABI
16. Async Across The ABI
17. Generic Code At ABI Boundaries
18. Target-Conditioned Imports
19. Generated Headers And Metadata
20. Interop Design Patterns

## 18. [Expressions, Statements, And Operators Reference](18-expressions-statements-and-operators-reference.md)

1. Expression Typing And Target Typing
2. Literals
3. Operator Precedence
4. Arithmetic, Bitwise, And Boolean Operators
5. Assignment And Update
6. Names And Member Access
7. Property And Indexer Rewrites
8. Indexing, From-End Indexes, And Ranges
9. Calls And Arguments
10. Trailing `out` Result Binding
11. Method References And Callable Invocation
12. Lambdas
13. Casts And Conversion Boundaries
14. Construction, Initialization, And Cleanup Expressions
15. Special Expressions
16. Blocks, Locals, And Fixed Storage
17. Conditions
18. Control-Flow Statements
19. `switch`, `case`, And `default`
20. `foreach` And `yield`
21. Error Handling And Cleanup Statements
22. `within` Statements
23. Async And Deferred Call Forms
24. Quick Statement Table
25. Quick Expression Table

## 19. [Attributes, Documentation Comments, And Metadata Hints](19-attributes-documentation-comments-and-metadata-hints.md)

1. Attribute Syntax
2. Documentation Comments
3. Documentation Attributes
4. Child Targets
5. Links And `symbolof`
6. Examples In Documentation
7. Target Availability With `@notsupported`
8. Metadata Attributes And Generated Output
9. Attribute Summary
10. Where To Look Back
