"use strict";

/* ============================================================
   Kontur Code — Electron main process.
   Owns the window, the .NET sidecar backend (AIClient.Server)
   and the native bridges (dialogs, shell, menu).
   ============================================================ */

const { app, BrowserWindow, ipcMain, dialog, shell, Menu, session } = require("electron");
const { spawn } = require("node:child_process");
const crypto = require("node:crypto");
const path = require("node:path");
const fs = require("node:fs");

const DEV_URL = process.env.KONTUR_RENDERER_URL || "http://127.0.0.1:5173";
const isDev = !app.isPackaged;

const SIDECAR_PORT = 45631;
const LOOPBACK_URL = `http://127.0.0.1:${SIDECAR_PORT}`;

/* Largest file the renderer may pull off disk directly, in bytes. Every ordinary source file and
   every project asset is orders of magnitude below this; anything larger belongs to the sidecar,
   which enforces its own caps and can report a useful refusal. */
const MAX_LOCAL_READ_BYTES = 8 * 1024 * 1024;

let mainWindow = null;
let sidecar = null;
let backendUrl = "";
let backendReadyResolve = null;
const backendReady = new Promise((resolve) => {
  backendReadyResolve = resolve;
});

/* One token per launch, shared between this process, the sidecar it starts, and the renderer that
   talks to it. It is the sidecar's only credential, so it is generated here where it never touches
   the file system: the renderer receives it over the context bridge, the sidecar over an inherited
   environment variable, and neither can be read by a web page the user happens to have open. */
const authToken = crypto.randomBytes(32).toString("base64");

/* Folders the user has actually chosen through the OS dialog in this session. The renderer is not
   trusted to nominate one on its own: anything it can name here would become readable through
   kontur:readFileLocal, so the authority for the list is a directory the person picked, and this
   process is the only thing that sees that pick. */
const userFolders = new Set();

function rememberFolder(dir) {
  if (typeof dir !== "string" || dir.length === 0) return;
  try {
    userFolders.add(path.resolve(dir));
  } catch {
    /* not a path we can use */
  }
}

function isInsideKnownWorkspace(target) {
  for (const folder of userFolders) {
    const relative = path.relative(folder, target);
    if (relative === "") continue;
    if (!relative.startsWith("..") && !path.isAbsolute(relative)) return true;
  }
  return false;
}

function sidecarCandidates() {
  const list = [];
  if (process.env.KONTUR_SIDECAR) list.push(process.env.KONTUR_SIDECAR);
  // packaged layout: <resources>/sidecar/AIClient.Server(.exe)
  list.push(path.join(process.resourcesPath || "", "sidecar", process.platform === "win32" ? "AIClient.Server.exe" : "AIClient.Server"));
  // dev layout: repo checkout next to electron/
  list.push(path.join(__dirname, "..", "..", "src", "AIClient.Server", "bin", "Debug", "net10.0-windows", "AIClient.Server.exe"));
  list.push(path.join(__dirname, "..", "..", "src", "AIClient.Server", "bin", "Debug", "net10.0-windows", "AIClient.Server"));
  return list;
}

