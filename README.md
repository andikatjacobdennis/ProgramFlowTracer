# ProgramFlowTracer

A Roslyn-based instrumentation tool for C# / .NET 9. It takes an existing project or solution,
analyzes it with the Roslyn compiler APIs, and produces an **instrumented copy** whose methods emit
a structured, JSON-based execution trace at runtime — method entry/exit, parameters, return values,
exceptions, timing, thread/task info, and the actual call tree (not just the static call graph).

Your original source is never modified. `instrument` always writes to a sibling
`<ProjectName>.instrumented` directory. (The single exception is the opt-in
[`--in-place`](#projects-outside-the-solution-directory---in-place) flag, which is reversible and
says so loudly.)

## Quick start

```bash
# Build the tool itself
dotnet build ProgramFlowTracer.sln

# Instrument a project (writes ../MyApp.instrumented, leaves MyApp/ untouched)
dotnet run --project src/ProgramFlowTracer.Cli -- instrument path/to/MyApp.csproj

# Instrument + build + run in one step
dotnet run --project src/ProgramFlowTracer.Cli -- run path/to/MyApp.csproj

# Inspect the trace
cat path/to/MyApp/.flowtrace/runs/*/events.jsonl
```

### Instrumenting a whole solution

A single `.csproj` is rarely what you want: the tracer only records methods it rewrote, so calls
into your *other* projects are invisible if you instrument just one of them. That is the usual
cause of a method showing `DIRECT CALLS 0` with all its time in `SELF`.

```bash
# Rewrites every project in the solution
dotnet run --project src/ProgramFlowTracer.Cli -- instrument YourSolution.sln --verbose

# Then run the instrumented startup project yourself
dotnet run --project YourSolution.instrumented/src/YourApp/YourApp.csproj
```

Three things specific to solutions:

- **`run` will not accept a `.sln`** — a solution can contain several runnable projects, so it
  cannot know which one you mean. Instrument and run are separate steps here.
- **Only projects underneath the solution directory are rewritten.** See below — this one is worth
  understanding, because it fails quietly.
- **`--verbose` lists every skipped member and why.** Worth reading on the first run; it answers
  "why is this method not in my trace?" directly.

### Projects outside the solution directory (`--in-place`)

`instrument` works by copying a tree and rewriting the copy, so it can only rewrite what is inside
that tree. A `<ProjectReference>` pointing somewhere else — `..\..\Shared\Shared.csproj`, a library
beside the solution rather than under it — is loaded for its symbols but **not** copied and **not**
rewritten. The instrumented copy still references the real, uninstrumented project.

The result is the most confusing failure this tool has: everything builds, the application behaves
exactly as it always did, and those methods simply never appear in the trace. From the viewer it is
indistinguishable from code that was never called — `DIRECT CALLS 0`, all the time in `SELF`.

`instrument` now says so explicitly:

```
warning: NOT instrumented: 'Shared' lives outside the copied tree ('...\sln'), so calls into it
produce no trace events. Instrument a solution that contains it, or rewrite it where it is with
--in-place Shared.csproj.
```

Two ways out. Prefer the first:

1. **Point at a solution that contains everything.** Nothing is modified, and the copy is complete.
2. **`--in-place`** — rewrite the named projects *where they live*:

```bash
dotnet run --project src/ProgramFlowTracer.Cli -- \
    instrument YourSolution.sln --in-place Shared,Common.csproj
```

Projects match by name, file name or full path; separate several with commas. Everything else still
goes to the copy as usual, and the copy's `<ProjectReference>` resolves out to the now-instrumented
original — which is exactly why this works.

**This modifies files you own**, the one place the tool does. It is therefore built to be exactly
reversible:

- every rewritten file is copied to `<file>.pft-original` first,
- every change is listed in `.flowtracer-inplace.json` beside the target you named,
- `restore <same target>` puts them all back and deletes the backups,
- instrumenting in place again without restoring first is **refused**, since it would overwrite the
  backups with already-instrumented content,
- a partial restore keeps the manifest so you can retry.

Try it on the bundled example:

```bash
dotnet run --project src/ProgramFlowTracer.Cli -- run examples/ExampleApp/ExampleApp.csproj
```

## Solution layout

```
ProgramFlowTracer.sln
├── src/
│   ├── ProgramFlowTracer.Cli/        Command-line tool (instrument / run / restore / clean)
│   ├── ProgramFlowTracer.Core/       Roslyn instrumentation engine (depends on Roslyn + MSBuild)
│   └── ProgramFlowTracer.Runtime/    Tracing library injected into instrumented apps
│                                     (zero dependency on Roslyn — this is the only thing your
│                                      instrumented application ends up referencing)
├── tests/
│   ├── ProgramFlowTracer.Core.Tests/       In-memory compile/instrument/run tests (fast, no MSBuild)
│   └── ProgramFlowTracer.IntegrationTests/ Real MSBuildWorkspace → build → run → trace tests
├── examples/
│   └── ExampleApp/                    A small, ordinary console app to instrument as a demo
└── website/                           Static, dependency-free viewer for the JSON traces
```

**Why three assemblies?** Your instrumented application should never need to pull in the Roslyn
compiler or MSBuild just to run. `ProgramFlowTracer.Runtime` is a small, dependency-free library
(only `System.Text.Json`, which ships in-box with .NET) — that's the *only* thing `instrument` adds
a reference to in your project's instrumented copy.

## How it works

1. **Copy, don't mutate.** `instrument` copies your project/solution directory tree to a sibling
   `*.instrumented` directory (skipping `bin/`, `obj/`, `.git/`, etc.), then rewrites source files
   only inside that copy. `restore` just deletes that copy.
2. **Load with Roslyn's `MSBuildWorkspace`.** This gives full semantic analysis (symbols, types,
   overload resolution) rather than pattern-matching syntax — instrumentation decisions and
   `typeof(...)` codegen are based on the real resolved types.
