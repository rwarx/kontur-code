"use client";

import type {
  AgentMode,
  ApprovalDecision,
  CanvasEdge,
  CanvasNode,
  DiffLine,
  Goal,
  Message,
} from "./types";
import { useKontur, waitForApproval, newId } from "./store";
import { abortLiveChat, runLiveChat, runLiveSummary } from "./live";
import { isAgentRunActive, runAgentConversation, stopAgentRun } from "./agent-live";
import { isServerId, isServerMode, newLocalSession } from "./sync";
import { conversations } from "./backend";
import { translate } from "./i18n";
import { computeUnifiedDiff, diffStats } from "./diff";
import {
  DEMO_FILES,
  FIX_AUTH_FIND,
  FIX_AUTH_REPLACE,
  FIX_TOKENSERVICE_FIND,
  FIX_TOKENSERVICE_FIND2,
  FIX_TOKENSERVICE_LIFETIME_FIND,
  FIX_TOKENSERVICE_LIFETIME_REPLACE,
  FIX_TOKENSERVICE_REPLACE,
  FIX_TOKENSERVICE_REPLACE2,
  SKILLS,
} from "./data";

/* ============================================================
   Scenario engine — scripted agent playback over the real
   feature set (tools, approvals, plan, subagents, verification)
   ============================================================ */

class ScenarioAbort extends Error {}

const S = () => useKontur.getState();

async function sleep(ms: number) {
  const slices = Math.max(1, Math.round(ms / 80));
  for (let i = 0; i < slices; i++) {
    if (S().runner.status === "stopped") throw new ScenarioAbort();
    await new Promise((r) => setTimeout(r, ms / slices));
  }
}

async function delay(ms: number) {
  await sleep(ms);
}

function setLabel(label: string) {
  S().setRunner({ label });
}

async function streamText(msgId: string, text: string, cps = 340) {
  const chunks = text.match(/\S+\s*|\n/g) ?? [text];
  let buffer = "";
  let lastFlush = performance.now();
  for (const chunk of chunks) {
    if (S().runner.status === "stopped") throw new ScenarioAbort();
    buffer += chunk;
    const now = performance.now();
    if (now - lastFlush > 40 || chunk.includes("\n")) {
      S().appendToMessage(msgId, buffer);
      buffer = "";
      lastFlush = now;
      await sleep(Math.max(8, (1000 / cps) * 2));
    }
  }
  if (buffer) S().appendToMessage(msgId, buffer);
}

/* ---------- builders ---------- */

/*
 * Edit diffs are COMPUTED from the actual find/replace scripts and the
 * DEMO_FILES baseline — the approval gates, tool headlines, event titles
 * and the checkpoint viewer in the Code surface all read from these, so
 * the numbers can never drift from what the edits really do.
 */
const baseline = (path: string): string =>
  DEMO_FILES.find((f) => f.path === path)?.content ?? "";

const AUTH_PATH = "src/AuthFlow.Application/Services/AuthService.cs";
const TOKEN_PATH = "src/AuthFlow.Application/Services/TokenService.cs";

const withFileHeader = (path: string, lines: DiffLine[]): DiffLine[] => [
  { kind: "context", text: path },
  ...lines,
];

const AUTH_DIFF: DiffLine[] = withFileHeader(
  AUTH_PATH,
  computeUnifiedDiff(baseline(AUTH_PATH), baseline(AUTH_PATH).replace(FIX_AUTH_FIND, FIX_AUTH_REPLACE)),
);
const AUTH_STATS = diffStats(AUTH_DIFF);

const TOKEN_DIFF: DiffLine[] = withFileHeader(
  TOKEN_PATH,
  computeUnifiedDiff(
    baseline(TOKEN_PATH),
    baseline(TOKEN_PATH)
      .replace(FIX_TOKENSERVICE_LIFETIME_FIND, FIX_TOKENSERVICE_LIFETIME_REPLACE)
      .replace(FIX_TOKENSERVICE_FIND, FIX_TOKENSERVICE_REPLACE)
      .replace(FIX_TOKENSERVICE_FIND2, FIX_TOKENSERVICE_REPLACE2),
  ),
);
const TOKEN_STATS = diffStats(TOKEN_DIFF);

const authHeadline = `AuthService.cs · +${AUTH_STATS.add} −${AUTH_STATS.del}`;
const tokenHeadline = `TokenService.cs · +${TOKEN_STATS.add} −${TOKEN_STATS.del}`;
const totalHeadline = `+${AUTH_STATS.add + TOKEN_STATS.add} −${AUTH_STATS.del + TOKEN_STATS.del}`;

/** hand-built diff for the generic README append (no find/replace backing data) */
const handDiff = (lines: [DiffLine["kind"], string][]): DiffLine[] =>
  lines.map(([kind, text]) => ({ kind, text }));

const PLAN_STEPS = [
  "Analyze the auth flow (TokenService · AuthService)",
  "Add silent renewal to AuthService",
  "Fix refresh lifetime + rotation in TokenService",
  "Build the solution",
  "Run the token tests",
  "Review the diff",
];

const CONTRACT = [
  "Build succeeds",
  "Existing tests pass (3/3)",
  "Renew preserves the afi claim",
  "No compiler warnings",
  "Requested behavior implemented",
  "Changes reviewed",
];

function planNodes(): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
  const nodes: CanvasNode[] = PLAN_STEPS.map((title, i) => ({
    id: `plan-${i}`,
    kind: "plan" as const,
    title: `${i + 1} · ${title}`,
    x: 880,
    y: -60 + i * 150,
    w: 250,
    h: 58,
    meta: "plan",
  }));
  const edges: CanvasEdge[] = nodes.slice(0, -1).map((n, i) => ({
    id: `pe-${i}`,
    from: n.id,
    to: nodes[i + 1].id,
    kind: "plans" as const,
  }));
  return { nodes, edges };
}

