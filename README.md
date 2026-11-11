# Vajra Shelf

Vajra Shelf is a private, local drag-and-drop shelf for Windows. It keeps frequently used files, folders, links, text, colors, and clipboard images close at hand without uploading or monitoring anything.

## Use

- Drag supported content onto the shelf to add a reference.
- Use **Add clipboard** from the shelf or tray only when you want the current clipboard content added.
- Pin items that should survive an app restart. Unpinned items are temporary and remain session-only.
- Open **Settings** from the tray to choose appearance, placement, motion, expiry, and startup preferences. The default position sits below Vajra Island so both utilities can run together.
- Use the tray's **Clear temporary** command to remove every unpinned shelf entry.

Only one Vajra Shelf instance runs per Windows session. Closing or hiding the shelf does not require background clipboard monitoring; use **Exit** from the tray to stop it.

## Hotkeys

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+V` | Show or toggle Vajra Shelf |
| `Ctrl+Alt+P` | Add the current clipboard content |

Hotkeys use Windows' no-repeat flag. If another application already owns one shortcut, Vajra Shelf continues running and leaves only that shortcut unavailable.

## Themes

Settings support **Dark**, **Light**, and translucent **Glass** themes, a custom accent color, display scaling, reduced motion, and per-monitor placement. The shelf applies appearance changes locally; no theme or usage information is transmitted.

## Privacy and file safety

Vajra Shelf is local-only:

- It has no telemetry, account, cloud service, or network requirement.
- It reads the clipboard only after an explicit button, tray command, or hotkey action. It does not watch clipboard changes.
- Dropped files and folders are stored as references. Adding, removing, expiring, unpinning, or clearing an item **never deletes or modifies the source file or folder**.
- Clipboard images may be copied into Vajra Shelf's private cache under `%LOCALAPPDATA%\VajraShelf\cache`. Cleanup is restricted to cache-owned files inside that directory.
- Dropping or pasting content never opens it. Opening potentially dangerous file types requires confirmation.
- Pinned metadata and settings stay under `%LOCALAPPDATA%\VajraShelf`.

## Build locally

Requirements:

- Windows 10 or Windows 11
- An already installed .NET 8 SDK with the Windows Desktop targeting pack

Run:

```bat
build.cmd
```

The script restores only from an empty local NuGet source and installed SDK packs, then creates a framework-dependent Release build in `dist`. It does not download dependencies and does not request administrator access.

Launch the built app with:

```bat
launch.cmd
```

Create a distributable ZIP with:

```bat
package-release.cmd
```

The package is written to `release\VajraShelf-framework-dependent.zip`. The destination computer needs the compatible .NET 8 Windows Desktop Runtime installed. Scripts do not install runtimes, register startup tasks, or change system settings.
