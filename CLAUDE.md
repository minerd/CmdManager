# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CmdManager is a WPF (.NET 8, **x64-only**) utility that monitors and remote-controls open `cmd.exe` windows on this machine. It lists cmd windows working on the E: or F: drives, shows a live colored preview of the selected console, indicates busy/idle state, sends commands to consoles without stealing focus, and keeps history/favorites of working directories. It is built around an external `cc` command assumed to be on PATH (the user's Claude Code launcher) — several actions open a cmd and run `cc` in it. All UI text is Turkish.

## Build & Run

- Build: `dotnet build -c Release`
- Run: launch `bin\Release\net8.0-windows\win-x64\CmdManager.exe`. The manifest declares `requireAdministrator` (needed for `ReadProcessMemory`/`AttachConsole` on other processes), so every launch triggers UAC.
- Publish single-file to `publish/`: `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- Regenerate the icon: `powershell -File make_icon.ps1` (draws the `>_` glyph programmatically and writes `app.ico`)
- There are no tests and no linter config.
- **Gotcha:** `App.xaml` sets `ShutdownMode="OnExplicitShutdown"` and nothing ever calls `Application.Shutdown()`, so closing the main window leaves the process running. A surviving instance locks the exe and makes the next build fail with a file-in-use error — kill `CmdManager` from Task Manager before rebuilding.

## Architecture

All logic lives in two files:

- `Native.cs` — every Win32 P/Invoke, exposed as static methods on `Native` (enumerate cmd windows, read another process's working directory, read/write another process's console, global hotkey, focus/keyboard helpers).
- `MainWindow.xaml.cs` — all UI state and behavior: the `CmdItem`/`HistoryItem`/`FavoriteItem` models, the polling timers, persistence, and every event handler. `InputDialog` (a code-built prompt window) is at the bottom of the same file.

### Cross-process console access (the core mechanism)

Every operation on another cmd's console (`ReadScreenBuffer`, `ReadScreenCells`, `SendText`) follows the pattern: `FreeConsole()` → `AttachConsole(pid)` → open `CONOUT$`/`CONIN$` → operate → `FreeConsole()`. Console attachment is **process-wide state**, so these calls must not overlap; everything currently runs on the UI thread via `DispatcherTimer`. Do not move `Native` calls to background threads without serializing them.

### Working-directory discovery

`Native.GetWorkingDirectory` reads the target process's PEB via `NtQueryInformationProcess` + `ReadProcessMemory` using **hardcoded x64 offsets** (ProcessParameters at PEB+0x20, CurrentDirectory UNICODE_STRING at ProcessParameters+0x38). This is why the csproj pins `Platforms`/`PlatformTarget` to x64 — the offsets are wrong on x86.

### Drive filter

Only cmds whose current directory starts with a drive in `AllowedDrives` (`E:`, `F:` — `MainWindow.xaml.cs`) appear in the list or get recorded to history. All other cmd windows are deliberately invisible to the app.

### Polling loops

- `_timer` (1.5 s): re-enumerates cmd processes, detects closed ones (their last-known directory is pushed to history), refreshes the preview. `_lastKnownByPid` preserves the previous directory/hwnd when a read transiently fails, so closed-cmd history stays accurate.
- `_busyTimer` (400 ms): hashes each cmd's screen text; unchanged for >700 ms ⇒ `IsIdle = true` (teal dashed border in the list, red while busy). If the command was sent from this app (`MarkSent`), a system beep plays when output settles — a completion notification.

### Sending input — two deliberate paths

- **Silent** (`Native.SendText`): writes KEY_EVENT records directly to the target's `CONIN$` via `WriteConsoleInputW` (default two trailing Enters, callers can pass `enters: 1`). No focus change. Turkish/Unicode works **only because** `KEY_EVENT_RECORD`/`INPUT_RECORD`/`CHAR_INFO` carry `CharSet = CharSet.Unicode` — without it the marshaler silently degrades `char` to one ANSI byte (ı→'1', ş→'_' in the preview; ş→'þ' on send). Don't remove those attributes.
- **Paste** (`SendViaPaste`): saves the clipboard, copies the text, focuses the cmd window, simulates Ctrl+V + Enter with `keybd_event`, then restores the clipboard and refocuses CmdManager. Kept as fallback for very long text; steals focus briefly.

### Preview rendering

`Native.ReadScreenCells` reads the visible console region row-by-row as `CHAR_INFO` (chars + color attributes; wide-char trailing cells are skipped). `RenderColoredPreview` converts rows into `FlowDocument` paragraphs with runs colored by the Campbell palette (`ConsoleColors`). A content hash skips re-rendering when nothing changed, and the scroll position sticks to the bottom only if the user was already there.

### Auto-resume on Claude Code usage limits

When a cmd's Claude Code session hits a usage limit ("5-hour limit reached · resets 3pm", "You've hit your limit …", "You're out of extra usage …"), the app parses the reset time from the screen and automatically sends a resume command when it arrives. All of it lives in `MainWindow.xaml.cs` (`CheckLimit` + parse helpers + `LimitState`) and runs on every 3rd busy tick (~1.2 s), wrapped in try/catch so screen content can never kill the timer.

Design decisions that look odd but are deliberate (each one fixes a verified failure mode):

- Detection reads a **cursor-anchored** region (`Native.ReadBufferTail`, rows ending at `dwCursorPosition.Y`), not the scrollable viewport — user scrolling must not drop an armed schedule or surface stale notices.
- The notice must be a single **non-blank line matching `LimitRx`**, which is anchored to line start after stripping TUI decoration (`NoticeDecor`); lines containing `` ` ``, `"` or `//` are skipped — this repo's own regex comments appear verbatim in cc diffs, and Claude echoes limit messages in backtick spans when asked about them. The "Approaching 5-hour limit" warning deliberately does not match.
- The reset time is parsed **only from the notice line (+ next line for wraps)** — input-box drafts like "git reset 5" must not hijack the schedule.
- Arming requires the same parsed text on **2 consecutive scans with the screen stable >2 s**.
- Absolute times ("resets 3pm") schedule the next occurrence +90 s grace; if that time-of-day passed within the last 6 h and no attempt was made, the limit is assumed already reset and it fires in 15 s. Dated notices ("resets Apr 10 at 9am") pick the year by proximity; any past date fires now.
- Before sending: postpone 30 s if that cmd is the foreground window (user typing); cancel if the cmd has no live child process (`Native.HasLiveChild` — claude exited, never run the message as a shell command; **conhost/openconsole don't count** since Windows parents them to every console app, and children born before the cmd are ignored to defeat PID reuse); bare prompt `E:\…>` as last line also drops the state.
- The send uses `SendText(…, enters: 1)` — a single Enter so a pending CC dialog can't be double-confirmed. 3-minute cooldown between attempts, max 6 per episode; state survives redraws during cooldown. After a successful send the notice's key is remembered (`SentKey`): the same line sitting in the transcript is inert and can only re-arm after leaving the screen once (a genuinely re-shown notice usually carries a new reset time anyway).

Settings (`%APPDATA%\CmdManager\settings.json`): `AutoResumeEnabled` (toolbar checkbox) and `ResumeMessage` (default `devam et`, edited via the ✏ button). Waiting cmds get an orange dashed border + countdown line in the list.

### Persistence & misc

- History, favorites and settings are JSON files under `%APPDATA%\CmdManager\` (max 100 history entries).
- Global hotkey **Ctrl+Alt+M** toggles the window (`RegisterHotKey` + `WM_HOTKEY` in `HwndHook`).
- New cmds always start with `/K chcp 65001` (UTF-8); the "open + cc" variants append `&& cd /d "<path>" && cc`.