function agentNodes(): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
  const defs: [string, string, string][] = [
    ["agent-architect", "Architect", "authsvc"],
    ["agent-researcher", "Researcher", "tests"],
    ["agent-coder", "Coder", "tokensvc"],
    ["agent-reviewer", "Reviewer", "authsvc"],
  ];
  const nodes: CanvasNode[] = defs.map(([id, title], i) => ({
    id,
    kind: "agent" as const,
    title,
    x: -880 + i * 240,
    y: 720,
    w: 170,
    h: 54,
    meta: "subagent",
  }));
  const edges: CanvasEdge[] = defs.map(([id, , target]) => ({
    id: `ae-${id}`,
    from: id,
    to: target,
    kind: "relates" as const,
  }));
  return { nodes, edges };
}

/** strip same-id nodes/edges left on the canvas by earlier runs
 *  (persisted canvas state) so re-runs never duplicate */
function resetCanvasGroup(nodes: CanvasNode[], edges: CanvasEdge[]) {
  useKontur.setState((prev) => ({
    canvas: {
      ...prev.canvas,
      nodes: prev.canvas.nodes.filter((n) => !nodes.some((x) => x.id === n.id)),
      edges: prev.canvas.edges.filter((e) => !edges.some((x) => x.id === e.id)),
    },
  }));
}

/**
 * Demo idempotency: previous runs (or a restored checkpoint) may have left the
 * workspace in the FIXED state — a re-run would then apply no-op edits while
 * the transcript still claims +9 −2. Reset every demo file to its buggy
 * baseline so each run starts from the same defect and the checkpoint
 * snapshot → current diff in the Code surface stays meaningful.
 */
function resetDemoWorkspace() {
  useKontur.setState((prev) => ({
    files: prev.files.map((f) => {
      const base = DEMO_FILES.find((d) => d.path === f.path);
      return base ? { ...f, content: base.content, modified: false } : f;
    }),
  }));
}

/* ---------- the demo scenario ---------- */

/* ============================================================
   Skills auto-selection — the runtime picks the skills relevant
   to a request before planning, mirroring how an agent loads
   capability packs for the task at hand.
   ============================================================ */

const SKILL_KEYWORDS: Record<string, string[]> = {
  dotnet: ["dotnet", "build", "solution", "csproj", "xunit", "c#", ".net", "compile", "project"],
  testing: ["test", "verify", "verification", "spec", "regression", "green", "reproduce"],
  "code-review": ["review", "diff", "refactor", "naming", "clean", "readme", "documentation"],
  architecture: ["architecture", "structure", "boundary", "layer", "diagram", "layout", "migration plan"],
  security: ["token", "auth", "secret", "claim", "jwt", "owasp", "security", "refresh", "session"],
  database: ["database", "ef core", "sqlite", "sql", "migration", "store"],
};

/** a skill is available for auto-selection when its store toggle is on
 *  (falls back to the SKILLS default for prefs that predate the toggle). */
function skillEnabled(id: string): boolean {
  const map = S().ui.skillsEnabled;
  const def = SKILLS.find((s) => s.id === id)?.enabled ?? false;
  return map?.[id] ?? def;
}

/** keyword-scored selection over the ENABLED skills, top 3, demo set for the scripted run */
function selectSkillsForRun(text: string, demo: boolean): string[] {
  if (demo) {
    return SKILLS.filter((s) => skillEnabled(s.id) && ["dotnet", "testing", "security"].includes(s.id)).map(
      (s) => s.id,
    );
  }
  const lower = text.toLowerCase();
  const scored = SKILLS.filter((s) => skillEnabled(s.id))
    .map((s) => ({ id: s.id, hits: (SKILL_KEYWORDS[s.id] ?? []).filter((k) => lower.includes(k)).length }))
    .filter((s) => s.hits > 0)
    .sort((a, b) => b.hits - a.hits || a.id.localeCompare(b.id));
  return scored.slice(0, 3).map((s) => s.id);
}

/** attach the activated skills to the run message + record a trajectory event */
function activateSkills(msgId: string, skills: string[]) {
  if (!skills.length) return;
  S().updateMessage(msgId, { skills });
  S().addEvent({
    id: newId("ev"),
    ts: Date.now(),
    kind: "skills",
    title: `Skills activated · ${skills.join(", ")}`,
    status: "done",
  });
}

