# Outstanding Bugs

Next bug number: BUG-036.

## BUG-035: Storing an await completion callback context requires an explicit escaped lifetime cast

Observed while implementing a timer-backed awaitable operation that stores the
compiler-supplied completion callback until the timer fires.

Minimal shape:

```camp
class EventLoop
{
	fn void(void*) timerCall;
	void* timerContext;
}

void delayAsync(EventLoop* loop, TimeSpan delay, once void() complete)
{
	loop.timerCall = complete.call;
	loop.timerContext = complete.context;
}
```

The compiler reports:

```text
Assignment cannot store a scoped pointer-bearing value in storage tied to 'this'.
```

For a true awaitable operation, the completion context is the caller's async
frame and must remain valid until the operation completes. The current lifetime
model does not appear to carry that async-completion guarantee through
`complete.context`.

Workaround used in the sample:

```camp
loop.timerContext = (escaped void*)complete.context;
```