function startSidecar() {
  // An externally hosted backend is a development convenience and has to be loopback too: the token
  // protects the sidecar, but pointing the renderer at a remote host would send conversation content
  // off the machine and the token with it.
  if (process.env.KONTUR_BACKEND_URL) {
    const raw = process.env.KONTUR_BACKEND_URL;
    let parsed = null;
    try {
      parsed = new URL(raw);
    } catch {
      parsed = null;
    }
    if (!parsed || !["127.0.0.1", "localhost", "::1", "[::1]"].includes(parsed.hostname)) {
      throw new Error(`KONTUR_BACKEND_URL must point at loopback; got ${raw}`);
    }
    backendUrl = raw;
    console.log(`[kontur] using external backend ${backendUrl} (loopback only)`);
    backendReadyResolve();
    return;
  }

  const exe = sidecarCandidates().find((p) => {
    try {
      return fs.existsSync(p);
    } catch {
      return false;
    }
  });
  if (!exe) {
    console.error("[kontur] sidecar not found; the renderer will fall back to the default port and fail to authenticate.");
    backendUrl = LOOPBACK_URL;
    backendReadyResolve();
    return;
  }

  console.log(`[kontur] starting sidecar: ${exe} --urls ${LOOPBACK_URL}`);
  sidecar = spawn(exe, ["--urls", LOOPBACK_URL], {
    stdio: ["ignore", "pipe", "pipe"],
    // Without this a console-subsystem child of a GUI app flashes a console window on every launch.
    windowsHide: true,
    env: { ...process.env, AICLIENT_AUTHTOKEN: authToken },
  });
  backendUrl = LOOPBACK_URL;

  let resolved = false;
  const done = () => {
    if (!resolved) {
      resolved = true;
      backendReadyResolve();
    }
  };
  const timer = setTimeout(done, 15000);
  sidecar.stdout.on("data", (d) => {
    const text = String(d);
    process.stdout.write(`[sidecar] ${text}`);
    if (text.includes("Now listening on") || text.includes("Application started")) {
      clearTimeout(timer);
      done();
    }
  });
  sidecar.stderr.on("data", (d) => process.stderr.write(`[sidecar:err] ${String(d)}`));

  // An unhandled 'error' event on a ChildProcess is thrown by Node. Without this listener a quarantined
  // exe, a partial install or an out-of-memory spawn takes the whole application down before it draws a
  // window, and the user sees nothing at all instead of an error.
  sidecar.on("error", (err) => {
    console.error(`[kontur] sidecar failed to start: ${err.message}`);
    clearTimeout(timer);
    done();
  });

  sidecar.on("exit", (code) => {
    console.error(`[kontur] sidecar exited with code ${code}`);
    clearTimeout(timer);
    done();
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("kontur:sidecarGone", code);
    }
  });
}

function stopSidecar() {
  if (!sidecar || sidecar.killed) return;
  const pid = sidecar.pid;
  try {
    if (process.platform === "win32" && pid) {
      /* A .NET GUI-subsystem child frequently ignores SIGTERM on Windows, so `kill()` leaves it
         running and holding port 45631. taskkill with /T takes the process tree and /F is
         unconditional; without this the sidecar outlives the app that started it. */
      spawn("taskkill", ["/PID", String(pid), "/T", "/F"], { stdio: "ignore", windowsHide: true });
    } else {
      sidecar.kill("SIGTERM");
    }
  } catch {
    /* already gone */
  }
  sidecar = null;
}