export async function runDemoScenario(userText: string) {
  const store = S();
  store.setRunner({ status: "running", label: "Planning" });
  store.setContextTokens(6200);

  /* every demo run starts from the buggy baseline (idempotent re-runs) */
  resetDemoWorkspace();

  /* assistant message shell */
  const msgId = newId("m");
  const message: Message = {
    id: msgId,
    role: "assistant",
    content: "",
    createdAt: Date.now(),
    modelId: store.ui.selectedModelId,
    providerId: "openrouter",
    streaming: true,
    toolCalls: [],
  };
  store.addMessage(message);

  /* skills auto-selection for this run */
  activateSkills(msgId, selectSkillsForRun(userText, true));

  /* goal */
  const goalId = newId("g");
  const goal: Goal = {
    id: goalId,
    title: "Fix refresh token renewal",
    createdAt: Date.now(),
    status: "planning",
    criteria: CONTRACT.map((text) => ({ id: newId("c"), text, done: false })),
    sessionId: store.currentSessionId,
  };
  store.addGoal(goal);
  store.addEvent({
    id: newId("ev"),
    ts: Date.now(),
    kind: "request",
    title: userText.slice(0, 80),
    status: "done",
  });

  try {
    /* --- plan --- */
    setLabel("Planning");
    await delay(700);
    await streamText(msgId, "I'll trace the refresh flow first, then fix it and prove it with tests.\n\n");
    store.addBlock(msgId, {
      type: "plan",
      goal: "Fix refresh token renewal",
      steps: PLAN_STEPS.map((title) => ({ id: newId("p"), title, status: "pending" as const })),
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "plan", title: "Plan created · 6 steps", status: "done" });
    store.updateGoal(goalId, { status: "running" });
    const pn = planNodes();
    resetCanvasGroup(pn.nodes, pn.edges);
    store.addNodes(pn.nodes);
    store.addEdges(pn.edges);
    store.setContextFiles([
      { path: "src/AuthFlow.Application/Services/TokenService.cs", tokens: 1840, reason: "pinned" },
      { path: "src/AuthFlow.Application/Services/AuthService.cs", tokens: 1420, reason: "pinned" },
      { path: "src/AuthFlow.Domain/Interfaces/ITokenService.cs", tokens: 380, reason: "relevant" },
      { path: "src/AuthFlow.Domain/Entities/RefreshToken.cs", tokens: 260, reason: "relevant" },
      { path: "tests/AuthFlow.Tests/TokenServiceTests.cs", tokens: 1980, reason: "relevant" },
    ]);
    store.setContextTokens(41800);

    /* --- investigation --- */
    setLabel("Reading the workspace");
    const tools: [string, string, string, string][] = [
      ["search_files", "“refresh” · 5 matches in 4 files", "src/…/TokenService.cs:24, 31\nsrc/…/AuthService.cs:38\ntests/…/TokenServiceTests.cs:19\nsrc/…/ITokenService.cs:12", "940ms"],
      ["read_file", "TokenService.cs · 48 lines", "IssueTokens grants refresh for 1 hour (line 30).\nRenew never invalidates the old value and drops claims (line 40).", "412ms"],
      ["read_file", "AuthService.cs · 39 lines", "EnsureSession returns SessionExpired without ever calling Renew (line 37).", "388ms"],
      ["git_status", "branch main · clean", "On branch main, nothing to commit. 0 changed files.", "210ms"],
    ];
    for (let i = 0; i < tools.length; i++) {
      const [tool, headline, body, dur] = tools[i];
      const callId = newId("t");
      store.addToolCall(msgId, {
        id: callId,
        tool,
        risk: "read",
        state: "running",
        headline,
        body,
      });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `${tool} · started`, status: "running" });
      await delay(650 + i * 90);
      store.updateToolCall(msgId, callId, { state: "succeeded", durationMs: parseInt(dur) });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `${tool} · ${headline}`, status: "done", durationMs: parseInt(dur) });
      store.updateBlock(msgId, 0, {
        steps: PLAN_STEPS.map((title, idx) => ({
          id: `step-${idx}`,
          title,
          status: idx === 0 ? "done" as const : "pending" as const,
        })),
      });
    }

    /* --- subagents --- */
    setLabel("Running subagents");
    const an = agentNodes();
    resetCanvasGroup(an.nodes, an.edges);
    store.addNodes(an.nodes);
    store.addEdges(an.edges);
    store.addBlock(msgId, {
      type: "agenttree",
      agents: [
        { id: "sa-1", name: "Architect", role: "maps the auth flow", status: "pending" },
        { id: "sa-2", name: "Researcher", role: "checks the tests", status: "pending" },
      ],
    });
    const treeIndex = () => {
      const m = S().currentSession()?.messages.find((x) => x.id === msgId);
      return (m?.blocks ?? []).findIndex((b) => b.type === "agenttree");
    };
    await delay(500);
    store.updateBlock(msgId, treeIndex(), {
      agents: [
        { id: "sa-1", name: "Architect", role: "maps the auth flow", status: "running" },
        { id: "sa-2", name: "Researcher", role: "checks the tests", status: "running" },
      ],
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "subagent", title: "Architect · started", agent: "Architect", status: "running" });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "subagent", title: "Researcher · started", agent: "Researcher", status: "running" });
    await delay(1100);
    store.updateBlock(msgId, treeIndex(), {
      agents: [
        {
          id: "sa-1",
          name: "Architect",
          role: "maps the auth flow",
          status: "done",
          summary: "Refresh lives in TokenService; renewal is never called from AuthService.",
          tokens: 3100,
        },
        {
          id: "sa-2",
          name: "Researcher",
          role: "checks the tests",
          status: "done",
          summary: "Tests expect a 30-day refresh with rotation and a preserved afi claim.",
          tokens: 2400,
        },
      ],
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "subagent", title: "Architect · completed", agent: "Architect", status: "done", detail: "Refresh lives in TokenService; renewal is never called.", durationMs: 4100 });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "subagent", title: "Researcher · completed", agent: "Researcher", status: "done", detail: "Tests expect 30-day refresh + rotation + afi claim.", durationMs: 3900 });
    store.setContextTokens(53400);

    /* --- fix #1: AuthService --- */
    setLabel("Proposing a change");
    await streamText(
      msgId,
      "Two defects: refresh tokens live **1 hour**, and `EnsureSession` never renews. I'll fix both — starting with the silent renewal.\n\n",
    );
    store.addBlock(msgId, {
      type: "checkpoint",
      label: "before auth changes",
      file: "src/AuthFlow.Application/Services/AuthService.cs",
      restorable: true,
    });
    S().addCheckpoint({
      id: newId("cp"),
      label: "before auth changes",
      createdAt: Date.now(),
      sessionId: S().currentSessionId,
      message: msgId,
      /* both files this run will edit — restorable + diffable in the Code surface */
      filesSnapshot: [AUTH_PATH, TOKEN_PATH].map((path) => ({
        path,
        content: S().files.find((f) => f.path === path)?.content ?? "",
      })),
      /* the plan nodes just drawn — restoring also rolls the graph back */
      canvasSnapshot: {
        nodes: S().canvas.nodes.map((n) => ({ ...n })),
        edges: S().canvas.edges.map((e) => ({ ...e })),
      },
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "checkpoint", title: "Checkpoint · before auth changes", status: "done" });

    store.setApproval({
      id: newId("ap"),
      tool: "edit_file",
      risk: "write",
      headline: authHeadline,
      consequence: "Changes files in the folder you opened.",
      diff: AUTH_DIFF,
      argsNote: "find: EnsureSession(TokenPair pair) … · replace: renewal attempt",
      allowForRunAvailable: true,
    });
    store.setRunner({ status: "awaiting-approval", label: "Waiting for approval" });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: "edit_file · AuthService.cs · approval required", status: "warning" });
    const decision1 = await waitForApproval(S().approval!.id);
    store.setRunner({ status: "running", label: "Applying the change" });
    store.addEvent({
      id: newId("ev"),
      ts: Date.now(),
      kind: "approval",
      title: `edit_file · AuthService.cs · ${decision1.decision === "denied" ? "denied" : "allowed"}`,
      status: decision1.decision === "denied" ? "failed" : "done",
    });

    if (decision1.decision === "denied") {
      await streamText(
        msgId,
        `\n\nUnderstood — I won't touch the files.${decision1.reason ? ` (${decision1.reason})` : ""} The proposed change stays above for review. Switch to **Build** when you want it applied.\n\n`,
      );
      store.updateGoal(goalId, { status: "failed" });
      finish(msgId, { inputTokens: 53400, outputTokens: 2100, ms: 96000 });
      return;
    }

    const editCallId = newId("t");
    store.addToolCall(msgId, {
      id: editCallId,
      tool: "edit_file",
      risk: "write",
      state: "running",
      headline: authHeadline,
      diff: AUTH_DIFF,
    });
    await delay(900);
    const authFile = S().files.find((f) => f.path === "src/AuthFlow.Application/Services/AuthService.cs");
    if (authFile) {
      S().setFileContent(authFile.path, authFile.content.replace(FIX_AUTH_FIND, FIX_AUTH_REPLACE));
      S().setFileModified(authFile.path, true);
    }
    store.updateToolCall(msgId, editCallId, { state: "succeeded", durationMs: 640 });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `edit_file · ${authHeadline}`, status: "done", durationMs: 640 });
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS.map((title, idx) => ({
        id: `step-${idx}`,
        title,
        status: idx <= 1 ? ("done" as const) : ("pending" as const),
      })),
    });
    store.setContextTokens(57100);

    /* --- build + tests (first pass) --- */
    const buildApproved = S().allowForRunTools.includes("run_command");
    const runCommandCall = (headline: string, body: string, ok: boolean, dur: number) => {
      const callId = newId("t");
      store.addToolCall(msgId, {
        id: callId,
        tool: "run_command",
        risk: "execute",
        state: "running",
        headline,
        body,
      });
      return { callId, headline, body, ok, dur };
    };

    if (!buildApproved) {
      store.setApproval({
        id: newId("ap"),
        tool: "run_command",
        risk: "execute",
        headline: "dotnet build",
        consequence: "Runs a program on this computer.",
        argsNote: "dotnet build AuthFlow.sln -v q",
        allowForRunAvailable: true,
      });
      store.setRunner({ status: "awaiting-approval", label: "Waiting for approval" });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: "run_command · dotnet build · approval required", status: "warning" });
      const d = await waitForApproval(S().approval!.id);
      store.setRunner({ status: "running", label: "Building" });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: `run_command · dotnet build · ${d.decision === "denied" ? "denied" : "allowed"}`, status: d.decision === "denied" ? "failed" : "done" });
      if (d.decision === "denied") {
        await streamText(msgId, "\n\nBuild permission denied — stopping here. The edit is already applied; run the build yourself when ready.\n\n");
        store.updateGoal(goalId, { status: "failed" });
        finish(msgId, { inputTokens: 57100, outputTokens: 2600, ms: 132000 });
        return;
      }
    }

    setLabel("Building");
    const build1 = runCommandCall("dotnet build", "", true, 3200);
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "build", title: "dotnet build · started", status: "running" });
    await delay(1900);
    store.updateToolCall(msgId, build1.callId, {
      state: "succeeded",
      durationMs: 3200,
      body: "Build succeeded in 3.2s\n  0 warnings · 0 errors",
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "build", title: "dotnet build · succeeded (0 errors)", status: "done", durationMs: 3200 });
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS.map((title, idx) => ({
        id: `step-${idx}`,
        title,
        status: idx <= 3 ? ("done" as const) : ("pending" as const),
      })),
    });

    setLabel("Running tests");
    const test1 = runCommandCall("dotnet test", "", false, 5100);
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "test", title: "dotnet test · started", status: "running" });
    await delay(2400);
    const testFailBody = `Failed AuthFlow.Tests.TokenServiceTests.Renew_ShouldRotateAndPreserveClaims [12ms]
  Assert.Null() Failure
  Expected: (null)
  Actual:   RefreshToken { Value = "9f2c…", … }
  The old refresh token is still valid after renewal.

Failed AuthFlow.Tests.TokenServiceTests.IssueTokens_ShouldGrantRefreshForThirtyDays [8ms]
  Assert.True() Failure
  Expected: True
  Actual:   False
  The refresh expiry equals the access expiry (1h).

Passed: 1 · Failed: 2 · Total: 3`;
    store.updateToolCall(msgId, test1.callId, { state: "failed", durationMs: 5100, body: testFailBody });
    store.addEvent({
      id: newId("ev"),
      ts: Date.now(),
      kind: "test",
      title: "dotnet test · 2/3 failed",
      detail: "Renew_ShouldRotateAndPreserveClaims, IssueTokens_ShouldGrantRefreshForThirtyDays",
      status: "failed",
      durationMs: 5100,
    });
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS.map((title, idx) => ({
        id: `step-${idx}`,
        title,
        status: idx <= 3 ? ("done" as const) : idx === 4 ? ("failed" as const) : ("pending" as const),
      })),
    });

    /* --- self-healing: fix TokenService --- */
    setLabel("Fixing the rotation");
    await streamText(
      msgId,
      "The tests caught it: `IssueTokens` grants a 1-hour refresh and `Renew` neither rotates nor preserves claims. Fixing `TokenService` now.\n\n",
    );
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "error", title: "2 test failures analyzed", detail: "refresh lifetime + rotation + afi claim", status: "warning" });

    const editAllowedForRun = S().allowForRunTools.includes("edit_file");
    if (!editAllowedForRun) {
      store.setApproval({
        id: newId("ap"),
        tool: "edit_file",
        risk: "write",
        headline: tokenHeadline,
        consequence: "Changes files in the folder you opened.",
        diff: TOKEN_DIFF,
        argsNote: "find: IssueTokens / Renew · replace: 30-day lifetime + rotation",
        allowForRunAvailable: true,
      });
      store.setRunner({ status: "awaiting-approval", label: "Waiting for approval" });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: "edit_file · TokenService.cs · approval required", status: "warning" });
      const d2 = await waitForApproval(S().approval!.id);
      store.setRunner({ status: "running", label: "Applying the change" });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: `edit_file · TokenService.cs · ${d2.decision === "denied" ? "denied" : "allowed"}`, status: d2.decision === "denied" ? "failed" : "done" });
      if (d2.decision === "denied") {
        await streamText(msgId, "\n\nDenied — leaving TokenService as is. The AuthService change is applied, but the rotation tests stay red.\n\n");
        store.updateGoal(goalId, { status: "failed" });
        finish(msgId, { inputTokens: 62400, outputTokens: 3300, ms: 168000 });
        return;
      }
    }

    const edit2Id = newId("t");
    store.addToolCall(msgId, {
      id: edit2Id,
      tool: "edit_file",
      risk: "write",
      state: "running",
      headline: tokenHeadline,
      diff: TOKEN_DIFF,
    });
    await delay(950);
    const tsFile = S().files.find((f) => f.path === "src/AuthFlow.Application/Services/TokenService.cs");
    if (tsFile) {
      const updated = tsFile.content
        .replace(FIX_TOKENSERVICE_LIFETIME_FIND, FIX_TOKENSERVICE_LIFETIME_REPLACE)
        .replace(FIX_TOKENSERVICE_FIND, FIX_TOKENSERVICE_REPLACE)
        .replace(FIX_TOKENSERVICE_FIND2, FIX_TOKENSERVICE_REPLACE2);
      S().setFileContent(tsFile.path, updated);
      S().setFileModified(tsFile.path, true);
    }
    store.updateToolCall(msgId, edit2Id, { state: "succeeded", durationMs: 720 });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "fix", title: "TokenService.cs · 30-day refresh + rotation", status: "done", durationMs: 720 });
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS.map((title, idx) => ({
        id: `step-${idx}`,
        title,
        status: idx <= 2 ? ("done" as const) : ("pending" as const),
      })),
    });
    store.setContextTokens(66800);

    /* --- verify again --- */
    setLabel("Verifying");
    const build2 = runCommandCall("dotnet build", "", true, 2800);
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "build", title: "dotnet build · started", status: "running" });
    await delay(1600);
    store.updateToolCall(msgId, build2.callId, {
      state: "succeeded",
      durationMs: 2800,
      body: "Build succeeded in 2.8s\n  0 warnings · 0 errors",
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "build", title: "dotnet build · succeeded (0 errors)", status: "done", durationMs: 2800 });

    const test2 = runCommandCall("dotnet test", "", true, 4300);
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "test", title: "dotnet test · started", status: "running" });
    await delay(2200);
    store.updateToolCall(msgId, test2.callId, {
      state: "succeeded",
      durationMs: 4300,
      body: "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n  ✓ IssueTokens_ShouldGrantRefreshForThirtyDays\n  ✓ Renew_ShouldRotateAndPreserveClaims\n  ✓ Renew_Expired_ShouldReturnNull",
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "test", title: "dotnet test · 3/3 passed", status: "done", durationMs: 4300 });
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS.map((title) => ({ id: `step-${PLAN_STEPS.indexOf(title)}`, title, status: "done" as const })),
    });
    store.updateGoal(goalId, { status: "verifying" });

    /* --- review + contract --- */
    setLabel("Reviewing");
    store.updateBlock(msgId, treeIndex(), {
      agents: [
        {
          id: "sa-1",
          name: "Architect",
          role: "maps the auth flow",
          status: "done",
          summary: "Refresh lives in TokenService; renewal is never called from AuthService.",
          tokens: 3100,
        },
        {
          id: "sa-2",
          name: "Researcher",
          role: "checks the tests",
          status: "done",
          summary: "Tests expect a 30-day refresh with rotation and a preserved afi claim.",
          tokens: 2400,
        },
        {
          id: "sa-3",
          name: "Reviewer",
          role: "audits the diff",
          status: "running",
        },
      ],
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "subagent", title: "Reviewer · started", agent: "Reviewer", status: "running" });
    await delay(1300);
    store.updateBlock(msgId, treeIndex(), {
      agents: [
        {
          id: "sa-1",
          name: "Architect",
          role: "maps the auth flow",
          status: "done",
          summary: "Refresh lives in TokenService; renewal is never called from AuthService.",
          tokens: 3100,
        },
        {
          id: "sa-2",
          name: "Researcher",
          role: "checks the tests",
          status: "done",
          summary: "Tests expect a 30-day refresh with rotation and a preserved afi claim.",
          tokens: 2400,
        },
        {
          id: "sa-3",
          name: "Reviewer",
          role: "audits the diff",
          status: "done",
          summary: `Two files, ${totalHeadline}. Boundaries respected, no security regressions.`,
          tokens: 1900,
        },
      ],
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "review", title: "Reviewer · completed", agent: "Reviewer", status: "done", detail: `Two files, ${totalHeadline}. No security regressions.`, durationMs: 4600 });

    store.addBlock(msgId, { type: "contract", title: "Done when", items: CONTRACT.map((text) => ({ id: newId("c"), text, done: false })) });
    const contractIndex = () => {
      const m = S().currentSession()?.messages.find((x) => x.id === msgId);
      return (m?.blocks ?? []).findIndex((b) => b.type === "contract");
    };
    for (let i = 0; i < CONTRACT.length; i++) {
      await delay(260);
      const m = S().currentSession()?.messages.find((x) => x.id === msgId);
      const block = m?.blocks?.[contractIndex()];
      if (block && block.type === "contract") {
        store.updateBlock(msgId, contractIndex(), {
          items: block.items.map((item, idx) => (idx <= i ? { ...item, done: true } : item)),
        });
        const goalCriteria = S().goals.find((g) => g.id === goalId)?.criteria ?? [];
        goalCriteria.forEach((c, idx) => {
          if (idx <= i) store.updateGoalCriteria(goalId, c.id, true);
        });
      }
    }

    /* --- summary (live model prose when the backend is up, scripted otherwise) --- */
    const summary = await liveSummaryOrScripted(
      msgId,
      [
        `The user asked: “${userText}”.`,
        "I ran the full agent loop in build mode:",
        "1. Planned 6 steps (analyze auth flow → fix AuthService → fix TokenService → build → test → review).",
        "2. Read tools: search_files (\"refresh\" · 5 matches in 4 files), read_file (TokenService.cs, AuthService.cs), git_status (branch main, clean).",
        "3. Launched two subagents: Architect (mapped the auth flow: refresh lives in TokenService, renewal is never called from AuthService) and Researcher (tests expect a 30-day refresh with rotation and a preserved afi claim).",
        `4. Checkpoint created before edits, then edit_file with diff approvals: ${authHeadline} (silent renewal in EnsureSession) and ${tokenHeadline} (30-day refresh lifetime + rotation, afi claim preserved).`,
        "5. Verification: dotnet build failed once (tests caught the missing rotation), self-healed with a second edit, then build succeeded (0 errors 0 warnings) and dotnet test passed 3/3 including Renew_ShouldRotateAndPreserveClaims.",
        `6. Reviewer subagent audited the diff: two files, ${totalHeadline}, boundaries respected, no security regressions.`,
        "7. Completion contract: 6/6 criteria met.",
        "No further actions remain. The goal is complete.",
      ].join("\n"),
      `Done — sessions now survive the hour mark.\n\n**What changed**\n\n- \`AuthService.EnsureSession\` silently renews an expired access token\n- \`TokenService\` issues 30-day refresh tokens with rotation; the \`afi\` claim survives\n- Old refresh values are invalidated on renewal\n\n**Verification**\n\n- Build: 0 errors, 0 warnings\n- Tests: 3/3 passed — \`Renew_ShouldRotateAndPreserveClaims\` is green\n\nTwo files changed, ${totalHeadline}. The canvas has the plan; checkpoints are restorable from the timeline.\n\n`,
    );
    if (summary.mode === "stopped") return;
    store.updateGoal(goalId, { status: "completed" });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "complete", title: "Goal completed · Fix refresh token renewal", status: "done" });
    store.setContextTokens(71200);
    finish(
      msgId,
      summary.mode === "live"
        ? { inputTokens: 71200, outputTokens: 4260 + summary.outputTokens, ms: 252000 }
        : { inputTokens: 71200, outputTokens: 4260, ms: 252000 },
    );
  } catch (err) {
    if (err instanceof ScenarioAbort) {
      const m = S().currentSession()?.messages.find((x) => x.id === msgId);
      const steps = m?.toolCalls.length ?? 0;
      store.updateMessage(msgId, { streaming: false });
      store.addEvent({
        id: newId("ev"),
        ts: Date.now(),
        kind: "complete",
        title: `Stopped after ${steps} tool step(s). Partial answer preserved.`,
        status: "warning",
      });
      store.setRunner({ status: "stopped", label: "Stopped" });
      store.updateGoal(goalId, { status: "failed" });
      return;
    }
    console.error("scenario error", err);
    store.updateMessage(msgId, { streaming: false });
    store.setRunner({ status: "idle", label: undefined });
  }
}

