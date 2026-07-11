# Target Capabilities And C Emission

## Target Definition Resolution

Target files are loaded from the target directory, merged through base target
chains, and validated before compiler requests use them. Variant overlays are
applied after selection.

## Target-Owned Defines

Target-owned defines are added to preprocessing and should be distinguishable
from user `--define` values where diagnostics or tooling need that distinction.

## Type Specs And Call Specs

Type specs apply to target-capable carrier types. Call specs apply to concrete
callables. The parser may accept names syntactically, but semantic validation
checks selected-target definitions.

## Conversion Policy Tables

Targets can define conversion policy tables for data pointers, function
pointers, natural integers, and ABI slot compatibility. The conversion
classifier consumes those tables.

## Primitive C Spelling

`[ctype]` defines C spellings for primitive Camp types. Unsupported primitive
types should be diagnosed when used for a selected target.

## C Reserved Identifiers

C emission must avoid reserved identifiers and collisions. Diagnostics should
catch source names that cannot be safely emitted when a stable translation is
not possible.

## Symbol Emission

Symbol emission combines source name, namespace, visibility, `@symbol`, target
export/import prefixes, and generated helper naming rules.

## Shared Library Export/Import

Shared library builds use target C-emitter values such as export/import
prefixes and shared-library C flags. API headers for shared dependencies must
expose the correct import/export surface.

## Object, Static, Shared, And Executable Artifacts

Native build templates compile source files to objects, then link or archive
objects into the requested artifact kind. Generated file lists must include
link and runtime files.

## Objective-C Capability Boundary

Targets can describe Objective-C-related capabilities, but Objective-C interop
is proposal-governed unless and until its source syntax and emission are
documented as current behavior.
