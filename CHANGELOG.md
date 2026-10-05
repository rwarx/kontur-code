# Changelog

All notable changes to Kontur Code are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html) with a
prerelease suffix while the surface is still moving.

Versions are also published on the
[GitHub releases page](https://github.com/rwarx/kontur-code/releases).

---

## [Unreleased]

Nothing yet.

---

## [0.1.1-alpha] — unreleased at the time of writing

The follow-up to `0.1.0-alpha`, closing what that release documented as gaps. Five
of the seven limitations listed below were fixed; the two that remain are recorded
in [SECURITY.md](SECURITY.md#known-gaps) with file references.

### Security

Two holes in the out-of-project file door, both found by re-reading
`AgentService.IsAllowedForRun` against the tool risk levels rather than trusting
the documentation — which had claimed external file access was "per-call approved".
It was not, for reads.

- **One approval used to mean the whole disk.** `IsAllowedForRun` grants a standing
  yes to any tool whose risk is not `Execute`, and the external *reads* were `Write`.
  So a single answer to one `read_external_file` let the agent call it for the rest
  of the run: `list_external_files C:\Users\me`, then a read of anything not named
  in the refusal list. `ReadExternalFileTool` and `ListExternalFilesTool` are now
  `Execute`, matching the writes that were already right.
- **The refusal list could be walked around.** `ExternalFileService` checked the
  *textual* segments of a path, where `WorkspaceService` resolves every reparse
  point. With no root out there to measure containment against, the only thing
  distinguishing a safe path from an unsafe one is the name it lands on — and a
  junction named `pub` makes every requested segment innocent. `.ssh\config` is
  protected by its *parent*, so `pub\config` passed and returned a real SSH config.
  Links are now resolved before any check, and the resolved location is what the
  name list, the data directory and the system folders are applied to.
- **`C:\ProgramData`** was missing from the system-folder list while Program Files
  was in it, so the write guard refused System32 and permitted ProgramData.

21 tests cover the external-file service, including two that build real junctions and
symlinks. Verified in both directions: with link resolution removed, five fail.

### Data loss

- **Every keystroke wrote the whole file.** `setFileContent` POSTed the entire
  document per character — a full-file request per keystroke, and on a poor
  connection a toast per keystroke as they all failed. In-memory state still updates
  immediately; the write is now a trailing debounce per path, with an `unsaved`
  state for the window in between and an explicit flush before a session switch, a
  bundle export and teardown. The bundle export needed it: it packages the
  renderer's in-memory files, so without a flush it could be written while the last
  edit was still queued.
- **`localStorage` would have stopped persisting, silently.** The persisted shape
  held every file's text, every checkpoint's `filesSnapshot` (every file's text
  again) and every message — megabytes against a 5–10 MB cap. The quota exception
  is thrown inside the persist middleware's own subscriber, where nothing catches it,
  so writes stop with no error and a reload loses the session. File bodies are out:
  the sidecar already persists them. Added a `version` + `migrate` and a size
  warning at 2 MB.
- **`events` and messages per session** only ever grew. Both are now a rolling
  window.

### Reliability

- **A silent stream was a failed run.** No frame meant no bytes, and a reasoning
  model thinks for minutes before its first token. Kestrel's `MinResponseDataRate`
  — like any proxy — closes a connection that goes quiet, and the run ended with no
  output and no error. A `ping` frame every five seconds keeps it alive; the
  renderer drops it before the buffer flush, since a keepalive is not a structural
  boundary.
- **Retry on a dropped stream**, three attempts with backoff. Re-POSTing is safe
  because the run id is the caller's, and `RunRegistry.Start` now refuses an id
  already in flight rather than overwriting the live entry — which orphaned that
  run's cancellation token and left its approval waiter waiting forever. Fifty
  concurrent runs maximum.
- **Approval polling could spin forever.** A sidecar that died mid-run left the
  loop at two requests a second indefinitely with nothing on screen. Eight
  consecutive failures now ends it and says so.
- **Approval waiters could hang a run.** `waitForApproval` built a promise that
  could only resolve, and resolved it through a single store slot, so two approvals
  arriving together left the first waiting forever. Waiters now reject, and
  `failPendingApprovals` releases everything still open when a run ends.

### Housekeeping

- **45 dead files and 36 dead dependencies removed.** `components/ui/` was a shadcn
  kit of which three files were reachable; the rest were imported only by each
  other. Dependencies went from 64 to 28. None of them were bundled before this
  either, so it is not a size win — it is 36 fewer packages anyone running
  `npm install` pulls into their tree.
- **`Ctrl+,` did nothing**: the comparison included a trailing space.
- **Dictation was brickable**: `triggerDictation` set a store flag and dispatched an
  event whose listener lives in the chat composer, so pressing Ctrl+M on another
  surface left the flag stuck true for the rest of the session.
- **`StatusBar`** subscribed to the whole canvas object and read four scalars from
  it, re-rendering on every `pointermove` while panning.
- `main.js` and `preload.js` still get no type checking. The smallest honest fix
  would be a second tsconfig over the shell; it is recorded as a gap rather than
  half-done.

917 tests pass, 8 skip by design. `tsc --noEmit` and `vite build` clean against a
fresh `npm ci`.

---

## [0.1.0-alpha] — 2026-10-03

The first public build. It is an **alpha**: the shape is settled enough to build
against, and three areas are known to be weak. They are listed under
**Known limitations** below rather than left for someone to discover.

This release also **replaces the project's licensing**, which was previously
unspecified. As of this commit the repository is MIT.

### Added

**Spatial workspace**
- Infinite canvas rendering the workspace as a graph — files, folders, modules,
  services, interfaces, data, tests, plans and tasks as nodes; containment,
  dependency and plan edges between them.
- `GraphCanvas` built on `DrawingVisual` retained visuals rather than an
  `ItemsControl`, with a uniform-grid spatial index for O(1) hit-testing and
  viewport culling so only on-screen nodes are attached.
- Snapshot diffing (`GraphProjection`) so a scene applies only what changed.
- Pan, zoom, marquee select, minimap, auto-layout, and a filterable outline tree.
- Diff-based `WorkspaceGraphIndexer`: new files appear, deleted files leave, node
  positions are preserved across refreshes.
- Undo/redo over a 100-entry history; every graph edit is persisted and
  timeline-counted.

**Agent**
- Agent runs with a step loop, a tool registry, and an approval gate that defaults
  to refusing.
- 17 tools: read, write and edit inside the workspace; directory listing and
  search; `submit_plan`; `run_command` behind an off-by-default allowlist with
  per-call approval and no shell.
- Out-of-project file tools (`list`, `read`, `write`, `edit` external file) —
  off by default, per-call approved.
- `fetch`, with an SSRF guard: redirects disabled and re-checked per hop, the
  address check performed at resolution time so DNS rebinding does not slip past
  it, private and link-local ranges refused, responses capped.
- Planning modes (`Plan`, `PlanCanvas`) that offer read-only tools and change the
  system prompt rather than adding a caveat to it.
- Plans enter the spatial graph through `IAgentPlanSink`, so a plan is an
  undoable, persisted graph change like any other edit.

**Electron shell**
- Electron shell over a React 19 renderer and the .NET sidecar, with a frameless
  custom title bar.
- CodeMirror 6 editor with ten language grammars, inline AI edits, and
  ghost-text completion.
- Git panel: status, diff, per-file diff, stage, commit, branch, push, pull, fetch.
- Session export/import as a `.zip` bundle.
- Keyboard-driven command palette, plus an onboarding flow and a project gate.

**Localisation**
- English, Russian and German, applied across the shell, chat, workspace and
  settings.

**Project governance** (this release)
- `LICENSE` — MIT. The repository previously had no licence, which meant nobody
  could legally use it.
- `PRIVACY.md`, `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
  `SUPPORT.md`, `THIRD-PARTY-NOTICES.md`, `CHANGELOG.md`.
- CI covering build-with-warnings-as-errors, tests, and the layer-boundary check.
- Release workflow producing a tagged GitHub prerelease.

### Security

Fixed before publishing, all of it in the local sidecar's HTTP surface:

- **The sidecar now requires a bearer token** on every route except `/api/health`.
  The token is generated per launch in the Electron main process, passed by
  environment variable, and never bundled or persisted. Previously the API was
  unauthenticated with `AllowAnyOrigin`, which meant any web page the user had
  open could drive every endpoint and read the responses.
- **CORS restricted** to loopback origins, the `null` origin a `file://` renderer
  reports, and requests with no `Origin`.
- **A non-loopback bind is refused at startup** rather than honoured from the
  launch arguments.
- **Git remote and branch names are validated.** `git fetch "ext::calc.exe"` is a
  documented git transport that runs a local program, and the remote reached the
  argument list unchecked — remote code execution from a JSON body.
- **`--` added before staged file paths**, so a file named `--all` is no longer
  staged as that option.
- **Settings that reduce containment cannot be written over HTTP**: the command
  allowlist, the command / network / external-file switches and the workspace
  root are refused by name.
- **`/api/ai/complete` is clamped** — prompt length, system length, temperature and
  output tokens — so it cannot be used as an open tab on the user's provider
  credit.
- **A workspace root that contains the user profile is refused**, not only a whole
  drive.
- **Electron hardening**: `window.open` and in-page navigation are blocked, all
  permission requests are refused, CSP no longer ships `unsafe-inline` in
  `script-src`, and CSP is injected at runtime from the address actually in use.
- **`kontur:readFileLocal` confined** to folders the user picked through the OS
  dialog, refusing symlinks and capping at 8 MB. It was an arbitrary-path read.
- **The sidecar process is killed properly on quit** (`taskkill /T /F` on
  Windows), had an `error` listener attached so a failed spawn no longer takes the
  process down, and had a single-instance lock added.

### Fixed

- The three agent-tool tests failed on any tool with a dependency the reflection
  harness could not supply; two refusing test doubles were added and the harness
  now covers the external-file and network tools.
- `Ctrl+,` did nothing: the key comparison included a trailing space.
- `streamNdjson` called the frame handler without awaiting it, so a delta could be
  applied after a run had already reported itself complete, and a rejection in the
  handler escaped as an unhandled promise rejection.
- Every request now carries a timeout; previously only two of roughly fifty did,
  and the rest could hang indefinitely with no way to cancel.

### Known limitations

Carried into this alpha deliberately, and detailed with file references in
[SECURITY.md](SECURITY.md#known-gaps):

1. `ExternalFileService` does not resolve reparse points, so a pre-existing
   junction inside an allowed path can defeat the credential-name denial list.
   External access is off by default and per-call approved.
2. An agent run can hang rather than fail when two approvals arrive together.
3. The NDJSON stream has no heartbeat and no reconnect, so a dropped connection
   loses the run.
4. `RunRegistry` accepts a caller-supplied run id and can orphan an in-flight run.
5. The renderer writes a file to the sidecar on every keystroke with no debounce,
   and persists workspace file contents into `localStorage`; large working sets can
   exceed the browser storage quota.
6. `ExternalFileService`, `FetchTool`, `RunRegistry`, the new git operations and
   the server itself have no test coverage. The paths that do have tests —
   `GitArguments`, the sidecar token and CORS policy, the workspace path guards —
   cover the fixes above.
7. Roughly forty unused npm dependencies remain in `electron/package.json` from a
   UI kit that was replaced. They are not bundled, but they are a supply-chain
   surface for anyone running `npm install`.

[Unreleased]: https://github.com/rwarx/kontur-code/compare/v0.1.1-alpha...HEAD
[0.1.1-alpha]: https://github.com/rwarx/kontur-code/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/rwarx/kontur-code/releases/tag/v0.1.0-alpha