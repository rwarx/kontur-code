/// <reference types="vite/client" />

interface KonturBridge {
  getBackendUrl: () => Promise<string>;
  /**
   * The sidecar's per-launch bearer token. Generated in the main process, handed to the sidecar over
   * an environment variable and fetched here on demand — never bundled, never persisted. Every route
   * except `/api/health` returns 401 without it.
   */
  getBackendAuth: () => Promise<string>;
  pickFolder: () => Promise<string | null>;
  saveDialog: (defaultName: string, filters: { name: string; extensions: string[] }[]) => Promise<string | null>;
  openDialog: (filters: { name: string; extensions: string[] }[]) => Promise<string[]>;
  showInFolder: (fullPath: string) => Promise<void>;
  readFileLocal: (fullPath: string) => Promise<string>;
  openExternal: (url: string) => Promise<void>;
  /** Window-chrome controls for the frameless shell. Absent in the plain browser build. */
  win?: {
    minimize: () => Promise<void>;
    maximizeToggle: () => Promise<void>;
    close: () => Promise<void>;
    isMaximized: () => Promise<boolean>;
    /** Subscribe to maximize/unmaximize; returns an unsubscribe fn. */
    onMaximizedChanged: (cb: (maximized: boolean) => void) => () => void;
  };
  /** Interface zoom (app scale) via webContents.setZoomFactor. Absent in the plain browser build. */
  view?: {
    /** Set the renderer zoom factor (1 = 100%); returns the applied, clamped factor. */
    setZoom: (factor: number) => Promise<number>;
  };
}

declare global {
  interface Window {
    kontur?: KonturBridge;
  }
}

export {};
