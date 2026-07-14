# Outstanding Bugs

Next bug number: BUG-049.

## BUG-045: Interface method return types cannot use `constof(this)`

Archived interface material documents `constof(this)` in interface method return
types, but the analyzer rejects an interface slot such as:

```camp
constof(this) int* value(const this);
```

The diagnostic is `constof anchor 'this' could not be resolved`. A comparable
`constof(this)` receiver method works for ordinary methods and callable newtype
ascription. Either bind `this` as a valid interface method `constof` anchor, or
amend the interface documentation/spec if interface slots intentionally cannot
express receiver-relative constness.

## BUG-046: `class iter` member with explicit `escaped this` produces `#ERROR`

The archived iterator material says a `class iter` member function whose
generated state retains `this` may either be a member of an `escaped class` or
declare an explicit `escaped this` parameter. The `escaped class` form compiles,
but a non-escaped class member written as:

```camp
class iter int values(escaped this)
{
	yield 1;
}
```

currently fails with `Unknown type '#ERROR'` before producing a useful
diagnostic. Either accept the explicit receiver form as specified, or reject it
with a clear source diagnostic and amend the language documentation if only
`escaped class` is supported.

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
