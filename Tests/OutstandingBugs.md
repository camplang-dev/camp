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
