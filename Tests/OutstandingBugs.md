# Outstanding Bugs

Next bug number: BUG-049.

## BUG-047: `postpone` rejects async calls whose completion slot should remain open

The current spec and async resumption supplement describe postponed async calls
as awaitable when the final async completion callback is left open:

```camp
auto later = postpone readByteCountAsync(path);
int count = await later();
```

A focused smoke test with a `@noawait async int readByteCountAsync(...)`
currently reports:

```text
Async calls must use await or provide an explicit completion callback as the final argument.
```

The analyzer appears to validate the postponed target as an ordinary async call
before applying the `postpone` rule that should leave the completion slot open.
Either support the specified async-postpone shape, or amend the spec and docs if
`postpone` is only intended to support ordinary non-async calls for now.

## BUG-048: `typenameof(classtype)` is accepted outside default-parameter position

The receiver/classtype proposal, language guide, and semantics supplement limit
`typenameof(classtype)` to default parameter values, where it is substituted at
the call site after `classtype` binding. A smoke test shows that the analyzer
accepts ordinary expression use inside a class method:

```camp
class Control
{
	string getName()
	{
		return typenameof(classtype);
	}
}
```

`campc dump lowering tmp/smoke_typenameof_classtype.camp` leaves the
`NameOfExpression` unresolved, and `campc build` fails during C emission with:

```text
C emission does not yet support expression node NameOfExpression.
```

The analyzer should reject `typenameof(classtype)` outside the permitted
default-parameter position with a source-ranged diagnostic, or the language and
semantics docs should be amended if ordinary expression use is intended.
