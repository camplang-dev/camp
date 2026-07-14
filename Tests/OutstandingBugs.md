# Outstanding Bugs

Next bug number: BUG-049.

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
