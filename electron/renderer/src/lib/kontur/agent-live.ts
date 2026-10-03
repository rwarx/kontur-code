"use client";

/* ============================================================
   Real agent driver — runs chat/agent turns against the .NET
   sidecar and maps the NDJSON stream onto the store primitives
   (messages, tool calls, approvals, trajectory events).
   No scripted content: everything on screen arrived on the wire.
   ============================================================ */

import { checkpoints, runs, type RunAttachment, type StreamFrame } from "./backend";
import { ensureServerSession, isServerId, refreshCanvasAfterPlan, refreshOpenTabs, reloadCurrentSession, syncWorkspaceFiles } from "./sync";
import { newId, useKontur, waitForApproval } from "./store";
import type { AgentMode, Approval, Attachment, DiffLine, Message, ToolCall } from "./types";

const S = () => useKontur.getState();

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

interface ActiveRun {
  runId: string;
  abort: AbortController;
}

let activeRun: ActiveRun | null = null;

export function stopAgentRun() {
  if (!activeRun) return;
  const { runId, abort } = activeRun;
  activeRun = null;
  // Mark aborted first so a gate that is mid-wait bails cleanly (see handleApproval),
  // then release any pending approval so the server thread unblocks and the gate closes.
  abort.abort();
  if (S().approval) S().resolveApproval("denied");
  void runs.cancel(runId);
}

export function isAgentRunActive() {
  return activeRun !== null;
}

/** Extract workspace-relative file paths from tool arguments (best effort). */
function argPaths(argsJson: string): string[] {
  try {
    const args = JSON.parse(argsJson) as Record<string, unknown>;
    const out: string[] = [];
    for (const key of ["path", "workspacePath", "file", "filePath", "directory", "from", "to"]) {
      const v = args[key];
      if (typeof v === "string" && v.length > 0 && v.length < 300) out.push(v);
    }
    return [...new Set(out)].slice(0, 8);
  } catch {
    return [];
  }
}

function parseDiffLines(preview: string): DiffLine[] | undefined {
  if (!preview || !preview.includes("\n")) return undefined;
  const lines = preview.split("\n").slice(0, 120);
  return lines.map((text): DiffLine => {
    if (text.startsWith("@@")) return { kind: "header", text };
    if (text.startsWith("+") && !text.startsWith("+++")) return { kind: "add", text };
    if (text.startsWith("-") && !text.startsWith("---")) return { kind: "del", text };
    return { kind: "context", text };
  });
}

const RISK_CONSEQUENCE: Record<string, string> = {
  Write: "Changes files in the folder you opened.",
  Execute: "Runs a program on this computer.",
  Read: "Reads from the folder you opened.",
};

function outcomeToState(outcome: string): ToolCall["state"] {
  if (outcome === "Succeeded") return "succeeded";
  if (outcome === "Failed") return "failed";
  if (outcome === "Denied") return "denied";
  return "abandoned";
}

function riskToUi(risk: string): ToolCall["risk"] {
  if (risk === "Execute") return "execute";
  if (risk === "Write") return "write";
  return "read";
}

