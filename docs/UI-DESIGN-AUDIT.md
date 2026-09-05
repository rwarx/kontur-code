# UI Design Audit — Kontur Code

Статус: аудит выполнен до начала имплементации. Документ фиксирует текущее состояние UI,
что переиспользуется, что заменяется, что создаётся с нуля, и миграционный план.

---

## 1. Current UI architecture

Приложение — одно окно `ui:FluentWindow` (WPF-UI 4.3), собираемое из DI:

```
MainWindow (FluentWindow, ExtendsContentIntoTitleBar)
├── TitleBar                      — WPF-UI caption
├── SidebarView                   — New Chat / поиск / список сессий / футер
├── ChatView                      — транскрипт + approval gate + композер
├── SettingsView                  — страница настроек (на весь main area)
├── CommandPaletteView            — overlay-палитра
├── FirstRunView                  — overlay-визард первого запуска
└── ModelPickerView (Popup)       — выбор модели
```

Роутинг — `MainViewModel.ShellPage { Chat, Settings }` через `IsChatVisible`/`IsSettingsVisible`.
Тема — WPF-UI `ApplicationThemeManager` (Light/Dark/System + Mica + акцент).
Стили — `Resources/Styles/Shared.xaml` (метрики, бейджи, diff-щётки, syntax-палитра)
+ `MarkdownTemplates.xaml` + конвертеры в `Converters.xaml`.

