using System.IO.Pipes;
using System.Reflection;
using Skua.Core.Interfaces;
using Skua.Headless.UI;

namespace HeadlessSkua;

/// <summary>
/// Starts and supervises the existing ButlerV5 plugin through its public follow controls.
/// </summary>
internal static class HeadlessButlerCommand
{
    private const string PipeName = "HeadlessSkua.Butler.Control";
    private const string PluginName = "ButlerV5 Avalonia";

    /// <summary>
    /// Checks whether ButlerV5 is loaded, enabled, and able to begin following.
    /// </summary>
    /// <param name="pluginManager">The initialized Skua plugin manager.</param>
    /// <returns>A user-facing reason when Butler cannot start, otherwise null.</returns>
    public static string? GetStartBlockReason(IPluginManager pluginManager)
    {
        IPluginContainer? container = pluginManager.GetContainer(PluginName);
        ISkuaPlugin? plugin = container?.Plugin;
        if (plugin is null)
            return $"The {PluginName} plugin is not loaded.";

        Type pluginType = plugin.GetType();
        if (pluginType.GetMethod("StartFollow", BindingFlags.Public | BindingFlags.Instance) is null
            || pluginType.GetMethod("StopFollow", BindingFlags.Public | BindingFlags.Instance) is null)
            return "The installed ButlerV5 plugin does not expose the expected follow controls.";

        if (!ReadBooleanProperty(plugin, "PluginEnabled", true))
            return "ButlerV5 is disabled in its plugin options.";

        if (ReadBooleanProperty(plugin, "IsScriptRunning", false))
            return "ButlerV5 cannot start while a Skua script is running.";

        return null;
    }

