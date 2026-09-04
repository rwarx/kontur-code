# Architecture

Why the code is shaped the way it is. [README.md](README.md) says what the application does;
this file is for someone about to change it.

The brief was a chat client that could grow into an AI-assisted IDE without being rewritten. That
sets the constraint everything below follows from: the parts that will still be true in an IDE -
provider abstraction, streaming, context assembly, storage - are separated from the parts that are
only true of a chat window.

## Projects and the dependency rule

```text
AIClient.App ─────────► AIClient.Application ─────────► AIClient.Domain
     │                          ▲                              ▲
     │                          │ implements                   │ implements
     └──► AddInfrastructure ──► AIClient.Infrastructure ────────┘
```

Four projects, and one rule: **dependencies point inwards, and no arrow ever leaves the App project
towards a provider or a database.**

| Project | TFM | May reference |
| --- | --- | --- |
| `AIClient.Domain` | `net10.0` | nothing |
| `AIClient.Application` | `net10.0` | Domain |
| `AIClient.Infrastructure` | `net10.0-windows` | Domain, Application |
| `AIClient.App` | `net10.0-windows` | Domain, Application, Infrastructure |

App does reference Infrastructure, because something has to call the composition root, and the two
types it uses are exactly that: `AddInfrastructure` and `DatabaseInitializer`, both in
[App.xaml.cs](src/AIClient.App/App.xaml.cs). No ViewModel names a provider, an `HttpClient` or a
`DbContext`; every one of them takes an interface declared in Application or Domain. The test for
whether a change respects this is mechanical - if deleting
[DependencyInjection.cs](src/AIClient.Infrastructure/DependencyInjection.cs) would break a file
under `ViewModels`, the layering has been violated.

The two middle projects target plain `net10.0` rather than `net10.0-windows`. That is deliberate and
load-bearing: it makes it a compile error for a domain entity or an application service to touch
`System.Windows`, DPAPI or a registry key, which is the kind of thing that otherwise creeps in one
`using` at a time.

## What lives where

### Domain

Entities, enums, and the contracts an outside system has to satisfy. No dependencies at all, not
even on `Microsoft.Extensions.*`.

- [`IAIProvider`](src/AIClient.Domain/Interfaces/IAIProvider.cs) - the seam that keeps the chat UI
  ignorant of OpenRouter. Four members: `GetModelsAsync`, `StreamChatAsync`, `TestConnectionAsync`,
  and the identity pair `Id`/`DisplayName`. Implementations must be safe to call concurrently,
  since one instance is shared by every conversation.
- [`AIStreamEvent`](src/AIClient.Domain/Models/AIStreamEvent.cs) - a closed record hierarchy
  (`ContentDelta`, `ReasoningDelta`, `ToolCallDelta`, `ToolCalls`, `Usage`, `Completed`, `Error`)
  rather than a struct with a kind flag. Tool calling was added exactly that way: two new cases, and
  the compiler pointed at every `switch` that had to learn them.
- [`AIErrorKind`](src/AIClient.Domain/Enums/AIErrorKind.cs) - fifteen provider-agnostic failure
  classes. The UI switches on this and never parses a status code.
- [`ISecureStorage`](src/AIClient.Domain/Interfaces/ISecureStorage.cs), `IContextBuilder`,
  `AIChatRequest`, `AIModelDescriptor`, `AIProviderException`, and the entities
  (`Conversation`, `Message`, `Attachment`, `Model`, `Provider`, `AppSettingsEntry`).

### Application

Use cases and the contracts the UI binds to. Knows about persistence as an interface and about HTTP
not at all.

- [`ChatService`](src/AIClient.Application/Services/ChatService.cs) - the one entry point for a
  turn, discussed below.
- [`ContextBuilder`](src/AIClient.Application/Services/ContextBuilder.cs) - composes the system
  prompt, the history and attachment text into a message list, then trims oldest-first to fit the
  model's window.
- [`ProviderErrorMapper`](src/AIClient.Application/Services/ProviderErrorMapper.cs) - HTTP status
  and transport exception to `AIErrorKind` plus a sentence fit to show a human.
- [`AgentService`](src/AIClient.Application/Services/AgentService.cs) - the multi-step loop, discussed
  below. Beside it, [`AgentToolRegistry`](src/AIClient.Application/Services/AgentToolRegistry.cs) and
  the eight tools in `Services/Tools/`, each one an `IAgentTool` that declares its own JSON schema and
  its own risk level.
