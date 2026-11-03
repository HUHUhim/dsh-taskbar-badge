/**
 * Host half of dsh-taskbar-badge.
 *
 * Starts the bundled Windows helper together with Harness, so the taskbar
 * overlay and window-bounds restore survive every Harness restart.
 *
 * This file must not import DSH packages: it is a bundle patch row and is
 * loaded by the host before client plugins exist.
 */
import { spawn } from "node:child_process";
import { appendFileSync, existsSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export const name = "dsh-taskbar-badge";

const packageRoot = dirname(fileURLToPath(import.meta.url));
const helperPath = join(packageRoot, "bin", "dsh-taskbar-badge.exe");
const dataDirectory = join(process.env.LOCALAPPDATA || join(homedir(), "AppData", "Local"), "DSH");
const logPath = join(dataDirectory, "taskbar-badge-bridge.log");

function log(message) {
  try {
    mkdirSync(dataDirectory, { recursive: true });
    appendFileSync(logPath, `${new Date().toISOString()} ${message}\n`);
  } catch {
    // Diagnostics must never break Harness startup.
  }
}

export function apply() {
  if (process.platform !== "win32") return;

  if (!existsSync(helperPath)) {
    log(`helper missing: ${helperPath}`);
    return;
  }

  try {
    const child = spawn(helperPath, [], {
      detached: true,
      stdio: "ignore",
      windowsHide: true
    });
    child.on("error", (error) => log(`spawn error: ${error.message}`));
    child.unref();
    log(`spawned pid=${child.pid}`);
  } catch (error) {
    log(`spawn exception: ${error.message}`);
  }
}