function hardenSession() {
  /* Nothing in this application needs a camera, a microphone, a geolocation or a notification while
     the window is up, and a permission request handler that refuses everything is the difference
     between a renderer bug being inert and it being a capability. */
  session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);

  /* The CSP in index.html pins connect-src to the loopback port. It is duplicated here, built from
     the value actually in use, so that pointing the app at a different loopback port cannot produce
     a blank window with a silent network failure. */
  const dev = isDev ? " http://127.0.0.1:5173 ws://127.0.0.1:5173" : "";
  const connect = backendUrl ? ` ${backendUrl}` : ` ${LOOPBACK_URL}`;
  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        "Content-Security-Policy": [
          `default-src 'self'; script-src 'self'${isDev ? " 'unsafe-inline'" : ""}; ` +
            `style-src 'self' 'unsafe-inline'; connect-src 'self'${connect}${dev}; ` +
            "img-src 'self' data: blob:; font-src 'self' data:; object-src 'none'; " +
            "base-uri 'none'; frame-src 'none'",
        ],
      },
    });
  });
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1360,
    height: 900,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: "#131417",
    autoHideMenuBar: true,
    frame: false,
    webPreferences: {
      preload: path.join(__dirname, "..", "preload", "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  /* A child window inherits this window's preload, contextIsolation and sandbox, so an unhandled
     window.open() is a second privileged surface rather than a browser tab. Nothing here needs one:
     outbound links go through kontur:openExternal, which protocol-gates them. */
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url)) shell.openExternal(url);
    return { action: "deny" };
  });

  /* Same reasoning for in-page navigation. A renderer-side `location.href` would otherwise replace
     the application with whatever it pointed at, still holding the preload bridge. */
  mainWindow.webContents.on("will-navigate", (event, url) => {
    const allowed = isDev ? DEV_URL : url.startsWith("file://");
    if (!allowed) {
      event.preventDefault();
      console.error(`[kontur] blocked navigation to ${url}`);
    }
  });

  mainWindow.webContents.on("will-attach-webview", (event) => event.preventDefault());

  // Keep the custom title-bar's maximize/restore control in sync with the OS.
  const emitMaximized = () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("kontur:winMaximizedChanged", mainWindow.isMaximized());
    }
  };
  mainWindow.on("maximize", emitMaximized);
  mainWindow.on("unmaximize", emitMaximized);

  if (isDev) {
    mainWindow.loadURL(DEV_URL).catch((err) => console.error("[kontur] loadURL failed:", err));
  } else {
    mainWindow.loadFile(path.join(__dirname, "..", "dist", "renderer", "index.html"));
  }

  mainWindow.on("closed", () => {
    mainWindow = null;
  });
}

