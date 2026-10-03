"use client";

/* ============================================================
   KONTUR CODE — canvas minimap.
   World bounding box projected into a 168×112 chip; the current
   viewport is drawn as an accent rectangle. Click / drag centers
   the main canvas on the corresponding world point.
   ============================================================ */

import { useRef } from "react";
import type { PointerEvent as ReactPointerEvent } from "react";
import type { CanvasNode } from "@/lib/kontur/types";
import { kindColorVar, nodeH, nodeW, nodesBounds } from "./canvas-utils";

const MM_W = 168;
const MM_H = 112;
const PAD = 10;

export interface MiniMapProps {
  nodes: CanvasNode[];
  selectedIds: string[];
  zoom: number;
  panX: number;
  panY: number;
  viewW: number;
  viewH: number;
  ariaLabel: string;
  onCenterWorld: (wx: number, wy: number) => void;
}

export function MiniMap({
  nodes,
  selectedIds,
  zoom,
  panX,
  panY,
  viewW,
  viewH,
  ariaLabel,
  onCenterWorld,
}: MiniMapProps) {
  const dragRef = useRef<number | null>(null);

  const bounds = nodesBounds(nodes);
  const bx = bounds?.x ?? 0;
  const by = bounds?.y ?? 0;
  const bw = Math.max(bounds?.w ?? 1, 1);
  const bh = Math.max(bounds?.h ?? 1, 1);
  const scale = Math.min((MM_W - PAD * 2) / bw, (MM_H - PAD * 2) / bh);
  const ox = (MM_W - bw * scale) / 2;
  const oy = (MM_H - bh * scale) / 2;

  const mapX = (wx: number) => (wx - bx) * scale + ox;
  const mapY = (wy: number) => (wy - by) * scale + oy;

  /* viewport rect in world coords: screen (0,0) → world (-panX/zoom, -panY/zoom) */
  const vpX = mapX(-panX / zoom);
  const vpY = mapY(-panY / zoom);
  const vpW = (viewW / zoom) * scale;
  const vpH = (viewH / zoom) * scale;

  const toWorld = (e: ReactPointerEvent<HTMLDivElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    return { x: (mx - ox) / scale + bx, y: (my - oy) / scale + by };
  };

  const handlePointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    dragRef.current = e.pointerId;
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort (synthetic / already-released pointers) */
    }
    const p = toWorld(e);
    onCenterWorld(p.x, p.y);
  };

  const handlePointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (dragRef.current !== e.pointerId) return;
    const p = toWorld(e);
    onCenterWorld(p.x, p.y);
  };

  const handlePointerUp = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (dragRef.current === e.pointerId) dragRef.current = null;
  };

  return (
    <div
      data-canvas-overlay
      role="application"
      aria-label={ariaLabel}
      title={ariaLabel}
      className="absolute bottom-3 right-3 z-30 hidden cursor-pointer touch-none overflow-hidden rounded-md border border-line bg-surface-1/90 transition-[border-color,box-shadow] duration-150 hover:border-line-strong hover:shadow-subtle md:block"
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerUp}
    >
      <svg width={MM_W} height={MM_H} className="block" aria-hidden>
        {nodes.map((n) => {
          const selected = selectedIds.includes(n.id);
          return (
            <rect
              key={n.id}
              x={mapX(n.x)}
              y={mapY(n.y)}
              width={Math.max(nodeW(n) * scale, 2)}
              height={Math.max(nodeH(n) * scale, 2)}
              rx={1}
              fill={kindColorVar(n.kind)}
              opacity={selected ? 1 : 0.7}
              stroke={selected ? "var(--kc-accent)" : undefined}
              strokeWidth={selected ? 0.75 : undefined}
            />
          );
        })}
        {viewW > 0 && viewH > 0 && (
          <rect
            x={vpX}
            y={vpY}
            width={Math.max(vpW, 2)}
            height={Math.max(vpH, 2)}
            rx={2}
            fill="var(--kc-accent-soft)"
            stroke="var(--kc-accent)"
            strokeWidth={1}
          />
        )}
      </svg>
    </div>
  );
}