- `AttachmentService`, `ExportService`, `HeuristicTitleGenerator`, `TokenEstimator`.
- [`MarkdownParser`](src/AIClient.Application/Markdown/MarkdownParser.cs) and
  [`SyntaxHighlighter`](src/AIClient.Application/Markdown/SyntaxHighlighter.cs) - Markdig in,
  a `MarkdownDocument` block model out. No WPF type appears in either; the App project turns the
  block model into controls.

### Infrastructure

Every dependency on the outside world, and the implementations of the interfaces the two inner
projects declare. Note that `ISettingsService` and `IConversationService` are *declared* in
Application and *implemented* here, in `Repositories/`: the use cases decide what persistence has to
provide, and EF Core decides how.

- [`AIClientDbContext`](src/AIClient.Infrastructure/Database/AIClientDbContext.cs) and the initial
  migration.
- [`OpenAiCompatibleProvider`](src/AIClient.Infrastructure/Providers/OpenAiCompatible/OpenAiCompatibleProvider.cs)
  plus two thin subclasses, and
  [`ProviderRegistry`](src/AIClient.Infrastructure/Providers/ProviderRegistry.cs), which owns the
  model cache and the key lifecycle.
- [`ServerSentEventReader`](src/AIClient.Infrastructure/Http/ServerSentEventReader.cs),
  [`DpapiSecureStorage`](src/AIClient.Infrastructure/SecureStorage/DpapiSecureStorage.cs),
  `NetworkConnectivityMonitor`, `AppPaths`.
- [`WorkspaceService`](src/AIClient.Infrastructure/Workspace/WorkspaceService.cs) - the sandbox. Every
  path the agent names is resolved against one root and refused if it lands outside, and it is the
  only place in the solution that touches a user file on the agent's behalf.
- [`DependencyInjection.cs`](src/AIClient.Infrastructure/DependencyInjection.cs) - the composition
  root, and the only file here that App is allowed to call.

### App

WPF and nothing else: `Views/` (XAML), `ViewModels/`, `Behaviors/`, `Converters/`, `Markdown/`
(the block model to `TextBlock`/`Run` renderer), `Services/` (theme, dialogs, `UiThread`), and the
file logging sink. Seven screen-level view models are registered as singletons in
[ServiceRegistration.cs](src/AIClient.App/ServiceRegistration.cs) - there is exactly one of each
screen in a single-window application, and a chat view that is navigated away from and back keeps
its draft and its scroll position for free. `MessageViewModel` and `ProviderSettingsViewModel` are
per-item and constructed by their owners, not resolved from the container.

## A chat turn, end to end

Three event vocabularies, each narrower than the last, translated at each boundary:

```text
provider bytes  ──►  AIStreamEvent  ──►  ChatTurnEvent  ──►  ObservableCollection<MessageViewModel>
  (SSE frames)       (Domain)            (Application)       (App)
```

They are separate on purpose. `AIStreamEvent` is what a provider can say. `ChatTurnEvent` is what a
turn can mean, and it carries database ids the provider knows nothing about - `UserMessageSaved`,
`AssistantMessageStarted`, `ContentDelta`, `Usage`-folded `Completed`, `Failed`, `Cancelled`,
`TitleGenerated`. Collapsing the two would put a `Guid` from the messages table into the type a
provider implementation returns.

### The order of operations

[`ChatService.SendMessageAsync`](src/AIClient.Application/Services/ChatService.cs) is the whole turn:

1. **Persist the question.** Before anything can fail, the user's own words are committed.
   `UserMessageSaved` carries the assigned id back so the ViewModel can stop showing an optimistic
   bubble.
2. **Title the conversation**, if this was the first exchange and auto-titling is on. Done here
   rather than at the end so the sidebar stops saying "New Chat" while the model is still thinking.
   A failure is logged and swallowed - a title is cosmetic and must never break a turn.
3. **Commit an empty assistant placeholder** with `Status = Streaming`, and emit
   `AssistantMessageStarted`. A crash, a kill or a power cut from here on leaves a transcript that
   still reads correctly on restart.
4. **Prepare.** Resolve the provider, read the model's capabilities from the cache, build the
   context, assemble the `AIChatRequest`. Everything that can fail before the socket opens fails
   here, is persisted against the placeholder, and comes back as `Failed`.
