using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CmdManager;

// Lightweight runtime localization. XAML strings resolve via DynamicResource keys that
// MainWindow.ApplyLanguage() rebuilds for the active language; code-behind strings use
// Loc.T(en, tr). Default language is English; Turkish is available from the 🌐 button.
static class Loc
{
    public static string Lang = "en";   // "en" or "tr"
    public static string T(string en, string tr) => Lang == "tr" ? tr : en;
}

public partial class MainWindow : Window
{
    public class CmdItem : INotifyPropertyChanged
    {
        public int Pid { get; set; }
        public IntPtr WindowHandle { get; set; }
        public string Title { get; set; } = "";
        public string CurrentDirectory { get; set; } = "";

        bool _isIdle;
        public bool IsIdle
        {
            get => _isIdle;
            set
            {
                if (_isIdle == value) return;
                _isIdle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIdle)));
            }
        }

        bool _isLimitWaiting;
        public bool IsLimitWaiting
        {
            get => _isLimitWaiting;
            set
            {
                if (_isLimitWaiting == value) return;
                _isLimitWaiting = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLimitWaiting)));
            }
        }

        string _resumeInfo = "";
        public string ResumeInfo
        {
            get => _resumeInfo;
            set
            {
                if (_resumeInfo == value) return;
                _resumeInfo = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResumeInfo)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class HistoryItem : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public DateTime ClosedAt { get; set; }

        [JsonIgnore]
        public string ClosedAgo => Humanize(DateTime.Now - ClosedAt);

        public event PropertyChangedEventHandler? PropertyChanged;
        public void RefreshAgo() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClosedAgo)));

        static string Humanize(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return Loc.T("just now", "az önce");
            if (ts.TotalMinutes < 60) return Loc.T($"{(int)ts.TotalMinutes} min ago", $"{(int)ts.TotalMinutes} dk önce");
            if (ts.TotalHours < 24) return Loc.T($"{(int)ts.TotalHours} h ago", $"{(int)ts.TotalHours} sa önce");
            return Loc.T($"{(int)ts.TotalDays} d ago", $"{(int)ts.TotalDays} gün önce");
        }
    }

    public class FavoriteItem : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public string? Label { get; set; }

        [JsonIgnore]
        public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Path : Label!;

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    static readonly string[] AllowedDrives = { "E:", "F:" };
    static bool IsRelevant(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var d in AllowedDrives)
            if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Campbell-ish palette (modern conhost)
    static readonly Color[] ConsoleColors =
    {
        Color.FromRgb(12, 12, 12),      // 0 Black
        Color.FromRgb(0, 55, 218),      // 1 DarkBlue
        Color.FromRgb(19, 161, 14),     // 2 DarkGreen
        Color.FromRgb(58, 150, 221),    // 3 DarkCyan
        Color.FromRgb(197, 15, 31),     // 4 DarkRed
        Color.FromRgb(136, 23, 152),    // 5 DarkMagenta
        Color.FromRgb(193, 156, 0),     // 6 DarkYellow
        Color.FromRgb(204, 204, 204),   // 7 Gray
        Color.FromRgb(118, 118, 118),   // 8 DarkGray
        Color.FromRgb(59, 120, 255),    // 9 Blue
        Color.FromRgb(22, 198, 12),     // 10 Green
        Color.FromRgb(97, 214, 214),    // 11 Cyan
        Color.FromRgb(231, 72, 86),     // 12 Red
        Color.FromRgb(180, 0, 158),     // 13 Magenta
        Color.FromRgb(249, 241, 165),   // 14 Yellow
        Color.FromRgb(242, 242, 242),   // 15 White
    };
    static readonly Brush[] FgBrushes = ConsoleColors.Select(c => { var b = new SolidColorBrush(c); b.Freeze(); return (Brush)b; }).ToArray();
    static readonly Brush[] BgBrushes = ConsoleColors.Select(c => { var b = new SolidColorBrush(c); b.Freeze(); return (Brush)b; }).ToArray();

    readonly ObservableCollection<CmdItem> _openItems = new();
    readonly ObservableCollection<HistoryItem> _historyItems = new();
    readonly ObservableCollection<FavoriteItem> _favoriteItems = new();
    readonly Dictionary<int, CmdItem> _lastKnownByPid = new();
    readonly Dictionary<int, IntPtr> _hwndCache = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Dictionary<int, BusyState> _busyCmds = new();
    readonly string _historyPath;
    readonly string _favoritesPath;
    readonly List<string> _commandHistory = new();
    int _commandHistoryIndex = -1;
    int _lastPreviewHash;

    class BusyState
    {
        public int LastHash;
        public DateTime LastChange;
        public DateTime Started;
        public bool EverChanged;
        public bool SentCommand;
    }

    // ---------------- Auto-resume (Claude Code limit reached) ----------------
    class LimitState
    {
        public DateTime? ResumeAt;       // when to send the resume command (null = not scheduled)
        public string? ParsedKey;        // raw matched text; reparse only when this changes
        public string? SentKey;          // key already resumed — inert until the notice leaves the screen once
        public string? PendingKey;       // candidate awaiting confirmation over consecutive checks
        public int PendingCount;
        public int Misses;               // consecutive checks without the limit message on screen
        public int Attempts;             // resume commands sent for this limit episode
        public DateTime CooldownUntil;   // ignore detection until this time after a send
    }

    class AppSettings
    {
        public bool AutoResumeEnabled { get; set; } = true;
        public string ResumeMessage { get; set; } = "continue";
        public string Language { get; set; } = "en";   // "en" (default) or "tr"
    }

    // Hard-stop notices, anchored to LINE START (after stripping TUI decoration) so prose that
    // merely mentions a limit can't match: "5-hour limit reached ∙ resets 3pm",
    // "Session limit reached ∙ resets 6pm", "Claude usage limit reached. Your limit will reset
    // at 3pm (America/Santiago).", "You've hit your limit · resets 7pm (Europe/Berlin)",
    // "You're out of extra usage · resets 10am". The bare "limit reached" alternative keeps
    // narrow-console wraps working. Deliberately does NOT match "Approaching 5-hour limit".
    static readonly Regex LimitRx = new(
        @"(?i)^(?:(?:\d+-hour|session|weekly|monthly|daily)\s+limit reached|claude(?:\s+\w+)? usage limit reached|usage limit reached|limit reached|you'?ve (?:hit|reached) your (?:usage |session |weekly )?limit|you'?re out of extra usage)",
        RegexOptions.Compiled);
    // Leading chrome Claude Code may draw before the notice text (box borders, bullets, ✗).
    // '>' is intentionally NOT here: a draft typed into the "│ >" input box must keep its prefix.
    static readonly char[] NoticeDecor = { ' ', '\t', '│', '┃', '|', '✗', '×', '∙', '·', '⎿', '⏺', '─' };
    // "resets Apr 10 at 9am" — dated reset (weekly / extra-usage limits)
    static readonly Regex ResetAtDateRx = new(
        @"(?i)resets?\s+(?:on\s+)?([A-Za-z]{3,9})\.?\s+(\d{1,2})\s+at\s+(\d{1,2})(?::(\d{2}))?\s*([ap]m)?\b",
        RegexOptions.Compiled);
    // "resets 3am" / "resets at 3:30pm" / "resets 2:00 PM" / "will reset at 12am"
    static readonly Regex ResetAtRx = new(
        @"(?i)(?:will\s+)?resets?\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*([ap]m)?\b",
        RegexOptions.Compiled);
    // "resets in 2h 59m" / "resets in 3 hours" (fallback, not seen in current CC versions)
    static readonly Regex ResetInRx = new(
        @"(?i)resets?\s+in\s+(?:(\d+)\s*h(?:ours?|rs?)?)?\s*(?:(\d+)\s*m(?:in(?:utes?)?)?)?",
        RegexOptions.Compiled);
    // "E:\cmd>" alone on the last line = claude exited, back at the shell
    static readonly Regex BarePromptRx = new(@"^[A-Za-z]:\\[^<>|]*>\s*$", RegexOptions.Compiled);

    readonly Dictionary<int, LimitState> _limitStates = new();
    readonly string _settingsPath;
    AppSettings _settings = new();
    bool _settingsLoaded;
    ResourceDictionary? _langDict;
    int _limitTickCounter;
    // Hotkey
    const int HOTKEY_ID = 0xBEEF;
    IntPtr _windowHandle;
    HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        OpenList.ItemsSource = _openItems;
        HistoryList.ItemsSource = _historyItems;
        FavoritesList.ItemsSource = _favoriteItems;

        OpenList.PreviewMouseRightButtonDown += List_RightClick;
        HistoryList.PreviewMouseRightButtonDown += List_RightClick;
        FavoritesList.PreviewMouseRightButtonDown += List_RightClick;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CmdManager");
        Directory.CreateDirectory(dir);
        _historyPath = Path.Combine(dir, "history.json");
        _favoritesPath = Path.Combine(dir, "favorites.json");
        _settingsPath = Path.Combine(dir, "settings.json");

        PreviewBox.Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)),
            FontFamily = PreviewBox.FontFamily,
            FontSize = PreviewBox.FontSize
        };

        LoadHistory();
        LoadFavorites();
        LoadSettings();
        Loc.Lang = _settings.Language == "tr" ? "tr" : "en";
        AutoResumeCheck.IsChecked = _settings.AutoResumeEnabled;
        _settingsLoaded = true;
        ApplyLanguage();

        _timer.Tick += OnTimerTick;
        _busyTimer.Tick += OnBusyTick;
        Loaded += (_, _) => { Refresh(); _timer.Start(); _busyTimer.Start(); };
        Closing += OnClosing;
    }

    // ---------------- Global hotkey ----------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(HwndHook);
        // Ctrl+Alt+M
        Native.RegisterGlobalHotKey(_windowHandle, HOTKEY_ID, Native.MOD_CONTROL | Native.MOD_ALT, 0x4D);
    }

    IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    void ToggleWindow()
    {
        if (WindowState == WindowState.Minimized || !IsActive)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        else
        {
            WindowState = WindowState.Minimized;
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _busyTimer.Stop();
        try { Native.UnregisterGlobalHotKey(_windowHandle, HOTKEY_ID); } catch { }
        _hwndSource?.RemoveHook(HwndHook);
        SaveHistory();
        SaveFavorites();
    }

    // ---------------- Refresh / Preview ----------------
    void OnTimerTick(object? sender, EventArgs e)
    {
        Refresh();
        UpdatePreview();
        foreach (var h in _historyItems) h.RefreshAgo();
        RefreshResumeCountdowns();
    }

    void OnBusyTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var pids = new HashSet<int>(_openItems.Select(o => o.Pid));
        foreach (var k in _busyCmds.Keys.ToList())
            if (!pids.Contains(k)) _busyCmds.Remove(k);
        foreach (var k in _limitStates.Keys.ToList())
            if (!pids.Contains(k)) _limitStates.Remove(k);

        foreach (var item in _openItems)
        {
            var buf = Native.ReadScreenBuffer(item.Pid);
            if (buf == null) continue;
            int hash = buf.GetHashCode();

            if (!_busyCmds.TryGetValue(item.Pid, out var state))
            {
                state = new BusyState { LastHash = hash, LastChange = now, Started = now };
                _busyCmds[item.Pid] = state;
                continue;
            }

            if (hash != state.LastHash)
            {
                state.LastHash = hash;
                state.LastChange = now;
                state.EverChanged = true;
                item.IsIdle = false;
            }
            else if ((now - state.LastChange).TotalMilliseconds > 700)
            {
                bool wasIdle = item.IsIdle;
                item.IsIdle = true;
                if (!wasIdle && state.SentCommand && state.EverChanged)
                {
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                    state.SentCommand = false;
                }
            }
        }

        // Limit scan on every 3rd tick (~1.2s): it needs its own console-attach round per cmd,
        // and a screen-content exception must never kill the timer.
        if (++_limitTickCounter % 3 == 0)
        {
            foreach (var item in _openItems)
            {
                try { CheckLimit(item, now); } catch { }
            }
        }
    }

    void MarkSent(int pid)
    {
        var now = DateTime.Now;
        var buf = Native.ReadScreenBuffer(pid);
        int hash = buf?.GetHashCode() ?? 0;
        if (!_busyCmds.TryGetValue(pid, out var state))
        {
            state = new BusyState();
            _busyCmds[pid] = state;
        }
        state.LastHash = hash;
        state.LastChange = now;
        state.Started = now;
        state.EverChanged = false;
        state.SentCommand = true;
        var item = _openItems.FirstOrDefault(o => o.Pid == pid);
        if (item != null) item.IsIdle = false;
    }

    // ---------------- Auto-resume on Claude Code limit ----------------
    const int MaxResumeAttempts = 6;
    const int LimitTailLines = 20;

    void CheckLimit(CmdItem item, DateTime now)
    {
        // Cursor-anchored read: immune to user scrolling, and finds the notice regardless of
        // where Claude Code's UI sits in the window.
        var tailRaw = Native.ReadBufferTail(item.Pid, LimitTailLines);
        if (tailRaw == null) return;
        var lines = tailRaw.Split('\n')
            .Select(l => l.TrimEnd('\r', ' '))
            .Where(l => l.Length > 0)
            .ToList();

        _limitStates.TryGetValue(item.Pid, out var st);

        // Bottom-most line carrying a hard-stop phrase. Lines that look like source code or a
        // quotation don't count — this repo's own regex comments appear verbatim in cc diffs,
        // and Claude often echoes limit messages in backtick code spans when asked about them.
        int idx = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string s = lines[i].TrimStart(NoticeDecor);
            if (!LimitRx.IsMatch(s)) continue;
            if (s.Contains('"') || s.Contains("//") || s.Contains('`')) continue;
            idx = i;
            break;
        }
        // "E:\foo>" as the live line = claude exited; any notice above it is a leftover.
        bool sessionGone = lines.Count > 0 && BarePromptRx.IsMatch(lines[^1]);

        if (idx < 0 || sessionGone)
        {
            // Notice gone (cc resumed / claude exited) — drop after a few clean reads, but never
            // while a post-send cooldown runs (a redraw right after our send must not reset Attempts).
            if (st != null)
            {
                st.SentKey = null; // it really left the screen — a reappearance is a fresh episode
                if (now >= st.CooldownUntil && ++st.Misses >= 5)
                {
                    _limitStates.Remove(item.Pid);
                    item.IsLimitWaiting = false;
                    item.ResumeInfo = "";
                }
            }
            return;
        }

        if (st == null)
        {
            st = new LimitState();
            _limitStates[item.Pid] = st;
        }
        st.Misses = 0;
        item.IsLimitWaiting = true;
        if (now < st.CooldownUntil) return;

        // Parse the reset moment from the notice line itself (+ the next line for narrow-console
        // wraps) — never from unrelated screen content like the user's input-box draft.
        string scan = lines[idx] + " " + (idx + 1 < lines.Count ? lines[idx + 1] : "");
        scan = Regex.Replace(scan, @"\s+", " ");

        string? key = null;
        DateTime? target = null;
        var dateMatch = ResetAtDateRx.Match(scan);
        if (dateMatch.Success && TryParseResetDate(dateMatch, now, out var dt))
        {
            key = "date:" + dateMatch.Value;
            target = dt;
        }
        if (target == null)
        {
            var atMatch = ResetAtRx.Match(scan);
            if (atMatch.Success && TryParseResetAt(atMatch, now, st.Attempts, out var at))
            {
                key = "at:" + atMatch.Value;
                target = at;
            }
        }
        if (target == null)
        {
            var inMatch = ResetInRx.Match(scan);
            if (inMatch.Success && (inMatch.Groups[1].Success || inMatch.Groups[2].Success))
            {
                int.TryParse(inMatch.Groups[1].Value, out int h);
                int.TryParse(inMatch.Groups[2].Value, out int m);
                if ((h > 0 || m > 0) && h <= 24 * 8 && m <= 600) // bound BEFORE constructing — ctor throws on overflow
                {
                    key = "in:" + inMatch.Value;
                    target = now + new TimeSpan(h, m, 0) + TimeSpan.FromSeconds(90);
                }
            }
        }

        // Already resumed for this exact notice: the line is just transcript residue, and acting
        // on it again would type into a healthy session. It must leave the screen once to re-arm.
        if (key != null && key == st.SentKey && st.ResumeAt == null)
        {
            item.IsLimitWaiting = false;
            return;
        }

        if (target == null)
        {
            if (st.ResumeAt == null)
                item.ResumeInfo = Loc.T("⏸ limit detected · reset time unreadable", "⏸ limit algılandı · sıfırlanma saati okunamadı");
        }
        else if (st.ParsedKey != key)
        {
            // Arm only after the same notice survived two consecutive scans on a stable screen —
            // output that merely scrolls past with a matching phrase never arms a send.
            bool stable = _busyCmds.TryGetValue(item.Pid, out var bs)
                          && (now - bs.LastChange).TotalMilliseconds > 2000;
            if (st.PendingKey == key && stable) st.PendingCount++;
            else { st.PendingKey = key; st.PendingCount = stable ? 1 : 0; }

            if (st.PendingCount >= 2)
            {
                st.ParsedKey = key;
                st.ResumeAt = target;
                st.PendingKey = null;
                st.PendingCount = 0;
                item.ResumeInfo = FormatResumeInfo((DateTime)target, now);
            }
        }

        if (st.ResumeAt is not DateTime ra || now < ra) return;

        // Time's up — send the resume command.
        if (!_settings.AutoResumeEnabled)
        {
            item.ResumeInfo = Loc.T($"⏸ limit reset ({ra:HH:mm}) · auto-resume off", $"⏸ limit sıfırlandı ({ra:HH:mm}) · oto-devam kapalı");
            return;
        }
        if (st.Attempts >= MaxResumeAttempts)
        {
            st.ResumeAt = null; // ParsedKey stays set so the same notice can't re-arm
            item.ResumeInfo = Loc.T("⏸ auto-resume attempts exhausted · resume manually", "⏸ oto-devam denemeleri tükendi · elle devam et");
            return;
        }
        if (Native.IsForegroundWindow(item.WindowHandle))
        {
            st.ResumeAt = now.AddSeconds(30); // user is in that window right now — don't type over them
            return;
        }
        if (!Native.HasLiveChild(item.Pid))
        {
            st.ResumeAt = null; // bare cmd left behind — never run the message as a shell command
            item.ResumeInfo = Loc.T("⏸ session appears closed · auto-resume canceled", "⏸ oturum kapanmış görünüyor · oto-devam iptal");
            return;
        }

        string msg = string.IsNullOrWhiteSpace(_settings.ResumeMessage) ? "devam et" : _settings.ResumeMessage;
        if (Native.SendText(item.Pid, msg, enters: 1)) // single Enter: must not double-confirm a dialog
        {
            st.Attempts++;
            st.CooldownUntil = now.AddMinutes(3);
            st.SentKey = st.ParsedKey; // this notice is now inert until it disappears once
            st.ResumeAt = null;
            st.ParsedKey = null;
            MarkSent(item.Pid);
            item.ResumeInfo = Loc.T($"▶ resume sent ({now:HH:mm})", $"▶ devam gönderildi ({now:HH:mm})");
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
        else
        {
            st.ResumeAt = now.AddSeconds(60); // send failed, retry shortly
        }
    }

    static bool TryParseResetDate(Match m, DateTime now, out DateTime resumeAt)
    {
        resumeAt = default;
        if (!DateTime.TryParseExact(
                $"{m.Groups[1].Value} {m.Groups[2].Value}",
                new[] { "MMM d", "MMMM d" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;
        if (!TryReadClock(m.Groups[3], m.Groups[4], m.Groups[5], out int h, out int min)) return false;

        // The notice carries no year — pick the candidate closest to now, so a stale "Apr 10"
        // read on Apr 13 means three days AGO (fire now), not next year, and "Dec 31" read on
        // Jan 2 resolves to last week, not eleven months out.
        DateTime best = default;
        double bestDist = double.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            DateTime cand;
            try { cand = new DateTime(now.Year + dy, date.Month, date.Day, h, min, 0); }
            catch { continue; } // Feb 29 in a non-leap candidate year
            double dist = Math.Abs((cand - now).TotalDays);
            if (dist < bestDist) { bestDist = dist; best = cand; }
        }
        if (bestDist == double.MaxValue) return false;

        resumeAt = best <= now ? now.AddSeconds(15) : best.AddSeconds(90);
        return true;
    }

    static bool TryReadClock(Group hourG, Group minG, Group apG, out int h, out int min)
    {
        min = 0;
        if (!int.TryParse(hourG.Value, out h)) return false;
        if (minG.Success && !int.TryParse(minG.Value, out min)) return false;
        string ap = apG.Value.ToLowerInvariant();
        if (ap == "pm" && h != 12) h += 12;
        else if (ap == "am" && h == 12) h = 0;
        return h <= 23 && min <= 59;
    }

    static bool TryParseResetAt(Match m, DateTime now, int attempts, out DateTime resumeAt)
    {
        resumeAt = default;
        if (!TryReadClock(m.Groups[1], m.Groups[2], m.Groups[3], out int h, out int min)) return false;

        var todayAt = now.Date.AddHours(h).AddMinutes(min);
        var next = todayAt <= now ? todayAt.AddDays(1) : todayAt;
        var prev = next.AddDays(-1);

        // Message states a time that passed within the last few hours → the limit already
        // reset (e.g. app started late, or the notice sat on screen past the hour): act now.
        // After a failed attempt assume a longer (weekly) limit and wait for the next occurrence.
        if (attempts == 0 && now - prev <= TimeSpan.FromHours(6))
            resumeAt = now.AddSeconds(15);
        else
            resumeAt = next.AddSeconds(90); // grace: limits reset on the minute, don't fire early

        return true;
    }

    void RefreshResumeCountdowns()
    {
        var now = DateTime.Now;
        foreach (var item in _openItems)
        {
            if (!_limitStates.TryGetValue(item.Pid, out var st) || st.ResumeAt is not DateTime ra)
                continue;
            if (ra <= now) continue; // past due: CheckLimit owns the status text now
            item.ResumeInfo = FormatResumeInfo(ra, now);
        }
    }

    string FormatResumeInfo(DateTime ra, DateTime now)
    {
        var left = ra - now;
        string lt = left.TotalSeconds <= 0 ? Loc.T("now", "şimdi")
            : left.TotalHours >= 1 ? Loc.T($"{(int)left.TotalHours}h {left.Minutes}m", $"{(int)left.TotalHours} sa {left.Minutes} dk")
            : left.TotalMinutes >= 1 ? Loc.T($"{(int)left.TotalMinutes}m", $"{(int)left.TotalMinutes} dk")
            : Loc.T($"{(int)left.TotalSeconds}s", $"{(int)left.TotalSeconds} sn");
        string when = ra.Date != now.Date ? ra.ToString("dd.MM HH:mm") : ra.ToString("HH:mm");
        return _settings.AutoResumeEnabled
            ? Loc.T($"⏸ limit · resume {when} ({lt})", $"⏸ limit · {when} devam ({lt})")
            : Loc.T($"⏸ limit · resets {when} · auto-resume off", $"⏸ limit · {when} sıfırlanır · oto-devam kapalı");
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, opts));
        }
        catch { }
    }

    // ---------------- Localization ----------------
    // key -> (English, Turkish). Consumed by ApplyLanguage as DynamicResource values.
    static readonly Dictionary<string, (string en, string tr)> UiStrings = new()
    {
        ["Refresh"]        = ("↻ Refresh", "↻ Yenile"),
        ["NewCmd"]         = ("+ New CMD", "+ Yeni CMD"),
        ["CloseAll"]       = ("Close All", "Hepsini Kapat"),
        ["AutoResume"]     = ("⏰ Auto-resume when limit resets", "⏰ Limit dolunca oto-devam"),
        ["AutoResumeTip"]  = ("When Claude Code says \"limit reached\", reads the reset time from the screen and automatically sends the resume command when it is time",
                              "Claude Code 'limit reached' deyince sıfırlanma saatini ekrandan okur ve vakti gelince devam komutunu otomatik gönderir"),
        ["EditResumeTip"]  = ("Change the command sent when the limit resets", "Limit sıfırlanınca gönderilecek komutu değiştir"),
        ["Favorites"]      = ("⭐ Favorites", "⭐ Favoriler"),
        ["OpenCmds"]       = ("Open CMDs", "Açık CMD'ler"),
        ["History"]        = ("History (double-click → cd + cc)", "Geçmiş (çift tıkla → cd + cc)"),
        ["Clear"]          = ("Clear", "Temizle"),
        ["MenuOpenCc"]     = ("Open + run cc", "Aç + cc çalıştır"),
        ["MenuOpenOnly"]   = ("Open only (cd)", "Sadece aç (cd)"),
        ["MenuRenameLabel"]= ("Change label...", "Etiketi değiştir..."),
        ["MenuRemoveFav"]  = ("Remove from favorites", "Favorilerden kaldır"),
        ["MenuFocus"]      = ("Bring to front", "Öne getir"),
        ["MenuClone"]      = ("New CMD in same folder", "Aynı dizinde yeni CMD"),
        ["MenuSendCc"]     = ("Send cc", "cc gönder"),
        ["MenuUtf8"]       = ("Set UTF-8 (chcp 65001)", "UTF-8 yap (chcp 65001)"),
        ["MenuDebugRaw"]   = ("🔍 Diagnostics: copy raw text to clipboard", "🔍 Tanılama: ham metni panoya kopyala"),
        ["MenuAddFav"]     = ("⭐ Add to favorites", "⭐ Favorilere ekle"),
        ["MenuClose"]      = ("Close", "Kapat"),
        ["MenuRemoveHist"] = ("Remove from history", "Geçmişten kaldır"),
        ["BtnFocus"]       = ("Bring to Front", "Öne Getir"),
        ["BtnClone"]       = ("New CMD in Same Folder", "Aynı Dizinde Yeni CMD"),
        ["BtnSendCc"]      = ("Send cc", "cc Gönder"),
        ["BtnClose"]       = ("Close", "Kapat"),
        ["InputTip"]       = ("Enter: send silently · ↑/↓: previous commands", "Enter: sessiz gönder · ↑/↓: önceki komutlar"),
        ["BtnSend"]        = ("Send", "Gönder"),
        ["SendTip"]        = ("Send silently (no focus change)", "Sessiz gönder (fokus kaymaz)"),
        ["PasteTip"]       = ("Send via clipboard — safe for long/Unicode text (focus switches to the cmd window)",
                              "Pano üzerinden gönder — Türkçe/uzun metin için güvenli (cmd pencereye geçer)"),
        ["LangTip"]        = ("Switch language (English / Türkçe)", "Dili değiştir (English / Türkçe)"),
    };

    void ApplyLanguage()
    {
        var rd = new ResourceDictionary();
        foreach (var kv in UiStrings)
            rd[kv.Key] = Loc.Lang == "tr" ? kv.Value.tr : kv.Value.en;
        if (_langDict != null) Resources.MergedDictionaries.Remove(_langDict);
        Resources.MergedDictionaries.Add(rd);
        _langDict = rd;
        if (LangButton != null) LangButton.Content = Loc.Lang == "tr" ? "🌐 Türkçe" : "🌐 English";
        UpdateStatusBar();
        foreach (var h in _historyItems) h.RefreshAgo();
    }

    void LangButton_Click(object sender, RoutedEventArgs e)
    {
        Loc.Lang = Loc.Lang == "tr" ? "en" : "tr";
        _settings.Language = Loc.Lang;
        SaveSettings();
        ApplyLanguage();
    }

    void UpdateStatusBar()
    {
        int limitWaiting = _openItems.Count(o => o.IsLimitWaiting);
        StatusBar.Text = string.Format(
            Loc.T("{0} open · {1} history · {2} favorites", "{0} açık · {1} geçmiş · {2} favori"),
            _openItems.Count, _historyItems.Count, _favoriteItems.Count)
            + (limitWaiting > 0
                ? Loc.T($" · ⏰ {limitWaiting} on limit", $" · ⏰ {limitWaiting} limitte")
                : "");
    }

    void AutoResume_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.AutoResumeEnabled = AutoResumeCheck.IsChecked == true;
        SaveSettings();
    }

    void EditResumeMsg_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.Show(this,
            Loc.T("Command to send when the limit resets (typed into the Claude Code session):",
                  "Limit sıfırlanınca gönderilecek komut (Claude Code oturumuna yazılır):"),
            Loc.T("Auto-resume Command", "Oto-devam Komutu"), _settings.ResumeMessage);
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _settings.ResumeMessage = text.Trim();
        SaveSettings();
    }

    void Refresh()
    {
        var current = Native.EnumerateCmdWindows(_hwndCache);
        var currentPids = new HashSet<int>(current.Select(c => c.Pid));

        foreach (var pid in _hwndCache.Keys.ToList())
            if (!currentPids.Contains(pid)) _hwndCache.Remove(pid);

        // Detect closed cmds — use the last known (possibly stale) directory
        foreach (var kv in _lastKnownByPid.ToList())
        {
            if (!currentPids.Contains(kv.Key))
            {
                if (IsRelevant(kv.Value.CurrentDirectory))
                    AddHistory(kv.Value.CurrentDirectory);
                _lastKnownByPid.Remove(kv.Key);
            }
        }

        // Update last-known, preserving previous non-empty dir/hwnd on flaky reads
        foreach (var c in current)
        {
            if (_lastKnownByPid.TryGetValue(c.Pid, out var prev))
            {
                if (string.IsNullOrEmpty(c.CurrentDirectory) && !string.IsNullOrEmpty(prev.CurrentDirectory))
                    c.CurrentDirectory = prev.CurrentDirectory;
                if (c.WindowHandle == IntPtr.Zero && prev.WindowHandle != IntPtr.Zero)
                    c.WindowHandle = prev.WindowHandle;
                c.IsIdle = prev.IsIdle;
                c.IsLimitWaiting = prev.IsLimitWaiting;
                c.ResumeInfo = prev.ResumeInfo;
            }
            _lastKnownByPid[c.Pid] = c;
        }

        // Display only E:/F: cmds
        int prevSelectedPid = (OpenList.SelectedItem as CmdItem)?.Pid ?? -1;
        _openItems.Clear();
        foreach (var c in current)
        {
            var tracked = _lastKnownByPid[c.Pid];
            if (IsRelevant(tracked.CurrentDirectory))
                _openItems.Add(tracked);
        }
        if (prevSelectedPid > 0)
        {
            var match = _openItems.FirstOrDefault(x => x.Pid == prevSelectedPid);
            if (match != null) OpenList.SelectedItem = match;
        }
        UpdateStatusBar();
    }

    void UpdatePreview()
    {
        if (OpenList.SelectedItem is not CmdItem item)
        {
            PreviewBox.Document.Blocks.Clear();
            PreviewStatus.Text = "";
            _lastPreviewHash = 0;
            return;
        }
        var lines = Native.ReadScreenCells(item.Pid);
        if (lines == null)
        {
            PreviewStatus.Text = Loc.T("screen unreadable", "ekran okunamadı");
            return;
        }
        int hash = ComputeHash(lines);
        PreviewStatus.Text = $"PID {item.Pid} · {item.CurrentDirectory}";
        if (hash == _lastPreviewHash) return;
        _lastPreviewHash = hash;

        var sv = GetScrollViewer(PreviewBox);
        double prevOffset = sv?.VerticalOffset ?? 0;
        double extent = sv?.ExtentHeight ?? 0;
        double viewport = sv?.ViewportHeight ?? 0;
        bool atBottom = (extent - viewport - prevOffset) < 20;

        RenderColoredPreview(lines);

        sv = GetScrollViewer(PreviewBox);
        if (sv != null)
        {
            if (atBottom) sv.ScrollToEnd();
            else sv.ScrollToVerticalOffset(prevOffset);
        }
    }

    void RenderColoredPreview(List<List<Native.Cell>> lines)
    {
        var doc = PreviewBox.Document;
        doc.Blocks.Clear();
        foreach (var line in lines)
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            if (line.Count == 0)
            {
                para.Inlines.Add(new Run(" "));
                doc.Blocks.Add(para);
                continue;
            }
            int i = 0;
            while (i < line.Count)
            {
                byte fg = line[i].Fg;
                byte bg = line[i].Bg;
                var sb = new StringBuilder();
                int j = i;
                while (j < line.Count && line[j].Fg == fg && line[j].Bg == bg)
                {
                    sb.Append(line[j].Ch);
                    j++;
                }
                var run = new Run(sb.ToString()) { Foreground = FgBrushes[fg] };
                if (bg != 0) run.Background = BgBrushes[bg];
                para.Inlines.Add(run);
                i = j;
            }
            doc.Blocks.Add(para);
        }
    }

    static int ComputeHash(List<List<Native.Cell>> lines)
    {
        unchecked
        {
            int h = 17;
            foreach (var line in lines)
            {
                foreach (var c in line)
                    h = h * 31 + c.Ch + (c.Fg << 8) + (c.Bg << 12);
                h = h * 31 + 0xAB;
            }
            return h;
        }
    }

    static System.Windows.Controls.ScrollViewer? GetScrollViewer(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var r = GetScrollViewer(child);
            if (r != null) return r;
        }
        return null;
    }

    // ---------------- History ----------------
    void AddHistory(string path)
    {
        if (!IsRelevant(path)) return;
        var existing = _historyItems.FirstOrDefault(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _historyItems.Remove(existing);
        _historyItems.Insert(0, new HistoryItem { Path = path, ClosedAt = DateTime.Now });
        while (_historyItems.Count > 100) _historyItems.RemoveAt(_historyItems.Count - 1);
        SaveHistory();
    }

    void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(_historyPath));
            if (items != null)
                foreach (var i in items)
                    if (IsRelevant(i.Path)) _historyItems.Add(i);
        }
        catch { }
    }

    void SaveHistory()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(_historyItems.ToList(), opts));
        }
        catch { }
    }

    // ---------------- Favorites ----------------
    void LoadFavorites()
    {
        try
        {
            if (!File.Exists(_favoritesPath)) return;
            var items = JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(_favoritesPath));
            if (items != null) foreach (var i in items) _favoriteItems.Add(i);
        }
        catch { }
    }

    void SaveFavorites()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_favoritesPath, JsonSerializer.Serialize(_favoriteItems.ToList(), opts));
        }
        catch { }
    }

    void AddToFavorites(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_favoriteItems.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Bu dizin zaten favorilerde.");
            return;
        }
        var label = InputDialog.Show(this, Loc.T("Label (leave empty to show the path):", "Etiket (boş bırakırsan yol gösterilir):"), Loc.T("Add to Favorites", "Favorilere Ekle"));
        if (label == null) return;
        _favoriteItems.Add(new FavoriteItem
        {
            Path = path,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
        });
        SaveFavorites();
    }

    void FavoritesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteItem f) StartCmd(f.Path, true);
    }

    void FavoriteOpenCc_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteItem f) StartCmd(f.Path, true);
    }

    void FavoriteOpenOnly_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteItem f) StartCmd(f.Path, false);
    }

    void FavoriteRename_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is not FavoriteItem f) return;
        var newLabel = InputDialog.Show(this, Loc.T("New label (empty → show the path):", "Yeni etiket (boş → yol gösterilir):"), Loc.T("Change Label", "Etiket Değiştir"), f.Label ?? "");
        if (newLabel == null) return;
        f.Label = string.IsNullOrWhiteSpace(newLabel) ? null : newLabel.Trim();
        f.NotifyChanged();
        SaveFavorites();
    }

    void FavoriteRemove_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteItem f)
        {
            _favoriteItems.Remove(f);
            SaveFavorites();
        }
    }

    void AddOpenToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item && !string.IsNullOrEmpty(item.CurrentDirectory))
            AddToFavorites(item.CurrentDirectory);
    }

    void AddHistoryToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h) AddToFavorites(h.Path);
    }

    // ---------------- Other Handlers ----------------
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    void NewCmd_Click(object sender, RoutedEventArgs e) => StartCmd(null, false);

    void KillAll_Click(object sender, RoutedEventArgs e)
    {
        if (_openItems.Count == 0) return;
        var result = MessageBox.Show(
            Loc.T($"Close {_openItems.Count} cmd window(s)?", $"{_openItems.Count} cmd penceresini kapatmak istiyor musun?"),
            Loc.T("Close All", "Hepsini Kapat"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        foreach (var item in _openItems.ToList()) TryKill(item.Pid);
        Refresh();
    }

    void OpenList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdatePreview();

    void OpenList_DoubleClick(object sender, MouseButtonEventArgs e) => Focus_Click(sender, new RoutedEventArgs());

    void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item)
            Native.BringToFront(item.WindowHandle);
    }

    void CloneCmd_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item)
            StartCmd(item.CurrentDirectory, false);
    }

    void SendCc_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item)
        {
            if (!Native.SendText(item.Pid, "cc"))
                MessageBox.Show(Loc.T("Could not send the command.", "Komut gönderilemedi."));
            else
                MarkSent(item.Pid);
        }
    }

    void MakeUtf8_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item)
        {
            if (!Native.SendText(item.Pid, "chcp 65001"))
                MessageBox.Show(Loc.T("Could not send the command.", "Komut gönderilemedi."));
        }
    }

    void DebugCopyRaw_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is not CmdItem item) return;
        var raw = Native.ReadScreenBuffer(item.Pid);
        if (raw == null) { MessageBox.Show(Loc.T("Could not read.", "Okunamadı.")); return; }
        try
        {
            System.Windows.Clipboard.SetText(raw);
            MessageBox.Show(Loc.T($"Copied to clipboard ({raw.Length} chars). Paste into Notepad to see whether '?' is really there.", $"Panoya kopyalandı ({raw.Length} karakter). Notepad'e yapıştır, '?' gerçekten var mı gör."));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    void SendInput_Click(object sender, RoutedEventArgs e) => SubmitInput();

    void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SubmitInput();
        }
        else if (e.Key == Key.Up && _commandHistory.Count > 0)
        {
            if (_commandHistoryIndex < 0) _commandHistoryIndex = _commandHistory.Count - 1;
            else if (_commandHistoryIndex > 0) _commandHistoryIndex--;
            InputBox.Text = _commandHistory[_commandHistoryIndex];
            InputBox.CaretIndex = InputBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _commandHistory.Count > 0 && _commandHistoryIndex >= 0)
        {
            if (_commandHistoryIndex < _commandHistory.Count - 1)
            {
                _commandHistoryIndex++;
                InputBox.Text = _commandHistory[_commandHistoryIndex];
                InputBox.CaretIndex = InputBox.Text.Length;
            }
            else
            {
                _commandHistoryIndex = -1;
                InputBox.Text = "";
            }
            e.Handled = true;
        }
    }

    void SubmitInput()
    {
        if (OpenList.SelectedItem is not CmdItem item)
        {
            MessageBox.Show(Loc.T("Select a cmd on the left first.", "Önce soldan bir cmd seç."));
            return;
        }
        var text = InputBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _commandHistory.Remove(text);
            _commandHistory.Add(text);
            while (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
        }
        _commandHistoryIndex = -1;
        InputBox.Text = "";

        if (!Native.SendText(item.Pid, text))
            MessageBox.Show(Loc.T("Could not send the command.", "Komut gönderilemedi."));
        else
            MarkSent(item.Pid);
        InputBox.Focus();
    }

    void SendViaPaste(CmdItem item, string text)
    {
        string? saved = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText()) saved = System.Windows.Clipboard.GetText();
        }
        catch { }

        try
        {
            if (!string.IsNullOrEmpty(text))
                System.Windows.Clipboard.SetText(text);

            Native.BringToFront(item.WindowHandle);
            System.Threading.Thread.Sleep(130);

            if (!string.IsNullOrEmpty(text))
            {
                Native.SendCtrlV();
                System.Threading.Thread.Sleep(60);
            }
            Native.SendEnter();
            System.Threading.Thread.Sleep(25);
            Native.SendEnter();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        finally
        {
            System.Threading.Thread.Sleep(250);
            try
            {
                if (!string.IsNullOrEmpty(saved)) System.Windows.Clipboard.SetText(saved);
                else System.Windows.Clipboard.Clear();
            }
            catch { }
            try { Native.BringToFront(_windowHandle); } catch { }
        }
    }

    void PasteSend_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is not CmdItem item)
        {
            MessageBox.Show(Loc.T("Select a cmd on the left first.", "Önce soldan bir cmd seç."));
            return;
        }
        var text = InputBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _commandHistory.Remove(text);
            _commandHistory.Add(text);
            while (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
        }
        _commandHistoryIndex = -1;
        InputBox.Text = "";
        SendViaPaste(item, text);
        MarkSent(item.Pid);
        InputBox.Focus();
    }

    void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (OpenList.SelectedItem is CmdItem item)
        {
            TryKill(item.Pid);
            Refresh();
        }
    }

    void TryKill(int pid)
    {
        try { Process.GetProcessById(pid).Kill(); } catch { }
    }

    void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h) StartCmd(h.Path, true);
    }

    void HistoryOpenCc_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h) StartCmd(h.Path, true);
    }

    void HistoryOpenOnly_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h) StartCmd(h.Path, false);
    }

    void HistoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem h)
        {
            _historyItems.Remove(h);
            SaveHistory();
        }
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyItems.Count == 0) return;
        var r = MessageBox.Show(Loc.T("Clear all history?", "Tüm geçmişi silmek istiyor musun?"), Loc.T("Clear History", "Geçmişi Temizle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _historyItems.Clear();
        SaveHistory();
    }

    // ---------------- Helpers ----------------
    void StartCmd(string? path, bool runCc)
    {
        try
        {
            string args;
            if (string.IsNullOrWhiteSpace(path))
                args = runCc ? "/K chcp 65001 >nul && cc" : "/K chcp 65001 >nul";
            else if (runCc)
                args = $"/K chcp 65001 >nul && cd /d \"{path}\" && cc";
            else
                args = $"/K chcp 65001 >nul && cd /d \"{path}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            var lbi = FindAncestor<System.Windows.Controls.ListBoxItem>(d);
            if (lbi != null) lbi.IsSelected = true;
        }
    }

    static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t) return t;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}

public static class InputDialog
{
    public static string? Show(Window owner, string prompt, string title, string defaultText = "")
    {
        var promptTb = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Margin = new Thickness(12, 12, 12, 4),
            Foreground = Brushes.LightGray
        };
        var tb = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(12, 4, 12, 8),
            Padding = new Thickness(6),
            Text = defaultText,
            FontFamily = new FontFamily("Consolas"),
            Background = Brushes.Black,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = Brushes.Gray
        };
        var okBtn = new System.Windows.Controls.Button { Content = Loc.T("OK", "Tamam"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4), IsDefault = true };
        var cancelBtn = new System.Windows.Controls.Button { Content = Loc.T("Cancel", "İptal"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4), IsCancel = true };
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        var root = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(promptTb, System.Windows.Controls.Dock.Top);
        System.Windows.Controls.DockPanel.SetDock(btnPanel, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(promptTb);
        root.Children.Add(btnPanel);
        root.Children.Add(tb);

        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = Brushes.White,
            Content = root,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };
        string? result = null;
        okBtn.Click += (_, _) => { result = tb.Text; dlg.Close(); };
        dlg.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }
}