MVVM — CommunityToolkit.Mvvm (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`).
Практически вся логика — в ViewModel; code-behind — только фокус/скролл/drag-drop/клавиши.

**Оценка:** каркас здоровый (чистые слои, DI, MVVM, streaming через IAsyncEnumerable),
но presentation layer — это «чат-клиент»: один режим работы, одна поверхность, нет
пространственного слоя, нет представления проекта/файлов, нет inspector-а, палитра команд
бедная (8 команд), визуальный язык целиком делегирован WPF-UI Fluent.

## 2. Current WPF views

| View | Строк | Судьба |
|---|---|---|
| MainWindow.xaml | 229 | **Заменить** новым AppShell (TopBar/Sidebar/Workspace/Context/Status + GridSplitter) |
| ChatView.xaml | 991 | **Пересобрать визуально** на новых токенах; биндинги/VM сохранить 1:1 |
| SidebarView.xaml | 171 | **Заменить** (навигация режимов + workspace + сессии + collapse) |
| CommandPaletteView.xaml | 118 | **Пересобрать** + расширить палитру новыми командами |
| ModelPickerView.xaml | 122 | **Сохранить** (Popup выбора модели), под новые токены |
| SettingsView.xaml | 667 | **Сохранить** как страницу/оверлей внутри нового shell |
| FirstRunView.xaml | 191 | **Сохранить** как overlay |
| Dialogs/RenameDialog.xaml | — | **Сохранить** |

## 3. Existing Canvas

**Canvas в проекте отсутствует.** В README прямо сказано: «editor and repository awareness —
later stages». Поэтому главная часть ТЗ (Graph → Projection → Renderer) не «перенос», а
**создание первого и единственного canvas-стека**. Дуализма не возникает по построению.

Единственный существующий задел, спроектированный автором именно под canvas:
`AgentMode.PlanCanvas` + `IAgentPlanSink`/`AgentPlan`/`AgentPlanPart` (`AgentPlanPartKind`:
folder/file/module/service/interface/data/view/test/external) + `SubmitPlanTool`.
Это готовый контракт «AI → план → поверхность»: sink читается ДО планирования (`CanDraw`
сообщается модели), `AcceptAsync` может блокировать agent-loop и спросить пользователя.
Сегодня sink один — `TranscriptPlanSink` («записано, не нарисовано»).

## 4. Existing ViewModels

| ViewModel | Строк | Судьба |
|---|---|---|
| MainViewModel | 333 | **Расширить**: ShellPage → WorkspaceMode + новые дочерние VM; существующие дочерние не трогать |
| ChatViewModel | 1333 | **Сохранить полностью** (единственный владелец чата/агента) |
| MessageViewModel | 324 | Сохранить |
| AgentToolCallViewModel / AgentApprovalViewModel / DiffLine | 497 | Сохранить |
| SessionListViewModel | 327 | Сохранить (список сессий в новом sidebar) |
| ModelPickerViewModel | 180 | Сохранить (+ использовать в панели Models) |
| CommandPaletteViewModel | 144 | Расширить (PaletteCommand → новые команды; контракт `CommandInvoked` не меняется) |
| SettingsViewModel / ProviderSettingsViewModel / FirstRunViewModel | 1011 | Сохранить |

## 5. Existing styles / 6. Existing resources

См. §1. Переиспользуемое напрямую: конвертеры (`BoolToVisibility`, `Equality`, `RelativeTime`,
`UsageSummary`, `UserAlignment`, …), `BindingProxy`, `MarkdownHost` (Spans/CodeLines/Table
attached properties — это и есть «cached text» механика для блоков кода), `MarkdownTemplates`
(подписать на новые семантические токены), `CodeFont`, diff-щётки, syntax-палитра.
`Shared.xaml` → замещается дизайн-системой `Resources/Design/*` (§11).

## 7. Reusable components

- `DialogService`/`IDialogService` (ContentDialog/файлы/буфер), `AgentApprovalService`
  (UI-гейт поверх `IAgentApproval`, паттерн «ask-the-user из agent-loop» — на нём же строится
  подтверждение плана), `AppThemeService`, `UiThread`, `FileLoggerProvider`.
- `MarkdownParser`/`SyntaxHighlighter`/`LanguageProfiles` — движок подсветки для CodeView.
- `TextDiff`/`DiffLines` — diff-рендер в approval-карточках (используется CodeView preview).
- Инфраструктура DI: `AddInfrastructure` → `AddAppServices` (порядок load-bearing:
  последний `IAgentApproval`/`IAgentPlanSink` выигрывает — место переопределения sink'а).

## 8. Components that should be replaced

1. Shell: `MainWindow` layout (одна колонка «sidebar+chat») → трёхпанельный AppShell с
   GridSplitter и workspace-режимами.
2. SidebarView: «список чатов» → навигация + workspace + sessions.
3. ChatView: визуальный слой (Fluent-контролы → собственные стили), логика биндингов та же.
4. Shared.xaml как «дизайн-система» → токенизированная система ресурсов.
5. Палитра: дефолт-стек WPF-UI остаётся для хрома окна/диалогов (FluentWindow, TitleBar,
   ContentDialog, Snackbar — нативное поведение окна не переписываем), всё содержимое —
   на собственных стилях.

## 9. Architecture risks

- **Порядок DI-регистраций**: `AddAppServices` должен идти после `AddInfrastructure`,
  иначе `CanvasPlanSink` не перехватит `IAgentPlanSink` (risk: регресс PlanCanvas).
- **Потоки**: `IWorkspaceService.RootChanged`, `IProviderRegistry.ModelsChanged`,
  `AgentEvent` — фоновые; все переходы в UI-слой через `UiThread.Post` (паттерн уже есть).
- **Canvas производительность**: запрет на ItemsControl-per-node; только DrawingVisual +
  spatial index + viewport culling + dirty-set инвалидация; FormattedText кэшируется,
  перерисовка текста только при смене zoom-бакета.
- **Чат не должен знать о canvas**: интеграция только через `MainViewModel`-composer
  (события ChatViewModel → TasksPanel; graph context → Draft через GraphContextSource).
- **Ломка существующих тестов**: тесты ссылаются на Domain/Application/Infrastructure —
  новые типы в этих слоях должны быть чистыми C# (без WPF-ссылок); ничего из существующих
  контрактов не менять сигнатурно.
- **Сохранение canvas state**: собственный JSON-store в DataDirectory (без EF-миграций),
  атомарная запись; ключ — workspace root.

## 10. Proposed UI architecture

```
App
├── Design tokens (Resources/Design/*)          — Colors/Typography/Spacing/Radii/Shadows/Icons/Controls/Theme
├── Shell
│   ├── MainWindow (FluentWindow, custom title row)
│   ├── TopBarView          — identity | workspace/model/agent state | search | window controls
│   ├── SidebarView (new)   — nav (Canvas/Graph/Files/Code/Chat/Models/Tasks), workspace, sessions, collapse
│   ├── WorkspaceView       — режимы; состояние режима сохраняется (zoom/selection/scroll/tabs)
│   │   ├── CanvasMode      — GraphCanvas (custom FrameworkElement: renderer + interactions + minimap)
│   │   ├── GraphMode       — outline/структурный вид графа (tree + relations)
│   │   ├── FilesMode       — дерево workspace + детали
│   │   ├── CodeMode        — табы, breadcrumbs, подсветка, diff-preview
│   │   └── ChatMode        — пересобранный ChatView (DataContext = ChatViewModel)
│   ├── ContextPanelView    — Context Surface: workspace/node/edge/selection/AI activity/plan proposal
│   ├── StatusBarView       — workspace root, graph stats, model, agent state, zoom
│   └── CommandPaletteView  — расширенная палитра
├── Canvas subsystem
│   ├── GraphProjection     — snapshot → render nodes/edges (отсоединяется от Domain)
│   ├── SpatialIndex        — uniform grid buckets, world-space запросы, hit-testing
│   ├── CanvasRenderer      — DrawingVisual-стек: background/grid/edges/nodes/overlays/minimap
│   ├── CanvasState/Store   — viewport/selection/hover per mode, инвалидация dirty-set
│   └── CanvasViewModel     — presentation state, команды (zoom/fit/focus/select/undo…)
└── Graph wiring (App → Application/Domain)
    ├── CanvasPlanSink      — IAgentPlanSink: план → GraphChangeSet (proposal) → подтверждение → canvas
    └── GraphContextSource  — selection → текстовый контекст для AI (Ctrl+I)
```

Пайплайн §16 ТЗ реализуется существующими + новыми контрактами:

```
AI (AgentService.PlanCanvas)
 → SubmitPlanTool → AgentPlan
 → CanvasPlanSink (App): GraphChangeSet (proposal)
 → Context Surface: preview (parts/edges) → Accept / Reject
 → GraphService.Apply (GraphMutator) → snapshot + Timeline entry
 → Canvas redraw (dirty) → Undo/Revert через Timeline
```

## 11. Design system

`Resources/Design/`: `Colors.xaml` (семантические Brush.*), `Typography.xaml`
(Title/Section/Body/Caption/Metadata + Code*), `Spacing.xaml` (XS 4 / SM 8 / MD 12 / LG 16 /
XL 24), `Radii.xaml` (SM 3 / MD 5 / LG 8), `Shadows.xaml` (Subtle/Resting/Overlay + Glow),
`Icons.xaml` (Path-геометрии 16px, единая оптическая сетка, `KonturIcon` — control с
`IconKind`), `Controls.xaml` (ImplicitButton/PrimaryButton/GhostButton/ToolButton/NavItem/
Input/ListBox/Tab/Toggle/Splitter…), `Theme.xaml` (агрегатор). Правила: цвета только
через семантические ключи; светлая тема — словарь-переопределение тех же ключей.

Палитра (dark-first, холодный near-black, teal-акцент, экономно):

| Токен | # |
|---|---|
| Surface0 (app bg / canvas) | #0C0D10 |
| Surface1 (панели) | #121418 |
| Surface2 (карточки/toolbar) | #171A1F |
| Surface3 (elevated/popups) | #1D2126 |
| Border / BorderStrong | #262B33 / #333A44 |
| TextPrimary / Secondary / Muted | #E8EAEE / #98A1AD / #66707C |
| Accent (teal) | #38C9A5 (soft #1E3B34, text #7FE6C8) |
| Success / Warning / Error | #43B971 / #D9A03F / #E0564F |

Glow — только: selected node, active connection, AI activity, plan proposal.

## 12. Migration plan

| Фаза | Содержание | Верификация |
|---|---|---|
| 1 | Design System (§11) | build |
| 2 | AppShell + TopBar + StatusBar + splitters | build |
| 3 | Sidebar (nav/workspace/sessions, collapse) | build |
| 4 | Workspace режимы + Files + Graph outline | build |
| 5 | Graph Domain + Application (snapshot/changeSet/mutator/timeline/indexer/store) + DI | build + тесты без регрессий |
| 6 | Canvas (projection/index/culling/renderer/interactions/minimap) + CanvasViewModel | build |
| 7 | Context Surface + CanvasPlanSink (пайплайн §16) | build |
| 8 | Chat re-skin в workspace + AI actions (Ctrl+I) | build |
| 9 | Code (табы/подсветка/diff) + Models + Tasks/Agents + палитра | build |
| 10 | Анимации, polish, перф-ревью renderer'а | build + повторный полный прогон |

После каждой фазы: `dotnet build AIClient.slnx -p:EnableWindowsTargeting=true` и
`dotnet test …` (baseline: 708 passed / 25 pre-existing env-падений на Linux — DPAPI/файлнеймы;
новых падений быть не должно). Запуск UI и интерактивная проверка возможны только на Windows —
в этом окружении верифицируется компиляция и не-регрессия тестов.