5. **Stream.** Each `ContentDelta` is appended to a `StringBuilder`, forwarded to the UI, and
   flushed to the database at most once a second. Every token would be a write per token; only at
   the end would lose the whole answer to a crash. One second is the bounded loss.
6. **Finish.** `Completed` persists the final text, the token counts and the elapsed time. An empty
   answer is recorded as a failure, because "the model returned an empty response" is more useful
   than an empty bubble.

Two details in that loop are worth knowing before editing it. The provider's sequence is stepped
with `GetAsyncEnumerator` and a hand-written `MoveNextAsync` loop rather than `await foreach`,
because C# forbids `yield return` inside a `try` with a `catch`, and a mid-stream exception has to be
caught, persisted and re-emitted as `Failed`. And every write after the stream opens passes
`CancellationToken.None` on purpose: the token that just fired is the reason control is there, and
the partial answer still has to be saved.

### Cancellation

Stop cancels the `CancellationTokenSource` the ViewModel owns. That aborts the HTTP response rather
than merely stopping the read of it - `ServerSentEventReader` checks the token each iteration and
passes it to `ReadLineAsync`. `ChatService` catches the `OperationCanceledException`, writes what
arrived with `Status = Cancelled`, and emits `Cancelled`. The partial text stays in the transcript
and remains usable as context for the next turn, which is the behaviour the brief asked for and the
reason cancellation is not modelled as an error.

`ProviderErrorMapper` separates the two cancellations that look identical from the outside:
`HttpClient` reports its own timeout as a `TaskCanceledException` wrapping a `TimeoutException`,
which becomes `AIErrorKind.Timeout`; a bare `OperationCanceledException` is the user pressing Stop.

## Context assembly

[`ContextBuilder`](src/AIClient.Application/Services/ContextBuilder.cs) is a separate service rather
than a private method on `ChatService`, and that is the single most future-facing decision in the
codebase. Today it composes three sources - the system prompt, the conversation history, and
attachment text inlined as `<file name="...">…</file>` blocks before the question that refers to
them. Project files, retrieved memory and tool definitions are additional sources of exactly the
same shape. The composition order and the trimming pass are what will survive; the list of sources
is what will grow.

Trimming is oldest-first against `ContextWindow - ReservedOutputTokens`, and it never drops the
system prompt or the final user turn. If the last turn alone overflows the window, the request goes
out and the provider says so - silently truncating the user's actual question would be worse than an
error message. A dangling assistant turn left at the head of the history after trimming is dropped
too, since several providers reject a history that starts with one. When the model's context window
is unknown, trimming is skipped entirely rather than guessed at.

Token counts come from [`TokenEstimator`](src/AIClient.Application/Services/TokenEstimator.cs), a
script-aware character-ratio heuristic - roughly 3.6 characters per token for Latin text, 1.8 for
Cyrillic and CJK, which fragment further. No tokeniser ships with the app: the correct one differs
per model family, the providers report real usage in the response, and an estimate is only needed to
decide what to trim. It deliberately over-estimates, because sending one message less history is
harmless while under-estimating is an HTTP 400 the user has to recover from.

## An agent run, end to end

[`AgentService.RunAsync`](src/AIClient.Application/Services/AgentService.cs) is the second entry point
into a conversation, parallel to `ChatService` rather than layered on it. A chat turn is one request
and one answer; an agent run is a loop, and the two share the context builder, the provider registry
and the message table but not a code path. Merging them was considered and rejected: `ChatService` is
the file to read to understand a turn, and folding a step loop, a tool registry and an approval gate
into it would cost that.

```text
provider bytes ──► AIStreamEvent ──► AgentEvent ──► MessageViewModel + AgentToolCallViewModel
  (SSE frames)      (Domain)          (Application)    (App)
```

One iteration of the loop is one step:

1. **Commit a placeholder** assistant row with `Status = Streaming` and emit `StepStarted`, exactly as
   a chat turn does. The transcript is the loop's memory, so it has to be readable at every instant.
2. **Prepare and stream.** The tool schemas come from `IAgentToolRegistry`, are offered on every step
   but the last, and the reply is accumulated in a buffer that is flushed to the database at most once
   a second. `ReasoningDelta` is forwarded here, unlike in chat: a step that spends thirty seconds
   deciding which file to open is otherwise thirty seconds of nothing.
3. **Decide what happened.** `AIStreamEvent.ToolCalls` - the provider's reassembled set - and not
   `finish_reason`, is what decides whether the run continues. Words and no calls ends the run.