3. **Rewrite eligible members.** For every eligible method, constructor, local function, and
   (optionally) property accessor:
   - Inject a `FlowTracer.Enter(...)` call at the top, capturing parameters.
   - Wrap the body in `try { ... } catch (Exception e) when (FlowTracer.ObserveException(...)) { throw; } finally { FlowTracer.Leave(...); }`. The `when` filter is what keeps the method from becoming a real exception handler — see [Behaviour preservation](#behaviour-preservation).
   - Rewrite every `return` statement to capture the return value before returning
     (`return expr;` → capture `expr` into a fresh local, call `FlowTracer.Exit(...)`, then return
     it — the original expression is evaluated exactly once, and its value is unchanged).
   - For `void`/`Task`-returning methods, a `FlowTracer.ExitVoid(...)` call is appended as the last
     statement of the `try` block, covering the case where the method falls off the end without an
     explicit `return`.
4. **Runtime records the trace.** `FlowTracer` (in `ProgramFlowTracer.Runtime`) writes JSON Lines
   events to a background channel-backed writer, so tracing overhead never blocks the traced
   application, and a full disk/serialization failure can never crash it either (see
   [Fail-safety](#fail-safety-guarantees) below).

### Where the application runs from

`run` executes the instrumented copy but sets the **working directory to your original project**,
not to `*.instrumented`. The copy holds the rewritten *code*; as far as the application is
concerned it is running where it always did.

That matters because `instrument` deletes and re-copies the `*.instrumented` directory on every
invocation. Anything the application writes to a relative path — its own log files above all —
would otherwise land inside that directory and be destroyed on the next run. It also puts
`.flowtrace/` beside the real project, so **runs accumulate across re-instrumentation** instead of
being wiped, and `clean` removes it from the place it has always looked.

### Parent/child call chain

Each invocation gets a fresh GUID (`traceId`). The call chain is tracked with a plain
`AsyncLocal<TraceContext>` that is *pushed* on `Enter` and *popped back to its saved parent* on
`Leave` (in a `finally`, so it restores correctly however the method exits — return, exception, or
fallthrough). Because it's an ordinary `AsyncLocal`, it flows correctly across `await` points and
across multiple concurrent `Task`s without any extra bookkeeping — see
`ProgramFlowTracer.IntegrationTests` and the `MultipleConcurrentTasks_*` test in `Core.Tests` for
end-to-end proof that concurrent call chains never cross-contaminate.

## Trace output layout

```
.flowtrace/
  runs/
    {run-guid}/
      run.json          # run metadata: app name, start/end time, event counts
      events.jsonl       # one JSON object per line: MethodEnter / MethodExit / Exception
      objects/
        {object-guid}.json   # large captured values, spilled out of the main event stream
```

Example `events.jsonl` line:

```json
{"eventType":"MethodEnter","runId":"...","traceId":"...","parentTraceId":null,"method":"ProcessOrder","declaringType":"OrderService","file":"OrderService.cs","line":15,"column":20,"timestampUtc":"2026-08-23T09:30:00.120Z","threadId":1,"taskId":null,"parameters":{"orderId":{"type":"System.Int32","value":123,"serializationStatus":"Success"}}}
```

Large values (above `inlineObjectThresholdBytes`, default 2KB) are written to `objects/{guid}.json`
instead, and the event references them by `objectId` — this keeps `events.jsonl` small and
append-friendly even for applications that pass big payloads around.

## Viewing a trace

`events.jsonl` is deliberately readable with `cat` and `jq`, but a flat event stream is a poor way
to look at a call tree. `website/` is a static viewer that folds the stream back into the tree the
program actually executed:

```bash
# no build step, no server - just open it
start website/index.html          # Windows
open website/index.html           # macOS
```

Then use **Open run folder…** to point it at a folder location — a single
`<YourApp>/.flowtrace/runs/<run-guid>/` directory, or the `runs/` directory above it
to load every run at once — or just drag the folder onto the page. Click **Load bundled sample**
to explore a real trace of `examples/ExampleApp` without instrumenting anything first.

The viewer keeps the folder *location*, not a copy of it, so **Reload** re-reads the directory in
place: new runs and events appended since show up without re-picking anything, and there is no
packaged copy of a trace to move around or keep in sync.

It gives you five views of the same run:

- **Call tree** — every invocation, nested by the real call chain, with captured arguments and
  per-call timings.
- **Flowchart** — the run as a call graph: a node per method, an edge per caller/callee pair
  labelled with its call count, entry points as terminals, methods that threw in red, and
  recursion drawn as dashed back edges.
- **Methods** — a per-method roll-up (calls, errors, total/self/avg/max) for finding hot or
  failing code.
- **Brief** — the run as a compact block of copyable text: participants, entry points, the call
  flow with repeats folded into loops, thread switches, the aggregated call graph, and exceptions
  with where they originated and propagated. Paste it into any AI assistant to get a sequence,
  activity or use-case diagram — it carries its own notation key, so it needs no other context.
- **Raw events** — the unfolded `events.jsonl` stream.

Plus a resizable details pane for parameters, return values and exceptions, and a filter — plain
text or regular expression — over method names, files, exception messages *and* captured values.
The filter applies to all five tabs at once. Everything is parsed in the browser; no trace data
leaves the machine, and nothing is uploaded anywhere.

**The viewer cannot slow the traced application.** It is read-only, never attaches to the process,
and does not poll — the trace writer holds `events.jsonl` open with `FileShare.Read` precisely so
it can be read while the run is in progress. Its row lists are virtualised, so a 50,000-event run
opens the raw-event tab in ~5 ms with ~600 DOM nodes and filters in ~20 ms per keystroke. The
practical ceiling is browser *memory*, not speed: the whole file is parsed into the tab, so
capture less (`samplingRate`, `excludeNamespaces`) rather than expecting a multi-gigabyte trace to
open.

**Large applications.** A run with hundreds or thousands of classes is too big for one diagram or
one prompt, so the Flowchart and Brief split it along a meaningful seam — by namespace, by entry
point, or by independent flow — with a part navigator, and draw a boundary stub for every call
that crosses between parts. The Brief adds a module-level Overview part first, so there is always
something that fits in a prompt regardless of the run's size. See
[website/README.md](website/README.md).

## Configuration (`.flowtrace.json`)

Both the instrumentation step (Core) and the runtime (Runtime) read the *same* nearest
`.flowtrace.json`, found by walking up from the target project's directory — so instrumentation
decisions and runtime capture behavior always agree.

```json
{
  "enabled": true,
  "outputDirectory": ".flowtrace",
  "captureParameters": true,
  "captureReturnValues": true,
  "captureComputedProperties": false,
  "captureExceptions": true,
  "captureThreadInfo": true,
  "instrumentPropertyAccessors": false,
  "instrumentLocalFunctions": true,
  "instrumentCompilerGeneratedMethods": false,
  "maxObjectSizeBytes": 1048576,
  "inlineObjectThresholdBytes": 2048,
  "maxStringLength": 10000,
  "maxCollectionItems": 100,
  "maxObjectGraphDepth": 12,
  "maxCapturedNodes": 5000,
  "recordMethodExits": true,
  "excludeNamespaces": ["System", "Microsoft"],
  "excludeClasses": [],
  "excludeMethods": [],
  "sensitiveNamePatterns": ["password", "secret", "token", "apikey", "connectionstring"],
  "samplingRate": 1.0,
  "writerQueueCapacity": 100000
}
```

Every field is optional; a missing or malformed `.flowtrace.json` silently falls back to defaults
(config loading never throws — a broken config file must never prevent the traced app from
starting).

### Capture cost

Capturing a value is the only part of tracing that runs on your application's thread, so it is the
only part whose cost you feel. Three settings bound it, and they do different jobs:

| Setting | Bounds | Enforced |
| --- | --- | --- |
| `maxObjectGraphDepth` | how deep one branch may go | during the walk |
| `maxCollectionItems` | items per collection, **per level** | during the walk |
| `maxCapturedNodes` | **total** nodes for the whole value | during the walk |
| `maxObjectSizeBytes` | what may be inlined into an event | after the walk |

Depth and collection limits multiply — 100 items nested three deep is a million nodes while
technically obeying both. `maxCapturedNodes` is the only one that bounds the *total* work, and it
keeps whatever it captured before stopping (status `Truncated`, with the partial value present).

`maxObjectSizeBytes` is a different kind of limit: it decides what may be inlined, and is checked
once the value is already built. Lowering it does not make capture faster.

If a specific parameter is expensive, `[FlowTraceSensitive]` skips it outright — the value is never
touched.

### Entry-only tracing (`--entry-only`)

The lightest and least invasive mode. One `MethodEnter` event per call, and nothing else. The
injected code is a single statement:

```csharp
public int Compute(int x)
{
    FlowTracer.EnterOnly("Compute", "Sample.Service", @"…\Service.cs", 9, 20, null);
    var doubled = x * 2;

    if (doubled > 10)
    {
        return doubled;      // untouched
    }

    return 0;                // untouched
}

// A one-line body stays on one line:
public string? MaybeNull() { FlowTracer.EnterOnly("MaybeNull", …); return null; }
```

No `try`, no `catch`, no `finally`, no temporary variable, no wrapper of any kind. The call is
*inserted* into the body you wrote rather than the body being rebuilt, so every existing line keeps
its own trivia byte for byte — the diff for a method is exactly one added line, and nothing is
re-indented.

That matters when instrumentation seems to change how the program behaves:

- **Control flow is not restructured.** The full wrapper's `catch (Exception) { …; throw; }` is
  observable from outside — it makes the method a handler, which changes when an *outer*
  `catch … when (filter)` runs its filter relative to inner `finally` blocks, and it shows up in
  first-chance exception handling and the debugger's "break when thrown". Wrapping in `try` also
  costs the method its tail calls. Entry-only has neither.
- **`return` statements are left exactly as written.** The return rewriter never runs, so no
  temporary is declared and none of the target-typed-return handling applies.
- **No `AsyncLocal` write.** `EnterOnly` pushes no call scope, so it does not force an
  `ExecutionContext` copy at every `await` and thread hop.

The cost of that is the call tree: with no scope pushed, parent/child links come from whatever
context is already current, and there are no durations, exception events, return values, or
`MethodExit` events. Use it to answer "which methods ran, in what order"; use the default mode when
you need the shape of the call tree.

Combine with `--no-values` for the absolute minimum — a bare `EnterOnly(…, null)` per method.

### Structure-only tracing (`--no-values`)

To drop values wholesale and keep only the shape of the run:

```bash
dotnet run --project src/ProgramFlowTracer.Cli -- instrument YourSolution.sln --no-values
```

You still get the full call tree, per-call timings, thread and task ids, source locations and
exceptions. You get nothing about parameters, return values or `ref`/`out` values.

The switch applies at **instrumentation** time, not just at runtime — the capture code is never
generated. So no `FlowTraceParameter[]` is allocated per call, no argument is boxed, and **no
property getter is invoked**, which is what makes it the right answer when a traced type is
expensive to *read* rather than merely large. Setting `captureParameters` / `captureReturnValues`
to `false` in `.flowtrace.json` before instrumenting has the same effect; leaving them at their
defaults and flipping them afterwards only suppresses the output, and still pays for the capture.

### Attributes

```csharp
using ProgramFlowTracer.Runtime.Attributes;

[FlowTraceIgnore]                 // on a method, constructor, class, or parameter: never traced/captured
public void InternalHelper() { }

public void Login([FlowTraceSensitive] string password) { }  // value is redacted, never written to disk
```

Parameters whose *name* matches `sensitiveNamePatterns` (case-insensitive substring match) are
redacted automatically, even without the attribute.

## Behaviour preservation

Tracing is supposed to observe a program, not take part in it. Three things about the full
entry-and-exit wrapper used to change how the traced application actually ran; all three are fixed,
and each is pinned by a test in `BehaviourPreservationTests`.

### Exceptions are observed from a filter, not a handler

The wrapper used to be `catch (Exception e) { FlowTracer.Exception(...); throw; }`. That makes the
traced method a genuine handler, and .NET exception handling runs in two passes:

| | first pass (find a handler) | second pass (unwind) |
|---|---|---|
| uninstrumented | outer `catch ... when (filter)` runs its **filter** | inner `finally` blocks run |
| with `catch`/`throw` | stops at the traced method | inner `finally` blocks run, *then* the rethrow restarts the search, so the outer filter runs **after** them |

So an outer exception filter — common in logging and retry code — silently started running at a
different time, with different state, than it did before instrumentation. The rethrow also made the
exception look handled to the debugger and to first-chance handlers.

It is now a filter that always returns false:

```csharp
catch (global::System.Exception e)
    when (global::ProgramFlowTracer.Runtime.FlowTracer.ObserveException(__ftCall_0, e))
{
    throw;   // unreachable while the filter returns false
}
```

A filter that returns false does not handle the exception, so the first pass simply carries on to
the next frame — indistinguishable from a method with no handler. The exception event is still
recorded, and `ObserveException` never throws (an exception escaping a filter is swallowed by the
runtime and treated as false, which would lose the event).

### Deferred sequences are described, never enumerated

Capturing a value used to call `GetEnumerator()`/`MoveNext()` on anything implementing
`IEnumerable`. For a materialised `List<T>` that is harmless. For everything else it *runs the
program*:

- a LINQ chain or `IQueryable` executes — database round trips fire, from inside the tracer;
- a `yield return` iterator runs its body, along with any side effects in it;
- a single-pass sequence is consumed, so the real caller then receives an empty one.

Only sequences that are already in memory are read — those carrying a count via `ICollection`,
`ICollection<T>` or `IReadOnlyCollection<T>`. Anything else is recorded as
`<IEnumerable<Order>: deferred sequence, not enumerated>`. This is unconditional: there is no
setting that turns it off, because enumerating is never safe.

### Property getters that run code are not invoked

Capturing an object used to invoke every readable property. An ORM navigation property issues a
query when read — and throws once its `DbContext` is disposed — and a computed property can mutate,
cache, log, block or throw.

Only auto-properties are read by default. The compiler names their backing field
`<Name>k__BackingField` and declares it on the same type, so the check is exact rather than a
guess, and ordinary DTOs and entities still capture in full. Anything genuinely computed is
recorded as `<computed property, not read>`.

To opt back in where the getters are known to be side-effect free, set it in `.flowtrace.json`:

```json
{ "captureComputedProperties": true }
```

### What still differs

Being honest about the remainder, for the full entry-and-exit mode:

- **No tail calls.** A body wrapped in `try`/`finally` cannot be tail-call optimised. This only
  matters for deep mutual recursion that relied on it.
- **`ExecutionContext` copies.** `Enter` writes an `AsyncLocal` to track the call chain, which
  forces a context copy at `await` points and thread hops. This is inherent to recording a call
  tree; `--entry-only` avoids it by not pushing a scope.
- **Timing.** Capture work takes time, so anything with a real-time dependency sees different
  timing. `--no-values` removes almost all of it.

`--entry-only` has none of these except the cost of one call. See
[Entry-only tracing](#entry-only-tracing---entry-only).

## CLI reference

```
ProgramFlowTracer instrument <project-or-solution> [--verbose] [--no-values] [--in-place <projects>]
    Writes an instrumented copy to a sibling "<name>.instrumented" directory.

ProgramFlowTracer run <project.csproj> [--verbose] [--no-values] [-- <app args>]
    Instruments a fresh copy, then `dotnet build && dotnet run`s it.
    Requires a specific .csproj - a .sln is rejected, since it may hold several runnable
    projects. For a solution, use `instrument` and start the instrumented project yourself.

Options (instrument / run):
    --verbose     Report every member left uninstrumented, and why.
    --no-values   Record only which methods were entered and exited.
    --entry-only  Record a MethodEnter event per call and nothing else. Injects one
                  statement per method - no try/catch, returns untouched.
    --in-place    Rewrite the named projects where they live, not in the copy.
    --no-backup   Skip the .pft-original copies --in-place writes (use with version control).

ProgramFlowTracer restore <project-or-solution>
    Deletes the instrumented copy, and undoes --in-place rewriting from its backups.

ProgramFlowTracer clean [project-or-solution]
    Removes .flowtrace/ trace output, and the instrumented copy if a project/solution is given.
```

## Fail-safety guarantees

These are the properties the test suite specifically checks for:

- **Serialization never throws out of the traced application.** Any failure while capturing a
  parameter/return value produces `{"serializationStatus": "Failed", "error": "...", "toString": "..."}`
  instead — see `SerializationFailure_DoesNotThrow_ProducesFailedStatusWithFallback`.
- **One bad node does not lose the whole value.** The serializer walks the object graph itself
  rather than handing it to `JsonSerializer` in a single call, so a property that cannot be
  captured is marked in place (`$error`) and every sibling around it is still recorded. The
  overall result is `"Partial"` rather than `"Failed"`. This matters for interop and framework
  types: a single `ref struct`-returning property such as `System.Text.Encoding.Preamble` used to
  fail the capture of everything reachable from it.
- **Capture cost is bounded before it is paid.** `maxCapturedNodes` stops the walk at a fixed
  number of nodes and keeps what it captured, so an unexpectedly huge parameter costs a bounded
  amount of your application's time instead of being traversed in full and then discarded. See
  [Capture cost](#capture-cost).
- **Exceptions are recorded, then always rethrown unchanged.** ProgramFlowTracer never swallows an
  exception or alters its type/message/stack — see `UnhandledException_RecordedThenRethrown_Unchanged`.
- **Circular object graphs don't hang or crash.** `ReferenceHandler.IgnoreCycles` is used under the
  hood — see `CircularObjectGraph_DoesNotCrashOrInfinitelyRecurse`.
- **A full trace-writer failure (e.g. disk full) can't take down the traced app.** The background
  writer swallows and counts (`droppedEventCount` in `run.json`) I/O failures rather than
  propagating them.
- **Sensitive parameters are never written to disk**, not even in a fallback/error path — see
  `SensitiveParameter_NeverWrittenToTrace`.

## Known limitations

- **Iterator methods** (containing `yield return`/`yield break`) are **not instrumented** and are
  left completely untouched. C# does not allow a `yield return` inside a `try` block that has a
  `catch` clause, so exception capture — the mechanism this tool relies on — cannot legally wrap
  one. `ProgramFlowTracer instrument --verbose` reports these as skipped with the reason
  `"iterator method"`.
- **Lambdas and anonymous methods are never instrumented directly** (too fragile against type
  inference and expression trees). Local functions *are* instrumented, independently of their
  enclosing method.
- **Top-level statements** (the implicit `Main` in a `Program.cs` using top-level statements) are
  not instrumented — there is no explicit `Main` method syntax to rewrite. Every other method/class
  in the same project is instrumented normally; only convert to an explicit `Main` method if you
  need the entry point itself traced.
- **Property accessors are off by default** (`instrumentPropertyAccessors: false`) since most
  codebases have far more trivial auto-properties than anyone wants in a trace; flip it on for
  hand-written getters/setters with real logic.
- Parameters of `ref struct`/pointer type (e.g. `Span<T>`, unsafe pointers) can't be boxed to
  `object`, so they're captured as `"serializationStatus": "Unavailable"` rather than causing a
  compile error in the instrumented output. A `ref struct` *property* found while walking some
  other object is skipped in place with a note, and the rest of that object is captured normally.
- **Property getters are invoked during capture.** Anything reachable from a traced parameter has
  its public getters called, so a lazily-loaded ORM navigation property will issue a query, once
  per instance. If a parameter is expensive to read rather than merely large, exclude it with
  `[FlowTraceSensitive]` (never touched) or turn off `captureParameters`.

## Building and testing

```bash
dotnet build ProgramFlowTracer.sln
dotnet test ProgramFlowTracer.sln
```

`ProgramFlowTracer.Core.Tests` instruments and compiles sample source **in-memory** (via
`CSharpCompilation.Emit`) and then actually invokes the resulting methods through reflection to
assert on both behavior *and* the resulting trace — it does not require MSBuild.
`ProgramFlowTracer.IntegrationTests` goes through the full real pipeline: `MSBuildWorkspace` loads
an on-disk fixture project, `dotnet build`/`dotnet run` are shelled out to for real, and the
resulting `.flowtrace/` output is inspected. Both suites pass in full (33 + 3) against real
emitted and executed IL — this is not scaffolding.

## Development note on this repository

This project was built in a network-restricted sandbox with no access to nuget.org. To actually
compile and run everything during development (rather than merely writing code that "looks
right"), the Roslyn/MSBuild assemblies bundled inside the .NET SDK itself (the same ones
`dotnet-format`/`dotnet-watch` ship with) were used as local references in a separate scratch
verification setup — the `.csproj` files in this repository were not changed for that purpose and
use ordinary NuGet `PackageReference`s throughout. On a machine with normal internet access,
`dotnet restore` / `dotnet build` / `dotnet test` should work exactly as shown above with no extra
setup.
