+++
nav_title = "15. Async And Await"
+++

# Async, Await, And Deferred Calls

Camp async code is meant to look like the code you wanted to write anyway:
start an operation, wait for its result, handle failure, and continue. The
interesting Camp part is not that `await` exists. It is that async remains a
plain callback-shaped API at the ABI boundary, and the source code says which
ordinary value is responsible for resuming the suspended function.

That makes async useful in the places Camp cares about: native libraries,
device APIs, UI loops, background workers, stream adapters, and callback-heavy
C code. You can write readable sequential source without pretending there is a
hidden task runtime underneath.

You will see a few plausible application and framework names, such as
`UiThread`, `DocumentView`, `BackgroundFiles`, and `TaskScheduler`. Treat them
as the surrounding APIs a real program would already have in scope; they are
there to make the async shape concrete.

## A Small Awaited Function

A UI command handler might look like this:

```camp
async void openDocumentAsync(
	DocumentView* view,
	const char[] path,
	@awaitwith UiThread* ui,
	thrown IoError error)
{
	int bytes = await BackgroundFiles.countBytesAsync(path);
	view.setByteCount(bytes);
}
```

Read it from top to bottom. The function starts a background read, suspends at
`await`, and continues when the count is ready. If the read fails, the
`IoError` flows through the `thrown` slot. If it succeeds, `bytes` is an
ordinary `int` local and the view is updated.

The one unfamiliar piece is `@awaitwith UiThread* ui`. That parameter says:
when this function suspends, use `ui` to resume it. In a UI program, that
probably means the continuation returns to the UI thread before touching
`view`.

You do not need to see the UI loop implementation yet. Most code that consumes
async APIs looks like the body above: call something async, `await` the result,
then keep writing ordinary Camp.

## The Shape Of An Async API

An async declaration describes the source result:

```camp
extern async int countBytesAsync(
	const char[] path,
	thrown IoError error);
```

Callers normally use it like this:

```camp
int bytes = await BackgroundFiles.countBytesAsync(path);
```

Under the hood, the function completes through a final callback. In Camp, that
callback is a `once` callable: a callback value promised to be invoked exactly
one time.

Conceptually, the async declaration above behaves like a callback-shaped
function:

```camp
void countBytesAsync(
	const char[] path,
	once void(int byteCount, thrown IoError error) complete);
```

That callback shape explains most of Camp async:

- the async function returns to its caller before the work is done;
- success values are delivered to the completion callback;
- failure values are delivered through the completion callback's `thrown` slot;
- `await` is source sugar over supplying a completion and resuming later.

It also keeps the ABI familiar. A C-facing view of the same idea is just a
function pointer plus context:

```c
void countBytesAsync(
	const char *path_elements,
	size_t path_length,
	void *context,
	void (*complete)(void *context, int byte_count, IoError error));
```

The exact emitted spelling belongs to the target, but the model is stable:
Camp async is callback async with a nicer source surface.

## A Real Flow: Load, Parse, Display

Async becomes easier to understand when several awaited operations sit
together:

```camp
struct DocumentSummary
{
	nuint sectionCount;
	bool hasWarnings;
}

async void showSummaryAsync(
	DocumentView* view,
	const char[] path,
	@awaitwith UiThread* ui,
	thrown IoError error)
{
	DocumentBuffer* buffer = await BackgroundFiles.readDocumentAsync(path)
		finally delete;
	DocumentSummary summary = await Documents.parseSummaryAsync(buffer);

	view.setSectionCount(summary.sectionCount);
	view.setWarningVisible(summary.hasWarnings);
}
```

Each `await` produces the success value for the next line of code. The function
does not block the current call stack while the background file read or parse
is in flight. It returns to its caller, and later resumes through the selected
`UiThread`.

Notice the named `DocumentSummary` result. Camp awaits at most one ordinary
success value. When an operation naturally produces several values, put them in
a struct and return that one value. That keeps both the source and the callback
ABI clean.