4. **Act.** Each call is parsed, checked against the repeat counter, put to the approval gate if its
   risk is above `Read`, executed, and given a row. In that order, so that nobody is shown a dialog
   about malformed JSON and a model stuck in a loop cannot turn the approval prompt into the loop.
5. **Loop**, with the tool rows now part of the history the next request is built from.

Every call gets an answer row, whatever became of it - unknown tool, unparseable arguments, denial,
too many attempts. A call with no answer is not a smaller failure; it is a hole in the next request,
which providers reject outright. The single exception is a run that stops mid-way: nothing is
fabricated on the model's behalf, and the replay drops the unanswered calls instead.

A run ends in exactly one of `Completed`, `Failed` or `Cancelled`, and `Completed` carries an
`AgentStopReason` - `Answered`, `StepLimit` or `TimeLimit`. On the last permitted step the tools are
withheld, so a run that hits the step limit ends in a sentence rather than on a file listing, and it
is still reported as `StepLimit` because saying it answered would hide that there may be more to do.

### The approval gate

[`IAgentApproval`](src/AIClient.Application/Interfaces/IAgentApproval.cs) is one method, and section
28's whole safety position rests on it. Everything above `AgentToolRisk.Read` passes through it; the
default registration is `DenyingAgentApproval`, which refuses, so a host that forgets to implement it
gets an agent that can only read.

It is an interface rather than an event because the loop has to *wait*, and the answer takes as long
as a person takes. The App layer's implementation marshals to the dispatcher, shows the card inline in
the transcript, and honours the run's cancellation token so that pressing Stop closes the question
instead of leaving the run wedged behind it. Cancellation while a question is open is not a denial:
nothing is reported to the model, because the turn is over.

A denial *is* reported - as a tool result saying so - because a model told "the user declined" can
propose something else, while a model told nothing repeats itself.

### The sandbox

[`IWorkspaceService`](src/AIClient.Application/Interfaces/IWorkspaceService.cs) is the only way a tool
reaches a file, and it is deliberately not a `FileInfo` wrapper. Every method takes a path relative to
one root, resolves it, and refuses anything that lands outside - through `..`, through an absolute
path, or through a symlink, which is why the enumeration options set
`AttributesToSkip = FileAttributes.ReparsePoint`. On top of that it refuses version-control internals
and the file names that carry credentials, and caps what one call can return: file size, characters
per read, directory entries, search hits.

The root is chosen by the user, persisted, and re-validated against the disk on load, since a folder
can be moved or deleted between sessions. With no root the service reports `IsOpen == false` and every
tool fails cleanly, which is the state the application starts in.

### Running a program, which the sandbox cannot cover

[`RunCommandTool`](src/AIClient.Application/Services/Tools/RunCommandTool.cs) is the one tool the
section above does not describe, because a path guard bounds what a program is *started* on and nothing
about what it does afterwards. `npm install` reaches the network, a test suite reads whatever the
machine will give it, and a build script is a program someone else wrote. So the containment is a
different shape, and none of its four parts is reachable by the model:

1. **Off until the user turns it on**, once, in Settings, where the consequence is written out.
2. **An allowlist of program names.** Matched bare, case-insensitively, with a trailing `.exe` ignored
   on both sides. Not on it is a refusal that names the list, so a model does not spend three steps
   guessing synonyms.
3. **Approval on every call.** `AgentToolRisk.Execute` is excluded from the standing yes a run can
   accumulate for file tools, so ten commands is ten questions, and the dialog shows every argument
   unabbreviated - the flag that makes `git clean -xfd` destructive is in the argument list.
4. **No shell.** The program is started directly with an argument list through
   [`IProcessRunner`](src/AIClient.Application/Interfaces/IProcessRunner.cs), so `&&`, `|`, `>` and
   `$HOME` are text the program receives rather than syntax anything interprets.

The fourth is what makes the second worth having - an allowlist in front of a shell is decoration,
since `cmd /c anything` passes it - which is why the schema has no command-line field and
`RunCommandTool.Describe` refuses a `command` carrying a path, whitespace or a shell operator before
the allowlist is consulted. `ProcessRunner` drains both pipes as they fill rather than after the wait,
so a chatty build cannot deadlock on a full one, closes standard input so a program waiting to be asked
something sees the end of it instead of burning the timeout, kills the whole process tree
(`Kill(entireProcessTree: true)`) on a timeout or a cancellation, and strips this application's own
environment variables so a child that prints its environment cannot print a key.