    /// <summary>
    /// Resolves ButlerV5 and starts its public follow operation.
    /// </summary>
    /// <param name="pluginManager">The initialized Skua plugin manager.</param>
    /// <param name="username">The player to follow.</param>
    /// <param name="session">The started follow session, when successful.</param>
    /// <param name="error">A user-facing explanation when startup fails.</param>
    /// <returns>True when ButlerV5 accepted the follow operation.</returns>
    public static bool TryStartFollow(IPluginManager pluginManager, string username, out ButlerFollowSession? session, out string? error)
    {
        session = null;
        error = null;

        if (string.IsNullOrWhiteSpace(username))
        {
            error = "Enter the player name Butler should follow.";
            return false;
        }

        IPluginContainer? container = pluginManager.GetContainer(PluginName);
        ISkuaPlugin? plugin = container?.Plugin;
        if (plugin is null)
        {
            error = $"The {PluginName} plugin is not loaded. Check the plugins folder and startup logs.";
            return false;
        }

        Type pluginType = plugin.GetType();
        MethodInfo? startMethod = pluginType.GetMethod("StartFollow", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? stopMethod = pluginType.GetMethod("StopFollow", BindingFlags.Public | BindingFlags.Instance);
        if (startMethod is null || stopMethod is null)
        {
            error = "The installed ButlerV5 plugin does not expose the expected follow controls.";
            return false;
        }

        ParameterInfo[] parameters = startMethod.GetParameters();
        if (parameters.Length != 2
            || parameters[0].ParameterType != typeof(string)
            || !parameters[1].ParameterType.IsEnum)
        {
            error = "The installed ButlerV5 plugin has an incompatible StartFollow signature.";
            return false;
        }

        if (ReadBooleanProperty(plugin, "PluginEnabled", true) is false)
        {
            error = "ButlerV5 is disabled in its plugin options. Enable ButlerV5 before starting a follow.";
            return false;
        }

        if (ReadBooleanProperty(plugin, "IsScriptRunning", false))
        {
            error = "ButlerV5 cannot start while a Skua script is already running.";
            return false;
        }

        try
        {
            object followSource = Enum.Parse(parameters[1].ParameterType, "Manual");
            startMethod.Invoke(plugin, new object?[] { username.Trim(), followSource });

            string? followingWho = pluginType.GetProperty("FollowingWho", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(plugin) as string;

            if (!string.Equals(followingWho, username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                stopMethod.Invoke(plugin, null);
                error = "ButlerV5 did not enter a follow session. Check the ButlerV5 plugin log for its reason.";
                return false;
            }

            session = new ButlerFollowSession(plugin, stopMethod);
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            error = $"ButlerV5 failed to start: {exception.InnerException.Message}";
            return false;
        }
        catch (Exception exception)
        {
            error = $"ButlerV5 failed to start: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Starts ButlerV5 and waits until the named-pipe stop command, cancellation, or optional duration expires.
    /// </summary>
    /// <param name="pluginManager">The initialized Skua plugin manager.</param>
    /// <param name="username">The player to follow.</param>
    /// <param name="durationText">Optional duration such as 30m, 2h, or 01:30:00.</param>
    /// <param name="cancellationToken">Token used to stop the session.</param>
    /// <returns>A process exit code.</returns>
    public static async Task<int> RunAsync(IPluginManager pluginManager, string username, string? durationText, CancellationToken cancellationToken)
    {
        if (!TryParseDuration(durationText, out TimeSpan? duration))
        {
            HeadlessConsole.Error($"Invalid Butler duration: {durationText}. Use 30m, 2h, 1d, or HH:MM:SS.");
            return 1;
        }

        if (!TryStartFollow(pluginManager, username, out ButlerFollowSession? session, out string? error) || session is null)
        {
            HeadlessConsole.Error(error ?? "ButlerV5 could not start.");
            return 2;
        }

        HeadlessConsole.Success($"ButlerV5 is following {username.Trim()}.");
        HeadlessConsole.Info(duration.HasValue
            ? $"Session ends after {duration.Value:g}. Use 'HeadlessSkua -butler stop' to stop early."
            : "Running indefinitely. Use 'HeadlessSkua -butler stop' to stop.");

        await WaitForSessionAsync(session, duration, cancellationToken, useNamedPipe: true, unloadPlugin: true, pluginManager);
        return 0;
    }

    /// <summary>
    /// Keeps an already-started follow session alive until cancellation or its optional duration expires.
    /// </summary>
    /// <param name="session">The active Butler follow session.</param>
    /// <param name="duration">Optional session duration.</param>
    /// <param name="cancellationToken">Token used to stop the session.</param>
    /// <param name="useNamedPipe">Whether to also listen for the CLI stop command.</param>
    /// <param name="unloadPlugin">Whether the plugin should be unloaded after the follow stops.</param>
    /// <param name="pluginManager">The plugin manager used when unloading is requested.</param>
    public static async Task WaitForSessionAsync(ButlerFollowSession session, TimeSpan? duration, CancellationToken cancellationToken, bool useNamedPipe, bool unloadPlugin, IPluginManager pluginManager)
    {
        using CancellationTokenSource stopListenerSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task stopRequest = useNamedPipe
            ? WaitForStopRequestAsync(stopListenerSource.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, stopListenerSource.Token);
        Task durationElapsed = duration.HasValue
            ? Task.Delay(duration.Value, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        try
        {
            await Task.WhenAny(stopRequest, durationElapsed);
        }
        finally
        {
            stopListenerSource.Cancel();

            try
            {
                await stopRequest;
            }
            catch (OperationCanceledException) when (stopListenerSource.IsCancellationRequested)
            {
            }

            try
            {
                session.Stop();
                HeadlessConsole.Info("ButlerV5 follow stopped.");
            }
            finally
            {
                if (unloadPlugin)
                    pluginManager.Unload(session.Plugin);
            }
        }
    }

    /// <summary>
    /// Sends a stop request to the active ButlerV5 CLI session.
    /// </summary>
    /// <returns>A process exit code.</returns>
    public static async Task<int> StopAsync()
    {
        try
        {
            await using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            await using StreamWriter writer = new(client);
            await writer.WriteLineAsync("stop");
            await writer.FlushAsync();
            HeadlessConsole.Success("ButlerV5 stop request sent.");
            return 0;
        }
        catch (TimeoutException)
        {
            HeadlessConsole.Warn("No active headless Butler session was found.");
            return 1;
        }
        catch (IOException exception)
        {
            HeadlessConsole.Error($"Could not send the Butler stop request: {exception.Message}");
            return 1;
        }
    }

    private static bool ReadBooleanProperty(ISkuaPlugin plugin, string propertyName, bool fallback)
    {
        try
        {
            object? value = plugin.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(plugin);
            return value is bool result ? result : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task WaitForStopRequestAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream server = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken);
            using StreamReader reader = new(server);
            string? command = await reader.ReadLineAsync(cancellationToken);
            if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase))
                return;
        }
    }

    /// <summary>
    /// Parses an optional Butler duration.
    /// </summary>
    /// <param name="value">Duration text such as 30m, 2h, 1d, or HH:MM:SS.</param>
    /// <param name="duration">The parsed duration, or null for an unlimited session.</param>
    /// <returns>True when the value is valid.</returns>
    public static bool TryParseDuration(string? value, out TimeSpan? duration)
    {
        duration = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string input = value.Trim();
        if (TimeSpan.TryParse(input, out TimeSpan parsed))
        {
            if (parsed > TimeSpan.Zero)
            {
                duration = parsed;
                return true;
            }

            return false;
        }

        char unit = char.ToLowerInvariant(input[^1]);
        if (!double.TryParse(input[..^1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out double amount) || amount <= 0)
            return false;

        double seconds = unit switch
        {
            's' => amount,
            'm' => amount * 60,
            'h' => amount * 3600,
            'd' => amount * 86400,
            _ => -1
        };

        if (seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return false;

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }
}

/// <summary>
/// Holds the plugin instance and its public stop method for an active Butler follow.
/// </summary>
internal sealed class ButlerFollowSession
{
    private readonly MethodInfo _stopMethod;
    private int _stopped;

    /// <summary>
    /// The plugin instance that owns this follow session.
    /// </summary>
    public ISkuaPlugin Plugin { get; }

    /// <summary>
    /// Creates a session wrapper for an already-started Butler follow.
    /// </summary>
    public ButlerFollowSession(ISkuaPlugin plugin, MethodInfo stopMethod)
    {
        Plugin = plugin;
        _stopMethod = stopMethod;
    }

    /// <summary>
    /// Stops the follow operation once.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        try
        {
            _stopMethod.Invoke(Plugin, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException($"ButlerV5 could not stop cleanly: {exception.InnerException.Message}", exception.InnerException);
        }
    }
}