function buildMenu() {
  const template = [
    {
      label: "File",
      submenu: [{ role: "quit" }],
    },
    {
      label: "Edit",
      submenu: [
        { role: "undo" },
        { role: "redo" },
        { type: "separator" },
        { role: "cut" },
        { role: "copy" },
        { role: "paste" },
        { role: "selectAll" },
      ],
    },
    {
      label: "View",
      submenu: [
        { role: "reload" },
        { role: "toggleDevTools" },
        { type: "separator" },
        { role: "resetZoom" },
        { role: "zoomIn" },
        { role: "zoomOut" },
        { type: "separator" },
        { role: "togglefullscreen" },
      ],
    },
    {
      label: "Window",
      submenu: [{ role: "minimize" }, { role: "close" }],
    },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function registerIpc() {
  ipcMain.handle("kontur:getBackendUrl", async () => {
    await backendReady;
    return backendUrl || LOOPBACK_URL;
  });

  /* The sidecar's bearer token. It crosses the bridge rather than living in the bundle because it is
     generated per launch: a token baked into the renderer would be a token an attacker could read out
     of a published asar. The renderer attaches it as an Authorization header and nothing else. */
  ipcMain.handle("kontur:getBackendAuth", async () => {
    await backendReady;
    return authToken;
  });

  ipcMain.handle("kontur:pickFolder", async () => {
    const win = BrowserWindow.getFocusedWindow() || mainWindow;
    if (!win) return null;
    const res = await dialog.showOpenDialog(win, { properties: ["openDirectory"] });
    if (res.canceled || res.filePaths.length === 0) return null;
    rememberFolder(res.filePaths[0]);
    return res.filePaths[0];
  });

  ipcMain.handle("kontur:saveDialog", async (_evt, defaultName, filters) => {
    const win = BrowserWindow.getFocusedWindow() || mainWindow;
    if (!win) return null;
    const res = await dialog.showSaveDialog(win, {
      defaultPath: typeof defaultName === "string" && defaultName ? defaultName : "export.md",
      filters: Array.isArray(filters) ? filters : [],
    });
    if (res.canceled || !res.filePath) return null;
    return res.filePath;
  });

  ipcMain.handle("kontur:openDialog", async (_evt, filters) => {
    const win = BrowserWindow.getFocusedWindow() || mainWindow;
    if (!win) return [];
    const res = await dialog.showOpenDialog(win, {
      properties: ["openFile", "multiSelections"],
      filters: Array.isArray(filters) ? filters : [],
    });
    if (res.canceled) return [];
    return res.filePaths;
  });

  ipcMain.handle("kontur:showInFolder", async (_evt, fullPath) => {
    if (typeof fullPath !== "string" || fullPath.length === 0) return;
    // Resolved before it reaches the shell so a relative or malformed path cannot be handed to
    // Explorer, and so the check below is against an absolute path rather than whatever the caller
    // wrote.
    const resolved = path.resolve(fullPath);
    if (!isInsideKnownWorkspace(resolved)) return;
    shell.showItemInFolder(resolved);
  });

  ipcMain.handle("kontur:readFileLocal", async (_evt, fullPath) => {
    /* Read off disk for the renderer: a project file the user has already opened. It was an
       arbitrary-path read with no size cap, which meant one string in one IPC message could pull any
       file on the disk - including the DPAPI-protected keys - into the renderer. Now: the path must
       be absolute, must be inside a folder the user has opened in this session, must not be a link,
       and must be under the size cap. */
    if (typeof fullPath !== "string" || fullPath.length === 0) {
      throw new Error("A file path is required.");
    }
    if (!path.isAbsolute(fullPath)) {
      throw new Error("A local read needs an absolute path.");
    }

    const resolved = path.resolve(fullPath);
    if (!isInsideKnownWorkspace(resolved)) {
      throw new Error("That file is outside the folders opened in this session.");
    }

    const stat = await fs.promises.lstat(resolved);
    if (stat.isSymbolicLink()) {
      throw new Error("That path is a link, and links are not followed here.");
    }
    if (stat.size > MAX_LOCAL_READ_BYTES) {
      throw new Error(`That file is ${Math.round(stat.size / 1024 / 1024)} MB; the local read cap is 8 MB.`);
    }

    return fs.promises.readFile(resolved, "utf-8");
  });

  ipcMain.handle("kontur:openExternal", async (_evt, url) => {
    /* only ever hand http/https URLs to the OS browser */
    if (typeof url === "string" && /^https?:\/\//i.test(url)) {
      await shell.openExternal(url);
    }
  });

  // ----- frameless window chrome (custom TitleBar controls) -----
  const focusedWin = () => BrowserWindow.getFocusedWindow() || mainWindow;

  ipcMain.handle("kontur:winMinimize", () => {
    focusedWin()?.minimize();
  });

  ipcMain.handle("kontur:winMaximizeToggle", () => {
    const win = focusedWin();
    if (!win) return;
    if (win.isMaximized()) win.unmaximize();
    else win.maximize();
  });

  ipcMain.handle("kontur:winClose", () => {
    focusedWin()?.close();
  });

  ipcMain.handle("kontur:winIsMaximized", () => focusedWin()?.isMaximized() ?? false);

  // ----- interface zoom (app scale) -----
  ipcMain.handle("kontur:setZoom", (_evt, factor) => {
    const win = focusedWin();
    if (!win) return 1;
    const n = Number(factor);
    const f = Number.isFinite(n) ? Math.min(2, Math.max(0.6, n)) : 1;
    win.webContents.setZoomFactor(f);
    return f;
  });
}

/* Two instances of this application means two sidecars, and the second one fails to bind port 45631
   while both windows talk to the first - so the second launch is refused and the running window is
   brought forward instead. */
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(() => {
    registerIpc();
    buildMenu();
    startSidecar();
    hardenSession();
    createWindow();

    app.on("activate", () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });

  app.on("window-all-closed", () => {
    if (process.platform !== "darwin") app.quit();
  });

  /* Not before-quit: before-quit fires while windows may still be alive, and killing the sidecar then
     leaves a renderer mid-request. waiting-to-quit fires once, after everything has settled. */
  app.on("will-quit", () => {
    stopSidecar();
  });
}