## Persistence

One SQLite file, six tables, and a single migration
([`InitialCreate`](src/AIClient.Infrastructure/Database/Migrations)) applied by
`DatabaseInitializer` at startup before anything reads.

| Table | Notes |
| --- | --- |
| `Providers` | One row per provider, plus its enabled flag and last refresh time. Never a key. |
| `Models` | The cached catalogue. Unique on `(ProviderId, ModelId)`. |
| `Conversations` | Indexed on `(IsPinned, UpdatedAt)` - exactly the sidebar's ordering. |
| `Messages` | Indexed on `(ConversationId, SequenceNumber)`; cascade from the conversation. |
| `Attachments` | Indexed on `MessageId`; cascade from the message. |
| `Settings` | One row per section, value is JSON. |

Two decisions are worth defending.

**Timestamps are UTC ticks in an INTEGER column**, via
[`UtcTicksConverter`](src/AIClient.Infrastructure/Database/UtcTicksConverter.cs) applied as a
pre-convention rule over every `DateTimeOffset` in the model. SQLite has no date type; left alone,
EF Core maps `DateTimeOffset` to a TEXT form and then refuses to translate `ORDER BY`, `MIN`, `MAX`
or a range comparison over it, because two rows written in different time zones would sort by local
wall clock rather than by instant. Ticks are exact, fixed-width and monotonic, which is what makes
the `(IsPinned, UpdatedAt)` index usable rather than decorative. A pre-convention rule rather than
per-property configuration because forgetting the latter fails at query time, not at build time.

**The context is a factory, not a scoped service.** WPF has no request scope to hang a `DbContext`
off, a `DbContext` is not thread-safe, and a streaming turn writes from a background task while the
UI reads on the dispatcher. `AddDbContextFactory` plus a short-lived context per operation is the
only shape that stays correct under those conditions.

## Providers

Both shipping providers speak the OpenAI `/chat/completions` protocol, so the protocol lives once in
[`OpenAiCompatibleProvider`](src/AIClient.Infrastructure/Providers/OpenAiCompatible/OpenAiCompatibleProvider.cs)
and the subclasses carry only what actually differs - which, in both cases, is the catalogue.
`OpenRouterProvider` reads a rich one: real context windows, per-token pricing, modality flags and
an explicit list of accepted sampling parameters, all of it parsed rather than hardcoded, so a model
added upstream appears in the picker on the next refresh. `NvidiaProvider` faces the opposite
problem - `/v1/models` returns little beyond ids - so it surfaces every id it is given and annotates
known families with their published context window by longest-prefix match. An unrecognised model
still works; it just shows no context badge. Hardcoding a model list was ruled out for both.

The base class handles the request envelope, the SSE loop, the `[DONE]` sentinel, usage extraction,
error mapping and the truncation of an error body to 4 KiB. It reads with
`HttpCompletionOption.ResponseHeadersRead`, without which the first token would not appear until the
whole answer had been buffered - the single most important line for perceived speed.

A non-success status becomes an `AIStreamEvent.Error` rather than an exception, because a provider
can fail *after* sending usable text and the UI needs both the partial answer and the reason.

### Adding a provider

Four members are abstract and two are virtual, which is the whole surface:

```csharp
public sealed class MyProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "myprovider";

    public override string Id => ProviderId;
    public override string DisplayName => "My Provider";
    protected override string BaseUrl => "https://api.example.com/v1";
    protected override string HttpClientName => ProviderId;

    // Optional: ModelsPath and ChatCompletionsPath default to "models" and "chat/completions".
    protected override void ConfigureRequest(HttpRequestMessage request) { }

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document) => …;
}
```

Then two lines in
[`DependencyInjection.AddProviders`](src/AIClient.Infrastructure/DependencyInjection.cs):

```csharp
services.AddHttpClient(MyProvider.ProviderId, ConfigureStreamingClient);
services.AddSingleton<IAIProvider, MyProvider>();
```

That is all. `ProviderRegistry` takes `IEnumerable<IAIProvider>`, so it picks the new provider up
without being edited, and Settings, the model picker and the first-run screen are all driven from
the registry. A provider that is *not* OpenAI-compatible implements `IAIProvider` directly instead
and is registered the same way - the interface, not the base class, is the contract.

