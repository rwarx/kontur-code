/**
 * Makes the global `Electron` namespace available to the shell's JSDoc annotations.
 *
 * `electron.d.ts` declares the namespace as a side effect of the `electron`
 * module being resolved. In these CommonJS files the module is pulled in with
 * `require("electron")`, which does not register the global — so every
 * `@type {Electron.BrowserWindow | null}` annotation silently degraded to `any`
 * and the whole point of running a type checker over the shell was lost.
 *
 * Referencing it here is the supported way in, and it is also why `include` in
 * tsconfig.shell.json covers this directory.
 *
 * @see https://www.typescriptlang.org/ts-handbook/triple-slash-reference.html
 */
/// <reference types="electron" />

/** The bridge `preload.js` exposes. Mirrored by `kontur-bridge.d.ts` on the renderer side. */
interface KonturMainBridge {
  getBackendUrl(): Promise<string>;
  getBackendAuth(): Promise<string>;
  pickFolder(): Promise<string | null>;
  saveDialog(defaultName: string, filters?: unknown[]): Promise<string | null>;
  openDialog(filters?: unknown[]): Promise<string[]>;
  showInFolder(fullPath: string): Promise<void>;
  readFileLocal(fullPath: string): Promise<string>;
  openExternal(url: string): Promise<void>;
}
