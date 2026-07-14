# Rejected Proposal: Interface Implementation Visibility

Status: Rejected.

This proposal has been rejected. The proposed feature would have allowed
interface implementation edges on a class or struct to carry their own
visibility, independently of the interface type and the implementing type.

The feature was rejected because the extra visibility control is not useful
enough to justify the language and compiler complexity. In the cases where it
changes behavior, the behavior is narrow and difficult to motivate; in the cases
where the implemented interface is already exported, reducing the implementation
edge visibility does not meaningfully hide the implementation fact.

This proposal intentionally does not cover explicit interface method markers
such as `: Interface` or `: Interface.methodName`. That is a separate feature
and should be evaluated independently.

## Summary

The proposed feature would have changed interface entries in a class or struct
base list from plain type references into visibility-bearing implementation
edges:

```camp
class SomeClass:
	ILocalFileOnly,
	public ICurrentModuleOnly,
	export IExportedInterface,
	extern IReservedInterface
{
}
```

The intended meanings were:

- `IFoo`: implement `IFoo` privately, visible only where private members are
  visible.
- `public IFoo`: implement `IFoo` with public/module visibility.
- `export IFoo`: implement `IFoo` across the exported ABI/API boundary.
- `extern IFoo`: reserve the interface identity without exposing conversion,
  accessors, or implementation machinery.

The main practical effect would have been on generated interface accessor
methods such as `getIFoo()`, implicit conversion to `IFoo*`, API header output,
metadata, and derived-type reimplementation checks.

## Intended Semantics

The implementation edge visibility would have controlled whether callers could
obtain an interface pointer from an instance:

```camp
export interface IWidget
{
	void update();
}

export class Widget: export IWidget
{
	void update()
	{
	}
}
```

An exported implementation edge would have appeared in the generated Camp API
header and exposed the generated accessor:

```camp
export extern class Widget: export IWidget
{
	export extern constof(this) IWidget* getIWidget();
}
```

A private implementation of an exported interface would instead have needed to
preserve only the fact that the interface identity had already been claimed:

```camp
export extern class Widget: extern IWidget
{
}
```

That `extern IWidget` edge would not have exposed `getIWidget()`, implicit
conversion to `IWidget*`, or `vtableof(Widget: IWidget)`. It would only have
prevented a derived class from re-implementing `IWidget`.

## Rejection Rationale

The proposal does not buy enough expressive power.

If the implemented interface is private, the implementation is already private
in practice. There is no useful broader surface to hide.

If the implemented interface is public, the implementation is not exported
anyway. A private implementation of a public interface would only prevent other
files in the same module from acquiring the interface pointer. That is a real
distinction, but it is a feature of limited use.

If the implemented interface is exported, reducing the implementation edge from
`export` to `public` or private does not actually remove the implementation fact
from the exported API. The generated Camp API header still needs to declare an
`extern IInterface` reservation so that downstream derived classes cannot
re-implement the interface.

So the feature mostly enables one narrow pattern: a type can implement an
exported interface, publicly declare that the interface identity is already
claimed, and still prevent consumers from obtaining an interface pointer. It is
difficult to identify a common or compelling use case for this pattern, and it
does not justify the extra syntax, metadata shape, API header behavior, and
semantic distinction.

## Deferred Or Separate Work

This rejection does not reject explicit interface method implementation markers.
The following ideas are separate and may still be useful:

- requiring interface implementation methods to explicitly name the interface
  slot they implement;
- allowing `: Interface.methodName` to fill a differently named interface slot;
- using the interface slot's callable newtype and callspec as the implementing
  method's inherited callable contract;
- improving diagnostics for required and optional interface method mismatches.

