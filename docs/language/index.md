# Camp Language Reference

This directory contains the version 1 Camp language reference. It describes the
current source language for people writing Camp code. Compiler command-line,
build, package, metadata, and compiler-writer details live in sibling
documentation areas.

## 1. Overview

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

## 2. Lexical Structure

- Files, Whitespace, And Comments
- Identifiers And Keywords
- Literals
- Strings And Characters
- Preprocessor Directives
- Grammar Notation

## 3. Namespaces, Imports, And Visibility

- Qualified Names
- `using`
- `export as`
- Visibility Keywords
- Source Symbols And ABI Symbols
- Name Lookup Summary

## 4. Declarations

- Declaration Forms
- Declaration Grammar Summary

## 5. Type System Overview

- Type Categories
- Type Spelling
- Value, Reference, And Expanded Forms
- Type Qualifiers
- Target Specifiers And Call Specifiers
- When Types Are Nominal

## 6. Primitive Values And Literals

- Integer Types
- Natural Integer Types
- Floating-Point Types
- Boolean Values
- Character Types
- `void`, `default`, `null`, And `untyped`
- Literal Conversion Rules

## 7. Pointers, Qualifiers, And Conversions

- Pointer Types
- `const`, `volatile`, And `constof`
- Target Type Specifiers
- Implicit And Explicit Conversions
- `unsafe` Casts
- Raw Carriers
- Conversion Limits And Reconstruction

## 8. Functions And Callables

- Function Declarations
- Parameters And Arguments
- Default Arguments
- Named Arguments
- Receiver Parameters
- Callable Types
- Delegates, `once`, And Function Pointers
- Callable Newtypes
- Lambdas

## 9. Expressions And Operators

- Expression Grammar
- Operator Precedence
- Calls, Indexing, And Member Access
- Casts
- Construction Expressions
- Initializer Lists
- `sizeof`, `vtableof`, `typenameof`, And `symbolof`
- Target Typing

## 10. Statements And Control Flow

- Blocks And Empty Statements
- Local Declarations
- `if`, `while`, `do`, And `for`
- `switch`, `case`, And `default`
- `break`, `continue`, `goto`, And Labels
- `return` And `yield`
- `foreach`
- Statement Conditions

## 11. Structs, Classes, And Lifecycle

- Structs
- Classes
- Fields And Static Fields
- Methods And Receivers
- Constructors
- Destructors
- `init`, `new`, And `delete`
- Abstract, Virtual, Override, Sealed, And Extern Types

## 12. Interfaces And Dispatch

- Interface Declarations
- Implementing Interfaces
- Interface Inheritance
- Required, Defaulted, And Optional Methods
- Interface Calls
- Interface Conversions
- `vtableof`

## 13. Enums, Newtypes, And Inline Constants

- Enums
- Fixed-Representation Enums
- Enum Values And Symbols
- `newtype`
- Callable Newtypes
- Inline Constants
- Type-Scope Constants

## 14. Arrays, Slices, Optionals, And Strings

- Array Values
- Fixed-Size Arrays
- Array Literals
- Slices And Ranges
- Optional Values
- String Literals
- Counted Text
- Standard Array And String Helpers

## 15. Errors, Thrown, And Cleanup

- `thrown` Parameters
- `throw`
- `try`, `catch`, And `finally`
- Catch Arguments
- Cleanup Expressions
- Error Propagation In Calls

## 16. Lifetimes, Allocation, And `within`

- Lifetime Annotations
- Default Lifetimes
- `escaped`, `scoped`, And `unscoped`
- Lifetime Anchors
- Allocators And `within`
- Source-Level Allocation
- Safe Lifetime Casts

## 17. Generics And Type Capabilities

- Generic Type And Function Declarations
- Constraints
- `T: any`
- `T: copyable`
- Interface Constraints
- `sizeof(T)`, `typenameof(T)`, And `vtableof(T: Interface)`
- Generic Construction And Destruction
- Generic Static Members

## 18. Iterators, `foreach`, And Generators

- Iterator Type Forms
- Iterator Protocol
- Generator Functions
- `yield`
- `foreach`
- Iterator Cleanup
- Iterator Limitations

## 19. Async, Await, And Postpone

- Async Callable Shape
- Completion Callbacks
- `await`
- Resumer Selection
- `@awaitwith`
- `@noawait`
- `postpone`
- Async Frames And Allocation
- Async Lambdas And Callable Values
- Async Error Propagation

## 20. Attributes And Doc Comments

- Attribute Syntax
- Metadata Attributes
- Documentation Comments
- Child Documentation Targets
- Symbol Links
- Literal Regions
- Direct Attribute Authoring
- Relationship To Metadata JSON

## 21. Standard Library And Interop

- Standard Library Availability
- Arrays And Strings
- Console And Streams
- Files
- Time And Math
- Native Interop Declarations
- Call Specs And Type Specs
- Target-Conditioned Code
