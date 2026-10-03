import { spawn } from "node:child_process";
import { openSync, appendFileSync } from "node:fs";

const electron = "D:\\kontur-spatial-ui\\electron";
const viteBin = electron + "\\node_modules\\vite\\bin\\vite.js";
const logPath = electron + "\\.claude\\preview.log";

const fd = openSync(logPath, "a");

/* Dev server (HMR) so the preview reflects renderer source without a build.
   Honors the port the preview harness assigns via PORT (autoPort), falling
   back to 5173 for a manual launch. */
const port = process.env.PORT || "5191";
appendFileSync(logPath, `\n[${new Date().toISOString()}] launcher start; port=${port}; execPath=${process.execPath}\n`);
const child = spawn(process.execPath, [viteBin, "--port", port, "--strictPort"], {
  cwd: electron,
  stdio: ["ignore", fd, fd],
});
child.on("error", (e) => { appendFileSync(logPath, `spawn error: ${e.stack || e}\n`); process.exit(1); });
child.on("exit", (code, sig) => { appendFileSync(logPath, `child exit code=${code} sig=${sig}\n`); process.exit(code ?? 0); });
