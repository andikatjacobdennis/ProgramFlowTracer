# ProgramFlowTracer &mdash; Trace Viewer

A static, dependency-free web viewer for the JSON traces the tracer writes. It turns a flat
`events.jsonl` stream back into the call tree the traced program actually executed, and lets you
click through parameters, return values, exceptions, timings and threads.

There is no build step, no package manager, and no server requirement &mdash; it is plain HTML,
CSS and JavaScript. `js/fsaccess.js` uses modern syntax because the API it wraps is newer than
that syntax anyway; the rest stays ES5-compatible.

## Using it

Open `website/index.html` in a browser, then load a run in any of these ways:

- **Open run folder&hellip;** &mdash; pick a folder location. This is the best option: it reads
  `run.json` and the `objects/` directory too, so spilled values resolve inline.
- **Open files&hellip;** &mdash; pick `events.jsonl` on its own.
- **Drag and drop** a run folder (or the whole `runs/` directory) anywhere onto the page.
- **Load bundled sample** &mdash; a real trace of `examples/ExampleApp`, committed so the viewer
  demonstrates itself without you having to instrument anything first.

Point it at whichever level of the layout suits you &mdash; a single run directory, the `runs/`
directory holding many of them, or the whole `.flowtrace/` folder. Everything found is grouped
back into runs by directory, and a run picker appears in the header when there is more than one.

Everything is parsed locally in the browser. No file is uploaded anywhere.

### Working from a folder location, not a copy

The viewer holds on to the folder *location* you opened rather than a snapshot of the files in
it, which means:

- **Reload** re-reads that folder in place. New runs appear and events appended since show up,
  without re-picking anything. Whichever run you were looking at stays selected.
- On the next visit the viewer offers to **reopen the same location**. Browsers require a click
  to re-grant read access, so this is always a button, never an automatic load.
- There is no packaged copy of a trace to move around or keep in sync &mdash; nothing to zip up,
  and nothing that can go stale against the run directory it came from.

This uses the File System Access API, so the folder handle can be re-read and stored. The
absolute path is deliberately never exposed to the page, which is why the header shows only the
directory name. In browsers without that API (Firefox, Safari) the classic directory picker is
used instead: the trace loads exactly the same, but there is nothing to re-read later, so Reload
and reopen are not offered.

### Serving it instead

If you host the folder over HTTP, a run can also be linked directly:

```
http://localhost:8080/index.html?run=./traces/my-run
```

The viewer then fetches `<run>/events.jsonl` and `<run>/run.json` itself. This is same-origin
only, and it does not apply when the page is opened from disk &mdash; a `file://` page is not
allowed to fetch its sibling files, which is also why the bundled sample ships as a `.js` file
rather than as raw `.jsonl`.

## What you get

**Call tree** &mdash; one row per invocation, nested by the real parent/child call chain
(`traceId` / `parentTraceId`), not by source structure. Each row shows the captured arguments, the
source location, the thread, a duration bar relative to the slowest call in the run, and its
status. Calls that threw are red; calls with no recorded exit (the process died first) are amber.

**Filters** &mdash; free-text search across method names, types, files, exception messages and
captured parameter/return *values*, plus errors-only, a minimum-duration threshold and a thread
selector. Filtering keeps the ancestors of every match visible, so a matching frame is never shown
without its callers.

Tick **Regex** to treat the box as a regular expression &mdash; `^Order.*(Async|Batch)$`,
`OrderService\.Calculate\w+`, `Timeout|Cancelled`. Matching is case-insensitive either way, and
the qualified `Type.Method` name is one token, so patterns written the way you would write them in
code work. A pattern that is not yet valid (you are still typing the closing bracket) marks the
box and reports the error next to it rather than hiding every row.

**The filter applies to every tab**, not just the call tree &mdash; it sits above them all, so it
means the same thing in all of them. One pass over the trace feeds them:

