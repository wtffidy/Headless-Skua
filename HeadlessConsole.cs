using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Headless.UI;

/// <summary>Global switch so every color/escape helper collapses to "" for plain-text output.</summary>
internal static class Ansi
{
    public static bool On { get; set; } = true;
}

/// <summary>24-bit color. Emits ANSI truecolor escape sequences (or nothing when color is off).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Fg => Ansi.On ? $"\u001b[38;2;{R};{G};{B}m" : string.Empty;

    // Linear interpolation, used for gradients (banner, progress bar)
    public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}

public enum LogLevel { Debug, Info, Success, Warn, Error }

/// <summary>A palette. Add your own static instance, register it in All, and it's selectable via --theme.</summary>
public sealed record Theme(
    Rgb Primary, Rgb Accent, Rgb Text, Rgb Muted,
    Rgb Debug, Rgb Info, Rgb Success, Rgb Warn, Rgb Error,
    Rgb GradientStart, Rgb GradientEnd)
{
    public static readonly Theme Midnight = new(
        Primary: new(137, 180, 250), Accent: new(203, 166, 247),
        Text: new(205, 214, 244), Muted: new(108, 112, 134),
        Debug: new(147, 153, 178), Info: new(137, 220, 235),
        Success: new(166, 227, 161), Warn: new(249, 226, 175), Error: new(243, 139, 168),
        GradientStart: new(137, 180, 250), GradientEnd: new(245, 194, 231));

    public static readonly Theme Ember = new(
        Primary: new(255, 159, 67), Accent: new(255, 107, 107),
        Text: new(240, 230, 220), Muted: new(120, 105, 95),
        Debug: new(160, 145, 135), Info: new(255, 200, 120),
        Success: new(150, 220, 130), Warn: new(255, 214, 90), Error: new(255, 90, 90),
        GradientStart: new(255, 190, 70), GradientEnd: new(235, 60, 90));

    public static readonly Theme Neon = new(
        Primary: new(0, 255, 200), Accent: new(255, 0, 170),
        Text: new(225, 235, 240), Muted: new(90, 100, 115),
        Debug: new(130, 140, 160), Info: new(0, 200, 255),
        Success: new(60, 255, 130), Warn: new(255, 240, 60), Error: new(255, 60, 100),
        GradientStart: new(0, 255, 200), GradientEnd: new(255, 0, 170));

    // Name lookup for --theme (declared after the instances so static init order is safe)
    public static readonly Dictionary<string, Theme> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["midnight"] = Midnight,
        ["ember"] = Ember,
        ["neon"] = Neon
    };
}

/// <summary>Animated "working..." indicator. Create via HeadlessConsole.Spin(); dispose or Succeed/Fail to stop.</summary>
public sealed class Spinner : IDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly Task? _task;
    private volatile bool _stopped;

    /// <summary>Change this while spinning to update the text (e.g. "Killing mobs 3/10").</summary>
    public string Label { get; set; }

    internal Spinner(string label)
    {
        Label = label;

        // No animation possible when output is plain/redirected: log once instead
        if (!Ansi.On)
        {
            HeadlessConsole.Info($"{label}...");
            return;
        }

        _task = Task.Run(async () =>
        {
            int frame = 0;
            while (!_stopped)
            {
                HeadlessConsole.DrawSpinner(Frames[frame++ % Frames.Length], Label);
                await Task.Delay(80).ConfigureAwait(false);
            }
        });
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _task?.Wait(); // make sure no frame is drawn after we clear the line
        if (Ansi.On) HeadlessConsole.ClearSpinner();
    }

    public void Succeed(string message)
    {
        Stop();
        HeadlessConsole.Success(message);
    }

    public void Fail(string message)
    {
        Stop();
        HeadlessConsole.Error(message);
    }

    public void Dispose() => Stop();
}