function finish(msgId: string, usage: { inputTokens: number; outputTokens: number; ms: number }) {
  S().updateMessage(msgId, { streaming: false, usage });
  S().setRunner({ status: "done", label: "Completed" });
  S().pushToast("Run completed", "Build green · tests 3/3 · goal completed.");
}

/* ============================================================
   Live closing summary — real model prose after scripted runs.
   Falls back to the scripted text when the backend is down.
   Does NOT finish the message: each caller wraps up its own run.
   ============================================================ */

export type SummaryOutcome =
  | { mode: "live"; outputTokens: number }
  | { mode: "scripted" }
  | { mode: "stopped" };

async function liveSummaryOrScripted(
  msgId: string,
  runReport: string,
  scriptedText: string,
): Promise<SummaryOutcome> {
  setLabel("Summarizing");
  const live = await runLiveSummary(msgId, runReport);
  if (live.result === "completed") return { mode: "live", outputTokens: live.outputTokens };
  if (live.result === "stopped") return { mode: "stopped" };
  await streamText(msgId, scriptedText);
  return { mode: "scripted" };
}

/* ============================================================
   Generic replies for free-typed messages
   ============================================================ */

const OFF_REPLIES = [
  "Here's the short version: the demo workspace is a four-project .NET solution. **Domain** holds the entities and contracts, **Application** the services, **Infrastructure** the clock/store/JWT pieces, and **Tests** the xUnit facts.\n\nThe interesting seam is `ITokenService` — `TokenService` implements it and `AuthService` consumes it, which is exactly where the demo bug lives.\n\nAsk in **Build** mode if you want me to actually change something.",
  "Good question. In this prototype the agent has 15 tools (read/search/edit files, run allowed programs, git status/diff/commit/checkout/revert) gated by three risk levels: *read* runs freely, *write* needs a diff approval, *execute* asks every time.\n\nPlans land on the canvas in **Plan + canvas** mode, and every step is recorded in the **Trajectory** surface.",
];

