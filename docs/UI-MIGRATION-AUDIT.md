# UI Migration Audit — Kontur Code

Status: Phase 0 of the desktop UI migration. This document records what actually exists in the
repository as of this audit, what is worth keeping, what is broken or stale, and the architecture
and stage plan for moving the interface from WPF to Avalonia with a custom-rendered canvas.

The headline finding: **the hard part is already done, in the right place.** The graph stack —
domain model, mutation boundary, persistence, layout, AI context — is fully implemented in
`Domain` / `Application` / `Infrastructure` and is UI-agnostic by construction. The migration is a
*view* migration, not a system rewrite. Everything below is written to keep it that way.

---

## 1. Current architecture

Four-project onion, dependency rule enforced by target framework rather than convention:

```text
AIClient.Domain          net10.0            entities, graph model, provider contracts. Zero dependencies.
AIClient.Application     net10.0            services + contracts the UI binds to. No HTTP, no SQL, no WPF.
AIClient.Infrastructure  net10.0-windows    EF Core/SQLite, providers, DPAPI, workspace sandbox, graph persistence.
AIClient.App             net10.0-windows    WPF + WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.0. Composition root.
tests/AIClient.Tests     xunit.v3           885 passing, 8 self-skipping live tests.
```

`rg "Dispatcher|System.Windows|Application.Current"` over `Domain` and `Application` returns
nothing. UI coupling lives exactly where it should: `App/Services/UiThread.cs`. `CanvasViewport`,
`CanvasBounds`, `CanvasPlacement`, `CanvasViewState`, `CanvasLayout` use plain `double`s and their
own geometry types — the code comments in `CanvasDtos.cs` show this was done deliberately so no WPF
type could drift into the model. This is the single most important fact for the migration.

## 2. Graph = source of truth (verified, tested)

- **Model** (`Domain/Graph/`): `GraphNode` / `GraphEdge` are immutable `sealed record`s; edges carry
  no geometry. Kinds are open text wrappers (`GraphNodeKind`, `GraphEdgeKind`) — a new kind is
  data, not a migration. `GraphSnapshot` is built once per change, never mutated, and holds
  pre-materialised indexes (by id, by `(Kind, Key)`, adjacency both directions) plus a monotonic
  `Version`. Queries (`Neighbourhood`, `Subgraph`, `Children`, `Roots`, `Parent`) live on the
  snapshot itself.
- **Mutation boundary** (`GraphMutator`, pure static): `Apply(snapshot, changeSet)` is the only way
  the graph changes. Ownership invariant — an `Indexer`-origin change may only touch
  indexer-owned things; refusals are strings, the batch continues; removal cascades edges;
  result carries the computed `Inverse`.
- **Change sets**: `GraphChangeSet` (`Proposed → Applied → Reverted/Discarded`) unifies proposals,
  undo and the timeline. `GraphService` (Infrastructure) persists every change set as a journal row
  (`GraphChangeRow` with `MutationsJson` / `InverseJson`), exposes `ProposeAsync` / `ApplyAsync` /
  `AcceptAsync` / `DiscardAsync` / `RevertAsync` / `HistoryAsync`, publishes immutable snapshots
  after durable writes, raises `Changed` off the UI thread.
- **Canvas is a projection** — enforced by tests (`CanvasIsAProjectionTests`): the spatial tables
  (`CanvasViews`, `CanvasPlacements`, `CanvasAreas`) hold only geometry, camera, collapsed, accent,
  pinned. No semantic fact anywhere in canvas storage. `CanvasLayout` is a deterministic tidy tree
  over `Contains`/`Groups` edges with a `LayoutRevision` catch-up mechanism.
- **Indexer** (`WorkspaceGraphIndexer`): walks the workspace through the sandbox, produces
  `Project`/`Folder`/`File` nodes keyed by workspace-relative path (so re-index reuses ids and
  keeps placements and hand-drawn edges), `Contains` edges, marks indexer-owned nodes `Missing`
  rather than deleting, applies one change set per pass, honours `CanvasSettings.MaxIndexedNodes`.

