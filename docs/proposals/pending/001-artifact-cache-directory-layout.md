# Artifact And Cache Directory Layout

Status: pending  
Proposal date: 2026-07-11  
Last updated date: 2026-07-11

Archive source: `archive/docs/proposals/camp_artifact_cache_directory_layout_proposal.md`

## Summary

Defines artifact directory naming, root project output layout, compiler package
cache layout, remote/local package source layout, dependency artifact selection,
shared library ABI headers, shared dependency linking, cache semantics, and CLI
changes.

## Motivation

Camp builds need predictable artifact and cache directories so package,
standard-library, project-reference, static-library, and shared-library outputs
can be reused safely.

## Proposed Surface

The archived proposal describes `--out-dir`, removal of `--build-dir` from the
public surface, link kinds, project references, package cache roots, static and
shared artifacts, and target/profile-specific artifact directories.

## Current Implementation Status

The current compiler implements many of these concepts: `--out-dir`, artifact
directory names, local/global package roots, project references, dependency link
kinds, static/shared artifacts, and target-profile build templates.

## Open Questions

- Whether every archived proposal detail exactly matches current package and
  project-reference behavior.
- Whether the remaining proposal text should be converted entirely into
  `docs/compiler` and this file moved to accepted historical status.

## Acceptance Criteria

- Every current artifact/cache behavior is documented in `docs/compiler`.
- Any behavior that differs from the archived proposal is either corrected in
  code or documented as the current rule.
- The proposal is reclassified as accepted historical or narrowed to the parts
  still pending.

## Documentation Impact

Primary documentation belongs in `docs/compiler/05-artifacts-cache-and-output-layout.md`,
`docs/compiler/03-package-system.md`, and `docs/compiler/04-targets-and-native-builds.md`.

## Test Impact

Use command-line, project-loader, package, and native-build smoke coverage.

## Uncertainty Introduced By Compiler Changes

The implementation appears ahead of this pending record. This MUST be resolved
before treating the proposal as pending implementation work.