## Awaitable Result Shapes

`await` works with a final completion callback whose shape is simple enough to
turn back into a source result:

| Completion callback shape | `await` result |
|---|---|
| no ordinary success parameter | `void` |
| one ordinary success parameter | that parameter's type |
| one `thrown` parameter | rethrown unless the await site catches it |
| multiple ordinary success parameters | not awaitable |

These declarations are natural to await:

```camp
extern async void flushAsync(thrown IoError error);
extern async int countBytesAsync(const char[] path, thrown IoError error);
extern async DocumentSummary parseSummaryAsync(DocumentBuffer* buffer);
```

This shape is not awaitable because it has two success values:

```camp
extern void readHeaderAsync(
	const char[] path,
	once void(string title, string author, thrown IoError error) complete);
```

Use a named result instead:

```camp
struct HeaderInfo
{
	string title;
	string author;
}

extern async HeaderInfo readHeaderAsync(
	const char[] path,
	thrown IoError error);
```

The rule is intentionally a little strict. It means the type of an `await`
expression is always obvious.

## Error Handling

Async error flow is ordinary Camp `thrown` flow. The error does not become a
runtime exception object. It is a typed slot in the async completion callback,
and `await` rethrows it into the resumed function.

Use `try` / `catch` when the whole awaited block has the same recovery path:

```camp
async void openOrShowErrorAsync(
	DocumentView* view,
	const char[] path,
	@awaitwith UiThread* ui)
{
	try
	{
		int bytes = await BackgroundFiles.countBytesAsync(path);
		view.setByteCount(bytes);
	}
	catch (IoError error)
	{
		view.showError("Could not open document.");
	}
}
```

Use a catch argument when the recovery is local to one call:

```camp
async bool tryRefreshPreviewAsync(
	PreviewPane* preview,
	const char[] path,
	@awaitwith UiThread* ui)
{
	int bytes = await BackgroundFiles.countBytesAsync(path, catch auto error);
	if (error != default)
		return false;

	preview.setByteCount(bytes);
	return true;
}
```

The surface is deliberately the same style as non-async thrown calls. The
difference is only when the error arrives: later, through the async completion.

## Resuming In The Right Place

An async function that suspends has to resume somewhere. Camp does not assume
there is one global answer. A UI handler might resume on a UI thread; a server
request might resume on a worker pool; an embedded driver might resume from a
device event queue.

The selected resumer is part of the concrete async body. For a free function,
mark one ordinary parameter with `@awaitwith`:

```camp
async void runInBackgroundAsync(
	Job* job,
	@awaitwith TaskScheduler* scheduler,
	thrown JobError error)
{
	await scheduler.startAsync(job);
	await job.waitForCompletionAsync();
}
```

`@awaitwith` does not change how callers pass the parameter. It says the body
uses that parameter to resume after `await`.

An instance method can use its receiver instead:

```camp
class DownloadController
{
	void resumeAsync(escaped once void() continuation)
	{
		// Queue the continuation on the controller's dispatch loop.
	}

	async void refreshAsync(const char[] url, thrown NetworkError error)
	{
		Response response = await HttpClient.getAsync(url);
		this.update(response);
	}
}
```

Here `this` is the resumer. That is a good fit when an object owns the
scheduling policy for its own async methods.

## What A Resumer Provides

A resumer provides one compatible `resumeAsync` method. The common shape is:

```camp
void resumeAsync(escaped once void() continuation);
```

A `once` continuation is the suspended function's "keep going" callback. It is
`escaped` because the resumer may store it and invoke it later.

An inline resumer can call it immediately:

```camp
class InlineResumer
{
	void resumeAsync(escaped once void() continuation)
	{
		continuation();
	}
}
```

A UI resumer probably queues it:

```camp
class UiThread
{
	void resumeAsync(escaped once void() continuation)
	{
		// Store continuation in the UI event queue.
	}
}
```

A background scheduler might do something similar:

```camp
class TaskScheduler
{
	void resumeAsync(escaped once void() continuation)
	{
		// Schedule continuation on a worker thread.
	}
}
```

Those bodies are intentionally sketched. The storage and threading policy
belong to the application or library. Camp only requires a compatible
resumption surface and ordinary lifetime safety for anything the resumer keeps.

Camp also accepts an async `resumeAsync` with no ordinary parameters:

```camp
class AsyncResumer
{
	@noawait
	async void resumeAsync()
	{
		return;
	}
}
```

That works because an async `void` method already has a final `once void()`
completion callback. As a resumer surface, it must stay parameterless and
non-throwing.

## Writing Async Bodies

Inside an async body, `return` completes the operation:

```camp
async int countSectionsAsync(
	DocumentBuffer* buffer,
	@awaitwith TaskScheduler* scheduler)
{
	DocumentSummary summary = await Documents.parseSummaryAsync(buffer);
	return (int)summary.sectionCount;
}
```

`throw` completes it through the thrown slot:

```camp
async bool requireConfigAsync(
	const char[] path,
	@awaitwith TaskScheduler* scheduler,
	thrown ConfigError error)
{
	DocumentBuffer* buffer = await BackgroundFiles.readDocumentAsync(path)
		finally delete;
	if (buffer == null || !buffer.hasSection("config"))
		throw MISSING_CONFIG;

	return true;
}
```

A concrete async body must either have a selected resumer or promise that it
will not suspend:

```camp
@noawait
async int cachedAnswerAsync()
{
	return 42;
}
```

`@noawait` is a body promise. It does not change the callable shape. Callers
still see an async operation, but the implementation may not contain `await`.

## Lifetimes Across `await`

An async function that suspends becomes a state machine. Values used after an
`await` may have to live in the async frame, which means ordinary lifetime
rules still apply.

This is fine because the borrowed buffer is used before suspension:

```camp
async void sendNowAsync(
	scoped const byte[] packet,
	@awaitwith NetworkLoop* loop,
	thrown NetworkError error)
{
	Network.write(packet);
	await Network.flushAsync();
}
```

This is not fine:

```camp
async void sendLaterAsync(
	scoped const byte[] packet,
	@awaitwith NetworkLoop* loop,
	thrown NetworkError error)
{
	await Network.waitForReadyAsync();
	Network.write(packet); // ERROR: scoped view would cross suspension
}
```

The fix is not an async trick. It is the same ownership thinking you have used
throughout the guide: copy the packet into owned storage, require a longer-lived
owner, use an escaped value when the API truly stores it, or reorder the code
so the scoped value is consumed before suspension.

This same rule applies to pointers, array views, delegates, interface adapters,
generic values, and allocator values. If it must survive an `await`, the
signature must make that lifetime believable.

## Cleanup In Async Code

Cleanup remains deterministic. `finally`, `finally delete`, and cleanup methods
still express who owns a value and how it is released.

```camp
async int countPagesAsync(
	const char[] path,
	@awaitwith TaskScheduler* scheduler,
	thrown IoError error)
{
	Document* document = await DocumentStore.openAsync(path) finally delete;
	return document.PageCount;
}
```

If a later await fails, the opened document still needs cleanup. If the
function suspends while the document is live, the async frame carries the
cleanup obligation until the function completes or unwinds.

Generated async frames use ordinary allocation rules. The selected resumer
decides how continuation is invoked; it does not magically own the frame or
free application resources.

## Manual Async Calls

`await` is the comfortable surface, but the callback shape is still available
when you need it:

```camp
void printCountWhenReady(const char[] path)
{
	BackgroundFiles.countCachedBytesAsync(path, (int bytes) =>
	{
		Console.writeLine(bytes);
	});
}
```

That completion lambda is a `once` callback: the async operation must call it
exactly one time. If the async operation also reports errors, the manual
completion surface has to account for the thrown slot too. Manual calls are
useful at interop boundaries, for adapters, or when you are implementing the
async layer itself. Ordinary application code should usually prefer `await`.

