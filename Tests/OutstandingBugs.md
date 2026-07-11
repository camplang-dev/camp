# Outstanding Bugs

Next bug number: BUG-043.

## BUG-042: Aggregate initializer arguments can reach C emission for pointer parameters

When a method expects a pointer parameter and the caller supplies an aggregate
initializer directly, the analyzer can allow the call and the C emitter produces
invalid C such as:

```c
(*host)->handlePaint(host, { hdc, erase, bounds });
```

This was seen in the Win32 forms playground with `handlePaint(PaintEventArgs* e)`
called as `handlePaint({ ... })`. The compiler should either support an explicit
lowering by materializing a temporary where that is legal, or more likely report
a source-ranged diagnostic explaining that an aggregate initializer cannot be
passed directly to a pointer parameter and that the caller should initialize a
local and pass its address.

## BUG-041: Extra arguments to some instance method calls can reach C emission

An instance method call with too many arguments can avoid the normal analyzer
diagnostic and reach C emission. This was seen in the Win32 forms playground
after `Control.createControl` changed to take no parent parameter, while
`application.camp` still called:

```camp
form.createControl((HWND)0);
```

The generated C called `Control_createControl((Control *)(form), (HWND)(0))`
even though the emitted function declaration takes only the receiver. The
analyzer should report `Call has too many arguments` at the extra argument (or a
more specific method-call diagnostic) and prevent invalid C from being emitted.
