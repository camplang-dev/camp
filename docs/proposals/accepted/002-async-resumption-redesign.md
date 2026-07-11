# Async Resumption Redesign

Status: accepted historical proposal  
Proposal date: 2026-07-11  
Last updated date: 2026-07-11  
Accepted date: 2026-07-11  
Canonical docs updated: yes, in `docs/language/19-async-await-and-postpone.md` and `docs/semantics/08-async-resumption-lowering.md`.

This accepted proposal is historical. The canonical source is the current docs,
not this proposal record.

Archive sources:

- `archive/docs/proposals/accepted/camp_async_resumption_redesign_proposal_v2.md`
- `archive/docs/proposals/accepted/camp_async_resumption_redesign_implementation_plan.md`

Summary: resumer-based async design using `resumeAsync`, `@awaitwith`,
`@noawait`, await-site lowering, callback shape validation, metadata, and
diagnostics.
