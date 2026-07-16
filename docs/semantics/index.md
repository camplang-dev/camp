# Semantic Supplements

These supplements are for compiler writers and advanced maintainers. They are
normative for compiler behavior, diagnostics, lowering, metadata, and emitted
code. Ordinary Camp users should start with the language guide.

## 1. Binding, Analysis, And Lowering Pipeline

- Source Files And Compilation
- Preprocessing
- Tokenization And Parsing
- Bindable Node Construction
- Analysis Scopes
- Analyzer Passes
- Declaration Collection
- Type Binding
- Declaration Validation
- Expansion
- Body Analysis
- Flow Analysis
- Lowering
- Emission
- Provenance And Diagnostics
- Compiler Writer Checklist

## 2. Expanded Forms And ABI Shapes

- Expanded Form Definition
- Source Surface Versus ABI Surface
- Component Naming
- Arrays
- Target Specs On Expanded Carriers
- Fixed-Size Arrays
- Optionals
- Delegates And `once`
- Async Callable Shapes
- Iterators
- Grouped Params
- Thrown Params
- Materialized Storage With `struct(T)`
- Expanded Returns
- Generic Expanded Forms
- API, Metadata, And Dumps
- Test Expectations

## 3. Conversions, Raw Carriers, And Fence Casts

- Conversion Levels
- Cast Forms
- Value Conversion Versus Type Rewrite
- Target Specs And Call Specs
- Raw Carrier Families
- `void*` Fences
- `fn*` Fences
- `nint` And `nuint`
- `untyped`
- Const And Volatile Overrides
- Lifetime Overrides
- Physical Pointer Depth
- Data Pointer Families
- Class Pointer Casts
- Interface Pointer Casts
- Callable Conversions
- Expanded And Generic Types
- Target Conversion Policy
- Warnings
- Test Matrix

## 4. `constof` And Signature Compatibility

- Terms
- Anchor Binding
- Caller-Visible Substitution
- Callee View
- Storage Conversion Lattice
- Common Type Inference
- Parameter Passing Equality
- Return And `out` Positions
- Callable Variance
- Surfaces That Use Callable Compatibility
- Override Exactness
- Lambda Target Typing
- Callable `this`
- Metadata And API Output
- Diagnostics
- Test Expectations

## 5. Lifetime Analysis And Flow Facts

- Tracked Types
- Fact Form
- Lifetime Kinds
- Anchors
- Slot Facts And Value Facts
- Declaration Defaults
- Expression Facts
- Call-Site Relation Solving
- Assignment And Storage
- Fields
- Return, Yield, And Delete
- Construction And Retention
- Delegates And Captures
- Async And Iterator Frames
- Generic Boundaries
- Casts
- Diagnostics
- Test Surface
- Implementation Anchors

## 6. Generics, Erasure, And Capabilities

- Binding Model
- Receiver-Relative Type Forms
- Constraint Categories
- Erased Versus Materialized Values
- `T: any`
- `T: copyable`
- Size, VTable, And Type Name Capabilities
- Generic Arrays And Iterators
- Interface-Constrained Generics
- Generic Construction And Destruction
- Materialized Generic Results
- Static Members
- Generic Callable Policy
- Metadata And API Output
- Diagnostics
- Test Surface
- Implementation Anchors

## 7. Callable Lowering And Context Ownership

- Callable Shape
- Shape Expansion
- Direct Functions
- Delegates
- `once`
- Callable Newtypes
- Method References
- Lambdas
- Capture Collection
- Scoped Versus Escaped Contexts
- Escaped Context Allocation
- Capture Layout
- Context Deletion
- Default Arguments And Thunks
- Interface And Virtual Calls
- Async Callable Values
- Diagnostics
- Test Surface
- Implementation Anchors

## 8. Async Resumption Lowering

- Source Surface And ABI Surface
- Completion Callback Shape
- Await Site Collection
- `@noawait`
- Resumer Selection
- `@awaitwith`
- `resumeAsync`
- State Machine Frames
- Frame Allocation
- Await Lowering
- Manual Async Calls
- Tail Await Forwarding
- Error Propagation
- `postpone`
- Async Diagnostics
- Metadata And API
- Test Surface
- Implementation Anchors

## 9. Interface VTables And Dynamic Dispatch

- Source Interface Shape
- Slot Function Types
- Required Slots
- Defaulted Slots
- Optional Slots
- Interface Constructors
- Interface Destructors
- Struct Conformance
- Class Conformance
- Interface Inheritance
- VTable Generation
- `vtableof`
- Interface Conversions
- Virtual Class Dispatch
- Metadata And API
- Diagnostics
- Test Surface
- Implementation Anchors

## 10. Construction, Destruction, And Allocation

- Lifecycle Declarations
- Constructor Binding
- Default Constructors
- Definite Assignment
- Fixed Structs And Copyability
- Destructors
- Base Initialization
- `init`
- Initializer Lists
- Trailing Construction Initializers
- `new`
- `delete`
- Allocator Selection
- Allocator Lifetime
- Generated Cleanup And `finally`
- Async And Iterator Restrictions
- Extern Type Boundaries
- Interface And Virtual Lifecycle
- Metadata And API
- Diagnostics
- Test Surface
- Implementation Anchors

## 11. Metadata, API Surface, And Symbols

- API Header Model
- Metadata View Model
- Export/Public/All Filtering
- Generated Versus Source Declarations
- Symbol Names
- Symbol Collisions
- Metadata IDs
- Doc Comment Translation
- Attributes
- Property Metadata
- Stubs
- Type Object Details
- Function Object Details
- Capability Parameters
- Inline Constants
- Enum Metadata And Symbols
- API Versus ABI Inspection
- Diagnostics
- Test Surface
- Implementation Anchors

## 12. Target Capabilities And C Emission

- Target Definition Resolution
- Target Sections
- Target-Owned Defines
- Type Specs And Call Specs
- Conversion Policy Tables
- Natural Integers And Pointer Widths
- Primitive C Spelling
- C Emission Preconditions
- Expanded Forms In C
- Enums And Inline Constants In C
- C Reserved Identifiers
- Symbol Emission
- Headers
- Shared Library Export/Import
- Object, Static, Shared, And Executable Artifacts
- Objective-C Capability Boundary
- Diagnostics
- Test Surface
- Implementation Anchors

## 13. Diagnostics, Source Ranges, And Error Quality

- Diagnostic Model
- Stable Codes
- Parser Diagnostics
- Analysis Diagnostics
- Source Ranges
- Range Helpers
- Warnings
- Error Message Style
- Multi-Diagnostic Situations
- Driver And Emitter Diagnostics
- Golden Diagnostic Tests
- LSP Diagnostic Mapping
- Outstanding Bugs And Documentation Issues
- Test Surface
- Implementation Anchors

## 14. Core Expression, Statement, And Access Semantics

- Body-Analysis Ownership
- Property Accessor Binding
- Property Assignment Lowering
- Index-Aware Parameters
- Range-Aware Parameters
- Accessors, Ranges, And Generic Arrays
- Omitted Trailing `out` Result Binding
- Intentional Discard
- Labels, `goto`, And Cleanup
- Conditions And Truth Values
- Metadata And API
- Diagnostics
- Test Surface
- Implementation Anchors

## 15. Shadow Classes And Foreign Extension

- Source Forms
- Shadow-Capable Base Hooks
- Representation
- Construction And Constructor Lowering
- Field Access And Receiver Constness
- Shadow Inheritance
- Interfaces And Dynamic Dispatch
- `delete shadow`
- API And Metadata
- Diagnostics
- Test Surface
- Implementation Anchors
