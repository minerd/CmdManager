using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CmdManager;

public static class Native
{
    // ---- Window ----
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    const int SW_RESTORE = 9;

    // ---- Process ----
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int pic, ref PROCESS_BASIC_INFORMATION pbi, int size, out int ret);

    // ---- Console ----
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint create, uint flags, IntPtr template);
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1;
    const uint FILE_SHARE_WRITE = 0x2;
    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW")]
    static extern bool ReadConsoleOutputCharacter(IntPtr h, [Out] char[] buffer, uint length, COORD coord, out uint read);

    // ---- Input ----
    // CharSet.Unicode on both structs: with the ANSI default, UnicodeChar marshals as ONE byte
    // through the system codepage, so sending 'ş' typed 'þ' into the console (the original reason
    // the clipboard-paste path existed).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    struct INPUT_RECORD
    {
        public ushort EventType;
        public KEY_EVENT_RECORD KeyEvent;
    }
    const ushort KEY_EVENT = 0x0001;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    static extern bool WriteConsoleInput(IntPtr h, [MarshalAs(UnmanagedType.LPArray), In] INPUT_RECORD[] buffer, uint length, out uint written);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const uint KEYEVENTF_KEYUP = 0x0002;
    const byte VK_CONTROL = 0x11;
    const byte VK_V = 0x56;
    const byte VK_RETURN = 0x0D;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const int WM_HOTKEY = 0x0312;
    public static bool RegisterGlobalHotKey(IntPtr hWnd, int id, uint mods, uint vk) => RegisterHotKey(hWnd, id, mods, vk);
    public static bool UnregisterGlobalHotKey(IntPtr hWnd, int id) => UnregisterHotKey(hWnd, id);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputW")]
    static extern bool ReadConsoleOutput(IntPtr h, [Out] CHAR_INFO[] buffer, COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpReadRegion);

    // CharSet.Unicode is load-bearing: without it the marshaler treats `char` as ANSI, reads only
    // the LOW BYTE of the WCHAR and runs it through CP1254 — ı became '1', ş became '_', ● became 'Ï'.
    [StructLayout(LayoutKind.Explicit, Size = 4, CharSet = CharSet.Unicode)]
    struct CHAR_INFO
    {
        [FieldOffset(0)] public char UnicodeChar;
        [FieldOffset(2)] public ushort Attributes;
    }

    public class Cell
    {
        public char Ch;
        public byte Fg;
        public byte Bg;
    }

    // ---- Public API ----
    public static List<MainWindow.CmdItem> EnumerateCmdWindows(Dictionary<int, IntPtr> hwndCache)
    {
        var list = new List<MainWindow.CmdItem>();
        foreach (var p in Process.GetProcessesByName("cmd"))
        {
            IntPtr hwnd;
            if (!hwndCache.TryGetValue(p.Id, out hwnd))
            {
                hwnd = ResolveCmdWindow(p.Id);
                if (hwnd != IntPtr.Zero) hwndCache[p.Id] = hwnd;
            }
            if (hwnd == IntPtr.Zero) continue;

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrEmpty(title)) title = "cmd.exe";

            list.Add(new MainWindow.CmdItem
            {
                Pid = p.Id,
                WindowHandle = hwnd,
                Title = title,
                CurrentDirectory = GetWorkingDirectory(p.Id) ?? ""
            });
        }
        return list;
    }

    static IntPtr ResolveCmdWindow(int pid)
    {
        FreeConsole();
        IntPtr hwnd = IntPtr.Zero;
        if (AttachConsole((uint)pid))
        {
            hwnd = GetConsoleWindow();
            FreeConsole();
        }
        return hwnd;
    }

    static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? GetWorkingDirectory(int pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            if (!ReadPtr(hProcess, pbi.PebBaseAddress + 0x20, out IntPtr procParams)) return null;
            if (procParams == IntPtr.Zero) return null;

            // UNICODE_STRING at +0x38: USHORT Length, USHORT MaxLen, PAD, PVOID Buffer (at +0x40)
            byte[] us = new byte[16];
            if (!ReadProcessMemory(hProcess, procParams + 0x38, us, us.Length, out _)) return null;
            ushort length = BitConverter.ToUInt16(us, 0);
            IntPtr buffer = (IntPtr)BitConverter.ToInt64(us, 8);
            if (length == 0 || buffer == IntPtr.Zero) return null;
            if (length > 1024) length = 1024;

            byte[] raw = new byte[length];
            if (!ReadProcessMemory(hProcess, buffer, raw, raw.Length, out _)) return null;
            var s = Encoding.Unicode.GetString(raw).TrimEnd('\0');
            return s.TrimEnd('\\');
        }
        catch { return null; }
        finally { CloseHandle(hProcess); }
    }

    static bool ReadPtr(IntPtr h, IntPtr addr, out IntPtr value)
    {
        byte[] buf = new byte[8];
        value = IntPtr.Zero;
        if (!ReadProcessMemory(h, addr, buf, buf.Length, out _)) return false;
        value = (IntPtr)BitConverter.ToInt64(buf, 0);
        return true;
    }

    public static string? ReadScreenBuffer(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return null;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE) return null;
            if (!GetConsoleScreenBufferInfo(h, out var csbi)) return null;

            short top = csbi.srWindow.Top;
            short bottom = csbi.srWindow.Bottom;
            short left = csbi.srWindow.Left;
            short width = (short)(csbi.srWindow.Right - left + 1);
            if (width <= 0) return "";

            var sb = new StringBuilder(width * (bottom - top + 2));
            char[] line = new char[width];
            for (short y = top; y <= bottom; y++)
            {
                if (!ReadConsoleOutputCharacter(h, line, (uint)width, new COORD { X = left, Y = y }, out uint read))
                    break;
                sb.AppendLine(new string(line, 0, (int)read).TrimEnd());
            }
            return sb.ToString();
        }
        catch { return null; }
        finally
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE) CloseHandle(h);
            FreeConsole();
        }
    }

    public static bool SendText(int pid, string text, int enters = 2)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return false;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE) return false;

            var records = new List<INPUT_RECORD>(text.Length * 2 + 4);
            foreach (char c in text + new string('\r', Math.Max(1, enters)))
            {
                // Only use VK for true control characters; for printable/Unicode rely on UnicodeChar alone.
                ushort keyCode = c switch
                {
                    '\r' => (ushort)0x0D,
                    '\n' => (ushort)0x0D,
                    '\t' => (ushort)0x09,
                    '\b' => (ushort)0x08,
                    _ => (ushort)0
                };
                var down = new INPUT_RECORD
                {
                    EventType = KEY_EVENT,
                    KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = keyCode, UnicodeChar = c }
                };
                var up = new INPUT_RECORD
                {
                    EventType = KEY_EVENT,
                    KeyEvent = new KEY_EVENT_RECORD { bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = keyCode, UnicodeChar = c }
                };
                records.Add(down);
                records.Add(up);
            }
            return WriteConsoleInput(h, records.ToArray(), (uint)records.Count, out _);
        }
        catch { return false; }
        finally
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE) CloseHandle(h);
            FreeConsole();
        }
    }

    public static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
    }

    public static bool IsForegroundWindow(IntPtr hWnd)
        => hWnd != IntPtr.Zero && GetForegroundWindow() == hWnd;

    // Reads the last `lines` rows ending at the CURSOR row — unlike ReadScreenBuffer this is
    // anchored to where output actually happens, so user scrolling can't move the region.
    public static string? ReadBufferTail(int pid, int lines)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return null;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE) return null;
            if (!GetConsoleScreenBufferInfo(h, out var csbi)) return null;

            short width = csbi.dwSize.X;
            short bottom = csbi.dwCursorPosition.Y;
            short top = (short)Math.Max(0, bottom - lines + 1);
            if (width <= 0) return "";

            var sb = new StringBuilder(width * (bottom - top + 2));
            char[] line = new char[width];
            for (short y = top; y <= bottom; y++)
            {
                if (!ReadConsoleOutputCharacter(h, line, (uint)width, new COORD { X = 0, Y = y }, out uint read))
                    break;
                sb.AppendLine(new string(line, 0, (int)read).TrimEnd());
            }
            return sb.ToString();
        }
        catch { return null; }
        finally
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE) CloseHandle(h);
            FreeConsole();
        }
    }

    public static int GetParentPid(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0) return -1;
            return pbi.Reserved3.ToInt32(); // InheritedFromUniqueProcessId
        }
        catch { return -1; }
        finally { CloseHandle(h); }
    }

    // Does this cmd still host anything (claude/node)? A bare cmd has no children —
    // except conhost.exe, which Windows parents to every console app, so it doesn't count.
    public static bool HasLiveChild(int pid)
    {
        DateTime parentStart;
        try { using var parent = Process.GetProcessById(pid); parentStart = parent.StartTime; }
        catch { return false; }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == pid) continue;
                string name = p.ProcessName;
                if (name.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("openconsole", StringComparison.OrdinalIgnoreCase)) continue;
                if (GetParentPid(p.Id) != pid) continue;
                // InheritedFromUniqueProcessId is never updated: a child "born" before this cmd
                // means the creator's PID was recycled — not actually our child.
                if (p.StartTime < parentStart) continue;
                return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    public static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void SendEnter()
    {
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static List<List<Cell>>? ReadScreenCells(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return null;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE) return null;
            if (!GetConsoleScreenBufferInfo(h, out var csbi)) return null;

            short left = csbi.srWindow.Left;
            short top = csbi.srWindow.Top;
            short right = csbi.srWindow.Right;
            short bottom = csbi.srWindow.Bottom;
            short width = (short)(right - left + 1);
            short height = (short)(bottom - top + 1);
            if (width <= 0 || height <= 0) return new List<List<Cell>>();

            // ReadConsoleOutput has size limit (~8K cells). Read row-by-row for safety.
            var result = new List<List<Cell>>(height);
            for (short y = top; y <= bottom; y++)
            {
                var buf = new CHAR_INFO[width];
                var region = new SMALL_RECT { Left = left, Top = y, Right = right, Bottom = y };
                if (!ReadConsoleOutput(h, buf, new COORD { X = width, Y = 1 }, new COORD { X = 0, Y = 0 }, ref region))
                    continue;

                var line = new List<Cell>(width);
                int lastNonSpace = -1;
                for (int i = 0; i < width; i++)
                {
                    // Skip trailing byte of a wide (CJK/emoji) char to avoid duplicates
                    if ((buf[i].Attributes & 0x200) != 0) continue;
                    char c = buf[i].UnicodeChar == '\0' ? ' ' : buf[i].UnicodeChar;
                    var cell = new Cell
                    {
                        Ch = c,
                        Fg = (byte)(buf[i].Attributes & 0x0F),
                        Bg = (byte)((buf[i].Attributes >> 4) & 0x0F)
                    };
                    line.Add(cell);
                    if (c != ' ') lastNonSpace = line.Count - 1;
                }
                if (lastNonSpace >= 0 && lastNonSpace < line.Count - 1)
                    line.RemoveRange(lastNonSpace + 1, line.Count - lastNonSpace - 1);
                else if (lastNonSpace < 0)
                    line.Clear();
                result.Add(line);
            }
            return result;
        }
        catch { return null; }
        finally
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE) CloseHandle(h);
            FreeConsole();
        }
    }
}
