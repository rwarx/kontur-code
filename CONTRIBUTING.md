# Contributing to Kontur Code

Thanks for looking. This is a young project and contributions are genuinely
welcome — but the review bar is high on purpose, because this program can write
files and run programs on your machine.

Read [ARCHITECTURE.md](ARCHITECTURE.md) before you change anything structural.
Read [DEVELOPMENT.md](DEVELOPMENT.md) before you build anything. Both exist to save
you the hour of discovering a rule by breaking it.

---

## The one rule that is not negotiable

**Dependencies point inwards.**

```text
AIClient.Domain ← AIClient.Application ← AIClient.Infrastructure ← AIClient.App
                                  ← AIClient.Server
```

`Domain` and `Application` target plain `net10.0`. That is deliberate and it is
load-bearing: it makes a reference to `System.Windows`, DPAPI or a registry key a
**compile error** rather than a review comment. Please do not "fix" the target
framework to reach something WPF.

A change under `ViewModels` that needs a new type from Infrastructure needs a new
interface in `Application`, not a `using` directive. The mechanical test for
whether a change respects this: if deleting `DependencyInjection.cs` would break a
file under `ViewModels`, the layering has been violated.

---

## Getting set up

Requires **Windows 10 1809+ or Windows 11** and the **.NET 10 SDK**. There is no
cross-platform path, and the TFM says so rather than failing at run time.

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

dotnet build AIClient.slnx
dotnet test
dotnet run --project src/AIClient.App          # the WPF host

cd electron
npm install
npm run dev                                   # renderer only, needs a backend
```

The renderer can run on its own against a seeded demo workspace, which is the
fastest way to change a component.

---

## Before you open a pull request

```bash
dotnet build AIClient.slnx     # warnings are errors — TreatWarningsAsErrors is on
dotnet test                    # a real SQLite file, real DPAPI
cd electron && npm run typecheck
```

All three must be green. `TreatWarningsAsErrors` means a new warning fails the
build; that is deliberate, the codebase is fully annotated, and a new warning is a
real defect.

---

## Conventions that are not in `.editorconfig`

`.editorconfig` is enforced at build time. These are the things it cannot express:

- **Comments explain why.** A comment restating the code is noise. A comment naming
  the failure a line prevents is the only record that the failure was considered.
  `MaxHighlightLength`, `Pooling=False` and the `CancellationToken.None` in
  `ChatService` are each one line with a paragraph behind them.
- **A cancellation token is a parameter, never a field**, and is passed on every
  call that accepts one. There is exactly one documented exception: database writes
  after a stream has opened, where the token that just fired is the reason control
  is there and the partial answer still has to be saved.
- **Async all the way down.** No `.Result`, no `.Wait()`,
  no `GetAwaiter().GetResult()`. `async void` appears only in the App project on
  WPF overrides and event handlers, and each such body handles its own failures and
  says so.
- **No `Dispatcher` below the App project.** Services raise events on whatever
  thread finished the work; `UiThread` is the single place that hops back.
- **Application and Infrastructure use `ConfigureAwait(false)`** and contain no
  `ConfigureAwait(true)`. The App project's use is intentional and marks a
  continuation that must resume on the dispatcher.
- **Nullable annotations are honest.** `WarningsAsErrors=nullable` means a `!` is a
  claim made on purpose.
- **Interfaces are declared where they are consumed.** The implementing project is
  chosen by what the implementation needs, not by where the interface lives.

---

## Tests

Five conventions hold across the suite, and each is load-bearing:

- **A real SQLite file, never the in-memory provider.** The package is not
  referenced at all. The persistence bugs worth catching are SQLite's own.
- **Real DPAPI** in the storage tests. "The bytes on disk are unreadable
  ciphertext" cannot be asserted against a stub.
- **Fake handlers, not fake providers,** for wire-format tests — and the fake SSE
  body is split at arbitrary boundaries, so a frame straddling two reads is
  exercised rather than assumed.
- **A recording logger rather than a null logger** wherever secrecy is the subject.
  "No secret ever reaches the log" is a claim about output, so the output is
  captured and searched.
- **Real directories for the workspace, and a real escape attempt for each rule.**
  A path guard tested against strings alone is a guard tested against the author's
  idea of what a file system does.

Test names are sentences with underscores:
`A_stored_key_comes_back_exactly_as_it_went_in`, so a failure in CI output reads as
the broken behaviour rather than as a method to go and look up.

---

## If you touch the agent's safety

Read [DEVELOPMENT.md § The agent's safety model](DEVELOPMENT.md#the-agents-safety-model)
first. The rules there are not defensive habits; each one exists because its
absence has a named consequence. In particular:

- **Declare a risk level.** `Read`, `Write` or `Execute` — the level alone decides
  whether the approval gate stops your tool. There is no second list to keep in
  step.
- **Extend the refusal lists, do not add a check at a call site.** A rule that lives
  in one tool is a rule the next tool will not have.
- **A tool that reaches outside the workspace brings its own gates**, as
  `run_command` does, and those gates are the design work rather than the class.

---

## Commits and pull requests

Conventional commits, one logical change each, imperative subject under about 70
characters:

```text
feat(chat): keep partial text when a stream is stopped
fix(providers): map a 400 mentioning context length to ContextLengthExceeded
test: assert the secret-handling rules of sections 11, 26 and 28
refactor(app): move the dispatcher hop behind UiThread
```

Prefixes in use: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `build`.
The scope is the area rather than the file, and is omitted when a change is
genuinely cross-cutting. The body explains **why** when the subject cannot.

For a pull request: describe the behaviour that changed and what you verified. If
you changed the safety model, say so explicitly and say which gate you added it
behind. Review is a conversation, not an obstacle — but a change to the agent's
containment gets read carefully, so make it easy to find.

---

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).

---

## Licence

By contributing you agree that your contribution is licensed under the
[MIT Licence](LICENSE). If that is not acceptable, do not open a pull request.

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).