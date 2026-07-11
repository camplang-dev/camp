# Semantic Supplements

These supplements are for compiler writers and advanced maintainers. They are
normative for compiler behavior, diagnostics, lowering, metadata, and emitted
code. Ordinary Camp users should start with the language reference.

## 1. Binding, Analysis, And Lowering Pipeline

- Parse Model
- Bindable Node Model
- Analysis Scopes
- Declaration Passes
- Body Analysis
- Expansion
- Lowering
- Emission
- Generated Declaration Factory
- Provenance And Source Ranges

## 2. Expanded Forms And ABI Shapes

- Expanded Form Definition
- Arrays
- Delegates And `once`
- Iterators
- Async Callable Shapes
- Grouped Params
- Thrown Params
- Component Naming
- API Surface Versus Lowered Shape

## 3. Conversions, Raw Carriers, And Fence Casts

- Conversion Levels
- Target Specifier Domains
- Raw Carrier Families
- Const And Volatile Overrides
- Pointer Family Rules
- Callable Rules
- Array And Optional Rules
- Generic Constructed Types
- Diagnostics And Warnings
- Test Matrix

## 4. `constof` And Signature Compatibility

- Anchor Binding
- Caller-Visible Substitution
- Callee Implementation View
- Storage Conversions
- Parameter Passing
- Return And `out` Positions
- Callable Variance
- Override Exactness
- Lambda Target Typing
- `constof(this)`
- Diagnostics

## 5. Lifetime Analysis And Flow Facts

- Bound Lifetime Model
- Lifetime Anchors
- Slot Facts And Value Facts
- Defaults
- Assignment And Storage
- Return, Yield, And Delete
- Call-Site Relation Solving
- Constructors And Retained Values
- Delegates And Captures
- Iterators And Generated Contexts
- Generics
- Diagnostics

## 6. Generics, Erasure, And Capabilities

- Generic Parameter Binding
- Constraints
- Erased Versus Materialized Values
- `T: any`
- `T: copyable`
- Size, VTable, And Type Name Capabilities
- Generic Arrays And Iterators
- Generic Construction And Destruction
- Static Members
- Diagnostics

## 7. Callable Lowering And Context Ownership

- Callable Shape Service
- Direct Functions
- Delegates
- `once`
- Callable Newtypes
- Method References
- Lambdas
- Escaped Context Allocation
- Capture Layout
- Context Deletion
- Default Arguments And Thunks

## 8. Async Resumption Lowering

- Async Callable Expansion
- Await Site Collection
- Completion Callback Shape
- Resumer Selection
- `resumeAsync`
- `@awaitwith`
- `@noawait`
- State Machine Frames
- Tail Await Forwarding
- Error Propagation
- `postpone`
- Async Diagnostics

## 9. Interface VTables And Dynamic Dispatch

- Interface Shape
- Slot Function Types
- Required Slots
- Defaulted Slots
- Optional Slots
- Struct Conformance
- Class Conformance
- Interface Inheritance
- VTable Generation
- `vtableof`
- Interface Conversions
- Diagnostics

## 10. Construction, Destruction, And Allocation

- Constructor Binding
- Default Constructors
- Destructors
- Base Initialization
- `init`
- `new`
- `delete`
- Allocator Selection
- Async And Iterator Restrictions
- Extern Type Boundaries
- Diagnostics

## 11. Metadata, API Surface, And Symbols

- API Header Model
- Metadata View Model
- Export/Public/All Filtering
- Symbol Names
- Metadata IDs
- Doc Comment Translation
- Stubs
- Type And Function Object Details
- Generated Versus Source Declarations

## 12. Target Capabilities And C Emission

- Target Definition Resolution
- Target-Owned Defines
- Type Specs And Call Specs
- Conversion Policy Tables
- Primitive C Spelling
- C Reserved Identifiers
- Symbol Emission
- Shared Library Export/Import
- Object, Static, Shared, And Executable Artifacts
- Objective-C Capability Boundary

## 13. Diagnostics, Source Ranges, And Error Quality

- Diagnostic Severity
- Stable Codes
- Parser Diagnostics
- Analysis Diagnostics
- Source Ranges
- Warnings
- Golden Diagnostic Tests
- LSP Diagnostic Mapping
- Error Message Style