When a concrete async body calls another async function manually, that manual
call does not suspend the caller. It is just a callback call. Use `await` when
the current function should pause and resume afterward.

## `once` And Completion Ownership

`once` means "called exactly once." It does not, by itself, say who allocated or
deletes the callback context.

That distinction matters in async code:

- an async completion callback is `once` because the operation completes once;
- a resumer continuation is `once` because the suspended function resumes once;
- an escaped `once` lambda generated by the compiler may own and clean up its
  captured context;
- an arbitrary `once` value received from someone else does not become
  self-deleting merely because the type says `once`.

Most code only needs the first two ideas. The ownership detail becomes
important when writing low-level adapters, native callback bridges, or
libraries that store completions.

## `postpone`

`postpone` captures a call for later invocation. It is useful when a callback
or one-shot adapter should remember some arguments now and receive the rest
later.

```camp
int add(int left, int right) => left + right;

void calculateLater()
{
	auto addTen = postpone add(10);
	int result = addTen(7);
}
```

The postponed value is `once`-shaped. Calling `addTen(7)` invokes the original
function as `add(10, 7)` and consumes the postponed operation.

Receiver calls work the same way:

```camp
class StatusBar
{
	void showMessage(const char[] message)
	{
		// Display message in the UI.
	}
}

void scheduleMessage(StatusBar* status)
{
	auto showSaved = postpone status.showMessage("Saved");
	registerOneShotCallback(showSaved);
}
```

Only supplied argument slots are captured. A parameter marked `@awaitwith` is
an ordinary parameter for `postpone`: supply it if you want it captured, or
leave it open if the returned callable should receive it later.

For async work, it is often clearer to keep the async operation visible:

```camp
async void saveAndNotifyAsync(
	Document* document,
	StatusBar* status,
	@awaitwith UiThread* ui,
	thrown IoError error)
{
	await DocumentStore.saveAsync(document);
	status.showMessage("Saved");
}
```

Use `postpone` when you really want deferred invocation. Use `await` when you
want to wait for async work and then continue in source order.

## Async At ABI Boundaries

Because async is callback-shaped, it is a good fit for native APIs that already
complete through callbacks. A wrapper can expose a pleasant Camp async surface
while keeping the ABI direct:

```camp
extern void nativeCountBytesAsync(
	const char* path,
	void* context,
	fn void(void* context, int byteCount, IoError error) complete);

async int countBytesAsync(
	const char[] path,
	@awaitwith TaskScheduler* scheduler,
	thrown IoError error)
{
	// Convert the counted path as needed, call nativeCountBytesAsync,
	// and complete through the async callback shape.
}
```

The wrapper hides native string conversion and callback details from ordinary
callers. The exported or external boundary still stays honest: a native
function pointer, a context pointer, and explicit success/error slots.

## What Async Does Not Add

Camp async is intentionally small. The language does not add:

- a built-in task object;
- a hidden global event loop;
- runtime exception handling;
- multi-result await deconstruction;
- automatic lifetime extension for values used after suspension.

Libraries can build task abstractions, event loops, request schedulers, and UI
dispatchers on top of the callback shape. Camp's job is to make that shape
readable in source and predictable at the ABI boundary.

## Designing Async APIs

Good Camp async APIs usually follow a few habits:

- Return one success value, or make a named result struct.
- Use a `thrown` slot for failure that should propagate through `await`.
- Put `@awaitwith` on concrete bodies that need an explicit resumer.
- Let an instance method use `this` as the resumer when the object owns the
  scheduling policy.
- Keep borrowed pointers, array views, delegates, and interface adapters on the
  correct side of suspension points.
- Use manual callbacks for interop and low-level adapters.
- Use `postpone` for explicit one-shot deferred calls, not as a replacement for
  ordinary `await`.

The source should make the async story visible: what starts, what completes,
what can fail, what resumes, and what must stay alive while the operation is in
flight.
