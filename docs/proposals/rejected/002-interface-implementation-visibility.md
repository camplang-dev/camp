# Interface Implementation Visibility

Status: rejected  
Proposal date: 2026-07-11  
Last updated date: 2026-07-11  
Rejected date: 2026-07-11

Archive source: `archive/docs/proposals/rejected/camp_interface_implementation_visibility_proposal.md`

Rejected proposals should not be updated after rejection except for mechanical
renumbering or archive-path changes.

## Summary

This proposal considered interface implementation visibility rules that differ
from the current source/API visibility model.

## Rejected Design

The rejected design would have separated implementation visibility from the
surface required by interface conformance.

## Rejection Rationale

Interface conformance needs predictable callable slots and source/API behavior.
Adding separate implementation visibility complicates dispatch, conformance
checking, metadata, and diagnostics without enough benefit.

## Current Rule

Interface implementation visibility follows the current declaration visibility
and conformance rules documented in the language reference and semantic
supplements.

## Future Direction

Future interface visibility work should be proposed as a new feature with
specific metadata, API, and dispatch consequences.
