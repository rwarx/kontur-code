# CLAUDE.md

Guide for AI assistants working on this codebase. Read ARCHITECTURE.md for the full rationale;
this file is the quick-reference.

## What this is

A native Windows desktop LLM client evolving into a spatial AI development environment. .NET 10,
WPF, Clean Architecture, SQLite, streaming via `IAsyncEnumerable<T>`.

## Architecture rules (enforced at compile time)

```text
App → Application → Domain ← Infrastructure
 \--------> Infrastructure (only for DI composition root)
```

- **Domain** (`net10.0`): entities, enums, interfaces. Zero dependencies. No WPF, no HTTP, no SQL.
- **Application** (`net10.0`): use cases, service contracts. Knows about Domain only.
- **Infrastructure** (`net10.0-windows`): EF Core, providers, DPAPI, workspace, git. Implements
  Application/Domain interfaces.
- **App** (`net10.0-windows`): WPF views, view models, converters. References all three inner
  projects, but ViewModels never name a provider, HttpClient, or DbContext.

Violation = compile error (`net10.0` makes `System.Windows` unreferenceable from Domain/Application).

## Key patterns

| Pattern | Where | How |
|---|---|---|
| MVVM | App | CommunityToolkit.Mvvm source generators |
| Streaming | Domain → App | `AIStreamEvent` → `ChatTurnEvent`/`AgentEvent` → ViewModel collections |
| DbContext | Infrastructure | Factory-based (`IDbContextFactory<T>`), no shared context |
| Closed event hierarchies | Domain | `AIStreamEvent`, `AgentEvent`, `GraphChange`, `AgentRunTransition` — new kinds = additive |
| Risk-based approval | Application | Tools declare `AgentToolRisk`; gate runs before execution |
| Workspace sandbox | Infrastructure | `WorkspaceService` — one root, resolve+refuse every path |
| Canvas rendering | App | `DrawingVisual` retained visuals, uniform-grid spatial index |
| Git module | Domain/Infra | `IGitService` → `GitService` via ProcessRunner, 5 agent tools |
| Agent checkpoints | Domain/Infra | `AgentRunCheckpoint` → `IAgentRunStore` for crash recovery |

## Agent system

Three modes: `Build`, `Plan`, `PlanCanvas`. Mode is per-message, enforced at tool-request and
call-execution levels. See `AgentModePolicy`.

Tools: `list_files`, `read_file`, `search_files`, `submit_plan`, `write_file`, `edit_file`,
`create_directory`, `move_file`, `delete_file`, `run_command`,
`git_status`, `git_diff`, `git_commit`, `git_checkout`, `git_revert`.

Adding a tool:
1. Create class implementing `IAgentTool` in `src/AIClient.Application/Services/Tools/`
2. Declare risk level (`AgentToolRisk.Read`/`.Write`/`.Execute`)
3. Register in `AgentToolRegistry` (already there, auto-discovered via DI)
4. If it implements `IAgentPlanningTool`, it is withheld from Build mode

## Graph/Canvas system

Domain: `GraphNode` (14 kinds), `GraphEdge` (6 kinds), `GraphChangeSet`, `GraphSnapshot`.
Application: `GraphService` (undo/redo/timeline, 100-entry history).
Infrastructure: `JsonGraphStore` (atomic file persistence).
App: `GraphCanvas` (DrawingVisual), `CanvasController`, `SpatialIndex`, `GraphProjection`.

Plan pipeline: `SubmitPlanTool` → `AgentPlan` → `CanvasPlanSink` → `GraphChangeSet` → user confirms → `GraphService.Apply`.

## Adding a provider

1. Subclass `OpenAiCompatibleProvider` in `src/AIClient.Infrastructure/Providers/OpenAiCompatible/`
2. Override `Id`, `DisplayName`, `BaseUrl`, `HttpClientName`, `ParseModels`
3. Register in `DependencyInjection.cs`: `AddHttpClient` + `AddSingleton<IAIProvider, YourProvider>`

## Build & test

```bash
dotnet build AIClient.slnx -warnaserror
dotnet test
```

745+ tests. Real SQLite, real DPAPI, fake HTTP handlers. Live tests skip without keys.

## CI

GitHub Actions at `.github/workflows/ci.yml`: build+test, architecture tests (Domain/Application
no WPF refs), package audit. Runs on push/PR to main.

## Code style

- `.editorconfig` enforced at build time (`EnforceCodeStyleInBuild=true`)
- `TreatWarningsAsErrors=true` — fix warnings, don't suppress
- File-scoped namespaces, 4-space indent, `_camelCase` private fields
- XML doc comments explain *why*, not *what*
- No comments unless asked

## Conventional commits

```
feat(scope): description
fix(scope): description
test: description
docs: description
refactor(scope): description
```

Scopes: `chat`, `providers`, `db`, `settings`, `app`, `agent`, `graph`, `canvas`.

## Common tasks

**Add a setting:** Add property to the relevant section class in
`src/AIClient.Application/Configuration/`. No migration needed.

**Add a migration:**
```bash
dotnet ef migrations add Name --project src/AIClient.Infrastructure --output-dir Database/Migrations
```
All three files (migration, designer, snapshot) must be in the same commit.

**Add an agent tool:** Create `YourTool.cs` in `Services/Tools/`, implement `IAgentTool`, register.

**Add a graph node kind:** Add to `GraphNodeKind` enum, update `WorkspaceGraphIndexer` kind mapping,
update `CanvasPalette` brush/glyph.