## 3. Current UI (WPF)

- **Shell**: `FluentWindow` with custom title bar; `MainViewModel` holds a `ShellPage` enum
  (`Chat`, `Settings`, `Canvas`) and all three panes live permanently in the visual tree, toggled
  by visibility. Sidebar 280px with virtualizing session list; window-level `InputBindings`
  (`Ctrl+N/K/G/,/B`, `Ctrl+Shift+P`); command palette with 9 commands dispatching through one
  `switch` in `MainViewModel` — correct but the switch is the extensibility bottleneck.
- **Canvas** (`Views/Canvas/CanvasView.xaml` + `ViewModels/Canvas/CanvasViewModel.cs`, ~1,424
  lines): the view model contains **zero WPF types** — camera (`CanvasViewport`,
  `screen = world*Zoom + Pan`), viewport culling with incremental `Reconcile` (never `Clear()`),
  hit testing in world space, marquee ("touched, not enclosed"), drag with 3px threshold and
  pinned-on-placement, debounced viewport persistence, breadcrumb, AI surface popup anchored to
  the selection. Rendering, however, is an `ItemsControl` with a `DataTemplate` per node
  (~9 WPF visuals per node, ~5 per edge) inside a transformed `Canvas`. Culling exists in the VM
  (threshold 400, cap `MaxVisibleNodes` = 1500) but:
  - `RebuildVisible` is an O(n) scan with fresh allocations per camera change — fires per mouse
    move during pan;
  - the `.Take(cap)` keeps *dictionary order*, not nearest — arbitrary and flickering above cap;
  - no spatial index for hit testing / marquee (linear scans);
  - 1,500 realized containers on a transformed WPF `Canvas` will not hold frame rate; edge fan-out
    can push realized edges past the node cap;
  - `CanvasArea` (frames), collapse, `RootNodeId`/`Depth` views exist in the model but are unused.
- **Code-behind discipline is unusually strict** — `CanvasView.xaml.cs` is a pure gesture state
  machine that converts screen points and calls VM methods; zero decisions in views.
- **Inspector**: `InspectorViewModel` shows node detail (facts, relations, per-node history from
  `GraphService.HistoryAsync`), group summary for multi-select, and hides when nothing is
  selected. `Canvas.SelectionChanged → Inspector.Show` wired in `MainViewModel`.
- **Theme**: WPF-UI dictionaries + dynamic resource brushes; `AppThemeService` is the only seam
  behind the control library (the migration seam already exists in principle). `CanvasKindVisuals`
  colors are deliberately hardcoded muted hexes, theme-independent.

## 4. AI / agent layer

- **Pipeline** (`AgentService`, ~1,041 lines): per-message mode (`Build`/`Plan`/`PlanCanvas`),
  tools withheld on the last step, three budgets (steps 25, wall-clock 600s with approval-time
  suspension, identical-call 3), mode policy enforced twice (offers and per-call refusal), every
  tool call gets a persisted transcript row, errors mapped to `AIErrorKind`.
- **Approval gate**: `IAgentApproval` with refusing default in Infrastructure; the WPF host
  overrides with an inline card (`AgentApprovalService`). One approval per call, never remembered
  for `Execute`. `run_command` fenced by four independent gates (switch off by default, program
  allowlist, per-call approval, no shell).
- **Graph context is wired for chat only**: `ContextBuilder.WithGraphAsync` appends
  `IGraphContextSource.BuildAsync(selection, budget)` (detail ladder: excerpts → reference →
  described → outline) to the system prompt when a selection exists. **Agent runs are graph-blind**
  — `AgentRunRequest` has no `Selection` field, so planning runs that would most benefit read the
  raw filesystem.