const PLAN_STEPS_GENERIC = ["Read the relevant sources", "Map the affected calls", "Propose the change"];

export async function runGenericScenario(userText: string, mode: AgentMode) {
  const store = S();
  store.setRunner({ status: "running", label: mode === "off" ? "Answering" : "Working" });
  const msgId = newId("m");
  store.addMessage({
    id: msgId,
    role: "assistant",
    content: "",
    createdAt: Date.now(),
    modelId: store.ui.selectedModelId,
    providerId: "openrouter",
    streaming: true,
    live: mode === "off",
    toolCalls: [],
  });
  store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "request", title: userText.slice(0, 80), status: "done" });

  /* skills auto-selection for this run */
  activateSkills(msgId, selectSkillsForRun(userText, false));

  try {
    if (mode === "off") {
      setLabel("Answering");
      /* live model first — real streaming from the backend */
      const result = await runLiveChat(msgId);
      if (result === "completed" || result === "stopped") return;
      /* backend unavailable → canned fallback so the demo still works */
      const locale = store.ui.locale;
      store.updateMessage(msgId, { live: false });
      store.pushToast(translate(locale, "chat.live.errorToast"), translate(locale, "chat.live.fallbackToast"));
      await delay(600);
      const reply = OFF_REPLIES[userText.length % OFF_REPLIES.length];
      await streamText(msgId, `*${translate(locale, "chat.live.offline")}*\n\n${reply}`);
      finish(msgId, { inputTokens: 3200, outputTokens: 480, ms: 21000 });
      return;
    }

    /* plan-ish flow */
    setLabel("Planning");
    await delay(500);
    await streamText(msgId, "Let me look at that against the workspace.\n\n");
    store.addBlock(msgId, {
      type: "plan",
      goal: userText.slice(0, 60),
      steps: PLAN_STEPS_GENERIC.map((title) => ({ id: newId("p"), title, status: "pending" as const })),
    });
    store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "plan", title: "Plan created · 3 steps", status: "done" });

    const genericTools: [string, string, string][] = [
      ["search_files", `“${userText.split(/\s+/).slice(0, 2).join(" ")}” · 3 matches`, "src/…/TokenService.cs:24\ntests/…/TokenServiceTests.cs:19\nREADME.md:8"],
      ["read_file", "TokenService.cs · 48 lines", "IssueTokens / Renew are the two entry points worth checking."],
    ];
    for (let i = 0; i < genericTools.length; i++) {
      const [tool, headline, body] = genericTools[i];
      const callId = newId("t");
      store.addToolCall(msgId, { id: callId, tool, risk: "read", state: "running", headline, body });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `${tool} · started`, status: "running" });
      await delay(700);
      store.updateToolCall(msgId, callId, { state: "succeeded", durationMs: 420 + i * 130 });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `${tool} · ${headline}`, status: "done", durationMs: 420 + i * 130 });
    }
    store.updateBlock(msgId, 0, {
      steps: PLAN_STEPS_GENERIC.map((title, idx) => ({ id: `gs-${idx}`, title, status: "done" as const })),
    });
    store.setContextFiles([
      { path: "src/AuthFlow.Application/Services/TokenService.cs", tokens: 1840, reason: "pinned" },
      { path: "tests/AuthFlow.Tests/TokenServiceTests.cs", tokens: 1980, reason: "relevant" },
    ]);
    store.setContextTokens(12400);

    if (mode === "build") {
      await streamText(
        msgId,
        "This one is a documentation gap rather than a code defect, so I'll patch the README.\n\n",
      );
      store.setApproval({
        id: newId("ap"),
        tool: "edit_file",
        risk: "write",
        headline: "README.md · +6 −0",
        consequence: "Changes files in the folder you opened.",
        diff: handDiff([
          ["context", "README.md"],
          ["context", ""],
          ["add", "## Troubleshooting"],
          ["add", ""],
          ["add", "- Sessions drop after an hour → check the refresh lifetime in `TokenService`."],
          ["add", "- Tests fail locally → `dotnet build` first; the tests compile the solution."],
          ["add", "- Rotated tokens rejected → the `afi` claim must survive `Renew`."],
        ]),
        argsNote: "find: # AuthFlow … · append: Troubleshooting section",
        allowForRunAvailable: true,
      });
      store.setRunner({ status: "awaiting-approval", label: "Waiting for approval" });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "approval", title: "edit_file · README.md · approval required", status: "warning" });
      const decision: ApprovalDecision = (await waitForApproval(S().approval!.id)).decision;
      store.setRunner({ status: "running", label: "Applying the change" });
      if (decision === "denied") {
        await streamText(msgId, "\n\nDenied — nothing was changed.\n\n");
        finish(msgId, { inputTokens: 12400, outputTokens: 820, ms: 42000 });
        return;
      }
      const callId = newId("t");
      store.addToolCall(msgId, {
        id: callId,
        tool: "edit_file",
        risk: "write",
        state: "running",
        headline: "README.md · +6 −0",
        body: "Appended the Troubleshooting section.",
      });
      await delay(800);
      store.updateToolCall(msgId, callId, { state: "succeeded", durationMs: 380 });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: "edit_file · README.md · +6 −0", status: "done", durationMs: 380 });
      const summary = await liveSummaryOrScripted(
        msgId,
        [
          `The user asked: "${userText}".`,
          "I ran the agent loop in build mode: planned 3 steps (read sources → map calls → propose change), ran read tools (search_files, read_file TokenService.cs), decided the request is a documentation gap rather than a code defect, and applied one approved edit: README.md +6 −0 adding a Troubleshooting section (three bullets covering the refresh lifetime, local test builds, and the afi claim on rotation).",
          "Verification: dotnet build succeeded (0 errors, 0 warnings); no tests are affected by a README change. The goal is complete.",
        ].join("\n"),
        "README updated with a Troubleshooting section. Build stays green and no tests are affected.\n\n",
      );
      if (summary.mode === "stopped") return;
      finish(
        msgId,
        summary.mode === "live"
          ? { inputTokens: 13800, outputTokens: 1120 + summary.outputTokens, ms: 61000 }
          : { inputTokens: 13800, outputTokens: 1120, ms: 61000 },
      );
      return;
    }

    const summary = await liveSummaryOrScripted(
      msgId,
      [
        `The user asked: "${userText}".`,
        "I ran the agent loop in plan mode (read-only): planned 3 steps (read sources → map calls → propose change) and completed them, running search_files and read_file (TokenService.cs).",
        "Findings: the two hot spots are TokenService.IssueTokens and AuthService.EnsureSession; the existing tests already describe the expected behaviour. No files were changed (plan mode).",
        "Tell the user what you would change and suggest switching to build mode to execute the plan. This is a proposal, not an execution report.",
      ].join("\n"),
      "Here's the plan — nothing was changed. The two hot spots are `TokenService.IssueTokens` and `AuthService.EnsureSession`; the tests already describe the expected behaviour. Switch to **Build** to execute it.\n\n",
    );
    if (summary.mode === "stopped") return;
    finish(
      msgId,
      summary.mode === "live"
        ? { inputTokens: 12400, outputTokens: 960 + summary.outputTokens, ms: 38000 }
        : { inputTokens: 12400, outputTokens: 960, ms: 38000 },
    );
  } catch (err) {
    if (err instanceof ScenarioAbort) {
      store.updateMessage(msgId, { streaming: false });
      store.addEvent({ id: newId("ev"), ts: Date.now(), kind: "complete", title: "Stopped. Partial answer preserved.", status: "warning" });
      store.setRunner({ status: "stopped", label: "Stopped" });
      return;
    }
    console.error(err);
    store.updateMessage(msgId, { streaming: false });
    store.setRunner({ status: "idle", label: undefined });
  }
}

