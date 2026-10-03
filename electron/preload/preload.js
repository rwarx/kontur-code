"use strict";

/* Kontur Code — preload bridge (context-isolated). */

const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("kontur", {
  getBackendUrl: () => ipcRenderer.invoke("kontur:getBackendUrl"),

  /* The sidecar's per-launch bearer token. It is fetched, never bundled, and the renderer only ever
     attaches it as an Authorization header. */
  getBackendAuth: () => ipcRenderer.invoke("kontur:getBackendAuth"),
  pickFolder: () => ipcRenderer.invoke("kontur:pickFolder"),
  saveDialog: (defaultName, filters) => ipcRenderer.invoke("kontur:saveDialog", defaultName, filters),
  openDialog: (filters) => ipcRenderer.invoke("kontur:openDialog", filters),
  showInFolder: (fullPath) => ipcRenderer.invoke("kontur:showInFolder", fullPath),
  readFileLocal: (fullPath) => ipcRenderer.invoke("kontur:readFileLocal", fullPath),
  openExternal: (url) => ipcRenderer.invoke("kontur:openExternal", url),
  win: {
    minimize: () => ipcRenderer.invoke("kontur:winMinimize"),
    maximizeToggle: () => ipcRenderer.invoke("kontur:winMaximizeToggle"),
    close: () => ipcRenderer.invoke("kontur:winClose"),
    isMaximized: () => ipcRenderer.invoke("kontur:winIsMaximized"),
    onMaximizedChanged: (cb) => {
      const listener = (_evt, maximized) => cb(Boolean(maximized));
      ipcRenderer.on("kontur:winMaximizedChanged", listener);
      return () => ipcRenderer.removeListener("kontur:winMaximizedChanged", listener);
    },
  },
  view: {
    setZoom: (factor) => ipcRenderer.invoke("kontur:setZoom", factor),
  },
});