| Tab | What the filter does |
| --- | --- |
| Call tree | Shows matching calls, keeping their callers as dimmed context |
| Flowchart | Draws only the matching methods plus the callers that lead to one, the latter dashed and faded |
| Methods | Lists methods with at least one matching call. Times still cover *every* call of that method, and the note says so |
| Brief | Summarises only the matching calls, with a prominent `!! FILTERED VIEW` header saying what was included and how much was left out |
| Raw events | Shows the events belonging to matching calls, numbered by their position in the file |

**Details pane** &mdash; for the selected call: total/self/in-children time, direct and nested call
counts, thread and task ids, source location, every captured parameter with its serialization
status, the return value, `ref`/`out` values captured at exit, the exception with its stack trace,
and the raw events behind the row.

Each captured value is stacked &mdash; name, type and status on one line, the value across the
pane's full width below &mdash; rather than laid out as columns, which is what makes a JSON
payload readable in a sidebar. The pane itself is resizable: drag the splitter, double-click it to
reset, or focus it and use the arrow keys. The width is remembered per browser, and the stacked
narrow layout remembers its own height separately.

**Flowchart** &mdash; the run drawn as a call graph: one node per method, one edge per
caller/callee pair labelled with how many times that call happened. Entry points are drawn as
stadium terminals, methods that threw are red, and a bar along the bottom of each node shows its
share of total time. Hovering a node dims everything it is not connected to; clicking one opens
that method's first call in the details pane and selects it in the tree. Zoom with the buttons or
the wheel, pan by dragging, **Fit** to reframe.

Recursion is drawn honestly: a depth-first pass marks exactly the edges that close a cycle, and
those are the only ones dashed as recursive calls &mdash; a plain forward call whose target
happens to sit higher in the graph is not one.

The layout is a cut-down Sugiyama: layers by longest path, then **barycentre ordering** followed
by an **adjacent-transposition** pass to cut edge crossings, then orthogonal elbow routing.
Diagonal curves are what make a layered graph read as spaghetti &mdash; orthogonal runs from
different edges overlap into what looks like a bus instead. Wide fan-out (a dispatcher calling
forty handlers) wraps into evenly balanced sub-rows rather than one unreadably wide line, and
call-count labels are suppressed on charts dense enough for them to become confetti.

Anything past 80 methods is **split into several charts** &mdash; see below.

**Methods** &mdash; every invocation rolled up per method: calls, errors, total, self, average and
max time, sortable by any column. This is how you find the hot or the failing method when the tree
is too large to read. Selecting a row jumps to that method's first call in the tree.

**Brief** &mdash; the whole run boiled down to a block of plain text, with a copy button. Paste it
into any AI assistant and ask for a **sequence diagram**, an **activity diagram**, a **use-case
diagram**, or a written walkthrough &mdash; it opens with its own notation key, so it needs no
other context. It is also just a readable summary if you would rather skim than click.

It keeps what those diagrams actually need and drops everything else:

| Section | What a diagram gets from it |
| --- | --- |
| Participants | Lifelines, and the boundary of each type |
| Entry points | Use cases / actors' starting points |
| Call flow, in order, indented by depth | Sequence messages and activity steps |
| Folded repeats (`x50`) | Loops, without the transcript of every iteration |
| `[thread N]` where execution moves | Async boundaries and parallel lanes |
| Aggregated caller → callee pairs | Overall control flow, including anything the flow section had to truncate |
| Exceptions with origin and propagation | Alternate / exception paths |
| Top methods by self time | Where to focus |

Dropped as pure volume: per-call trace ids, timestamps, stack traces, and the second through
n<sup>th</sup> identical sibling call.

A **Detail** control trades size against completeness &mdash; Compact drops argument *values* but
keeps parameter names, Full widens the value and depth limits. Every section has its own ceiling
and says plainly when it hit one, so a large run degrades into an honest summary rather than an
unusable wall of text. The header shows the line count and an approximate token count so you know
whether it will fit in a chat window.

