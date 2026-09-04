# Development

How to build, run, migrate, test and extend this repository. [README.md](README.md) is the
user-facing description and [ARCHITECTURE.md](ARCHITECTURE.md) explains why the code is shaped the
way it is; this file is the day-to-day mechanics.

## Prerequisites

- Windows 10 version 1809 or later, or Windows 11. The App, Infrastructure and Tests projects target
  `net10.0-windows`, so the solution does not build on Linux or macOS - DPAPI and WPF are both
  Windows-only, and the TFM says so rather than failing at run time.
- [.NET 10 SDK](https://dotnet.microsoft.com/download). The runtime alone is enough to run a
  published build but not to build one.
- `dotnet-ef` only if you intend to add a migration:

```bash
dotnet tool install --global dotnet-ef
```

- An API key from at least one provider, for anything beyond the first-run screen. Not needed to
  build or to run the test suite.

Visual Studio 2026 or Rider will open `AIClient.slnx` directly. Nothing in the repository depends on
an IDE: there are no `.user` files, no launch profiles and no editor-specific settings beyond
[.editorconfig](.editorconfig).

## Build and run

```bash
dotnet build AIClient.slnx
```

```bash
dotnet run --project src/AIClient.App
```

```bash
dotnet publish src/AIClient.App -c Release -r win-x64 --self-contained false
```

`--self-contained true` also works and produces something that runs on a machine with no .NET
installed, at the cost of about 70 MB.

Warnings are not errors, with one exception: `WarningsAsErrors` is set to `nullable` in
[Directory.Build.props](Directory.Build.props), so a nullability warning fails the build. That is
deliberate - the codebase is fully annotated and a new `CS8618` is a real defect, whereas an
analyzer suggestion usually is not. `EnforceCodeStyleInBuild` is on, so `.editorconfig` violations
surface as build warnings rather than only in the IDE.

## Where the app writes

Everything is under `%APPDATA%\AIClient`, created on first construction of
[`AppPaths`](src/AIClient.Infrastructure/Configuration/AppPaths.cs) so no caller has to check whether
a directory exists. Nothing is written beside the executable and nothing anywhere in the repository.

| Path | Contents |
| --- | --- |
| `aiclient.db` | Conversations, messages, attachments, the model cache, settings |
| `secrets\<provider>.dat` | One DPAPI-encrypted API key per provider |
| `logs\aiclient-<date>.log` | One file per day, older ones deleted at startup |
| `attachments\` | Copies of attached files, while `CopyAttachmentsToStore` is on |

Deleting `aiclient.db` resets the app to a first run. Deleting `secrets\` forgets the keys and
nothing else. The tests never touch any of this: `AppPaths` has a constructor overload taking a root
directory, and every test that needs a profile passes a temporary one.

To read the log while the app runs, tail the newest file in `logs\`. EF Core's own categories are
filtered to `Warning` in [App.xaml.cs](src/AIClient.App/App.xaml.cs) - at `Information` it prints
every statement it executes, which buries the application's own diagnostics and puts SQL one step
away from user content. Sensitive-data logging stays off, so parameters appear as `?`.

The file sink is configured with a default `StorageSettings`, at `Information` with seven days of
retention, because the logging pipeline has to exist before the database it would read the values
from. `MinimumLogLevel` and `LogRetentionDays` are therefore persisted and shown in Settings but do
not yet reach the running logger; the attachment limits in the same section are read live by
`AttachmentService` and do. Closing that gap means reconfiguring the sink after `LoadAsync`, not
moving the settings.

## Configuration

There is no configuration file in the repository, and the app runs correctly without one. Two
optional sources are read, in this order, by
[`BuildHost`](src/AIClient.App/App.xaml.cs):

1. `appsettings.json` beside the executable - optional, not reloaded on change.
2. Environment variables prefixed `AICLIENT_`.

Everything a user would normally change lives in the database instead, edited through Settings, which
is why this surface is small. It exists for the things that have to be settable before the app can
start: provider endpoints and HTTP timeouts.

[`ProviderEndpointOptions`](src/AIClient.Infrastructure/Providers/ProviderEndpointOptions.cs) binds
the `Providers` section:

| Key | Default | Meaning |
| --- | --- | --- |
| `Providers:Nvidia` | `https://integrate.api.nvidia.com/v1` | Base URL for the NVIDIA-compatible endpoint |
| `Providers:StreamTimeoutSeconds` | `600` | Timeout on each provider's named `HttpClient`, floored at 30 |
| `Providers:RequestTimeoutSeconds` | `100` | Bound, but not yet read by anything - see below |

`StreamTimeoutSeconds` has to outlast a slow model rather than a slow network: a reasoning model
routinely runs past two minutes before its last token. Because it is set on the named client it
currently governs the catalogue call and the connection test as well, which is what
`RequestTimeoutSeconds` was meant to shorten - separating them needs a linked
`CancellationTokenSource` per call inside the provider base, not a second client-level timeout, so
the option is declared and unused for now. `ChatSettings.RequestTimeoutSeconds` in the database is a
different, similarly unwired knob; neither is exposed as a promise anywhere in the UI.

OpenRouter's base URL is deliberately absent: it is a hosted gateway with no self-hosted equivalent,
so the subclass hardcodes it. Only NVIDIA's is overridable, which is enough to point the app at a
NIM container, or at a local Ollama or LM Studio speaking the same protocol.

Both sources use the same keys. In `appsettings.json`, nested:

```jsonc
{
  "Providers": {
    "Nvidia": "http://localhost:11434/v1",
    "StreamTimeoutSeconds": 900
  }
}
```

In the environment, with `__` for the section separator and the `AICLIENT_` prefix - note that the
prefix is stripped before binding, so the key inside is `Providers:Nvidia`, not
`AICLIENT:Providers:Nvidia`:

```bash
set AICLIENT_Providers__Nvidia=http://localhost:11434/v1
```

Anything the app writes rather than reads - theme, sampling defaults, attachment limits, log
retention - is in the `Settings` table, one JSON row per section, and is not configurable from a
file. Adding a setting means adding a property to one of the four classes in
[`Configuration`](src/AIClient.Application/Configuration) and nothing else: no migration, and an
older row deserialises with the new property at its default.

## Database and migrations

Migrations live in
[`src/AIClient.Infrastructure/Database/Migrations`](src/AIClient.Infrastructure/Database/Migrations)
and are applied at startup by
[`DatabaseInitializer`](src/AIClient.Infrastructure/Database/DatabaseInitializer.cs), which logs which
ones it applied before it applies them. There is one so far, `InitialCreate`.

Adding one:

```bash
dotnet ef migrations add DescribeTheChange --project src/AIClient.Infrastructure --output-dir Database/Migrations
```

No startup project is needed.
[`DesignTimeDbContextFactory`](src/AIClient.Infrastructure/Database/DesignTimeDbContextFactory.cs)
lets the tooling construct a context without booting a WPF application, and points it at
`design-time.db` in the build output - so scaffolding reads the model and can never touch the real
database. That file is disposable; `.gitignore` covers it through `*.db*`.

Three things to know before writing one:

- **Providers are seeded from code, not from `HasData`.** A model seed is baked into the schema, so
  correcting a display name would need a migration on every user's machine. `SeedProvidersAsync` runs
  on every startup, is idempotent, and refreshes only the name and sort order - the fields the app
  owns rather than the user.
- **`DateTimeOffset` is stored as UTC ticks in an INTEGER column**, applied as a pre-convention rule
  in `ConfigureConventions` rather than per property. A new entity with a timestamp needs no
  attention; a new *column type* that needs converting does. See
  [ARCHITECTURE.md](ARCHITECTURE.md#persistence) for why.
- **The generated file is exempt from the project's style rules.** There is an
  [`.editorconfig`](src/AIClient.Infrastructure/Database/Migrations/.editorconfig) in that folder
  setting `generated_code = true`, because the generator emits block-scoped namespaces and its own
  formatting and hand-editing each new migration buys nothing.

To inspect the schema, open `%APPDATA%\AIClient\aiclient.db` with any SQLite browser. The app holds no
long-lived connection - a context per operation through `IDbContextFactory<T>` - so reading it while
the app runs is safe.

## Tests

```bash
dotnet test
```

A fresh clone with no API key and no network passes. The eight tests in
[`LiveProviderTests`](tests/AIClient.Tests/LiveProviderTests.cs) skip themselves and say why, because
§36 forbids a committed key and a committed key is the only thing that would make them
unconditional. To run them:

```bash
set AICLIENT_TEST_OPENROUTER_KEY=sk-or-v1-...
```

```bash
dotnet test --filter "Category=Live"
```

`AICLIENT_TEST_NVIDIA_KEY` does the same for NVIDIA. Each live test asks one model for one word at
temperature 0 with a 64-token cap. The variables are read and never written, and nothing in the suite
stores a key through `ISecureStorage` or under `%APPDATA%`.

Four conventions hold across the suite, and each is load-bearing rather than stylistic:

- **A real SQLite file, never the in-memory provider.** The package is not referenced at all.
  [`TestDatabase`](tests/AIClient.Tests/Support/TestDatabase.cs) creates a temporary directory,
  migrates it through the production `DatabaseInitializer`, and hands out contexts through the same
  `IDbContextFactory<T>` seam the app uses. `Pooling=False` so the directory can be deleted
  afterwards. `EnsureCreated` is not used: it builds the schema from the model and would hide a
  migration that disagrees with it.
- **Real DPAPI.** [`SecureStorageTests`](tests/AIClient.Tests/SecureStorageTests.cs) encrypts for the
  Windows account running the tests. The one claim worth making - that the bytes on disk are
  unreadable ciphertext - cannot be asserted against a stub that returns what it was given.
- **Fake handlers, not fake providers, for wire-format tests.**
  [`FakeHttpMessageHandler`](tests/AIClient.Tests/Support/FakeHttpMessageHandler.cs) replays recorded
  responses from [`WireFixtures`](tests/AIClient.Tests/Support/WireFixtures.cs), and
  [`ChunkedStream`](tests/AIClient.Tests/Support/ChunkedStream.cs) splits an SSE body at arbitrary
  boundaries so a frame straddling two reads is exercised rather than assumed.
- **`RecordingLogger<T>` rather than `NullLogger<T>` wherever §26 is the subject.** "No secret ever
  reaches the log" is a claim about output, so the output is captured and searched - including
  `Trace` and `Debug`, where careless logging is likeliest to hide.

Test names are sentences with underscores - `A_stored_key_comes_back_exactly_as_it_went_in` - so a
failure in CI output reads as the broken behaviour rather than as a method to go and look up. Comments
in a test say why the case matters, not what the code does.

`xUnit1051` is suppressed in [the test project](tests/AIClient.Tests/AIClient.Tests.csproj): nothing
in the suite is long-running, and threading the run-level token through several hundred call sites
would cost readability and buy nothing. The tests that are genuinely about cancellation pass their own
token, which is the behaviour under test.

## Conventions

Formatting is in [.editorconfig](.editorconfig) and enforced at build time, so it is not worth
restating here. What that file cannot express:

**The dependency rule comes first.** Domain and Application target plain `net10.0`, which makes a
reference to `System.Windows` or to DPAPI a compile error rather than a review comment. App may
reference Application and Domain; it may name exactly two Infrastructure types,
`AddInfrastructure` and `DatabaseInitializer`. If a change under `ViewModels` needs a third, the
missing piece is an interface in Application, not a using directive.

**Comments explain why.** The codebase records its reasoning in XML doc comments on the type, and
that is deliberate: both of these documents were written largely by reading them. A comment
restating the code is noise; a comment naming the failure a line prevents is the only record that
the failure was considered. `MaxHighlightLength`, `Pooling=False` and `CancellationToken.None` in
`ChatService` are all one line with a paragraph behind them.

**Interfaces are declared where they are consumed.** `IConversationService` and `ISettingsService`
live in Application and are implemented in `Infrastructure/Repositories`; `ISecureStorage` and
`IAIProvider` live in Domain. The implementing project is chosen by what the implementation needs,
not by where the interface lives.

**A cancellation token is a parameter, never a field**, and it is passed on every call that accepts
one, with one documented exception: the database writes issued after a stream has opened use
`CancellationToken.None`, because the token that just fired is the reason control reached that code
and the partial answer still has to be saved.

**Async all the way down.** No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()` anywhere.
`async void` appears only in the App project, on WPF overrides and event handlers where the signature
leaves no choice; each such body either cannot throw or handles its own failures, and says so in a
comment, because an exception escaping one is an unhandled crash rather than a faulted task.
`ConfigureAwait(true)` in the App project is intentional and marks a continuation that must resume on
the dispatcher; Application and Infrastructure use `ConfigureAwait(false)` throughout and contain no
`ConfigureAwait(true)` at all.

**No `Dispatcher` below the App project.** Services raise events on whatever thread finished the
work, and [`UiThread`](src/AIClient.App/Services/UiThread.cs) is the single place that hops back.

**Nullable annotations are honest.** `WarningsAsErrors=nullable` means a `!` is a claim made on
purpose, and there are five in the whole of `src`: two in `ChatService` on the line after the guard
that makes them safe, and three on an EF navigation property inside an expression that becomes SQL,
where the foreign key is what guarantees it.

## Commits

Conventional commits, one logical change each, subject in the imperative and under about 70
characters:

```text
feat(chat): keep partial text when a stream is stopped
fix(providers): map a 400 mentioning context length to ContextLengthExceeded
test: assert the secret-handling rules of sections 11, 26 and 28
docs: describe the architecture, the workflow and the conventions
refactor(app): move the dispatcher hop behind UiThread
```

Prefixes in use: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `build`. The scope is the area
rather than the file - `chat`, `providers`, `db`, `settings`, `app` - and is omitted when a change is
genuinely cross-cutting. The body explains why when the subject cannot.

**Never committed**, and each of these is in [.gitignore](.gitignore) rather than left to discipline:

| Pattern | Why |
| --- | --- |
| `.env`, `.env.*`, `secrets.json`, `**/apikeys.json` | §41: no credentials in history |
| `appsettings.Development.json`, `appsettings.Local.json` | Local endpoints, occasionally a key |
| `*.db*`, `*.sqlite*`, `/data/` | The user's conversations are their own |
| `*.log`, `/logs/` | Content and paths, and no value to a reader |
| `*.pfx`, `*.snk` | Signing material |

The API keys themselves are never in the tree to begin with: they live in
`%APPDATA%\AIClient\secrets`, which no pattern needs to cover because it is outside the repository.
A key that reaches a commit cannot be removed by a later one - a rewrite plus a revocation is the
only fix - which is why the store was put outside the tree rather than ignored inside it.

Before pushing: `dotnet build` clean, `dotnet test` green, and no secret in `git diff --staged`.
`main` is the only long-lived branch.

## Troubleshooting

**The app shows an error dialog and exits on launch.** That path is only reached when the host cannot
be built or the database cannot be migrated, so the dialog carries the exception and the newest file
in `%APPDATA%\AIClient\logs` has the rest. A database from a newer build than the executable is the
usual cause; deleting `aiclient.db` recreates it empty.

**The model picker is empty.** A provider with no key contributes nothing. Settings → Providers,
paste a key, **Refresh**. The catalogue is cached in SQLite, so the picker works offline afterwards but
never before the first successful refresh.

**A key that worked yesterday is reported as absent.** DPAPI is scoped to the Windows account, so a
blob written by a different account - a restored backup, a copied profile, a different user - cannot be
decrypted and deliberately reads as `null` rather than throwing. The log says `could not be
decrypted`. Re-enter the key; that is the only fix and the only action the UI can usefully offer.

**Live tests still skip after setting the variable.** `set` in one shell does not reach another, and
Visual Studio caches the environment it was started with. Confirm with `echo
%AICLIENT_TEST_OPENROUTER_KEY%` in the same shell that runs `dotnet test`.

**`dotnet ef` reports no DbContext.** Point it at the Infrastructure project, not the solution or the
App project - see [Database and migrations](#database-and-migrations). If it reports a version
mismatch, `dotnet tool update --global dotnet-ef` to match the .NET 10 SDK.

**The build fails on `net10.0-windows`.** The solution is Windows-only by design; there is no
cross-platform path, because WPF and DPAPI have no substitutes that would satisfy the brief's stack
and §11's requirement that a key be held by a Windows storage mechanism.

**A test leaves a directory in `%TEMP%`.** `TestDatabase` and `SecureStorageTests` both swallow
`IOException` on cleanup rather than failing a run over a locked file. `%TEMP%\aiclient-tests` and
`%TEMP%\aiclient-secrets` are safe to delete at any time.
