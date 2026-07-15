# Outstanding Bugs

Next bug number: BUG-050.

## BUG-049: Virtual string-returning getter can produce unresolved const receiver diagnostic

A virtual property-getter-shaped method returning `string` can currently fail during analysis with a source-less diagnostic:

```camp
export virtual class Widget
{
	export virtual string getName()
	{
		return "Widget";
	}
}

export sealed class Gauge: Widget
{
	export override string getName()
	{
		return "Gauge";
	}
}
```

When this shape was tried in `DotNetInteropExample.camp`, the compiler reported:

```text
DotNetInteropExample.camp(1,1): (no line,column) error: Argument cannot convert 'const Widget*' to 'Widget*'.
```

The likely issue is an interaction between getter-compatible methods being `const this` by default, virtual override/vtable lowering, and the expanded/string return path. The compiler should either accept this shape or report a precise source-ranged diagnostic if the signature is invalid.