**Raw events** &mdash; the unfolded `events.jsonl` stream, in the order the tracer wrote it, for
when you need to see exactly what was recorded.

**Copy events** puts them on the clipboard as JSON Lines — one record per line, the same shape as
the file, ready to paste into a `.jsonl`, pipe through `jq`, or hand to an assistant. With a
filter active you get two buttons: the matching events, or all of them.

The records are re-serialised from the parsed objects rather than kept as raw text, since holding
a second copy of every line would double what a large trace costs in memory. Field order is
preserved, so records match the file field for field. They are not byte-identical:
`System.Text.Json` escapes characters such as `'` and `+` as `\u0027` / `\u002B`, where
`JSON.stringify` writes them literally. Both parse to the same value.

Keyboard: `↑`/`↓` move between rows, `→` expands, `←` collapses or jumps to the caller.

## Large runs: splitting into parts

An application with hundreds or thousands of classes has a call graph nobody can read as one
diagram and no assistant can take as one prompt. Rather than truncating and pretending, the
flowchart and the brief cut it along a seam that means something to a programmer, and record what
crosses each seam &mdash; so no part ever looks self-contained when it is not.

`js/partition.js` picks the seam, in this order of preference:

| Seam | What a part is | When it is used |
| --- | --- | --- |
| **Namespace** | One subsystem | Default when the run is too big for one chart. Depth is adaptive: the shallowest grouping whose largest group still fits |
| **Entry point** | Everything reachable from one root | On request. Parts overlap where they share helpers — shared code really is shared |
| **Independent flow** | A weakly connected component | When namespaces give no useful structure |
| **Size** | An arbitrary slice | Last resort for a group with no smaller natural boundary. Labelled as arbitrary, never disguised |

Both tabs get a part navigator (‹ › and a dropdown). The flowchart adds a **Split** control so you
can change the seam, and its summary always names the seam actually in use — so `Auto` tells you
what it chose. A chart caps at 80 methods, and each one draws a dashed **boundary stub** for every
neighbouring part — `called from Billing (12x)` — so calls leaving the picture stay visible.

The brief adds an **Overview** part first: how big the run is, which groups exist, how they call
each other, plus entry points, exceptions, concurrency and hotspots. It stays module-level, so it
fits in a prompt whatever the run's size. Each following part covers one group — its methods, its
internal call graph, and its boundary — and **Copy all parts** concatenates everything with
`===== Part n of N =====` separators when you would rather paste the lot.

A worked example: a synthetic 12-subsystem, 1,500-class, 3,012-method run splits into 48 charts of
≤ 80 methods, and into a 1,800-token overview plus 36 group briefs of ~3,300 tokens each. Note
that *Compact* produces **more, smaller** parts than *Full* — that is the point when the target
has a small context window.

## Cost of the viewer itself

The viewer is read-only and never polls, so it cannot slow the traced application — the writer
opens `events.jsonl` with `FileShare.Read` precisely so both can work at once. What it can do is
be slow itself, so two things keep it responsive:

- **Only the visible tab renders.** The toolbar filter feeds all five views, so a keystroke used
  to re-render every one of them — four of which are off screen. Hidden views now record that they
  are stale and catch up when their tab is opened. On a 2,900-event run that took a keystroke from
  **114 ms to 23 ms**, and the initial load from **442 ms to 28 ms**.
- **Only the visible rows exist.** The call tree and the raw-event list are virtualised
  (`js/virtual.js`): a full-height spacer keeps the scrollbar honest while a window of a few dozen
  real rows moves around inside it. Cost stops scaling with the size of the trace and starts
  scaling with the size of the screen.
- **Long tables skip off-screen rows** via `content-visibility` on `.grid` rows.

Measured on a **50,000-event / 25,000-call** run:

| | Before | After |
| --- | --- | --- |
| Open the Raw events tab | 109 ms, 82,449 DOM nodes, capped at 5,000 rows | **5 ms, 631 nodes, all 50,000 events** |
| Keystroke in the filter | — | **~20 ms** (12 ms scanning 25,000 calls, 8 ms in the tree) |
| Expand all | — | **5 ms**, 38 rows in the DOM |
| Scroll repaint | — | **2–9 ms** |

Because rows are virtualised, the raw-event list is no longer capped at all — it shows every
event in the run. Row heights come from the `--row-height` custom property, so the JS and the
stylesheet cannot drift apart.

### One thing splitting cannot fix

The viewer loads the whole `events.jsonl` into memory. A run of hours can produce millions of
events, and no amount of splitting the *views* changes that. Capture less instead: set
`samplingRate` below 1.0, or put hot namespaces in `excludeNamespaces`, in `.flowtrace.json`. The
viewer warns when a run exceeds 200,000 events.

## Theme

The viewer uses an Azure theme: Fluent 2 neutrals, Azure communication blue (`#0078D4`) as the
accent, Segoe UI, Fluent corner radii (2/4/6/8px) and depth shadows. Dark mode follows the Azure
portal's own dark palette rather than inverting the light one, and is applied both by the system
preference and by the header's theme toggle, which is remembered in `localStorage`.

Every colour, radius and shadow is a custom property in the `:root` block at the top of
`css/viewer.css`, so retheming means editing that block and nothing else. All foreground /
background pairs in both themes meet WCAG AA contrast (≥ 4.5:1).

## How it reads a run

| File | Required | Used for |
| --- | --- | --- |
| `events.jsonl` | yes | The event stream. One JSON object per line. |
| `run.json` | no | Application name, start/end time, event and dropped-event counts. |
| `objects/*.json` | no | Values too large to inline. Loading them resolves `objectId` references in the details pane. |

Malformed lines are counted and skipped rather than failing the load, because a trace from a
process that was killed mid-write ends in a torn line and is still worth reading. The viewer
surfaces a warning when that happens, when `run.json` reports dropped events, or when a run has no
end time.

## Source layout

```
website/
├── index.html            App shell
├── css/viewer.css        Azure theme; every colour, radius and shadow is a custom property
├── js/
│   ├── format.js         Pure formatting helpers (durations, type names, value previews)
│   ├── clipboard.js      Clipboard writes, with a fallback for file:// and denied permissions
│   ├── model.js          events.jsonl -> invocations -> call tree, method stats, call graph
│   ├── loader.js         File / drag-and-drop reading, grouped into runs
│   ├── filter.js         The toolbar filter, compiled once and shared by every tab
│   ├── fsaccess.js       Folder-location handles: pick, walk, re-read, remember
│   ├── virtual.js        Virtual list: render only the rows on screen
│   ├── partition.js      Splits a large call graph into readable/pasteable parts
│   ├── tree.js           Call tree view
│   ├── flow.js           Flowchart view: call graph, layering, inline SVG
│   ├── details.js        Details pane
│   ├── methods.js        Methods table and raw event list
│   ├── brief.js          Paste-ready text summary for diagram generation
│   └── app.js            Wiring: pickers, filters, tabs, keyboard, theme
└── sample/
    └── sample-trace.js   A real captured trace of examples/ExampleApp
```

Scripts are plain `<script>` tags sharing a `window.PFT` namespace rather than ES modules,
specifically so the page works when opened directly from disk &mdash; module scripts are blocked
by CORS on `file://`.

## Regenerating the bundled sample

```bash
dotnet run --project src/ProgramFlowTracer.Cli -- run examples/ExampleApp/ExampleApp.csproj
```

Then wrap the resulting `events.jsonl` and `run.json` as `window.PFT_SAMPLE = { run: {...},
eventsText: "..." }` in `sample/sample-trace.js`. Replace absolute paths and the machine name with
placeholders before committing.