/* ============================================================
   Entry points
   ============================================================ */

/*
 * Demo detection — must stay PRECISE. A message that merely mentions
 * the word "demo" ("what does the demo workspace do?") must NOT be
 * hijacked into the scripted run. We match either:
 *  - short imperative commands: "demo", "run demo", "run the demo scenario"
 *  - the actual task statements the demo button / suggestions send
 */
const DEMO_PHRASES = [
  "fix the refresh token",
  "users get logged out",
  "run the demo scenario",
  "run demo scenario",
];
const DEMO_EXACT = /^\s*(please\s+)?(run\s+)?(the\s+)?demo(\s+scenario)?\s*[.!]?\s*$/i;

export function isDemoRequest(text: string): boolean {
  if (DEMO_EXACT.test(text)) return true;
  const lower = text.toLowerCase();
  return DEMO_PHRASES.some((p) => lower.includes(p));
}

export function sendMessage(text: string) {
  const store = S();
  const trimmed = text.trim();
  if (!trimmed) return;
  // Composer attachments shown on the user bubble (server mode threads the
  // full text through runAgentConversation, which clears the tray itself).
  const attachments = store.draftAttachments.length
    ? store.draftAttachments.map((a) => ({ id: a.id, name: a.name, sizeKb: a.sizeKb }))
    : undefined;
  if (isDemoRequest(trimmed)) {
    // Scripted showcase in a local session — demo theater never touches server data.
    if (isServerMode() && isServerId(store.currentSessionId)) {
      newLocalSession("Demo scenario");
    }
    S().addMessage({
      id: newId("m"),
      role: "user",
      content: trimmed,
      createdAt: Date.now(),
      toolCalls: [],
      attachments,
    });
    S().setDraft("");
    S().clearDraftAttachments();
    void runDemoScenario(trimmed);
    return;
  }
  if (isServerMode()) {
    // Real backend: chat + agent modes all stream from the sidecar.
    void runAgentConversation(trimmed, store.ui.agentMode);
    return;
  }
  store.addMessage({
    id: newId("m"),
    role: "user",
    content: trimmed,
    createdAt: Date.now(),
    toolCalls: [],
    attachments,
  });
  store.setDraft("");
  store.clearDraftAttachments();
  void runGenericScenario(trimmed, store.ui.agentMode);
}

