# CmdManager

A Windows WPF utility for managing `cmd.exe` windows that run [Claude Code](https://claude.com/claude-code) sessions. It watches every open cmd window on selected drives, shows a live colored preview of the selected console, and lets you drive sessions without ever touching the windows themselves.

![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet) ![Windows](https://img.shields.io/badge/platform-Windows%20x64-blue)

## Features

- **Live console preview** — reads the target console's screen buffer (colors included) and renders it in the app; no focus stealing.
- **Busy/idle indicators** — each cmd gets a red (working) or teal (idle) border by watching screen-buffer changes; a beep fires when a command you sent finishes.
- **Silent input** — type a command in the app and it's injected straight into the target console via `WriteConsoleInput`; a clipboard-paste fallback handles long/Unicode text.
- **Auto-resume on usage limits** — when Claude Code stops with "5-hour limit reached · resets 3pm" (or weekly / extra-usage variants), the app parses the reset time from the screen and automatically sends a resume command when the limit lifts. Guarded against false positives (code/quotes on screen don't trigger), against typing into a bare shell after Claude exits, and against interrupting you while you're using that window.
- **History & favorites** — directories of closed cmds are kept (double-click to reopen + relaunch `cc`); favorite directories with custom labels.
- **Global hotkey** — `Ctrl+Alt+M` toggles the window from anywhere.

## Build

```
dotnet build -c Release
```

Publish a single-file exe:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Requirements

- Windows 10/11, x64
- .NET 8 Desktop Runtime
- Runs elevated (`requireAdministrator`) — needed to read other processes' working directories and attach to their consoles
- A `cc` command on `PATH` that launches Claude Code (the "open + cc" actions run it)

## Notes

- Only cmd windows whose current directory is on the drives listed in `AllowedDrives` (`MainWindow.xaml.cs`) are shown; adjust to taste.
- Settings, history and favorites live under `%APPDATA%\CmdManager\`.
