using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Headless.UI;

namespace HeadlessSkua;

/// <summary>
/// Writes Skua logs to the console while retaining a bounded in-memory history.
/// </summary>
internal sealed class HeadlessLogService : ILogService
{
    private const int MaxEntries = 10_000;
    private readonly object _sync = new();
    private readonly Dictionary<LogType, Queue<string>> _logs = new()
    {
        [LogType.Debug] = new(),
        [LogType.Script] = new(),
        [LogType.Flash] = new()
    };

    /// <inheritdoc />
    public void DebugLog(string message) => Write(LogType.Debug, message);

    /// <inheritdoc />
    public void ScriptLog(string message) => Write(LogType.Script, message);

    /// <inheritdoc />
    public void FlashLog(string message) => Write(LogType.Flash, message);

    /// <inheritdoc />
    public void ClearLog(LogType logType)
    {
        lock (_sync)
            if (_logs.TryGetValue(logType, out Queue<string>? entries))
                entries.Clear();
    }

    /// <inheritdoc />
    public List<string> GetLogs(LogType logType)
    {
        lock (_sync)
            return _logs.TryGetValue(logType, out Queue<string>? entries) ? entries.ToList() : new List<string>();
    }

    /// <summary>
    /// Writes one log entry to the console and bounded history.
    /// </summary>
    /// <param name="type">The Skua log category.</param>
    /// <param name="message">The message to write.</param>
    private void Write(LogType type, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        LogLevel level = type switch
        {
            LogType.Debug => LogLevel.Debug,
            LogType.Script => LogLevel.Info,
            LogType.Flash => LogLevel.Warn,
            _ => LogLevel.Info
        };
        HeadlessConsole.Log(level, $"[{type}] {message}");

        lock (_sync)
        {
            if (!_logs.TryGetValue(type, out Queue<string>? entries))
                return;

            while (entries.Count >= MaxEntries)
                entries.Dequeue();

            entries.Enqueue(message);
        }
    }
}