export function stopGeneration() {
  stopAgentRun();
  const store = S();
  abortLiveChat(); /* no-op unless a live reply is in flight */
  const approval = store.approval;
  if (approval) {
    approvalWaitersResolve(approval.id, { decision: "denied", reason: "stopped by user" });
  }
  store.setRunner({ status: "stopped", label: "Stopping…" });
}

function approvalWaitersResolve(id: string, r: { decision: ApprovalDecision; reason?: string }) {
  /* resolve through the store so waiters map is cleaned up */
  const pending = S().approval;
  if (pending && pending.id === id) {
    S().resolveApproval(r.decision, r.reason);
  }
}

export function regenerate() {
  const store = S();
  const session = store.currentSession();
  if (!session || isAgentRunActive()) return;
  if (isServerMode() && isServerId(session.id)) {
    const kept = [...session.messages];
    // Drop the failed assistant reply(ies); keep everything up to and including the user turn.
    const popped: Message[] = [];
    while (kept.length && kept[kept.length - 1].role === "assistant") popped.unshift(kept.pop() as Message);
    const lastUser = kept[kept.length - 1];
    if (!lastUser || lastUser.role !== "user") return;
    const sessionId = session.id;
    const text = lastUser.content;
    // The message right before the retried user turn (undefined ⇒ it is the first turn).
    const anchor = kept[kept.length - 2];
    // Keep the user bubble locally so the transcript never flashes empty and the text is never
    // lost if the re-send can't start; runAgentConversation rebinds it (no duplicate bubble).
    useKontur.setState((prev) => ({
      sessions: prev.sessions.map((s) => (s.id === sessionId ? { ...s, messages: kept.map((m) => ({ ...m })) } : s)),
    }));
    void (async () => {
      // Clean the SERVER back to *before* the retried user turn. The re-send always persists a
      // fresh user message, so leaving the old one duplicates the user turn server-side (the
      // "two identical user bubbles" symptom); and truncating to a rolled-back/empty turn is
      // what wiped the whole chat on a provider error. Truncating to the anchor removes the
      // user turn and its failed reply together.
      try {
        if (anchor && isServerId(anchor.id)) {
          await conversations.truncateAfter(anchor.id);
        } else {
          for (const m of popped) if (isServerId(m.id)) await conversations.deleteMessage(m.id).catch(() => undefined);
          if (isServerId(lastUser.id)) await conversations.deleteMessage(lastUser.id).catch(() => undefined);
        }
      } catch {
        /* server may have already rolled the failed turn back */
      }
      void runAgentConversation(text, S().ui.agentMode, { rebindUserId: lastUser.id });
    })();
    return;
  }
  const messages = [...session.messages];
  while (messages.length && messages[messages.length - 1].role === "assistant") {
    messages.pop();
  }
  const lastUser = messages[messages.length - 1];
  if (!lastUser) return;
  /* reset the transcript to end at the last user message */
  const patch = { messages: messages.map((m) => ({ ...m })) };
  useKontur.setState((prev) => ({
    sessions: prev.sessions.map((s) => (s.id === prev.currentSessionId ? { ...s, ...patch } : s)),
  }));
  if (isDemoRequest(lastUser.content)) {
    void runDemoScenario(lastUser.content);
  } else {
    void runGenericScenario(lastUser.content, store.ui.agentMode);
  }
}
