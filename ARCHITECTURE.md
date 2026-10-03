# Architecture

Why the code is shaped the way it is. [README.md](README.md) says what the
application does; this file is for someone about to change it.

The brief was a chat client that could grow into an AI-assisted IDE without being
rewritten. That sets the constraint everything below follows from: the parts that
will still be true in an IDE — provider abstraction, streaming, context assembly,
storage — are separated from the parts that are only true of a chat window.

> **Status.** This describes `0.1.0-alpha`. Where the code and this document
> disagree, the code is right and this document is a bug. Known gaps are listed in
> [SECURITY.md](SECURITY.md#known-gaps).

---

## The two hosts

There are two ways to run this application, and they share everything except the
window.

```text
┌─────────────────────────┐         ┌─────────────────────────┐
│  AIClient.App           │         │  electron/              │
│  WPF · net10.0-windows  │         │  Electron + React 19    │
│                         │         │  main / preload /       │
│  Views, ViewModels,     │         │  renderer               │
│  WPF-UI, DrawingVisual  │         │                         │
└───────────┬─────────────┘         └───────────┬─────────────┘
            │                                 │
            │        AIClient.Application ───┤   ChatService, AgentService,
            │        AIClient.Domain        │   ContextBuilder, tools, graph
            │        AIClient.Infrastructure│   EF Core, providers, DPAPI
            ▼                                 ▼
        ┌───────────────────────────────────────────┐
        │  AIClient.Server                          │
        │  ASP.NET Core on 127.0.0.1:45631          │
        │  HTTP + NDJSON, bearer token required     │
        └───────────────────────────────────────────┘
```

The **WPF host** is the original application. The **Electron host** is a second
shell over the same Application and Infrastructure layers, added so the product
could ship a UI that is not limited by WPF's control set.

**The sidecar exists because Electron cannot host .NET.** `AIClient.Server` is an
ASP.NET Core process the Electron main process starts as a child and talks to over
a loopback socket. It is not a network service in any meaningful sense — it binds
one address on one machine, refuses any other, and requires a per-launch bearer
token (see [The sidecar boundary](#the-sidecar-boundary)).

The practical consequence: **the two hosts do not feature-parity, and they are not
trying to.** The WPF host has a richer canvas (`GraphCanvas` with retained
`DrawingVisual`s); the Electron renderer has a richer editor (CodeMirror 6), a git
panel, and session bundles. New work should go where the feature belongs, and the
host that lacks it should say so rather than pretend.

---

## Projects and the dependency rule

```text
AIClient.Domain ◄──── AIClient.Application ◄──── AIClient.Infrastructure
                          ▲                          ▲            ▲
                          │                          │            │
                          └──────── AIClient.App ────┘            │
                          └──────── AIClient.Server ─────────────┘

                    electron/  —  TypeScript, depends on the server over HTTP
```

Five projects, and one rule: **dependencies point inwards, and nothing outside
Infrastructure names a provider, a socket or a database.**

| Project | TFM | May reference |
| --- | --- | --- |
| `AIClient.Domain` | `net10.0` | nothing |
| `AIClient.Application` | `net10.0` | Domain |
| `AIClient.Infrastructure` | `net10.0-windows` | Domain, Application |
| `AIClient.App` | `net10.0-windows` | Domain, Application, Infrastructure |
| `AIClient.Server` | `net10.0-windows` | Domain, Application, Infrastructure |

The two middle projects target plain `net10.0` rather than `net10.0-windows`, and
that is load-bearing: it makes it a **compile error** for a domain entity or an
application service to touch `System.Windows`, DPAPI or a registry key, which is
the kind of thing that otherwise creeps in one `using` at a time. CI asserts the
target framework explicitly, so "fixing" the build by retargeting them is caught.

**Both hosts may name exactly two Infrastructure types**: `AddInfrastructure` and
`DatabaseInitializer`, both called from the composition root. No ViewModel names a
provider, an `HttpClient` or a `DbContext`. The mechanical test for whether a change
respects this is: if deleting
[`DependencyInjection.cs`](src/AIClient.Infrastructure/DependencyInjection.cs) would
break a file under `ViewModels` or under `Endpoints/`, the layering has been
violated.

---

## What lives where

### Domain

Entities, enums, the graph model, and the contracts an outside system has to
satisfy. No dependencies at all, not even on `Microsoft.Extensions.*`.

- [`IAIProvider`](src/AIClient.Domain/Interfaces/IAIProvider.cs) — the seam that
  keeps the UI ignorant of OpenRouter. Implementations must be safe to call
  concurrently, since one instance is shared by every conversation.
- [`AIStreamEvent`](src/AIClient.Domain/Models/AIStreamEvent.cs) — a closed record
  hierarchy (`ContentDelta`, `ReasoningDelta`, `ToolCallDelta`, `ToolCalls`,
  `Usage`, `Completed`, `Error`) rather than a struct with a kind flag. Tool
  calling was added exactly that way: two new cases, and the compiler pointed at
  every `switch` that had to learn them.
- [`AIErrorKind`](src/AIClient.Domain/Enums/AIErrorKind.cs) — provider-agnostic
  failure classes. The UI switches on this and never parses a status code.
- [`GraphNode`, `GraphEdge`, `GraphSnapshot`, `GraphChangeSet`, `GraphChange`,
  `GraphModel`](src/AIClient.Domain/Graph/) — the spatial model, described below.
- [`SensitiveFiles`](src/AIClient.Domain/Workspace/SensitiveFiles.cs) — the
  credential-shaped filename list, deliberately in Domain so that both the
  workspace and external-file services can share one copy.
- `ISecureStorage`, `IContextBuilder`, `AIChatRequest`, `AIModelDescriptor`,
  `AIProviderException`, and the entities (`Conversation`, `Message`,
  `Attachment`, `Model`, `Provider`, `AppSettingsEntry`).

### Application

Use cases and the contracts the UI binds to. Knows about persistence as an
interface and about HTTP not at all.

- [`ChatService`](src/AIClient.Application/Services/ChatService.cs) — the one entry
  point for a turn, discussed below.
- [`ContextBuilder`](src/AIClient.Application/Services/ContextBuilder.cs) — composes
  the system prompt, the history and attachment text into a message list, then
  trims oldest-first to fit the model's window.
- [`ProviderErrorMapper`](src/AIClient.Application/Services/ProviderErrorMapper.cs) —
  HTTP status and transport exception to `AIErrorKind` plus a sentence fit to show a
  human.
- [`AgentService`](src/AIClient.Application/Services/AgentService.cs) — the
  multi-step loop, discussed below. Beside it,
  [`AgentToolRegistry`](src/AIClient.Application/Services/AgentToolRegistry.cs),
  [`AgentModePolicy`](src/AIClient.Application/Services/AgentModePolicy.cs) and the
  **20 tools** in `Services/Tools/`, each one an `IAgentTool` that declares its own
  JSON schema and its own risk level.
- **Graph**: [`GraphService`](src/AIClient.Application/Graph/GraphService.cs) (state,
  undo/redo, persistence), [`WorkspaceGraphIndexer`](src/AIClient.Application/Graph/WorkspaceGraphIndexer.cs)
  (diff-based workspace → graph), `GraphContextSource` (graph selection → prompt
  text), and [`AgentPlanGraphBuilder`](src/AIClient.Application/Graph/AgentPlanGraphBuilder.cs)
  (a plan → a change set, shared by both hosts' plan sinks).
- `AttachmentService`, `ExportService`, `HeuristicTitleGenerator`, `TokenEstimator`.
- [`MarkdownParser`](src/AIClient.Application/Markdown/MarkdownParser.cs) and
  [`SyntaxHighlighter`](src/AIClient.Application/Markdown/SyntaxHighlighter.cs) —
  Markdig in, a `MarkdownDocument` block model out. No WPF type appears in either;
  each host renders the block model in its own idiom.

### Infrastructure

Every dependency on the outside world.

- [`AIClientDbContext`](src/AIClient.Infrastructure/Database/AIClientDbContext.cs) and
  its migrations.
- [`OpenAiCompatibleProvider`](src/AIClient.Infrastructure/Providers/OpenAiCompatible/OpenAiCompatibleProvider.cs)
  plus `OpenRouterProvider` and `NvidiaProvider`, and
  [`ProviderRegistry`](src/AIClient.Infrastructure/Providers/ProviderRegistry.cs).
- [`ServerSentEventReader`](src/AIClient.Infrastructure/Http/ServerSentEventReader.cs),
  [`DpapiSecureStorage`](src/AIClient.Infrastructure/SecureStorage/DpapiSecureStorage.cs),
  `AppPaths`.
- [`WorkspaceService`](src/AIClient.Infrastructure/Workspace/WorkspaceService.cs) — the
  sandbox for paths inside the open folder.
- [`ExternalFileService`](src/AIClient.Infrastructure/Workspace/ExternalFileService.cs) —
  the second, narrower door, for paths outside it. See
  [Two doors to the file system](#two-doors-to-the-file-system).
- [`HttpFetcher`](src/AIClient.Infrastructure/Http/HttpFetcher.cs) — the SSRF-guarded
  fetcher behind the `fetch` tool.
- [`GitService`](src/AIClient.Infrastructure/Git/GitService.cs) and
  [`GitArguments`](src/AIClient.Infrastructure/Git/GitArguments.cs) — `git` with no
  shell, and the validator that keeps a transport specifier out of the argument list.
- `JsonGraphStore` — atomic graph persistence per workspace under `%APPDATA%`.

### AIClient.Server

The ASP.NET Core host for the Electron renderer. Thin on purpose: four endpoint
groups and three supporting types.

| File | Responsibility |
| --- | --- |
| [`Program.cs`](src/AIClient.Server/Program.cs) | Composition root, auth, CORS, bind validation. |
| [`SidecarAuthentication.cs`](src/AIClient.Server/SidecarAuthentication.cs) | The bearer token, its handler, and the origin and bind policy. |
| `RunEndpoints` | Chat and agent runs over NDJSON, plus approval polling and cancellation. |
| `DataEndpoints` | Conversations, settings, workspace, graph, tools, export, checkpoints. |
| `GitEndpoints` | Repository reads and writes. |
| `AiEndpoints` | The single-shot completion behind inline AI and ghost text. |
| `RunRegistry` | In-flight runs, their cancellation tokens and approval waiters. |
| `HttpAgentApproval` | `IAgentApproval` over HTTP — replaces Infrastructure's refusing default. |
| `ServerCanvasPlanSink` | `IAgentPlanSink` that draws plans onto the shared graph. |

It names `AddInfrastructure` and `DatabaseInitializer` and nothing else from
Infrastructure. Every read and every write goes through the same Application and
Infrastructure services the WPF host uses — **there is no second implementation of
a use case behind the HTTP surface.** That is the property that makes the sidecar a
host rather than a parallel application.

### The renderer

`electron/` is a TypeScript project that knows nothing about the .NET types. It
speaks HTTP.

- `main/main.js` — the window, the sidecar process, and the native bridges.
  `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`.
- `preload/preload.js` — a deliberately small `contextBridge` surface: the backend
  URL, the bearer token, four dialogs, `openExternal`, window chrome, zoom. If a
  capability is not on this list, the renderer cannot reach the OS with it.
- `renderer/src/lib/kontur/backend.ts` — the HTTP and NDJSON client. Every call
  carries the bearer token and a timeout.
- `renderer/src/lib/kontur/store.ts` — one Zustand store, persisted to
  `localStorage`.
- `renderer/src/components/kontur/` — the surfaces: `chat/`, `workspace/`,
  `canvas/`, `shell/`, `settings/`, and `ui.tsx`, which is the real design system.
  `components/ui/` is an unused shadcn kit retained for reference;
  see [CHANGELOG](CHANGELOG.md#known-limitations).

---

## The sidecar boundary

This is the newest and least documented part, and it is where the security posture
lives.

**Loopback is not an authorisation boundary.** Any web page the user has open can
send a request to `127.0.0.1` and, under permissive CORS, read the response. So:

1. **A bearer token, required everywhere.** The Electron main process generates 32
   bytes of CSPRNG output at launch, passes it to the sidecar as
   `AICLIENT_AUTHTOKEN`, and hands it to the renderer over the bridge. It is not
   bundled, not persisted, not in a URL. A sidecar started by hand with no
   configured token generates one rather than falling back to a constant.
2. **Comparison is constant-time**, over SHA-256 digests rather than the strings, so
   neither length nor a prefix leaks.
3. **CORS is narrow**: loopback origins, the literal `null` origin a `file://`
   renderer reports, and requests with no `Origin`. Everything else is refused, so a
   hostile page fails early and visibly rather than being allowed to try.
4. **A non-loopback bind throws at startup.** The launcher's `--urls` is validated,
   not honoured.
5. **`/api/health` is the only anonymous route**, it reports only a version string,
   and the token itself is never logged — only its source ("supplied by the
   launcher" or "generated for this process").

**Settings that reduce containment are not writable over HTTP.** `AllowCommands`,
`AllowedCommands`, `AllowNetwork`, `AllowExternalFiles` and `WorkspaceRoot` are
refused by name in `DataEndpoints`. The reasoning is specific: the approval gate
runs over the same authenticated surface, so a caller that could widen the agent's
reach could then answer its own approvals. The user's Settings window goes through
the WPF host, not this route.

**`/api/ai/complete` is clamped** — prompt length, system length, temperature and
output tokens. It is the cheapest way to turn a loopback socket into an open tab on
someone's provider credit, and the token alone is not a reason to leave it unbounded.

**`git` arguments are validated** by `GitArguments`. A remote name containing `:` is
refused, because `ext::sh -c …` is a documented git transport that runs a local
program — `git fetch "ext::calc.exe"` is remote code execution from a JSON body.

The full list, including what remains open, is in
[SECURITY.md](SECURITY.md).

---

## A chat turn, end to end

Three event vocabularies, each narrower than the last, translated at each boundary:

```text
provider bytes  ──►  AIStreamEvent  ──►  ChatTurnEvent  ──►  the UI
  (SSE frames)       (Domain)            (Application)       (WPF or React)
```

They are separate on purpose. `AIStreamEvent` is what a provider can say.
`ChatTurnEvent` is what a turn can mean, and it carries database ids the provider
knows nothing about. Collapsing the two would put a `Guid` from the messages table
into the type a provider implementation returns.

### The order of operations

[`ChatService.SendMessageAsync`](src/AIClient.Application/Services/ChatService.cs) is
the whole turn:

1. **Persist the question.** Before anything can fail, the user's own words are
   committed. `UserMessageSaved` carries the assigned id back.
2. **Title the conversation**, if this was the first exchange and auto-titling is on.
   Done here rather than at the end so the sidebar stops saying "New chat" while the
   model is still thinking. A failure is logged and swallowed — a title is cosmetic
   and must never break a turn.
3. **Commit an empty assistant placeholder** with `Status = Streaming`, and emit
   `AssistantMessageStarted`. A crash from here on leaves a transcript that still
   reads correctly on restart.
4. **Prepare.** Resolve the provider, read the model's capabilities from the cache,
   build the context, assemble the `AIChatRequest`. Everything that can fail before
   the socket opens fails here, is persisted against the placeholder, and comes back
   as `Failed`.
5. **Stream.** Each `ContentDelta` is appended to a `StringBuilder`, forwarded to the
   UI, and flushed to the database at most once a second. Every token would be a
   write per token; only at the end would lose the whole answer to a crash. One
   second is the bounded loss.
6. **Finish.** `Completed` persists the final text, the token counts and the elapsed
   time. An empty answer is recorded as a failure, because "the model returned an
   empty response" is more useful than an empty bubble.

Two details in that loop are worth knowing before editing it. The provider's
sequence is stepped with `GetAsyncEnumerator` and a hand-written `MoveNextAsync`
loop rather than `await foreach`, because C# forbids `yield return` inside a `try`
with a `catch`, and a mid-stream exception has to be caught, persisted and
re-emitted as `Failed`. And every write after the stream opens passes
`CancellationToken.None` on purpose: the token that just fired is the reason control
is there, and the partial answer still has to be saved.

### Cancellation

Stop cancels the `CancellationTokenSource` the caller owns. That aborts the HTTP
response rather than merely stopping the read of it. `ChatService` catches the
`OperationCanceledException`, writes what arrived with `Status = Cancelled`, and
emits `Cancelled`. The partial text stays in the transcript and remains usable as
context for the next turn — which is why cancellation is not modelled as an error.

`ProviderErrorMapper` separates the two cancellations that look identical from
outside: `HttpClient` reports its own timeout as a `TaskCanceledException` wrapping
a `TimeoutException`, which becomes `AIErrorKind.Timeout`; a bare
`OperationCanceledException` is the user pressing Stop.

### The same turn, over HTTP

Over the sidecar, the same `ChatService` runs; only the transport differs. The
endpoint writes NDJSON — one JSON object per line — and holds the request open for
the turn's duration. A terminal frame always arrives. Two consequences worth
knowing:

- **`onFrame` is awaited.** The renderer's frame handler does asynchronous work, and
  firing it un-awaited against a tight read loop lets a delta land after the run has
  reported itself complete. The audit that prompted this found exactly that.
- **There is no heartbeat.** During a long provider stall, or while an approval is
  open, zero bytes are written, and Kestrel's minimum data rate can drop the
  connection. This is a known gap, not a design choice.

---

## Context assembly

[`ContextBuilder`](src/AIClient.Application/Services/ContextBuilder.cs) is a
separate service rather than a private method on `ChatService`, and that is the
single most future-facing decision in the codebase. Today it composes three
sources — the system prompt, the conversation history, and attachment text inlined
as `<file name="...">…</file>` blocks before the question that refers to them.
Project files, retrieved memory, graph selections and tool definitions are
additional sources of exactly the same shape.

Trimming is oldest-first against `ContextWindow - ReservedOutputTokens`, and it
never drops the system prompt or the final user turn. If the last turn alone
overflows the window, the request goes out and the provider says so — silently
truncating the user's actual question would be worse than an error. A dangling
assistant turn left at the head after trimming is dropped too, since several
providers reject a history that starts with one. When the model's window is unknown,
trimming is skipped rather than guessed at.

Token counts come from [`TokenEstimator`](src/AIClient.Application/Services/TokenEstimator.cs),
a script-aware character-ratio heuristic. No tokeniser ships with the app: the
correct one differs per model family, the providers report real usage in the
response, and an estimate is only needed to decide what to trim. It deliberately
over-estimates, because sending one message less history is harmless while
under-estimating is an HTTP 400 the user has to recover from.

---

## An agent run, end to end

[`AgentService.RunAsync`](src/AIClient.Application/Services/AgentService.cs) is the
second entry point into a conversation, parallel to `ChatService` rather than
layered on it. A chat turn is one request and one answer; an agent run is a loop,
and the two share the context builder, the provider registry and the message table
but not a code path.

```text
provider bytes ──► AIStreamEvent ──► AgentEvent ──► the UI
  (SSE frames)      (Domain)          (Application)   (WPF or React)
```

One iteration of the loop is one step:

1. **Commit a placeholder** assistant row with `Status = Streaming` and emit
   `StepStarted`, exactly as a chat turn does.
2. **Prepare and stream.** Tool schemas come from `IAgentToolRegistry`, filtered to
   what the run's mode allows, are offered on every step but the last, and the reply
   is accumulated in a buffer flushed to the database at most once a second.
   `ReasoningDelta` is forwarded here: a step that spends thirty seconds deciding
   which file to open is otherwise thirty seconds of nothing.
3. **Decide what happened.** `AIStreamEvent.ToolCalls` — the provider's reassembled
   set — and not `finish_reason`, is what decides whether the run continues. Words
   and no calls ends the run.
4. **Act.** Each call is resolved to a tool, checked against the mode, parsed,
   checked against the repeat counter, put to the approval gate if its risk is above
   `Read`, executed, and given a row. In that order, so that a call the mode forbids
   is refused identically whether or not its arguments were well formed, nobody is
   shown a dialog about malformed JSON, and a model stuck in a loop cannot turn the
   approval prompt into the loop.
5. **Loop**, with the tool rows now part of the history the next request is built
   from.

Every call gets an answer row, whatever became of it. A call with no answer is not a
smaller failure; it is a hole in the next request, which providers reject outright.

A run ends in exactly one of `Completed`, `Failed` or `Cancelled`, and `Completed`
carries an `AgentStopReason` — `Answered`, `StepLimit` or `TimeLimit`. On the last
permitted step the tools are withheld, so a run that hits the step limit ends in a
sentence rather than on a file listing, and it is still reported as `StepLimit`.

### What kind of run it is

`AgentRunRequest.Mode` is a `Build`, `Plan` or `PlanCanvas`, and it is a property
of the message rather than of the application, because "plan this, then build it" is
two messages and a setting would make it two visits to Settings as well.

[`AgentModePolicy`](src/AIClient.Application/Services/AgentModePolicy.cs) is the
whole rule, expressed in terms a tool written next year satisfies without being
listed anywhere: a planning run offers `AgentToolRisk.Read` and withholds
everything above it; a build offers everything except the tools marking themselves
[`IAgentPlanningTool`](src/AIClient.Application/Interfaces/IAgentPlanningTool.cs).
Nothing consults a tool's name or its position in the registration list.

It is applied twice, deliberately. `AgentToolRegistry` precomputes one offer per
mode, so the request carries only what the mode allows; and `AgentService`
re-checks each arriving call **before reading its arguments**, so a model that names
a withheld tool anyway is refused rather than obeyed. The offer is a courtesy and
the second check is the enforcement; either alone would pass a test suite while the
feature was broken.

The prompt changes with the mode rather than gaining a caveat, since telling a
model both disciplines leaves it following neither: `AgentPrompt` substitutes the
planning instructions for the build ones and drops the command block a planning run
cannot use.

### The approval gate

[`IAgentApproval`](src/AIClient.Application/Interfaces/IAgentApproval.cs) is one
method. Everything above `AgentToolRisk.Read` passes through it, and the default
registration is `DenyingAgentApproval` — so a host that forgets to implement it
gets an agent that can only read. Each host replaces it: the WPF host shows a card
inline in the transcript; `HttpAgentApproval` polls and answers over the sidecar.

It is an interface rather than an event because the loop has to *wait*, and the
answer takes as long as a person takes. Cancellation while a question is open is
not a denial: nothing is reported to the model, because the turn is over. A denial
*is* reported, because a model told "the user declined" can propose something else.

### Two doors to the file system

This is worth being precise about, because it is the project's central safety
claim and it has grown a second door.

**Door one — the workspace.** `IWorkspaceService` is the only way a tool reaches a
file *inside the folder the user opened*. It is deliberately not a `FileInfo`
wrapper: every method takes a path relative to one root, resolves it, and refuses
anything that lands outside — through `..`, through an absolute path, or through a
symlink, which is why enumeration sets `AttributesToSkip =
FileAttributes.ReparsePoint` and links are resolved to their final target before
comparison. On top of that it refuses version-control internals and the
credential-shaped filenames by name, and caps what one call can return.

**Door two — external files.** `IExternalFileService` exists because some work is
genuinely outside a project folder, and it takes absolute paths, so none of the
workspace's relative-path guarantees apply. It rebuilds the guard from scratch: the
application's own data directory and every credential-shaped name are refused
outright, and writes to operating-system folders on top of that.

It is not a way around the sandbox. The tools that call it are gated on a setting
that is **off until the user turns it on**, and each call is put in front of the
approval gate.

> **Known gap.** `ExternalFileService` checks the sensitive-name list over the
> *textual* segments, where `WorkspaceService` also resolves reparse points. A
> pre-existing junction inside an allowed path can therefore defeat the
> name-based list. This is the first entry in
> [SECURITY.md § Known gaps](SECURITY.md#known-gaps) and the highest-priority fix
> after this release.

### Running a program, which neither door contains

[`RunCommandTool`](src/AIClient.Application/Services/Tools/RunCommandTool.cs) is the
one tool the sandbox cannot describe, because a path guard bounds what a program is
*started* on and nothing about what it does afterwards. `npm install` reaches the
network, a test suite reads whatever the machine will give it, and a build script is
a program someone else wrote. So the containment is a different shape, and none of
its four parts is reachable by the model:

1. **Off until the user turns it on**, once, in Settings, where the consequence is
   written out.
2. **An allowlist of program names.** Matched bare, case-insensitively, with a
   trailing `.exe` ignored on both sides. Not on it is a refusal that names the
   list.
3. **Approval on every call.** `AgentToolRisk.Execute` is excluded from the standing
   yes a run can accumulate for file tools, so ten commands is ten questions, and
   the dialog shows every argument unabbreviated.
4. **No shell.** The program is started directly with an argument list through
   `IProcessRunner`, so `&&`, `|`, `>` and `$HOME` are text the program receives
   rather than syntax anything interprets.

The fourth is what makes the second worth having — an allowlist in front of a shell
is decoration, since `cmd /c anything` passes it. `ProcessRunner` drains both pipes
as they fill rather than after the wait, closes standard input, kills the whole
process tree on a timeout or cancellation, and strips this application's own
environment variables so a child that prints its environment cannot print a key.

---

## The spatial graph

The graph is a spatial representation of a workspace: files, folders, modules,
services, interfaces, data, tests, plans and tasks are nodes; containment,
dependency and plan edges connect them.

### Domain

All immutable records in `AIClient.Domain.Graph`:

- **`GraphNode`** — `Id`, `Kind` (14 kinds), `Title`, `Subtitle`, `Path`, position
  (`X`, `Y`), size (`Width`, `Height`).
- **`GraphEdge`** — `SourceId`, `TargetId`, `Kind` (`Contains`, `Depends`, `Calls`,
  `Implements`, `Relates`, `Plans`), optional `Label`.
- **`GraphSnapshot`** — immutable, persistable whole-graph state with O(1) lookup
  indexes. Equality by content.
- **`GraphChangeSet`** — a titled bundle of `GraphChange` records with an `Origin`
  tag (`User`, `Agent`, `Indexer`, `Layout`, `Undo`, `Redo`).
- **`GraphChange`** — a closed hierarchy: `AddNode`, `UpdateNode`, `MoveNode`,
  `RemoveNode`, `AddEdge`, `UpdateEdge`, `RemoveEdge`.
- **`GraphModel`** — the sole mutator. `Restore` replaces everything wholesale.

### Application

`GraphService` owns state, a 100-entry undo/redo history and persistence.
`ApplyAsync` pushes the pre-snapshot to undo, clears redo, records a timeline entry
and fires events. `WorkspaceGraphIndexer` maps the workspace folder to graph nodes
diff-based: new files appear, deleted files leave, positions are preserved, plan
nodes are untouched. `GraphContextSource` serialises the selection into prompt text.

### The plan pipeline

```text
AgentService.PlanCanvas → SubmitPlanTool → AgentPlan
  → AgentPlanGraphBuilder → GraphChangeSet → IAgentPlanSink → GraphService.ApplyAsync
```

The builder is in Application and the sink is per-host, which is why the same plan
draws on both. The WPF host's `CanvasPlanSink` asks the user first —
"Draw this plan on the canvas?" — and `ServerCanvasPlanSink` draws immediately,
because a sidecar has no WPF dispatcher to ask on and a plan the user just asked for
does not need a modal.

Either way the plan goes through the same path as a user edit: undoable, persisted,
timeline-counted.

### Two renderers

The same graph, two very different rendering strategies, because the two hosts have
different ceilings.

**WPF** uses `DrawingVisual` retained visuals, not `ItemsControl`:
`GraphCanvas` composes a `MatrixTransform` root with edge and node
`ContainerVisual` layers and handles all pointer input; `CanvasController` holds
interaction state with no WPF dependency and diffs snapshots via `GraphProjection`;
`CanvasScene` applies deltas; `SpatialIndex` is a uniform grid (256-unit buckets)
answering hit-testing in O(1); `CanvasMinimap` is a miniature of the whole graph.

**The renderer** uses SVG with a pan/zoom transform, a minimap and a filterable
outline tree, which is what the DOM is good at.

The design assumption worth stating: a very large graph is where the two will
diverge. The WPF path is built for retained-mode cost; the SVG path is not. If the
graph grows past a few thousand nodes, the Electron host will need its own culling.

---

## Persistence

One SQLite file, six tables, and two migrations applied by `DatabaseInitializer` at
startup before anything reads.

| Table | Notes |
| --- | --- |
| `Providers` | One row per provider, plus its enabled flag and last refresh time. Never a key. |
| `Models` | The cached catalogue. Unique on `(ProviderId, ModelId)`. |
| `Conversations` | Indexed on `(IsPinned, UpdatedAt)` — exactly the sidebar's ordering. |
| `Messages` | Indexed on `(ConversationId, SequenceNumber)`; cascade from the conversation. |
| `Attachments` | Indexed on `MessageId`; cascade from the message. |
| `Settings` | One row per section, value is JSON. |

Two decisions worth defending.

**Timestamps are UTC ticks in an `INTEGER` column**, via `UtcTicksConverter` applied
as a pre-convention rule over every `DateTimeOffset` in the model. SQLite has no
date type; left alone, EF Core maps `DateTimeOffset` to TEXT and then refuses to
translate `ORDER BY`, `MIN`, `MAX` or a range comparison over it, because two rows
written in different time zones would sort by local wall clock rather than by
instant. Ticks are exact, fixed-width and monotonic, which is what makes the
`(IsPinned, UpdatedAt)` index usable rather than decorative.

**The context is a factory, not a scoped service.** WPF has no request scope to
hang a `DbContext` off, a `DbContext` is not thread-safe, and a streaming turn
writes from a background task while the UI reads on the dispatcher.
`AddDbContextFactory` plus a short-lived context per operation is the only shape
that stays correct under those conditions. The server uses the same seam.

---

## Providers

Both shipping providers speak the OpenAI `/chat/completions` protocol, so the
protocol lives once in `OpenAiCompatibleProvider` and the subclasses carry only what
differs — which, in both cases, is the catalogue. `OpenRouterProvider` reads a rich
one: real context windows, per-token pricing, modality flags and an explicit list
of accepted sampling parameters, all parsed rather than hardcoded. `NvidiaProvider`
faces the opposite problem — `/v1/models` returns little beyond ids — so it surfaces
every id and annotates known families by longest-prefix match. Hardcoding a model
list was ruled out for both.

The base class handles the request envelope, the SSE loop, the `[DONE]` sentinel,
usage extraction, error mapping and truncation of an error body to 4 KiB. It reads
with `HttpCompletionOption.ResponseHeadersRead`, without which the first token would
not appear until the whole answer had been buffered — the single most important line
for perceived speed.

### Adding a provider

Four members are abstract and two are virtual:

```csharp
public sealed class MyProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "myprovider";

    public override string Id => ProviderId;
    public override string DisplayName => "My Provider";
    protected override string BaseUrl => "https://api.example.com/v1";
    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document) => …;
}
```

Then two lines in
[`DependencyInjection.AddProviders`](src/AIClient.Infrastructure/DependencyInjection.cs):

```csharp
services.AddHttpClient(MyProvider.ProviderId, ConfigureStreamingClient);
services.AddSingleton<IAIProvider, MyProvider>();
```

`ProviderRegistry` takes `IEnumerable<IAIProvider>`, so it picks the new provider up
without being edited, and Settings, the model picker and the first-run screen are
all driven from the registry. NVIDIA's base URL is overridable at runtime through
`Providers:Nvidia`, so pointing that client at a self-hosted NIM container, an
on-prem deployment or a local OpenAI-compatible server needs no code at all.

---

## Secrets

`ISecureStorage` is declared in Domain and implemented once, by
`DpapiSecureStorage`: DPAPI `CurrentUser` scope, one file per provider under
`%APPDATA%\AIClient\secrets\`, written to `<key>.dat.tmp` and moved into place, with
a `SemaphoreSlim` serialising writes.

| Rule | How it holds |
| --- | --- |
| Never in source or Git | The store is `%APPDATA%`, outside the tree; `.gitignore` covers the adjacent hazards |
| Never logged | Log messages carry the *key name* only, never the value |
| Never in an error message | Including from a rejected key name |
| Never on a shared client | Attached per `HttpRequestMessage` |
| Never in the visual tree | `ApiKeyBox` — a `PasswordBox` whose `Password` is deliberately not bound |
| Absent, not fatal | A blob that will not decrypt reads as `null` |

CI greps the tracked tree for credential shapes. `SecureStorageTests` runs against
real DPAPI over a temporary profile directory, because substituting the encryption
would assert the one claim worth making against a stub that hands back whatever it
was told.

---

## Errors

One enum, [`AIErrorKind`](src/AIClient.Domain/Enums/AIErrorKind.cs), is the only
failure vocabulary above the provider layer. `ProviderErrorMapper` produces it from
a status code or a transport exception, together with a sentence written for a
person and a technical detail string for an expandable section. The UI switches on
the kind; it never sees an HTTP status.

A 400 is split by inspecting the body for overflow wording, because providers signal
a context overflow in prose rather than with a distinct status, and "your
conversation is too long" and "that parameter is not accepted" need opposite advice.
`AIProviderException.IsRetryable` — not the kind — is what decides whether a failed
bubble offers Retry, so a 401 does not invite the user to try the same rejected key.

---

## Threading

- Every service method is `async` and none of them touches a `Dispatcher`. The
  Application and Infrastructure layers are UI-framework-agnostic, and that would be
  a lie if they marshalled.
- **WPF**: `ChatViewModel` consumes `IAsyncEnumerable<ChatTurnEvent>` with
  `await foreach` on the UI thread, so appending a delta needs no marshalling.
  `UiThread` is the one place that hops back, and it is in the App project because
  that is the only .NET project that knows what a dispatcher is.
- **Sidecar**: routes are async end to end. A stream's frames are written and flushed
  per frame and the request is held open; `RunEntry` owns its own
  `CancellationTokenSource`.
- **Renderer**: the NDJSON reader awaits the frame handler in a read loop, and
  store appends are batched into one `requestAnimationFrame` flush rather than one
  write per token.
- A streamed turn's database writes run on the thread pool through the context
  factory while the UI reads through its own context. That is safe only because
  there is no shared `DbContext`.

---

## Markdown rendering

An answer arrives a few characters at a time and has to be readable at every
intermediate state. Re-rendering the whole thing per token is what makes a naive
chat UI stutter, so the work is split by what each layer is good at:

- **WPF** splits it by reconciliation. `MessageViewModel` accumulates deltas in a
  `StringBuilder`, re-parses on a 60 ms `DispatcherTimer` rather than per token, and
  `Reconcile` walks the old and new block lists together, keeps the leading run
  whose `ContentHash` matches, and replaces only the tail. Mid-stream that means one
  growing paragraph is rebuilt per tick instead of the entire transcript.
- **The renderer** re-renders React components, which is the cheaper operation, so
  it parses per frame instead.
- Both share `MarkdownParser` and `SyntaxHighlighter`, so a heading is the same
  block model in both and a syntax-highlighted fence has the same tokens.

The pipeline is deliberately narrow: grid and pipe tables, emphasis extras,
autolinks and task lists, but not `UseAdvancedExtensions` — footnotes, figures and
custom containers are parse work on a hot path for syntax no model emits. The
parser must also tolerate a half-written fence or an unclosed bold marker, because
that is what every answer looks like while it streams.

The renderer builds its own React elements and never uses
`dangerouslySetInnerHTML`, so model output cannot inject HTML into the DOM. That is a
security property, not a style choice.

---

## Decisions and trade-offs

**No MediatR, no in-process bus.** Services are injected and called directly. A
request/handler indirection would buy pipeline behaviours and lose the ability to
follow a chat turn by reading one method. With five projects and a dozen services,
the call graph is the documentation.

**No EF Core InMemory provider — the package is not even referenced.** The
persistence bugs worth catching are SQLite's own. Tests run against a real migrated
file in a temporary directory.

**DPAPI rather than Windows Credential Manager.** Credential Manager caps a blob at
2560 bytes, which is not generous enough for a self-hosted gateway issuing a long
bearer token, and would fail at the worst moment — on save, for one user, with a
limit nothing in the UI could explain. DPAPI has no such cap, is scoped to the
Windows account the same way, and needs no P/Invoke.

**A hand-written SSE reader.** About a hundred lines against a dependency that
would do the same. It has to survive a final event with no trailing blank line,
`:` heartbeat comments, multiple `data:` lines joined with `\n`, and OpenAI's
non-standard `[DONE]`. A 4 MiB per-event cap stops a malformed stream from growing a
buffer without bound.

**A hand-written syntax highlighter.** A single-pass scanner that tracks whether it
is inside a string or a comment. Regex highlighting has no notion of state and so
mis-handles exactly the cases chat produces most — `//` inside a string, a quote
inside a comment. Beyond 200 000 characters a block is shown unhighlighted rather
than freezing the UI thread.

**No Rx.** `IAsyncEnumerable<T>` is the streaming primitive from provider to view
model, and `await foreach` needs no scheduler.

**Two hosts, not one.** A single UI would have been less code. WPF cannot express
the canvas or the editor this product needs, and the honest answer was a second
shell over the same layers rather than a compromise in one. The cost is that new
features have to be built twice or declared absent in one host, and that is stated
here rather than discovered later.

---

## What this is built to accept later

- **More context sources.** `ContextBuilder` already turns something-that-is-not-a-
  chat-message into a fenced `<file name="…">` block ahead of the question.
- **More providers.** `IAIProvider` is five members, and an OpenAI-compatible one is
  a subclass plus two registration lines.
- **More tools.** `IAgentTool` is a name, a JSON schema, a risk level and an
  `ExecuteAsync`. Adding one is a class and a registration line, and the risk level
  alone decides whether the approval gate stops it.
- **An editor surface** — the renderer has one; the WPF host does not.
- **A different shell** — a headless CLI would take `AgentService` and nothing else.
- **A different store** — `IConversationService` and `ISettingsService` are declared
  in Application and implemented under `Infrastructure/Repositories`.

The one thing that would be a rewrite is a UI that reached a provider directly. That
is why [`DependencyInjection.cs`](src/AIClient.Infrastructure/DependencyInjection.cs)
is the only Infrastructure file either host names, and why deleting it should break
nothing under `ViewModels` or under `Endpoints`.