"use client";

/* ============================================================
   Live model client — streams a real completion from the
   /api/ai/chat backend into an assistant message.
   Used by the Off ("Ask") agent mode and by live run summaries
   in Plan/Build flows; falls back to the canned scenario replies
   when the backend is unavailable.
   ============================================================ */

import { useKontur } from "./store";
import { newId } from "./store";
import { translate } from "./i18n";
import { getBackendUrl } from "./backend";
import { isServerId, splitCompositeModel } from "./sync";

const S = () => useKontur.getState();

export type LiveResult = "completed" | "stopped" | "failed";

let liveAbort: AbortController | null = null;

/** Abort an in-flight live reply (wired to Esc / Stop). */
export function abortLiveChat() {
  liveAbort?.abort();
  liveAbort = null;
}

interface StreamEvent {
  type: "delta" | "done" | "error";
  text?: string;
  message?: string;
  inputTokens?: number;
  outputTokens?: number;
  ms?: number;
}

function finalizeStopped(msgId: string) {
  const locale = S().ui.locale;
  S().updateMessage(msgId, { streaming: false });
  S().addEvent({
    id: newId("ev"),
    ts: Date.now(),
    kind: "complete",
    title: translate(locale, "chat.live.stopped"),
    status: "warning",
  });
  S().setRunner({ status: "stopped", label: "Stopped" });
}

/**
 * Stream a live reply into an existing (already created, streaming)
 * assistant message. Never throws — returns the outcome so the caller
 * can decide whether to fall back to a canned reply.
 */
export async function runLiveChat(msgId: string): Promise<LiveResult> {
  const store = S();
  const session = store.currentSession();
  // Server-backed sessions only: the sidecar owns the turn and persists it.
  // Anything else falls back to the canned reply via "failed".
  if (!session || !isServerId(session.id)) {
    return "failed";
  }
  const lastUser = [...session.messages].reverse().find((m) => m.role === "user" && m.content.trim());
  if (!lastUser) {
    return "failed";
  }
  const { providerId, modelId } = splitCompositeModel(store.ui.selectedModelId);
  const historyChars = session.messages.reduce((n, m) => n + m.content.length, 0);

  const controller = new AbortController();
  liveAbort = controller;
  const startedAt = performance.now();
  let received = 0;
  let doneInput: number | undefined;
  let doneOutput: number | undefined;
  let doneMs: number | undefined;

  try {
    const backend = await getBackendUrl();
    const res = await fetch(`${backend}/api/ai/chat`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ conversationId: session.id, content: lastUser.content, providerId, modelId }),
      signal: controller.signal,
    });
    if (!res.ok || !res.body) throw new Error(`backend ${res.status}`);

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    let sawDone = false;

    for (;;) {
      if (S().runner.status === "stopped") {
        controller.abort();
        throw new DOMException("stopped", "AbortError");
      }
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        let evt: StreamEvent | null = null;
        try {
          evt = JSON.parse(trimmed) as StreamEvent;
        } catch {
          continue;
        }
        if (evt.type === "delta" && evt.text) {
          received += evt.text.length;
          S().appendToMessageBuffered(msgId, evt.text);
        } else if (evt.type === "error") {
          throw new Error(evt.message ?? "stream error");
        } else if (evt.type === "done") {
          sawDone = true;
          if (typeof evt.inputTokens === "number") doneInput = evt.inputTokens;
          if (typeof evt.outputTokens === "number") doneOutput = evt.outputTokens;
          if (typeof evt.ms === "number") doneMs = evt.ms;
        }
      }
      if (sawDone) break;
    }

    /* success — finish the message; prefer the server's real usage numbers */
    S().flushMessageBuffer();
    const ms = doneMs ?? Math.max(200, Math.round(performance.now() - startedAt));
    S().updateMessage(msgId, {
      streaming: false,
      usage: {
        inputTokens: doneInput ?? Math.ceil(historyChars / 4) + 900,
        outputTokens: doneOutput ?? Math.max(1, Math.ceil(received / 4)),
        ms,
      },
    });
    S().setRunner({ status: "done", label: "Completed" });
    return "completed";
  } catch (err) {
    S().flushMessageBuffer();
    const aborted = err instanceof DOMException && err.name === "AbortError";
    if (aborted) {
      finalizeStopped(msgId);
      return "stopped";
    }
    /* interrupted mid-stream with partial content — keep what we have */
    if (received > 0) {
      const locale = S().ui.locale;
      const ms = Math.max(200, Math.round(performance.now() - startedAt));
      S().updateMessage(msgId, {
        streaming: false,
        usage: { inputTokens: Math.ceil(historyChars / 4) + 900, outputTokens: Math.ceil(received / 4), ms },
      });
      S().addEvent({
        id: newId("ev"),
        ts: Date.now(),
        kind: "complete",
        title: `${translate(locale, "chat.live.badge")} · interrupted — partial answer kept`,
        status: "warning",
      });
      S().setRunner({ status: "done", label: "Completed" });
      return "completed";
    }
    /* nothing received — signal fallback */
    S().setRunner({ status: "running", label: "Answering" });
    return "failed";
  } finally {
    liveAbort = null;
  }
}

export interface LiveSummaryResult {
  result: LiveResult;
  /** estimated output tokens of the streamed summary (0 when failed) */
  outputTokens: number;
}

/**
 * Stream a REAL final summary into an existing assistant message after a
 * scripted agent run (plan/build/demo). `runReport` describes everything the
 * run did; the model turns it into the user-facing closing prose.
 *
 * Contract:
 *  - "completed": summary streamed (partial counts too) — caller finishes
 *    the message with its own usage numbers + toast.
 *  - "stopped":   user aborted — message already finalized, caller returns.
 *  - "failed":    backend unavailable, nothing appended — caller streams
 *    its scripted summary instead (demo never breaks).
 */
export async function runLiveSummary(msgId: string, runReport: string): Promise<LiveSummaryResult> {
  // Scripted flows run in local sessions only; posting a synthetic summary
  // turn to the sidecar would persist fiction into a real transcript.
  // The caller streams its scripted summary instead (demo never breaks).
  void msgId;
  void runReport;
  return { result: "failed", outputTokens: 0 };
}
