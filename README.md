# AI Client

A native Windows desktop client for large language models. One window, your own API keys, your
conversations in a local SQLite file.

This is the first stage of a longer plan. The architecture is the one an AI-assisted IDE will
need - a domain that knows nothing about HTTP, a UI that cannot reach a provider, streaming that
runs end to end as `IAsyncEnumerable<T>` - and the feature set is a workspace plus an agent that can
work in a folder you choose: chat, sessions, models, providers, settings, local storage, and a tool
loop that reads and edits files under your supervision. There is no editor and no repository
awareness yet. Those are later stages, and nothing here forecloses them.

## What it does

- **Streaming chat.** Tokens appear as they arrive. Stop mid-answer and the partial text is kept,
  not discarded. Regenerate replaces the answer in place, optionally on a different model.
- **Agent mode.** A toggle in the composer sends the message to a tool loop instead of straight to
  the model. Each message says whether it is a **plan** or a **build**, and it starts on plan: the
  build can list, read, search, write, edit, move and delete files under one folder you nominate, and
  asks before every change; a plan reads and writes nothing. See [Agent mode](#agent-mode).
- **Markdown and code.** Headings, lists, tables, quotes and fenced code rendered as WPF content
  rather than HTML in a browser control, with syntax highlighting for the common languages.
- **Sessions.** Create, rename, pin, search and delete conversations. Titles are generated from
  the first message unless you set one yourself.
- **Model catalogue.** Fetched from each provider and cached in SQLite, so the picker opens
  instantly and still works with no network. Context window, pricing, streaming, image and tool
  support come from the provider rather than a hardcoded list.
- **Providers.** OpenRouter and NVIDIA NIM out of the box, both OpenAI-compatible. Test the
  connection before you rely on it.
- **Attachments.** Text and source files are read, size-capped and inlined into the prompt.
  Binaries are refused even when renamed to `.txt`.
- **Export.** A conversation to Markdown or JSON.
- **Settings.** Appearance, chat defaults, sampling parameters, storage and attachment limits, the
  agent's folder and its budgets, all persisted locally.
- **Fluent shell.** Light, dark or follow-the-system theme, Mica backdrop, command palette.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the runtime alone is enough to run
  a published build
- An API key from at least one supported provider

## Build and run

```bash
dotnet build AIClient.slnx
```

```bash
dotnet run --project src/AIClient.App
```

To produce something you can copy to another machine:

```bash
dotnet publish src/AIClient.App -c Release -r win-x64 --self-contained false
```

## First run

1. Launch the app. The first-run screen asks for a provider and a key.
2. Paste the key. It is encrypted with DPAPI and written under your profile - never into the
   repository, the database, or a log.
3. **Refresh** fetches the model catalogue. **Test connection** confirms the key without spending
   tokens.
4. Pick a model and start typing. The choice is remembered as the default for new chats.

Settings → Providers does the same later, and is where you replace or remove a key.

## Providers

| Provider | Endpoint | Where to get a key |
| --- | --- | --- |
| OpenRouter | `https://openrouter.ai/api/v1` | <https://openrouter.ai/keys> |
| NVIDIA NIM | `https://integrate.api.nvidia.com/v1` | <https://build.nvidia.com/settings/api-keys> |

Both speak the OpenAI `/chat/completions` protocol, which is why they share one implementation.
The NVIDIA base URL can be pointed elsewhere - a self-hosted NIM container, or a local Ollama or LM
Studio speaking the same protocol - through `appsettings.json` beside the executable, or through the
environment:

```jsonc
{
  "Providers": {
    "Nvidia": "http://localhost:11434/v1"
  }
}
```

```bash
set AICLIENT_Providers__Nvidia=http://localhost:11434/v1
```

Adding a provider properly is a subclass and one registration line; see
[ARCHITECTURE.md](ARCHITECTURE.md).

## Agent mode

Turn on **Agent** in the composer and the message goes to a loop instead of to a single completion.
The model gets a set of tools, asks for the ones it needs, sees what they returned, and continues
until it has an answer or a budget runs out. The transcript shows the work: one card per call, with
the arguments summarised, the outcome, and the result behind a chevron.

Beside the toggle is what kind of run it will be. It is a per-message choice rather than a preference
- plan, read the plan, then build - and it starts on **Plan**, so the first thing agent mode does for
somebody who has just found it is describe what it would do rather than start doing it.

| Mode | Can | Cannot |
| --- | --- | --- |
| **Plan** | Read the folder, and record a plan with `submit_plan` | Write, move, delete or run anything |
| **Plan + canvas** | The same, and hands the plan over as parts and dependencies rather than as prose, so it can be drawn | The same |
| **Build** | Everything in the table below, asking first wherever it says so | Record a plan - that is what the other two are for |

The mode is enforced rather than suggested. The tools a mode does not allow are left out of the
request, and a call for one that arrives anyway is refused before its arguments are read - the offer
is a courtesy, the mode is the rule. A refusal is a sentence the model can act on rather than an
error, so a planning run that reached for `write_file` is told to finish the plan instead of the run
ending.

The canvas is not in this build yet, so **Plan + canvas** records the same structured plan and the
model is told, in as many words, that there is nowhere to draw it and it should write the plan out
instead. That is the whole difference: when the canvas lands, one registration line turns the drawing
on and nothing else about the mode changes.

**Build** is the only mode that needs a folder, and choosing it without one asks which folder to use.
That folder is the whole of the agent's reach - it is remembered between sessions, shown under the
composer while the mode is on, and changed or closed in Settings → Agent. Cancelling the picker falls
back to Plan rather than switching the agent off. Planning with no folder open is not a degraded state
but the ordinary one for a project that does not exist yet: there is nothing to read, so the plan comes
from what you have told it.

| Tool | Does | Asks first |
| --- | --- | --- |
| `list_files` | Lists a directory, ignoring `.git`, `bin`, `obj`, `node_modules` and friends | No |
| `read_file` | Reads a text file, size-capped and optionally by line range | No |
| `search_files` | Searches file contents, literally or by regex, capped at 150 matching lines | No |
| `submit_plan` | Records the plan - title, steps, the parts it would create, the risks - and ends the run | No, and only while planning |
| `write_file` | Creates or replaces a file | Yes |
| `edit_file` | Replaces an exact snippet inside a file | Yes |
| `create_directory` | Creates a folder | Yes |
| `move_file` | Moves or renames | Yes |
| `delete_file` | Deletes a file or an empty folder | Yes |
| `run_command` | Runs one allowed program in the folder and returns its output and exit code | Yes |

Reads are refused outside the folder, and so are the paths that carry credentials or version-control
internals - `.git`, `.env`, key and certificate files - whether they are named directly, reached
through `..`, or reached through a symlink pointing out of the tree.

Every tool marked *Yes* stops and asks, every time. The question leads with one
line naming the effect - `Create src/Widget.cs`, `Overwrite 42 lines in src/Widget.cs`,
`Delete docs/old.md` - and an edit or an overwrite carries a diff under it. **Approve** applies it,
**Deny** hands the model a refusal it can react to and carry on from, and stopping the run marks
whatever was open as interrupted rather than guessing whether it landed. There is no "approve
everything" switch.

`run_command` is the one that reaches outside the folder, so it is fenced differently. It is off
until you turn it on in Settings → Running programs, and then only the programs on your list can
run - `dotnet`, `git`, `npm` and the other toolchains, by name and never by path. There is no shell:
the program is started directly with an argument list, so `&&`, `|`, `>` and `$HOME` are text passed
to it rather than syntax, and a shell is not on the shipped list because allowing one would make the
rest of the list decorative. Approval is asked for every single call and is never remembered - ten
commands is ten questions - and the dialog shows the program and each argument in full, because the
flag that makes a command destructive is in the argument list. What a program does once it is
running is bounded by your account, not by the workspace, which is why the switch is a decision you
take rather than a default.

Three budgets in Settings bound a run: steps per message (25), a time limit (10 minutes, 0 for
none), and the largest file the agent may open (512 KB). A command has two of its own: how long it
may run before it is killed (2 minutes) and how much of its output is kept (20,000 characters, the
end rather than the beginning, because that is where a build says what went wrong). A model that
proposes the same call three times in a row is told so rather than being allowed to loop.

## Where your data lives

Everything is under `%APPDATA%\AIClient`, and nothing leaves the machine except the requests you
send to your chosen provider.

| Path | Contents |
| --- | --- |
| `aiclient.db` | Conversations, messages, attachments, the model cache, settings |
| `secrets\<provider>.dat` | One DPAPI-encrypted API key per provider |
| `logs\` | Rolling log files, kept for seven days by default |
| `attachments\` | Copies of attached files, when that option is on |

There is no telemetry, no account, and no sync.

The folder you give the agent is the one exception: it is somewhere you chose, the agent writes there
directly, and only its path is kept under `%APPDATA%`. Nothing is copied into the application's own
storage, so undoing a change the agent made is a job for your version control, not for this app.

## API keys

Keys are encrypted with Windows DPAPI scoped to your user account, so another user on the same
machine cannot read them and a copied file is useless elsewhere. No key material is stored by the
application and there is no master password to lose.

A key is attached to a single outgoing request and never to a shared `HttpClient`, never written to
a log, never included in an error message, and never persisted anywhere but the encrypted file. It
is typed into a `PasswordBox` that is never data-bound - so the plaintext stays out of the visual
tree and the binding engine - and cleared the moment it is saved. `SecureStorageTests` and
`LiveProviderTests` assert these properties rather than trusting them.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | New chat |
| `Ctrl+K` | Search sessions |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+B` | Toggle the sidebar |
| `Ctrl+,` | Settings |
| `Esc` | Stop the answer being streamed |
| `Enter` / `Shift+Enter` | Send / newline (swappable in Settings) |

## Tests

```bash
dotnet test
```

745 tests against a real migrated SQLite file, real DPAPI, and fake HTTP handlers replaying recorded
provider responses. A fresh clone with no key and no network passes: the eight tests that need a
live provider skip themselves and say so.

To run those eight as well, put a key in the environment first:

```bash
set AICLIENT_TEST_OPENROUTER_KEY=sk-or-v1-...
```

```bash
dotnet test --filter "Category=Live"
```

Each asks one model for one word at temperature 0 with a 64-token cap, so the bill is negligible.
`AICLIENT_TEST_NVIDIA_KEY` does the same for NVIDIA. The variables are read and never written; no
key is committed, and nothing in the suite stores one under `%APPDATA%`.

## Tech stack

| Concern | Choice |
| --- | --- |
| Runtime | .NET 10, `net10.0-windows` for the app and infrastructure, `net10.0` for the domain |
| UI | WPF with MVVM, [WPF-UI](https://github.com/lepoco/wpfui) 4.3.0 for the Fluent shell |
| MVVM | CommunityToolkit.Mvvm 8.4.0, source generators rather than hand-written boilerplate |
| Storage | EF Core 10 over SQLite, migrations applied at startup |
| HTTP | `HttpClient` through `IHttpClientFactory`, one named client per provider |
| JSON | `System.Text.Json`, one shared options instance per wire format |
| Markdown | Markdig parses to a block model in Application; App renders it as WPF controls |
| Secrets | DPAPI via `System.Security.Cryptography.ProtectedData` |
| Tests | xunit.v3 |

WPF-UI won over the two obvious alternatives. **ModernWpf** is a faithful port of the Windows 10
WinUI 2 look, which is a version behind: no Mica backdrop, none of the Windows 11 corner and
title-bar treatment, and no release since early 2022, so the shell would have started life dated
and with no upstream to follow. **MaterialDesignInXamlToolkit** is the better maintained of the
two, but it implements Google's Material Design: a client meant to look like it ships with Windows
would have spent its whole life arguing with the library's shadows, ripples and type ramp. WPF-UI
gives the Fluent controls, the Mica backdrop and the custom title bar the design calls for, and it
tracks the OS the app targets, so the platform doing something new is an upgrade rather than a
rewrite. Nothing about the choice reaches past the App layer: the theme service sits behind
`IAppThemeService`, and swapping the control library would leave the other three projects untouched.

## Project layout

```text
src/AIClient.Domain          Entities, enums, provider and storage contracts. No dependencies.
src/AIClient.Application     Services and the contracts the UI binds to. No HTTP, no SQL.
src/AIClient.Infrastructure  EF Core, SQLite, providers, DPAPI, file logging.
src/AIClient.App             WPF: windows, views, view models, converters, behaviours.
tests/AIClient.Tests         The suite above.
```

Dependencies point one way: App → Application → Domain, with Infrastructure implementing the
interfaces the two middle layers declare and registering itself through a single
`AddInfrastructure` call. The App project references no provider type and no `DbContext`: the agent
loop it drives lives in Application and is reached through `IAgentService`, which is what kept
adding an agent from having to be threaded through the UI, and is what a future editor will use too.
[ARCHITECTURE.md](ARCHITECTURE.md) has the diagram, the streaming pipeline and the reasoning.
[DEVELOPMENT.md](DEVELOPMENT.md) covers migrations, configuration and the conventions.

## Not in this version

No editor, no file tree, no repository awareness, no MCP, no image input, no plugins. The agent can
read files, change them and run the programs you have allowed, but there is no shell and no way for
it to ask for one. There is no canvas either: **Plan + canvas** is the mode and the structured plan
it produces, and drawing that plan is the next stage rather than part of this one. Multiple agents,
background runs and a diff-review pane are later stages too - the layering is the reason they can
arrive without a rewrite, but none of it is here yet, and the feature list above is the whole of it.
