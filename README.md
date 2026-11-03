# DSH Taskbar Badge

A Windows taskbar overlay badge for DeepSeek Harness that shows how many conversations finished while you were not looking. The same helper also remembers and restores the DSH window size and position across Harness restarts.

## Features

- Blue count badge on the DSH taskbar icon, like Codex/Teams unread badges.
- Counts completed, unread DSH conversations (subagent and archived sessions are ignored).
- Works while DSH is minimized, in the background, or has its sidebar collapsed.
- Restores the DSH window bounds from the previous close, including the maximized state.
- Starts with Harness through the plugin host half; there is no Windows Startup-folder entry.
- Windows only. On other platforms the plugin loads but does nothing.

## Requirements

- Windows 10 or Windows 11.
- A DSH desktop build that supports plugin bundles (`dsh.bundle`).
- .NET Framework 4.x (included with Windows 10/11), because the helper is a small .NET executable.

## Install

### From the plugin market

Once the plugin is listed, open **DSH → Plugins → Plugin Market**, search for **DSH Taskbar Badge**, and install it. Restart Harness fully after installation.

### From GitHub

In **DSH → Plugins → Add plugin**, enter:

```text
HUHUhim/dsh-taskbar-badge
```

Then fully quit and restart Harness. The bundle layer is assembled at startup, so a page refresh is not enough.

After the restart, the taskbar icon should get a blue number badge when another conversation completes. Opening that conversation clears its unread count.

## How it works

The package has two halves:

1. **Client half** (`client.js`) is a renderless DSH client plugin. It watches the session store and writes a `[N] ` prefix into the document title when there are `N` completed, unread sessions. Nothing is shown inside the app UI.
2. **Host half** (`index.js`, inserted by `cordis.patch.yml`) starts the bundled `bin/dsh-taskbar-badge.exe` when Harness starts.

The helper reads the `[N]` prefix from the DSH window title. If the prefix is absent it falls back to counting completed rows in the sidebar through Windows UI Automation. Every 800 ms it calls `ITaskbarList3.SetOverlayIcon` with a generated 24×24 badge, so minimized windows and collapsed sidebars do not matter.

For window bounds, the helper uses `GetWindowPlacement` / `SetWindowPlacement`, saves the normal rectangle to `%LOCALAPPDATA%\DSH\window-bounds.txt`, and clamps it to the monitor work area on restore.

## Privacy and security

- No network access and no telemetry.
- Reads only the DSH window title and, as a fallback, DSH sidebar status rows through Windows UI Automation.
- Writes local diagnostics and state under `%LOCALAPPDATA%\DSH\`:
  - `taskbar-badge.log`
  - `taskbar-badge-bridge.log`
  - `window-bounds.txt`
- The helper is an unsigned .NET executable. Windows SmartScreen may warn the first time; the full C# source is in `bin/dsh-taskbar-badge.cs` and can be rebuilt locally.

## Build the helper from source

The release binary is committed at `bin/dsh-taskbar-badge.exe`. To rebuild it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-helper.ps1
```

The script uses the .NET Framework 4.x `csc.exe` and the WPF UI Automation reference assemblies that ship with Windows. It writes `bin/dsh-taskbar-badge.exe` in place.

## Uninstall

1. Remove **dsh-taskbar-badge** from the DSH plugin list.
2. Quit Harness, then end any remaining `dsh-taskbar-badge.exe` process.
3. Optionally delete `%LOCALAPPDATA%\DSH\taskbar-badge.log`, `taskbar-badge-bridge.log`, and `window-bounds.txt`.

No Windows startup entry is created, so there is nothing to remove from `shell:startup`.

## License

MIT. See [LICENSE](LICENSE).
