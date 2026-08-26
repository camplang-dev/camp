# Outstanding Bugs

Next bug number: BUG-087.

## BUG-085 - Unknown configuration flag diagnostics lose the expression source range

Confirmed: 2026-08-25

The compiler reports unknown configuration flags used inside `configured(...)`
at the start of the file with a `(no line,column)` suffix instead of pointing at
the `configured(...)` expression or the unknown flag token.

Minimal repro:

```camp
export int main()
{
	if (configured(APP_UNKNOWN))
		return 1;
	return 0;
}
```

Command:

```sh
campc build repro.camp --artifact none
```

Observed diagnostic:

```text
repro.camp(1,1): (no line,column) error: Unknown configuration flag 'APP_UNKNOWN'.
```

Expected behavior:

The diagnostic should point at the `APP_UNKNOWN` token or at least the
`configured(...)` expression. Proposal 018 and the semantic docs make
configuration flags expression-level syntax in `configured(...)`, so users need
the source range of the offending query.

## BUG-086 - @require declarations can bypass unconditional duplicate validation when unreferenced

Confirmed: 2026-08-25

Requirement-bearing internal declarations with the same source name can bypass
the unconditional duplicate declaration diagnostic when they are not exported
and not referenced. Requirements are not supposed to participate in duplicate
declaration validation at all; even provably non-overlapping requirements must
not allow duplicate source declaration identities.

Plain duplicate internal declarations are diagnosed, and required duplicates
are also diagnosed once a call site references the name, so this appears to be a
declaration validation path incorrectly filtering or skipping unreferenced
required declarations.

Minimal repro:

```camp
@require(APP_A)
int same()
{
	return 1;
}

@require(APP_B)
int same()
{
	return 2;
}
```

Command:

```sh
campc build repro.camp --artifact none --declare APP_A --declare APP_B --configure APP_A --configure APP_B
```

Observed behavior:

The build succeeds.

Expected behavior:

The compiler should report a duplicate declaration/symbol diagnostic. The
accepted configuration requirements proposal preserves the rule that source
declarations remain unique in each source scope; requirements make declarations
available or unavailable, but do not create overload sets or permit same-name
collisions. The equivalent unrequired duplicate declaration already reports
`Duplicate symbol name 'same'.`