NVIDIA's base URL is overridable at runtime through `Providers:Nvidia`, so pointing that client at a
self-hosted NIM container, an on-prem deployment or a local OpenAI-compatible server needs no code at
all. OpenRouter's is fixed, since a gateway URL has nothing to point elsewhere. See
[DEVELOPMENT.md](DEVELOPMENT.md#configuration).

## Secrets

`ISecureStorage` is declared in Domain with four members - `GetAsync`, `SetAsync`, `DeleteAsync`,
`ContainsAsync` - and implemented once, by
[`DpapiSecureStorage`](src/AIClient.Infrastructure/SecureStorage/DpapiSecureStorage.cs): DPAPI
`CurrentUser` scope, one file per provider under `%APPDATA%\AIClient\secrets\`, written to
`<key>.dat.tmp` and moved into place so a concurrent read can never land on a half-written blob, with
a `SemaphoreSlim` serialising writes in-process.

The rules the brief states as prohibitions are enforced structurally rather than by convention:

| Rule | How it holds |
| --- | --- |
| Never in source or Git | The store is `%APPDATA%`, outside the tree; `.gitignore` covers the adjacent hazards - `.env`, `secrets.json`, `appsettings.Local.json`, `*.db`, `*.log` |
| Never logged | Log messages carry the *key name* only, never the value |
| Never in an error message | Including `ArgumentException` from a rejected key name |
| Never on a shared client | Attached per `HttpRequestMessage`; `DefaultRequestHeaders` is never touched |
| Never in the visual tree | [`ApiKeyBox`](src/AIClient.App/Behaviors/ApiKeyBox.cs) - a `PasswordBox` whose `Password` is deliberately not bound |
| Absent, not fatal | A blob that will not decrypt reads as `null`, so the user is led to re-enter it |

`ContainsAsync` checks for the file without decrypting, which is what lets every screen show a
"configured" badge without a DPAPI round trip per provider - and is why a corrupt blob is reported as
present but unreadable rather than as absent.

`SecureStorageTests` runs against real DPAPI over a temporary profile directory. Substituting the
encryption would leave the one claim worth making - that the bytes on disk are unreadable ciphertext
- asserted against a stub that hands back whatever it was told.

## Settings

Four sections - `General`, `Appearance`, `Chat`, `Storage` - each serialised to one JSON row in the
`Settings` table by [`SettingsService`](src/AIClient.Infrastructure/Repositories/SettingsService.cs).
Adding a setting is therefore a property on a record and no migration. A section that will not
deserialise is logged and reset to its defaults, alone: a configuration file the user cannot fix by
hand must never stop the application from starting, and one bad section must not take the other three
with it.

The in-memory tree is authoritative during a session. `UpdateAsync<TSection>` mutates, persists and
raises `SettingsChanged` as one operation, with the event raised outside the lock so a handler that
reads settings cannot deadlock.

Application settings are distinct from `appsettings.json`, which holds only what has to be known
before the database is open: provider endpoints and timeouts. See
[DEVELOPMENT.md](DEVELOPMENT.md#configuration).

## Errors

One enum, [`AIErrorKind`](src/AIClient.Domain/Enums/AIErrorKind.cs), is the only failure vocabulary
above the provider layer. `ProviderErrorMapper` produces it from a status code or a transport
exception, together with a sentence written for a person and a technical detail string for the
expandable section. The UI switches on the kind; it never sees an HTTP status.

| Kind | From | What the user is told |
| --- | --- | --- |
| `InvalidApiKey` | 401 | The key was rejected - check Settings → Providers |
| `PermissionDenied` | 403 | The key lacks access to this model, or billing is off |
| `NotFound` | 404 | Model or endpoint unknown - refresh the model list |
| `RateLimited` | 429 | Rate-limited or out of credit - wait and retry |
| `ContextLengthExceeded` | 400 *and* overflow wording in the body | Start a new chat or pick a larger window |
| `InvalidRequest` | any other 400 | The request was rejected as invalid |
| `ServiceUnavailable` | 502, 503, 504 | Temporary, usually brief |
| `ServerError` | 500, other 5xx | The provider's own fault |
| `Timeout` | 408, or `TaskCanceledException` wrapping `TimeoutException` | It took too long |
| `NetworkError` | `HttpRequestError` for DNS, connect or TLS | Check the connection; TLS names proxies and antivirus |
| `Cancelled` | `OperationCanceledException` | Nothing - Stop was pressed |
| `NotConfigured` | no key stored | Configure the provider |

A 400 is split by inspecting the body for overflow wording, because providers signal a context
overflow in prose rather than with a distinct status, and "your conversation is too long" and "that
parameter is not accepted" need opposite advice. Where the provider's own message is more specific
than the mapper's, it is appended rather than discarded.

`AIProviderException.IsRetryable` covers `Timeout`, `NetworkError`, `ServerError`,
`ServiceUnavailable`, `RateLimited` and `ModelUnavailable`. That flag - not the kind - is what decides
whether the failed message bubble offers Retry, so a 401 does not invite the user to try the same
rejected key again.

## Threading

WPF has one UI thread and EF Core has no thread affinity, which makes the boundary explicit rather
than incidental:

- Every service method is `async` and none of them touches a `Dispatcher`. Application and
  Infrastructure are UI-framework-agnostic, and that would be a lie if they marshalled.
- `ChatViewModel` consumes `IAsyncEnumerable<ChatTurnEvent>` with `await foreach` on the UI thread,
  so appending a delta to the transcript needs no marshalling at all. The awaits are what keep it
  responsive.
- Events raised from background work - `IProviderRegistry.ModelsChanged` after a refresh,
  `ISettingsService.SettingsChanged` - fire on whichever thread finished the work. Subscribers own
  the hop; [`UiThread`](src/AIClient.App/Services/UiThread.cs) is the one place that does it, and it
  is in the App project because that is the only project that knows what a dispatcher is.
- A streamed turn's database writes run on the thread pool through the context factory while the UI
  reads through its own context. That is safe only because there is no shared `DbContext`.

## Markdown rendering

An answer arrives a few characters at a time and has to be readable at every intermediate state.
Re-rendering the whole thing per token is what makes a naive chat UI stutter, so the work is split
across the layers by what each is good at:

```text
delta  ──►  StringBuilder  ──►  MarkdownParser  ──►  MarkdownDocument  ──►  Reconcile  ──►  WPF
            (MessageViewModel)  (Markdig, Application)  (blocks + hashes)   (diff by hash)   (visuals)
```

[`MarkdownParser`](src/AIClient.Application/Markdown/MarkdownParser.cs) lets Markdig do the
CommonMark parsing and then projects its AST onto a closed
[block model](src/AIClient.Application/Markdown/MarkdownDocument.cs): paragraph, heading, code,
list, quote, table, thematic break, with inline spans carrying `InlineStyle` flags so bold+italic is
expressible. The projection is what keeps Markdig types out of the view, makes the renderer a total
switch over seven cases, and gives every block a `ContentHash` computed once at parse time.

That hash is the whole trick. `MessageViewModel` accumulates deltas in a `StringBuilder` - string
concatenation is quadratic over a long answer - and re-parses on a 60 ms `DispatcherTimer` rather
than per token. `Reconcile` then walks the old and new block lists together, keeps the leading run
whose hashes match, and replaces only the tail. Mid-stream that means one growing paragraph is
rebuilt per tick instead of the entire transcript. The timer stops when the turn ends and
`RebuildBlocks` runs once more, so the final state is never a partially rendered fence.

The pipeline is deliberately narrow: `UseGridTables`, `UsePipeTables`, `UseEmphasisExtras`,
`UseAutoLinks` and `UseTaskLists`, but not `UseAdvancedExtensions` - footnotes, abbreviations,
figures and custom containers are parse work on a hot path for syntax no model emits. The parser
must also tolerate a half-written fence or an unclosed bold marker, because that is what every
answer looks like while it streams.

Rendering lives in [`MarkdownHost`](src/AIClient.App/Markdown/MarkdownHost.cs), and only three
things are built imperatively there: a paragraph's styled runs, a code block's coloured tokens, and
a table's column count - the three shapes that depend on the data rather than on the template.
Everything else is a `DataTemplate` in `MarkdownTemplates.xaml`. The alternative, an `ItemsControl`
per line or per inline run, produces hundreds of containers for one answer; a `TextBlock` filled
with `Run`s is a single element. `HighlightCode` is an inherited dependency property, so `ChatView`
sets it once at its root and code blocks nested inside list items pick it up.

## Decisions and trade-offs

The choices below cost more code than the obvious alternative, and each is here for a reason worth
writing down.

**No MediatR, no in-process bus.** Services are injected and called directly. A request/handler
indirection would buy pipeline behaviours and lose the ability to follow a chat turn by reading one
method. With four projects and a dozen services, the call graph is the documentation.

**No EF Core InMemory provider - the package is not even referenced.** The persistence bugs worth
catching are SQLite's own. It refuses to translate `ORDER BY` over EF Core's default
`DateTimeOffset` mapping; the in-memory provider accepts that query happily and the application
crashes on launch. Tests run against a real migrated file in a temporary directory, and having the
package available at all invites the wrong tool.

**DPAPI rather than Windows Credential Manager.** Credential Manager is the more conventional answer
and caps a credential blob at 2560 bytes. That is generous for `sk-or-v1-…` and not generous enough
for a self-hosted gateway issuing a long bearer token, which would fail at the worst possible
moment - on save, for one user, with a limit nothing in the UI could explain. DPAPI has no such cap,
is scoped to the Windows account the same way, and needs no P/Invoke.

**A hand-written SSE reader.** [`ServerSentEventReader`](src/AIClient.Infrastructure/Http/ServerSentEventReader.cs)
is about a hundred lines against a dependency that would do the same. It has to survive things a
general-purpose client is not obliged to: a final event with no trailing blank line, `:` heartbeat
comments, multiple `data:` lines joined with `\n`, and OpenAI's non-standard `[DONE]` sentinel. A
4 MiB per-event cap stops a malformed stream from growing a buffer without bound.

**A hand-written syntax highlighter.** [`SyntaxHighlighter`](src/AIClient.Application/Markdown/SyntaxHighlighter.cs)
is a single-pass scanner that tracks whether it is inside a string or a comment. Regex highlighting
has no notion of state and so mis-handles exactly the cases chat produces most - `//` inside a
string, a quote inside a comment. Profiles are grouped by family rather than per language, since C#,
Java, TypeScript and Go differ only in keyword sets, and adding a language is a dictionary entry.
Beyond 200 000 characters a block is shown unhighlighted rather than freezing the UI thread.

**No Rx.** `IAsyncEnumerable<T>` is the streaming primitive from the provider to the view model, and
`await foreach` on the UI thread needs no scheduler. Two `EventHandler` events cover the rest.

**Two `JsonSerializerOptions` instances, not source generation.** One per wire format - the provider
protocol and the settings rows - configured once and shared. The payloads are small and the reflection
cost is nowhere near the network cost; a `JsonSerializerContext` per DTO would be ceremony for no
measurable gain.

## What this is built to accept later

None of the following is implemented. The point of the layering is that each arrives as an addition
rather than a rewrite, and it is worth naming where.

- **More context sources.** `ContextBuilder` already turns something-that-is-not-a-chat-message into
  a fenced `<file name="…">` block ahead of the question. An editor selection, an open document or a
  repository search result is another source feeding the same assembly and the same trimming budget.
- **More providers.** `IAIProvider` is five members, and an OpenAI-compatible one is a subclass plus
  two registration lines. Anthropic or a local runtime with a different protocol implements the
  interface directly; nothing above Infrastructure changes.
- **More tools.** [`IAgentTool`](src/AIClient.Application/Interfaces/IAgentTool.cs) is a name, a JSON
  schema, a risk level and an `ExecuteAsync`. Adding one is a class and a registration line, and the
  risk level alone decides whether the approval gate stops it - there is no second list to keep in
  step. The one thing the seam is not built for is a tool that reaches outside the workspace: it has to
  bring its own gates, as `run_command` does, and those gates are the design work rather than the
  class.
- **An editor.** The agent already reads and writes through `IWorkspaceService`, so a document surface
  would share the sandbox rather than open files itself, and `ContextBuilder` already accepts a source
  that is not a chat message.
- **A different shell.** `UseWPF` is set in exactly one project, so the WPF assemblies are not even
  referenced by the other three, and WPF-UI reaches no further than App with the theme service behind
  `IAppThemeService`. Domain and Application could not touch a UI type if they tried - plain `net10.0`
  makes it a compile error rather than a convention.
- **A different store.** `IConversationService` and `ISettingsService` are declared in Application and
  implemented under `Infrastructure/Repositories`. Nothing outside Infrastructure names
  `AIClientDbContext`.

The one thing that would be a rewrite is a UI that reached a provider directly. That is why
[`DependencyInjection.cs`](src/AIClient.Infrastructure/DependencyInjection.cs) is the only
Infrastructure file the App project names, and why deleting it should break nothing under
`ViewModels`.