internal static class HeadlessLogRedactor
{
    private static readonly Regex LoginPayloadRegex = new(
        @"<login\b[^>]*>.*?</login\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex PasswordTagRegex = new(
        @"(<(?:pword|password|passwd)\b[^>]*>).*?(</(?:pword|password|passwd)\s*>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex JsonPasswordRegex = new(
        @"([""'](?:password|pword|passwd)[""']\s*:\s*[""'])(.*?)([""'])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes credentials from AQW login packets and common structured log payloads.
    /// </summary>
    /// <param name="message">The original log message.</param>
    /// <returns>A safe message suitable for console and dashboard output.</returns>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? string.Empty;

        string redacted = LoginPayloadRegex.Replace(message, "<login>[REDACTED]</login>");
        redacted = PasswordTagRegex.Replace(redacted, "$1[REDACTED]$2");
        return JsonPasswordRegex.Replace(redacted, "$1[REDACTED]$3");
    }
}

public static class HeadlessConsole
{
    private const int HeaderLines = 3;

    /// <summary>
    /// Whether low-level Ruffle browser and ActionScript trace output should be hidden.
    /// Host status, prompts, and actionable errors remain visible.
    /// </summary>
    public static bool Quiet { get; private set; }

    private static readonly object _lock = new();
    private static Theme _theme = Theme.Midnight;

    // Escape helpers collapse to "" when color is off, so call sites never need to branch
    private static string Reset => Ansi.On ? "\u001b[0m" : string.Empty;
    private static string Bold => Ansi.On ? "\u001b[1m" : string.Empty;
    private static string ClearLine => Ansi.On ? "\r\u001b[2K" : string.Empty; // wipes a leftover spinner/progress bar

    // Pinned header state
    private static Timer? _headerTimer;
    private static Func<(string Key, string Value)[]>? _headerProvider;
    private static DateTime _startedAt;
    private static int _lastHeight;
    private static bool _exitHooked;

    // Progress fallback state (plain mode only logs on 10% steps)
    private static int _lastPlainPct = -1;

    // Legacy conhost (cmd.exe) needs VT processing switched on manually
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>
    /// Pass your Main args to honour:  --theme neon | --theme=ember   and   --no-color.
    /// Color also auto-disables when output is redirected or NO_COLOR is set.
    /// </summary>
    public static void Init(string[]? args = null, Theme? fallback = null, string title = "Skua • Headless")
    {
        args ??= [];
        Quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);

        string? requested = ArgValue(args, "--theme");
        bool found = Theme.All.TryGetValue(requested ?? string.Empty, out Theme? picked);
        _theme = picked ?? fallback ?? Theme.Midnight;

        Ansi.On = !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && !args.Contains("--no-color", StringComparer.OrdinalIgnoreCase);

        Console.OutputEncoding = Encoding.UTF8;

        if (Ansi.On)
        {
            IntPtr handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (handle != IntPtr.Zero && GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | 0x0004 | 0x0008); // VT processing + disable auto newline
        }

        try { Console.Title = title; }
        catch (IOException) { } // no console attached