export async function runAgentConversation(
  text: string,
  mode: AgentMode,
  opts?: { rebindUserId?: string },
): Promise<void> {
  const store = S();
  if (isAgentRunActive()) {
    store.pushToast("A run is already in progress", "Stop it before sending another message.", "destructive");
    return;
  }
  const trimmed = text.trim();
  if (!trimmed) return;

  let conversationId: string;
  try {
    conversationId = await ensureServerSession();
  } catch {
    store.pushToast("Backend unavailable", "The message was not sent.", "destructive");
    return;
  }

  const { providerId, modelId } = splitComposite(store.ui.selectedModelId);
  // The sidecar types runId/conversationId as `Guid?` and constrains the
  // approval/cancel routes to `{runId:guid}`. A `newId("run")` prefixed id
  // fails Guid binding → /api/chat/send & /api/agent/run return 400 before
  // streaming. Mint a real UUID so the whole run lifecycle binds.
  const runId = crypto.randomUUID();
  const abort = new AbortController();
  activeRun = { runId, abort };

  store.setRunner({ status: "running", label: mode === "off" ? "Answering" : "Working" });
  store.setDraft("");

  // Files staged on the paperclip travel with this one turn, then the tray clears.
  const staged = store.draftAttachments;
  const runAttachments: RunAttachment[] | undefined =
    staged.length > 0
      ? staged.map((a) => ({ fileName: a.name, mimeType: a.mimeType, textContent: a.textContent }))
      : undefined;
  const displayAttachments: Attachment[] | undefined =
    staged.length > 0 ? staged.map((a) => ({ id: a.id, name: a.name, sizeKb: a.sizeKb })) : undefined;
  store.clearDraftAttachments();

  let assistantId: string | null = null;
  let needsReload = false;
  // On retry we keep the failed turn's user bubble locally; the wire then persists a fresh
  // user message. Rebind the kept bubble onto that new id instead of appending a duplicate.
  let rebindUserId = opts?.rebindUserId ?? null;

  const ensureAssistant = (serverId: string, createdAt: string) => {
    if (assistantId) return assistantId;
    const msg: Message = {
      id: serverId,
      role: "assistant",
      content: "",
      createdAt: Date.parse(createdAt) || Date.now(),
      modelId: store.ui.selectedModelId,
      providerId,
      streaming: true,
      toolCalls: [],
      live: true,
    };
    assistantId = serverId;
    S().addMessage(msg);
    return serverId;
  };

  const finishStreaming = () => {
    S().flushMessageBuffer();
    if (assistantId) S().updateMessage(assistantId, { streaming: false });
  };

  const fail = async (title: string, fatal: boolean) => {
    finishStreaming();
    // Pin the error onto the assistant bubble so it shows inline immediately,
    // even before the server reload — a failed run must never look like an
    // empty reply. reloadCurrentSession re-applies the same error from the wire.
    if (assistantId) S().updateMessage(assistantId, { error: title });
    S().addEvent({ id: newId("ev"), ts: Date.now(), kind: "error", title, status: "failed" });
    S().setRunner({ status: "idle", label: undefined });
    if (fatal) S().pushToast(title, undefined, "destructive");
    try {
      await reloadCurrentSession();
    } catch {
      /* keep the local transcript */
    }
    await refreshAfterRun();
  };

  const refreshAfterRun = async () => {
    // Agent edits land on disk through the server tools; the Files/Code
    // surfaces read the store, so re-pull what changed.
    try {
      await syncWorkspaceFiles();
      await refreshOpenTabs();
    } catch {
      /* files stay as they were */
    }
  };

  const complete = async (label: string, inputTokens?: number, outputTokens?: number, ms?: number) => {
    S().flushMessageBuffer();
    if (assistantId && (inputTokens || outputTokens)) {
      S().updateMessage(assistantId, {
        streaming: false,
        usage: { inputTokens: inputTokens ?? 0, outputTokens: outputTokens ?? 0, ms: ms ?? 0 },
      });
    } else {
      finishStreaming();
    }
    S().setRunner({ status: "done", label: label || "Completed" });
    try {
      await reloadCurrentSession();
    } catch {
      /* keep the local transcript */
    }
    await refreshAfterRun();
  };

  const handleApproval = async (data: Record<string, unknown>) => {
    const approvalId = data["approvalId"] as string;
    const tool = (data["toolName"] as string) ?? "tool";
    const risk = (data["risk"] as string) ?? "Read";
    const summary = (data["summary"] as string) ?? undefined;
    const preview = (data["preview"] as string) ?? undefined;
    const argsNote = (data["argumentsJson"] as string) ?? undefined;

    // checkpoint-before-edit, like the prototype: capture the files this call may touch
    if ((risk === "Write" || risk === "Execute") && isServerId(conversationId)) {
      const paths = argPaths(argsNote ?? "{}");
      if (paths.length > 0 || risk === "Execute") {
        void checkpoints
          .create(conversationId, `before ${tool}`, assistantId, paths, true)
          .then((c) =>
            checkpoints
              .get(c.id)
              .then((full) =>
                S().addCheckpoint?.({
                  id: full.id,
                  label: full.label,
                  createdAt: Date.parse(full.createdAt) || Date.now(),
                  filesSnapshot: Object.entries(full.files).map(([path, content]) => ({ path, content })),
                  sessionId: conversationId,
                  message: assistantId ?? "",
                }),
              )
              .catch(() => undefined),
          )
          .catch(() => undefined);
      }
    }

    const approval: Approval = {
      id: approvalId,
      tool,
      risk: riskToUi(risk),
      headline: summary || tool,
      consequence: RISK_CONSEQUENCE[risk] ?? RISK_CONSEQUENCE.Read,
      diff: preview ? parseDiffLines(preview) : undefined,
      argsNote: argsNote && argsNote.length < 500 ? argsNote : undefined,
      allowForRunAvailable: risk !== "Execute",
    };
    S().setApproval(approval);
    S().setRunner({ status: "awaiting-approval", label: "Waiting for approval" });
    S().addEvent({
      id: newId("ev"),
      ts: Date.now(),
      kind: "approval",
      title: `${tool} · approval required`,
      status: "warning",
    });
    const decision = await waitForApproval(approvalId);
    if (abort.signal.aborted) return; // stopped while waiting — the gate was already released
    S().setRunner({ status: "running", label: "Working" });
    const outcome =
      decision.decision === "allowed" ? "allowed" : decision.decision === "allowed-for-run" ? "allowed-for-run" : "denied";
    try {
      await runs.answerApproval(runId, approvalId, outcome, decision.reason);
    } catch {
      /* run may have ended while answering */
    }
    S().addEvent({
      id: newId("ev"),
      ts: Date.now(),
      kind: "approval",
      title: `${tool} · ${outcome}`,
      status: outcome === "denied" ? "failed" : "done",
    });
  };

  // Approvals are served through the run registry, not the NDJSON stream: the server
  // blocks the agent thread on a PendingApproval and expects the renderer to poll for it
  // (GET /api/runs/{runId}/approval → the question, or 204 while none is pending).
  // We poll concurrently with the stream and feed any new question into handleApproval,
  // which shows the gate, waits for the user, then POSTs the decision back.
  let lastHandledApprovalId: string | null = null;
  let pollActive = false;

  const pollApprovals = async () => {
    pollActive = true;
    while (pollActive && activeRun?.runId === runId && !abort.signal.aborted) {
      let pending: Awaited<ReturnType<typeof runs.approval>> = null;
      try {
        pending = await runs.approval(runId);
      } catch {
        pending = null;
      }
      if (pending && pending.approvalId !== lastHandledApprovalId) {
        lastHandledApprovalId = pending.approvalId;
        await handleApproval(pending as unknown as Record<string, unknown>);
      } else {
        await sleep(500);
      }
    }
  };

  const onFrame = async (frame: StreamFrame) => {
    const st = S();
    const data = (frame.data ?? {}) as Record<string, unknown>;
    if (frame.type === "delta" && frame.text) {
      const id = (frame.messageId as string) ?? assistantId;
      if (id) {
        if (!assistantId) ensureAssistant(id, new Date().toISOString());
        st.appendToMessageBuffered(id, frame.text);
      }
      return;
    }
    // Any non-delta frame is a structural boundary (tool call, completion,
    // error, reload). Commit buffered stream text first so transcript order
    // and content stay exact.
    st.flushMessageBuffer();
    if (frame.type === "error") {
      await fail((frame.message as string) || "Request failed", false);
      activeRun = null;
      return;
    }
    if (frame.type !== "event") return;
    switch (frame.name) {
      case "user-saved": {
        const m = data["message"] as { id: string; content: string; createdAt: string };
        const createdAt = Date.parse(m.createdAt) || Date.now();
        const rebindTarget = rebindUserId
          ? st.currentSession()?.messages.find((x) => x.id === rebindUserId)
          : undefined;
        if (rebindTarget) {
          // retry: reuse the kept user bubble by rebinding its id onto the freshly-persisted
          // server message — never append a second identical user bubble.
          st.updateMessage(rebindUserId as string, {
            id: m.id,
            content: m.content,
            createdAt,
            attachments: displayAttachments,
          });
        } else {
          st.addMessage({
            id: m.id,
            role: "user",
            content: m.content,
            createdAt,
            toolCalls: [],
            attachments: displayAttachments,
          });
        }
        rebindUserId = null;
        st.addEvent({ id: newId("ev"), ts: Date.now(), kind: "request", title: m.content.slice(0, 80), status: "done" });
        break;
      }
      case "assistant-started":
      case "step-started": {
        const m = data["message"] as { id: string; createdAt: string };
        ensureAssistant(m.id, m.createdAt);
        break;
      }
      case "reasoning-delta": {
        const id = data["messageId"] as string;
        const cur = st.currentSession()?.messages.find((x) => x.id === id);
        if (cur) st.updateMessage(id, { reasoning: (cur.reasoning ?? "") + ((data["text"] as string) ?? "") });
        break;
      }
      case "tool-proposed": {
        const call = data["call"] as { id: string; name: string; argumentsJson: string };
        const msgId = (data["messageId"] as string) ?? assistantId;
        if (!msgId) break;
        st.addToolCall(msgId, {
          id: call.id,
          tool: call.name,
          risk: riskToUi((data["risk"] as string) ?? "Read"),
          state: "proposed",
          headline: call.name,
        });
        st.addEvent({ id: newId("ev"), ts: Date.now(), kind: "tool", title: `${call.name} · proposed`, status: "running" });
        break;
      }
      case "tool-started": {
        const call = data["call"] as { id: string };
        const msgId = (data["messageId"] as string) ?? assistantId;
        if (msgId && call) st.updateToolCall(msgId, call.id, { state: "running" });
        break;
      }
      case "tool-finished": {
        const call = data["call"] as { id: string; name: string };
        const outcome = (data["outcome"] as string) ?? "Failed";
        const row = data["message"] as { content: string; createdAt: string } | undefined;
        const summary = (data["summary"] as string) ?? undefined;
        const detail = (data["detail"] as string) ?? undefined;
        const owner =
          st.currentSession()?.messages.find((m) => m.toolCalls.some((t) => t.id === call.id)) ?? null;
        const body = row?.content ? row.content.slice(0, 4000) : undefined;
        if (owner) {
          st.updateToolCall(owner.id, call.id, {
            state: outcomeToState(outcome),
            headline: summary || owner.toolCalls.find((t) => t.id === call.id)?.headline || call.name,
            body,
          });
          if (detail && detail !== body) {
            // surface the diff separately when the tool offered one
            st.updateToolCall(owner.id, call.id, { body: detail.slice(0, 4000) });
          }
        }
        st.addEvent({
          id: newId("ev"),
          ts: Date.now(),
          kind: outcome === "Succeeded" ? "tool" : outcome === "Denied" ? "approval" : "error",
          title: `${call.name} · ${outcome.toLowerCase()}`,
          status: outcome === "Succeeded" ? "done" : outcome === "Denied" ? "warning" : "failed",
        });
        // submit_plan draws onto the shared graph server-side (ServerCanvasPlanSink);
        // re-pull it so the plan appears on the canvas without a dedicated wire event.
        if (call.name === "submit_plan" && outcome === "Succeeded") {
          void refreshCanvasAfterPlan().catch(() => undefined);
        }
        break;
      }
      case "step-completed": {
        const id = data["messageId"] as string;
        if (id) {
          st.updateMessage(id, {
            usage: {
              inputTokens: (data["inputTokens"] as number) ?? 0,
              outputTokens: (data["outputTokens"] as number) ?? 0,
              ms: 0,
            },
          });
        }
        break;
      }
      case "completed": {
        await complete("Completed", data["inputTokens"] as number, data["outputTokens"] as number, data["generationTimeMs"] as number);
        activeRun = null;
        break;
      }
      case "run-completed": {
        const reason = (data["reason"] as string) ?? "Answered";
        await complete(
          reason === "Answered" ? "Completed" : reason === "StepLimit" ? "Step limit reached" : "Time limit reached",
        );
        if (reason !== "Answered") {
          st.pushToast(reason === "StepLimit" ? "Step limit reached" : "Time limit reached", "Send another message to continue.");
        }
        activeRun = null;
        break;
      }
      case "cancelled": {
        finishStreaming();
        st.addEvent({ id: newId("ev"), ts: Date.now(), kind: "complete", title: "Stopped. Partial answer preserved.", status: "warning" });
        st.setRunner({ status: "stopped", label: "Stopped" });
        try {
          await reloadCurrentSession();
        } catch {
          /* keep local */
        }
        await refreshAfterRun();
        activeRun = null;
        break;
      }
      case "title": {
        const conv = data["conversationId"] as string;
        const title = data["title"] as string;
        if (conv && title) st.renameSession(conv, title);
        break;
      }
      case "compacted": {
        needsReload = true;
        st.addEvent({
          id: newId("ev"),
          ts: Date.now(),
          kind: "compact",
          title: `Folded ${data["messagesFolded"] ?? 0} message(s)`,
          status: "done",
        });
        break;
      }
      default:
        break;
    }
  };

  // Plain chat ("off") never proposes tools, so it needs no approval poll.
  const pollPromise = mode === "off" ? null : pollApprovals();

  try {
    const attachments: RunAttachment[] | undefined = runAttachments;
    if (mode === "off") {
      await runs.chatSend(
        { runId, conversationId, content: trimmed, providerId, modelId },
        onFrame,
        abort.signal,
      );
    } else {
      const serverMode = mode === "plan" ? "Plan" : mode === "plancanvas" ? "PlanCanvas" : "Build";
      await runs.agentRun(
        { runId, conversationId, content: trimmed, providerId, modelId, mode: serverMode, attachments },
        onFrame,
        abort.signal,
      );
    }
  } catch (err) {
    if (abort.signal.aborted || S().runner.status === "stopped") {
      finishStreaming();
      S().setRunner({ status: "stopped", label: "Stopped" });
      try {
        await reloadCurrentSession();
      } catch {
        /* keep local */
      }
      await refreshAfterRun();
    } else {
      await fail(err instanceof Error ? err.message : "Request failed", true);
    }
  } finally {
    pollActive = false;
    if (activeRun?.runId === runId) activeRun = null;
    if (pollPromise) await pollPromise.catch(() => undefined);
  }
}

function splitComposite(composite: string): { providerId: string; modelId: string } {
  const i = composite.indexOf("/");
  if (i < 0) return { providerId: "openrouter", modelId: composite };
  return { providerId: composite.slice(0, i), modelId: composite.slice(i + 1) };
}
