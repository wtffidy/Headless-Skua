using System.Runtime.InteropServices;
using System.Text;

namespace Skua.Headless.UI;

/// <summary>24-bit color. Emits ANSI truecolor escape sequences.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Fg => $"\u001b[38;2;{R};{G};{B}m";

    // Linear interpolation, used for gradients (banner, progress bar)
    public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}

public enum LogLevel { Debug, Info, Success, Warn, Error }

/// <summary>A palette. Add your own static instance and pass it to Init().</summary>
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
}

public static class HeadlessConsole
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string ClearLine = "\r\u001b[2K"; // wipes a leftover progress bar before a log line

    private static readonly object _lock = new();
    private static Theme _theme = Theme.Midnight;

    // Legacy conhost (cmd.exe) needs VT processing switched on manually
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    public static void Init(Theme? theme = null, string title = "Skua • Headless")
    {
        _theme = theme ?? Theme.Midnight;
        Console.OutputEncoding = Encoding.UTF8;

        IntPtr handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
        if (handle != IntPtr.Zero && GetConsoleMode(handle, out uint mode))
            SetConsoleMode(handle, mode | 0x0004 | 0x0008); // VT processing + disable auto newline

        try { Console.Title = title; }
        catch (IOException) { } // no console attached (redirected output)
    }

    // ───────────────────────── Banner ─────────────────────────
    public static void Banner(string version = "")
    {
        string[] art =
        [
            @"  ____  _  ___   _   _      _  ",
            @" / ___|| |/ / | | | / \    | | ",
            @" \___ \| ' /| | | |/ _ \   | | ",
            @"  ___) | . \| |_| / ___ \  |_| ",
            @" |____/|_|\_\\___/_/   \_\ (_) ",
        ];

        StringBuilder sb = new();
        sb.AppendLine();
        for (int i = 0; i < art.Length; i++)
        {
            // One gradient step per row, plus a per-character shift for a diagonal sweep
            for (int c = 0; c < art[i].Length; c++)
            {
                double t = Math.Clamp((i * 6 + c) / (double)(art.Length * 6 + art[i].Length), 0, 1);
                sb.Append(Rgb.Lerp(_theme.GradientStart, _theme.GradientEnd, t).Fg).Append(art[i][c]);
            }
            sb.AppendLine(Reset);
        }
        sb.AppendLine($"  {_theme.Muted.Fg}headless mode {_theme.Accent.Fg}{version}{Reset}");
        sb.AppendLine($"  {_theme.Muted.Fg}{new string('─', 34)}{Reset}");
        Write(sb.ToString());
    }

    // ───────────────────────── Logging ─────────────────────────
    public static void Log(LogLevel level, string message)
    {
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
    public static void Debug(string msg) => Log(LogLevel.Debug, msg);

    /// <summary>Item drop / farming line, e.g. ◆ +3 Dragon Scale (47/100)</summary>
    public static void Item(string name, int gained, int total, int? goal = null)
    {
        string count = goal is null ? $"{total}" : $"{total}/{goal}";
        Write($"{ClearLine}{_theme.Muted.Fg}{DateTime.Now:HH:mm:ss} {_theme.Accent.Fg}◆{Reset} {_theme.Success.Fg}+{gained}{Reset} {_theme.Text.Fg}{name}{Reset} {_theme.Muted.Fg}({count}){Reset}");
    }

    // ───────────────────────── Panel ─────────────────────────
    /// <summary>Rounded box with key/value rows, for status/summary screens.</summary>
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

    // Single choke point so concurrent bot threads never interleave escape codes
    private static void Write(string text)
    {
        lock (_lock)
            Console.WriteLine(text);
    }
}