        if (requested is not null && !found)
            Warn($"Unknown theme '{requested}'. Available: {string.Join(", ", Theme.All.Keys)}");
    }

    // Supports "--name value" and "--name=value"
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    // ───────────────────────── Banner ─────────────────────────
    // Skua emblem. Each character is a shade level; BirdShade() maps it onto the theme gradient.
    private static readonly string[] Bird =
    [
        "            %%%%%%%%%%%#            ",
        "        %%%%%%%%%%%%%%%%%%%#        ",
        "      ************%%%%%%%%%%%%      ",
        "    -*****************%%%%%%%%%%    ",
        "  -----*****************#%%%%%%%%   ",
        " :-------*****************%%%%%%%%  ",
        " ----------:***************%%%%%%%% ",
        "-----------:::*+%#**********%%%%%%% ",
        "------------::=###**********#%%%%%%%",
        "-------------=***************%%%%%%%",
        "-----------------+**********##%%%%%%",
        "----------------------*#####%%##%%% ",
        " -----------------------=#########% ",
        "  -----------------****%%%%#########",
        "   -------------+*****%%%%%%%%%#####",
        "    -----------*******%%%%%%%%%#   #",
        "      --------********%%%%%%%%      ",
        "        -----*********%%%%%         ",
        "            +*********#             ",
    ];

    private static readonly string[] Wordmark =
    [
        @"  ____  _  ___   _   _      _  ",
        @" / ___|| |/ / | | | / \    | | ",
        @" \___ \| ' /| | | |/ _ \   | | ",
        @"  ___) | . \| |_| / ___ \  |_| ",
        @" |____/|_|\_\\___/_/   \_\ (_) ",
    ];

    // Densest glyph (%) takes the gradient end color, lightest (-) the start color,
    // so the bird shades left-to-right and the wordmark beside it matches
    private static Rgb BirdShade(char c)
    {
        double t = c switch
        {
            '%' => 0.0,
            '#' => 0.15,
            '*' => 0.35,
            '+' => 0.5,
            '=' => 0.65,
            ':' => 0.8,
            _ => 1.0
        };
        return Rgb.Lerp(_theme.GradientEnd, _theme.GradientStart, t);
    }

    /// <param name="solidBird">true renders the emblem with solid blocks (█) instead of the ASCII shade characters.</param>
    public static void Banner(string version = "", bool solidBird = false)
    {
        const int gap = 3;
        const int wordmarkTop = 6; // bird row where the wordmark starts (roughly vertically centered)

        StringBuilder sb = new();
        sb.AppendLine();
        for (int row = 0; row < Bird.Length; row++)
        {
            sb.Append("  ");
            foreach (char ch in Bird[row])
            {
                if (ch == ' ') sb.Append(' ');
                else sb.Append(BirdShade(ch).Fg).Append(solidBird ? '█' : ch);
            }
            sb.Append(Reset);

            int w = row - wordmarkTop;
            if (w >= 0 && w < Wordmark.Length)
            {
                sb.Append(' ', gap);
                for (int col = 0; col < Wordmark[w].Length; col++)
                {
                    // One gradient step per row, plus a per-character shift for a diagonal sweep
                    double t = Math.Clamp((w * 6 + col) / (double)(Wordmark.Length * 6 + Wordmark[w].Length), 0, 1);
                    sb.Append(Rgb.Lerp(_theme.GradientStart, _theme.GradientEnd, t).Fg).Append(Wordmark[w][col]);
                }
                sb.Append(Reset);
            }
            else if (w == Wordmark.Length + 1)
                sb.Append(' ', gap).Append($"{_theme.Muted.Fg} headless mode {_theme.Accent.Fg}{version}{Reset}");
            else if (w == Wordmark.Length + 2)
                sb.Append(' ', gap).Append($"{_theme.Muted.Fg} {new string('─', 30)}{Reset}");

            sb.AppendLine();
        }
        Write(sb.ToString());
    }

    // ───────────────────────── Logging ─────────────────────────
    public static void Log(LogLevel level, string message)
    {
        message = HeadlessLogRedactor.Redact(message);
        (string tag, Rgb color) = level switch
        {
            LogLevel.Debug => ("DBG", _theme.Debug),
            LogLevel.Info => ("INF", _theme.Info),
            LogLevel.Success => (" OK", _theme.Success),
            LogLevel.Warn => ("WRN", _theme.Warn),
            _ => ("ERR", _theme.Error)
        };

        Write($"{ClearLine}{_theme.Muted.Fg}{DateTime.Now:HH:mm:ss} {color.Fg}{Bold}{tag}{Reset} {_theme.Muted.Fg}│{Reset} {_theme.Text.Fg}{message}{Reset}");
    }

    public static void Info(string msg) => Log(LogLevel.Info, msg);
    public static void Success(string msg) => Log(LogLevel.Success, msg);
    public static void Warn(string msg) => Log(LogLevel.Warn, msg);
    public static void Error(string msg) => Log(LogLevel.Error, msg);
    public static void Debug(string msg)
    {
        if (!Quiet)
            Log(LogLevel.Debug, msg);
    }

    /// <summary>Item drop / farming line, e.g. ◆ +3 Dragon Scale (47/100)</summary>
    public static void Item(string name, int gained, int total, int? goal = null)
    {
        string count = goal is null ? $"{total}" : $"{total}/{goal}";
        Write($"{ClearLine}{_theme.Muted.Fg}{DateTime.Now:HH:mm:ss} {_theme.Accent.Fg}◆{Reset} {_theme.Success.Fg}+{gained}{Reset} {_theme.Text.Fg}{name}{Reset} {_theme.Muted.Fg}({count}){Reset}");
    }

    // ───────────────────────── Panel ─────────────────────────
    /// <summary>Rounded box with key/value rows, for one-off summary screens.</summary>
    public static void Panel(string title, params (string Key, string Value)[] rows)
    {
        int keyWidth = rows.Max(r => r.Key.Length);
        int inner = Math.Max(title.Length + 4, rows.Max(r => keyWidth + 3 + r.Value.Length)) + 2;

        StringBuilder sb = new();
        sb.AppendLine($"{_theme.Primary.Fg}╭─ {Bold}{title}{Reset}{_theme.Primary.Fg} {new string('─', inner - title.Length - 3)}╮{Reset}");
        foreach ((string key, string value) in rows)
        {
            int pad = inner - (keyWidth + 3 + value.Length) - 1;
            sb.AppendLine($"{_theme.Primary.Fg}│{Reset} {_theme.Muted.Fg}{key.PadRight(keyWidth)}{Reset} {_theme.Muted.Fg}›{Reset} {_theme.Text.Fg}{value}{Reset}{new string(' ', Math.Max(0, pad))}{_theme.Primary.Fg}│{Reset}");
        }
        sb.Append($"{_theme.Primary.Fg}╰{new string('─', inner + 1)}╯{Reset}");
        Write(sb.ToString());
    }

    // ───────────────────────── Progress ─────────────────────────
    /// <summary>In-place gradient progress bar. Call repeatedly; the next Log() line clears it.</summary>
    public static void Progress(string label, int current, int max, int width = 28)
    {
        double ratio = max <= 0 ? 0 : Math.Clamp(current / (double)max, 0, 1);

        // Plain mode: can't redraw in place, so emit a line per 10% instead of spamming the log
        if (!Ansi.On)
        {
            int pct = (int)(ratio * 100) / 10 * 10;
            if (pct == _lastPlainPct) return;
            _lastPlainPct = pct;
            Info($"{label} {pct}% ({current}/{max})");
            return;
        }

        int filled = (int)(ratio * width);
        StringBuilder bar = new();
        for (int i = 0; i < width; i++)
        {
            if (i < filled) bar.Append(Rgb.Lerp(_theme.GradientStart, _theme.GradientEnd, i / (double)width).Fg).Append('█');
            else bar.Append(_theme.Muted.Fg).Append('░');
        }

        lock (_lock)
            Console.Write($"{ClearLine}{_theme.Text.Fg}{label} {bar}{Reset} {_theme.Accent.Fg}{ratio:P0}{Reset} {_theme.Muted.Fg}{current}/{max}{Reset}");
    }

    // ───────────────────────── Spinner ─────────────────────────
    /// <summary>using Spinner s = HeadlessConsole.Spin("Joining map"); ... s.Succeed("Joined");</summary>
    public static Spinner Spin(string label) => new(label);

    internal static void DrawSpinner(string frame, string label)
    {
        lock (_lock)
            Console.Write($"{ClearLine}{_theme.Accent.Fg}{frame}{Reset} {_theme.Text.Fg}{label}{Reset}");
    }

    internal static void ClearSpinner()
    {
        lock (_lock)
            Console.Write(ClearLine);
    }

    // ───────────────────────── Pinned live header ─────────────────────────
    /// <summary>
    /// Pins a 3-line status header to the top and keeps logs scrolling beneath it.
    /// The provider is polled every interval, e.g. () => [("Map", map), ("HP", hp), ("Drops", drops)].
    /// Call this BEFORE Banner(): it clears the screen to set up the scroll region.
    /// </summary>
    public static void StartHeader(Func<(string Key, string Value)[]> provider, int intervalMs = 1000)
    {
        if (!Ansi.On || _headerTimer is not null) return;

        _headerProvider = provider;
        _startedAt = DateTime.Now;
        _lastHeight = 0; // force the scroll region to be applied on first draw

        // Make sure the terminal is never left with a stuck scroll region
        if (!_exitHooked)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopHeader();
            _exitHooked = true;
        }

        lock (_lock)
        {
            Console.Write("\u001b[2J\u001b[H");
            DrawHeader();
            Console.Write($"\u001b[{HeaderLines + 1};1H"); // park the cursor under the header
        }

        _headerTimer = new(_ =>
        {
            lock (_lock)
                DrawHeader();
        }, null, intervalMs, intervalMs);
    }

    public static void StopHeader()
    {
        if (_headerTimer is null) return;

        _headerTimer.Dispose();
        _headerTimer = null;

        lock (_lock)
            Console.Write($"\u001b[r\u001b[{Console.WindowHeight};1H"); // reset scroll region, cursor to bottom
    }

    // Must be called under _lock
    private static void DrawHeader()
    {
        int width = Math.Max(20, Console.WindowWidth);
        int height = Console.WindowHeight;

        (string Key, string Value)[] items;
        try { items = _headerProvider?.Invoke() ?? []; }
        catch (Exception) { items = []; } // never let a bad provider kill the timer thread

        TimeSpan up = DateTime.Now - _startedAt;
        string sep = $" {_theme.Muted.Fg}│{Reset} ";
        string stats = string.Join(sep, items.Select(i => $"{_theme.Muted.Fg}{i.Key} {_theme.Text.Fg}{Bold}{i.Value}{Reset}"));

        StringBuilder sb = new();
        sb.Append("\u001b7"); // save cursor so logging position is untouched

        // (Re)apply the scroll region on first draw and whenever the window is resized
        if (height != _lastHeight)
        {
            sb.Append($"\u001b[{HeaderLines + 1};{height}r");
            _lastHeight = height;
        }

        sb.Append($"\u001b[1;1H\u001b[2K {Bold}{_theme.Primary.Fg}SKUA{Reset} {_theme.Muted.Fg}headless{Reset}  {_theme.Muted.Fg}⏱{Reset} {_theme.Accent.Fg}{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}{Reset}");
        sb.Append($"\u001b[2;1H\u001b[2K {stats}");
        sb.Append($"\u001b[3;1H\u001b[2K{_theme.Muted.Fg}{new string('─', width)}{Reset}");
        sb.Append("\u001b8"); // restore cursor

        Console.Write(sb.ToString());
    }

    // Single choke point so concurrent bot threads never interleave escape codes
    /// <summary>Writes an interactive prompt and reads one line from the terminal.</summary>
    public static string? Prompt(string label)
    {
        lock (_lock)
        {
            Console.Write($"{_theme.Accent.Fg}{label}{Reset} ");
            return Console.ReadLine();
        }
    }

    /// <summary>Reads a password without echoing its characters to the terminal.</summary>
    public static string ReadSecret(string label)
    {
        lock (_lock)
        {
            Console.Write($"{_theme.Accent.Fg}{label}{Reset} ");
            StringBuilder password = new();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return password.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length == 0)
                        continue;

                    password.Length--;
                    if (Ansi.On)
                        Console.Write("\b \b");
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    if (Ansi.On)
                        Console.Write('•');
                }
            }
        }
    }

    private static void Write(string text)
    {
        lock (_lock)
            Console.WriteLine(text);
    }
}
