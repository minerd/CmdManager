# Security Policy

CmdManager is a Windows utility that, by design, runs **elevated** and
interacts with other processes on the same machine. Before running it, be aware
that it:

- requests `requireAdministrator` (a UAC prompt on every launch);
- reads another process's current working directory out of its PEB via
  `NtQueryInformationProcess` + `ReadProcessMemory`;
- attaches to other consoles (`AttachConsole`) to read their screen buffers; and
- injects keystrokes into other consoles via `WriteConsoleInput` (with a
  clipboard-paste fallback for long text).

It runs entirely locally, makes **no network connections**, and stores nothing
outside `%APPDATA%\CmdManager\`. The full source is in this repository — please
audit it before running an elevated binary you did not build yourself.

## Reporting a vulnerability

Please report security issues privately using GitHub's **"Report a vulnerability"**
button under the repository's **Security → Advisories** tab, rather than opening
a public issue. This is a personal project, so responses may be slow, but
genuine reports are appreciated.
