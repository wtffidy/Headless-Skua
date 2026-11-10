using System.IO.Pipes;
using System.Reflection;
using Skua.Core.Interfaces;

namespace HeadlessSkua;

/// <summary>
/// Runs the existing ButlerV5 plugin through its public follow controls and provides
/// a local named-pipe command for stopping an unattended session.
/// </summary>
internal static class HeadlessButlerCommand
{
    private const string PipeName = "HeadlessSkua.Butler.Control";
    private const string PluginName = "ButlerV5 Avalonia";

    /// <summary>
    /// Starts ButlerV5 following the requested player until stopped or the optional duration expires.
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
            Console.Error.WriteLine($"Invalid Butler duration: {durationText}. Use 30m, 2h, 1d, or HH:MM:SS.");
            return 1;
        }

        IPluginContainer? container = pluginManager.GetContainer(PluginName);
        ISkuaPlugin? plugin = container?.Plugin;
        if (plugin is null)
        {
            Console.Error.WriteLine($"The {PluginName} plugin could not be loaded. Check the plugins folder and startup logs.");
            return 2;
        }

        MethodInfo? startMethod = plugin.GetType().GetMethod("StartFollow", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? stopMethod = plugin.GetType().GetMethod("StopFollow", BindingFlags.Public | BindingFlags.Instance);
        if (startMethod is null || stopMethod is null)
        {
            Console.Error.WriteLine("The installed ButlerV5 plugin does not expose the expected follow controls.");
            return 2;
        }

        ParameterInfo[] parameters = startMethod.GetParameters();
        if (parameters.Length != 2 || !parameters[1].ParameterType.IsEnum)
        {
            Console.Error.WriteLine("The installed ButlerV5 plugin has an incompatible StartFollow signature.");
            return 2;
        }

        object followSource = Enum.Parse(parameters[1].ParameterType, "Manual");
        startMethod.Invoke(plugin, new[] { username, followSource });

        Console.WriteLine($"[ButlerV5] Following {username}.");
        Console.WriteLine(duration.HasValue
            ? $"[ButlerV5] Session will end after {duration.Value:g}. Use 'HeadlessSkua -butler stop' to stop early."
            : "[ButlerV5] Running indefinitely. Use 'HeadlessSkua -butler stop' to stop.");

        Task stopRequest = WaitForStopRequestAsync(cancellationToken);
        Task durationElapsed = duration.HasValue
            ? Task.Delay(duration.Value, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        await Task.WhenAny(stopRequest, durationElapsed);

        try
        {
            stopMethod.Invoke(plugin, null);
            Console.WriteLine("[ButlerV5] Follow stopped.");
        }
        finally
        {
            if (plugin is not null)
                pluginManager.Unload(plugin);
        }

        return 0;
    }

    /// <summary>
    /// Sends a stop request to the active ButlerV5 process.
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
            Console.WriteLine("[ButlerV5] Stop request sent.");
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[ButlerV5] No active headless Butler session was found.");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[ButlerV5] Could not send stop request: {exception.Message}");
            return 1;
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

    private static bool TryParseDuration(string? value, out TimeSpan? duration)
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
