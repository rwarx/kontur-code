# Kontur Code

**Read this first:** Kontur Code is a **developer tool that can write to your files and run programs
on your machine.** That is the product, not a bug in it. Everything below about the agent's
containment — and every known gap — is in
[SECURITY.md](SECURITY.md). Read it before you point this at anything you care about.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

A desktop LLM client that grew into a **spatial AI development environment**. One window, your own
API keys, your conversations in a local SQLite file — and a workspace that turns the folder you point
it at into a graph you can actually see.

Two hosts share every layer below the window: a **WPF application** and an **Electron + React**
shell over the same .NET core, so the app is not limited by either toolkit's ceiling.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="The spatial canvas: the workspace as a dependency graph with labelled edges" width="100%">
</p>

---

## Contents

- [Screenshots](#screenshots)
- [What it does](#what-it-does)
- [The agent](#the-agent)
- [Privacy, in short](#privacy-in-short)
- [Requirements](#requirements)
- [Install](#install)
- [First run](#first-run)
- [Architecture](#architecture)
- [Development](#development)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Licence](#licence)
- [Status](#status)

---

## Screenshots

### Chat

<p align="center">
  <img src="docs/screenshots/chat.png" alt="A chat session with an assistant answer and the workspace context panel" width="100%">
</p>

Tokens appear as they arrive. Stop mid-answer and **the partial text is kept**, not discarded — it
stays usable as context for the next turn.

### The spatial canvas

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Nodes and labelled dependency edges on an infinite canvas, with a minimap" width="100%">
</p>

Your project as a graph: files, folders, modules, services, interfaces, data, tests and plans as
nodes, with containment and dependency edges between them. Pan, zoom, marquee-select, and watch a
minimap of the whole graph in the corner. Edges are labelled — `Login() → CreateTokenAsync` is a call
edge; "compile-time only" is a dependency that never runs.

### The graph outline

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="A filterable outline tree of the graph, grouped by node kind" width="100%">
</p>

The same graph as a structure you can read and filter by name or path.

### The editor

<p align="center">
  <img src="docs/screenshots/editor.png" alt="A C# file open in the editor with syntax highlighting and a change counter" width="100%">
</p>

CodeMirror 6 with ten language grammars, inline AI edits on a selection, and ghost-text completion.

### Settings

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Settings: theme, language, interface and chat defaults" width="100%">
</p>

Theme, interface language, app scale, system prompt, sampling parameters — all local, all persisted
in your own database.

---

## What it does

- **Streaming chat.** Tokens arrive as they are produced. Stop keeps the partial answer. Regenerate
  replaces it in place, optionally on a different model.
- **Three work modes.** *Chat* for conversation, *Cowork* for analysis, and *Code* — where the agent
  gets a workspace and a tool loop. The mode is a property of the message, not of the app, so "plan
  this, then build it" is two messages rather than two trips to Settings.
- **The spatial graph.** Your folder is indexed into nodes and edges automatically. A diff-based
  indexer adds new files, drops deleted ones, and **preserves the layout you arranged**. Plans the
  agent produces land on the canvas as node-and-edge sets — undoable, persisted, and yours to reject.
- **Unified workspace surfaces.** The canvas as a map, the graph as a structure, a file tree, the
  editor, a git panel, the trajectory of a run, and a tasks view — all one `Ctrl+Shift+P` apart.
- **Git.** Status, staged and unstaged diffs, stage, commit, branch, revert, push, pull, fetch. All
  through `git` with **no shell** and validated arguments.
- **Token accounting.** Live usage, estimated cost, and what the model is actually holding in
  context — with a **Compact session** button that folds older turns into a summary.
- **Markdown rendering.** Headings, lists, tables, quotes, task lists and fenced code, with syntax
  highlighting. Rendered as structured content, **never as injected HTML**.
- **Model catalogue.** Fetched from each provider and cached in SQLite, so the picker works offline
  afterwards. Context window, pricing and capabilities come from the provider, not a hardcoded list.
- **Two providers out of the box** — OpenRouter and NVIDIA NIM, both OpenAI-compatible. Point
  NVIDIA's endpoint at a local Ollama, LM Studio or self-hosted NIM container and nothing leaves your
  machine.
- **Session bundles.** Export the whole session — chat, canvas, files, goals — as a `.zip`.
- **Three languages.** English, Russian and German, applied live across the whole interface.
- **Light and dark**, following the system or pinned.

---

## The agent

The agent runs a tool loop, and its reach is the thing to understand before you use it.

| | |
| --- | --- |
| **Works in** | One folder you nominate, and refuses to read or write outside it |
| **Also refused, inside that folder** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — by name, always |
| **Asks before** | Every write, every external file, every network request, every program |
| **Never** | Runs a shell. `&&`, `\|`, `>` and `$HOME` are text the program receives |
| **Programs** | Off by default. Then an allowlist only a person edits. Then approval on *every* call |
| **Undo** | Your version control. Changes are shown before they are made, not reversed after |

A refusal names the rule and tells the model what to do instead, so it stops reaching for the same
tool three times.

**Everything outside that folder is opt-in, and off until you turn it on.** Network fetching and
out-of-project file access are separate switches in Settings, and each call still goes through the
approval prompt.

> The full containment model — and **eight known gaps**, including one that lets a Windows junction
> defeat the credential-filename rules for out-of-project files — is in
> [SECURITY.md](SECURITY.md). This is an alpha; read it before trusting it.

---

## Privacy, in short

- **No telemetry. No analytics. No crash reporting. No accounts.** There is no code in this
  repository that opens a connection to any address this project owns.
- **Your conversations never touch a server.** They are a SQLite file in your own user profile.
- **API keys are encrypted** with Windows DPAPI, scoped to your Windows account, and never written to
  a log.
- **What leaves your machine:** exactly what you send to a model provider, and only when you press
  Send. The complete list of network destinations is in
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Consecutive transcripts are readable without this app.** The database is not encrypted at rest —
  a deliberate trade-off, documented rather than glossed over.
- **Your model provider sees your prompt**, under *its* policy, not this project's. That is the deal
  a client for someone else's model makes.

Full detail, written against GDPR, Russian 152-FZ and CCPA/CPRA, is in
[PRIVACY.md](PRIVACY.md). It also explains how to export and how to delete everything.

---

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — for the installer only; a
  published build needs it, the sources need the SDK
- An API key from [OpenRouter](https://openrouter.ai) or [NVIDIA](https://integrate.api.nvidia.com)
- About 500 MB of disk, and a folder you are willing to let an agent read

There is no cross-platform build. DPAPI and WPF are Windows-only, and the target framework says so
rather than failing at run time.

---

## Install

Download the installer from the
[releases page](https://github.com/rwarx/kontur-code/releases). It is a per-user NSIS install — no
administrator rights required.

The first release is an **alpha**. It is published because the shape is settled enough to build
against, not because it is ready for unattended use.

<details>
<summary>Build it yourself</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# The sidecar has to be published next to where Electron looks for it
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Building the .NET solution on its own gives you the WPF host:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## First run

1. **Settings → Providers**, paste an API key, press **Refresh**. The model picker stays empty until
   one provider succeeds — the catalogue is cached afterwards, so it works offline from then on.
2. **Open a folder.** In *Code* mode, point it at a project. It is indexed into the graph, and from
   then on the agent's world is that folder.
3. **Commit before you let it work.** `git commit` an empty commit if you like. The agent writes
   directly into your working tree with nothing staged and nothing backed up; your history is the
   undo, and the only one.
4. **Read [SECURITY.md](SECURITY.md)** if you intend to enable command execution or out-of-project
   file access. Both are off by default, and both are the features with sharp edges.

---

## Architecture

Five projects, one rule: **dependencies point inwards.** `Domain` and `Application` target plain
`net10.0`, which makes reaching for WPF or DPAPI a compile error rather than a review comment.

```text
AIClient.Domain ◄──── AIClient.Application ◄──── AIClient.Infrastructure
                          ▲                          ▲            ▲
                          └──────── AIClient.App ────┘            │
                          └──────── AIClient.Server ─────────────┘
```

```text
provider bytes ──► AIStreamEvent ──► ChatTurnEvent ──► the UI
   (SSE frames)       (Domain)          (Application)    (WPF or React)
```

Three event vocabularies, each narrower than the last, translated at each boundary. A provider
cannot put a database id in the type it returns, because the type it returns is not the type the UI
consumes.

The local API requires a per-launch bearer token and refuses to bind to anything but loopback —
being on `127.0.0.1` is not an authorisation boundary, and the code treats it as one that is not.

Full reasoning, including the two doors to the file system and the two canvas renderers, is in
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## Development

```bash
dotnet build AIClient.slnx     # warnings are errors — that is deliberate
dotnet test                    # 896 tests, no network and no API key needed

cd electron
npm install
npm run typecheck
npm run dev                    # renderer against a seeded demo workspace, no backend needed
```

Requires Windows and the .NET 10 SDK. Node 22 is needed only for the renderer.

Conventions that matter, and that `.editorconfig` cannot express, are in
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Documentation

| Document | What is in it |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Why the code is shaped this way. Read before changing structure. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Build, migrate, test, extend. Read before changing anything. |
| [SECURITY.md](SECURITY.md) | The threat model, what is protected, **and the known gaps**. |
| [PRIVACY.md](PRIVACY.md) | What data exists, where it goes, and your rights. GDPR / 152-ФЗ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Every change, with the security fixes called out. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Bundled components and their licences. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | How to take part. |

---

## Contributing

Contributions are welcome, and the review bar on changes to the agent's safety model is high on
purpose — because that code can write files and run programs on your machine.

Start with [CONTRIBUTING.md](CONTRIBUTING.md). The short version: one logical change per pull
request, `dotnet test` green, and if you touch the agent's reach, say in the description which gate
you put it behind.

Please **do not open a public issue for a security vulnerability** — see
[SECURITY.md](SECURITY.md) for private reporting.

---

## Licence

**MIT.** See [LICENSE](LICENSE).

Third-party components keep their own licences — 40-odd bundled packages plus Electron and Chromium
— catalogued in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Status

`0.1.0-alpha`. Published as a prerelease, deliberately.

**Works:** streaming chat, both hosts, the spatial graph and canvas, the agent tool loop with its
approval gate, the editor, git, sessions and bundles, three languages.

**Known not to work well** — all listed in [CHANGELOG](CHANGELOG.md#known-limitations), all listed
with file references in [SECURITY.md](SECURITY.md#known-gaps):

1. A Windows junction can defeat the credential-filename rules for out-of-project file access.
2. Two approvals arriving together can hang a run instead of failing it.
3. The event stream has no heartbeat and no reconnect — a dropped connection loses the run.
4. The editor writes to disk on every keystroke, with no debounce.
5. The renderer persists workspace file contents into `localStorage`; large projects can exceed the
   browser quota.
6. The newest surfaces — external file tools, the fetcher, the server, the git operations — have no
   test coverage. The fixes made for this release *are* covered.

This is a `0.x` version from a small project with no funding behind it. It is built in the open,
issues are answered best-effort, and there is no SLA. If you need one, that is a conversation with a
vendor, not with this repository.

---

<p align="center"><sub>MIT licensed. Built in the open. Screenshots taken from the running application.</sub></p>