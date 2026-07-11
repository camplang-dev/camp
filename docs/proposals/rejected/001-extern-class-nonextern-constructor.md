# Non-Extern Constructors On Derived Extern Classes

Status: rejected  
Proposal date: 2026-07-11  
Last updated date: 2026-07-11  
Rejected date: 2026-07-11

Archive source: `archive/docs/proposals/rejected/camp_extern_class_nonextern_constructor_proposal.md`

Rejected proposals should not be updated after rejection except for mechanical
renumbering or archive-path changes.

## Summary

This proposal considered allowing non-extern constructors on Camp classes
derived from extern classes.

## Rejected Design

The rejected design would have allowed source constructors to initialize Camp
state on derived extern class types while sharing a native base-class boundary.

## Rejection Rationale

Extern class lifecycle belongs to the native boundary. Allowing Camp source
constructors on derived extern classes risks ambiguous ownership, partial
initialization, and misleading lifecycle guarantees.

## Current Rule

Camp does not allow source lifecycle rules that imply ownership of native base
state for derived extern class boundaries.

## Future Direction

Future work can define explicit interop construction patterns that keep native
ownership and Camp-owned state separate.