- **Plan → canvas is unwired**: `SubmitPlanTool` produces a structured `AgentPlan` and hands it to
  `IAgentPlanSink`; the registered sink is `TranscriptPlanSink` with `CanDraw == false`. The seam
  is real and the comment in `DependencyInjection.cs` ("the WPF app registers a sink that also
  draws") is aspirational — no drawing sink exists anywhere. `PlanCanvas` currently degrades to
  `Plan` plus an extra prompt paragraph.
- **`GraphOrigin.Agent` / `GraphOrigin.Chat` are dead ends**: the only change-set producers are the
  indexer and user edits. `AgentService` knows nothing about `GraphChangeSet`; no tool proposes
  graph mutations. The scaffolding (`Origin`, `SourceExecutionId`, proposal state machine,
  ghost-rendering of proposals) is ready and unused.

## 5. Repository hygiene findings

1. `src/AIClient.App/Web/ui/node_modules/` — a dead artifact (a single rollup native binary; no
   source, no package manifest, no WebView2 reference anywhere in the solution). Delete.
2. `ARCHITECTURE.md` claims "this build has no canvas" — stale; a full graph canvas exists. The
   `DependencyInjection.cs` plan-sink comment overstates reality. Both need reconciling as part of
   Phase 4 rather than left to contradict the code.
3. Uncommitted working tree contains the entire graph/canvas/inspector implementation — it should
   be committed and protected before the migration starts.
4. Known scaling debts in the graph layer itself (acceptable now, on record for Phase 6):
   one giant transaction + multi-MB journal row per index pass; full snapshot rebuild per change;
   no FTS on `GraphNodeRow`; strictly sequential indexer walk.

## 6. What the migration must not break

Existing providers (OpenRouter/NVIDIA, streaming, catalogue cache), DPAPI key storage, sessions,
agent modes and approval gates, workspace sandbox, SQLite + EF migrations, graph persistence,
timeline/undo via inverse change sets, canvas placement/viewport persistence, and the 885-test
suite. The WPF app stays in the tree, untouched, until the Avalonia app reaches feature parity —
it is the fallback and the reference.

---

## 7. Target architecture

```text
AIClient.Avalonia (new, net10.0-windows while DPAPI binds us to Windows; no WPF anywhere)
  AppShell            custom-chrome window, TopBar, Sidebar, StatusBar, dialogs, notifications
  Canvas viewport     custom-drawn control, no per-node controls
  Inspector / AI surface / Command palette   Avalonia-native views over existing VMs
src/AIClient.App      WPF — untouched, fallback until parity (then retired, not deleted)
```

Layering rule unchanged: Avalonia views bind to `Application` interfaces; the renderer sees only
render models.

### Canvas rendering pipeline (the core of the migration)

```text
GraphSnapshot ──▶ CanvasViewModel (unchanged core: camera, selection, drag, persistence)
                        │
                        ▼
                CanvasSceneBuilder  ──▶ CanvasRenderModel (immutable per graph version)
                        │                  nodes: rect, kind color, cached text runs
                        │                  edges: polyline/curve points, arrow heads
                        ▼
                SpatialIndex (uniform grid) ──▶ viewport culling + hit testing + marquee
                        │
                        ▼
                CanvasRenderSurface (Avalonia custom Control)
                        Render(DrawingContext) over Avalonia/Skia backend
```

Design decisions:

1. **One control, zero node visuals.** `CanvasRenderSurface` derives from Avalonia `Control` and
   draws everything in `Render(DrawingContext)`. No `ItemsControl`, no per-node
   `ContentPresenter`. The dot grid, cards, edges, marquee and hover states are render commands.
2. **Render model, not view models, at draw time.** The scene builder converts the culled node/edge
   set into flat render primitives (positions, sizes, kind colors, prepared text) whenever the
   graph version or cull set changes — not per frame. Panning/zooming re-renders from the cached
   primitives under one transform; it allocates nothing.
3. **Spatial index** (uniform grid rebuilt on graph/cull change) replaces linear scans for hit
   testing, marquee, and capping (nearest-first instead of dictionary order).
4. **Renderer-agnostic seam.** The render model and spatial index are plain types in the App layer;
   `Render(DrawingContext)` is the only Avalonia-flavoured step. Swapping in another backend later
   means reimplementing one class.
5. **Text** is laid out once per (title, kind, width) and cached; hover/selection/hover states are
   brush swaps, not re-layout.
6. **Invalidation discipline**: camera change → re-render only; graph change → rebuild scene +
   spatial index; selection/hover change → re-render only. Never rebuild the scene per frame.
7. **Performance metrics** land with the renderer (frame time, p95, long frames, cull counts),
   surfaced in the status bar debug section, benchmarked at 216 / 1k / 5k / 10k synthetic nodes.

### Shell

Custom chrome via `ExtendClientAreaToDecorationsHint`; sidebar with collapse (`Ctrl+B`); top bar
minimal (project, context, agent state, model); status bar (graph status, selection, zoom,
metrics); command palette migrated to a handler-registry design (`Dictionary<string, Command>`
registered by features) replacing the enum+switch; theme system as Avalonia FluentTheme with light
/dark/system and an accent color, expressed as app-level resource tokens mirroring the current
brush vocabulary.

### AI wiring (Phase 4 — filling existing seams, not new ones)

1. `AgentRunRequest` gains `GraphSelection? Selection`; `AgentService.PrepareAsync` passes it to
   `ContextBuildRequest` — agent runs become graph-aware through the existing `GraphContextSource`.
2. A `CanvasPlanSink : IAgentPlanSink` (App/Infrastructure) draws `AgentPlan` parts as a
   `GraphChangeSet` with `Origin = GraphOrigin.Agent` via `IGraphService.ProposeAsync` — the
   "one registration line" the code already promises. Parts become proposed (ghost) nodes with
   `depends_on` edges; accepting applies them through `GraphMutator` like any other change.
3. A new `IAgentTool` (`propose_graph_changes`) lets a **Build** run propose graph mutations as a
   `GraphChangeSet`; it returns the proposal id and never mutates directly; acceptance stays human
   (approval gate + proposal review), and `RevertAsync` remains the undo path.
4. The AI surface keeps its current behavior (selection → context → ask → propose → review →
   timeline) and moves to the Avalonia shell unchanged.

---

## 8. Migration stages

| Stage | Content | Exit criterion |
| --- | --- | --- |
| 0 (this doc) | Audit, plan, tree hygiene | Audit committed; `Web/ui` artifact removed; graph work committed |
| 1 | `AIClient.Avalonia` project: window, custom chrome, sidebar, theme tokens, `UiThread` equivalent, DI composition reusing every Application service; command palette (handler registry) | App launches; chat + sessions + settings work on Avalonia; build + 885 tests green |
| 2 | Canvas port: `CanvasRenderSurface`, scene builder, spatial index; port of the gesture state machine (pan/zoom/select/marquee/drag/hover/keyboard); placement + viewport persistence over the same `ICanvasViewStore` | Canvas is the primary workspace at 60fps-feel on 10k synthetic nodes; nothing lost vs WPF canvas |
| 3 | Inspector + context surface on Avalonia; selection-driven AI surface | Parity with WPF inspector; context-aware asks |
| 4 | AI wiring: selection for agent runs, `CanvasPlanSink`, `propose_graph_changes` tool, proposal review UI, timeline/revert in inspector | `PlanCanvas` draws; agent proposals flow `ProposeAsync → review → ApplyAsync → undo` |
| 5 | Desktop polish: full shortcut map, notifications, empty/error/loading states, dialogs, drag & drop, settings screens, accessibility pass | Feature parity with WPF shell |
| 6 | Performance: metrics instrumentation, benchmarks at 216/1k/5k/10k, fix measured bottlenecks (indexer batching, journal size, snapshot churn) | p95 frame budget met at 10k nodes; index of 10k files bounded |

After each stage: `dotnet build` + `dotnet test` green, and the WPF app still builds and runs.

## 9. Explicit non-goals (carried from the product brief)

No WebView2/Electron/web UI for any first-class surface; no second graph store; no rewrite of
Domain/Application/Infrastructure; no new AI backend; no per-node controls; no permanent 35%-width
chat panel. The editor/terminal/git/MCP slots in the workspace are reserved by the architecture
(they consume `Application` interfaces next to the canvas) but are not built in this migration.
