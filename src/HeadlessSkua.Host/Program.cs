using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Skua.Core.Interfaces;
using Skua.Headless.UI;

namespace HeadlessSkua;

internal static class Program
{
    private const string Version = "0.1.0";
    private static readonly ConcurrentDictionary<string, byte> LoggedPacketTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the Headless Skua host or handles a command-line information request.
    /// </summary>
    /// <param name="args">Command-line arguments supplied by the process.</param>
    /// <returns>A process exit code indicating success or failure.</returns>
    private static async Task<int> Main(string[] args)
    {
        HeadlessConsole.Init(args);
        HeadlessConsole.Banner(Version);

        // Double-clicking the executable starts the normal headless session.
        // Keep the console visible on any interactive startup failure so the final error can be read.
        if (args.Length == 0)
            return await RunWithFailurePauseAsync(new[] { "run" });

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase) || args.Contains("-v", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Headless Skua {Version}");
            return 0;
        }

        if (args.Contains("doctor", StringComparer.OrdinalIgnoreCase))
        {
            PrintDiagnostics();
            return 0;
        }

        if (string.Equals(args[0], "-butler", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: HeadlessSkua -butler <username> [duration] | -butler stop");
                return 1;
            }

            if (string.Equals(args[1], "stop", StringComparison.OrdinalIgnoreCase))
                return await HeadlessButlerCommand.StopAsync();

            List<string> butlerArguments = new() { "run", "--butler", args[1] };
            int optionIndex = 2;
            if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
            {
                butlerArguments.Add("--duration");
                butlerArguments.Add(args[2]);
                optionIndex = 3;
            }

            for (int index = optionIndex; index < args.Length; index++)
                butlerArguments.Add(args[index]);

            return await RunWithFailurePauseAsync(butlerArguments.ToArray());
        }

        if (args.Contains("run", StringComparer.OrdinalIgnoreCase))
            return await RunWithFailurePauseAsync(args);

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        PrintHelp();
        return 1;
    }

    /// <summary>
    /// Starts a runtime command and keeps interactive consoles open when startup fails.
    /// </summary>
    /// <param name="args">The runtime command arguments.</param>
    /// <returns>The runtime process exit code.</returns>
    private static async Task<int> RunWithFailurePauseAsync(string[] args)
    {
        int exitCode = await RunAsync(args);
        if (exitCode == 0 || Console.IsInputRedirected || Console.IsOutputRedirected)
            return exitCode;

        Console.WriteLine();
        Console.WriteLine($"Headless Skua stopped with exit code {exitCode}. Press any key to close this window...");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
        }

        return exitCode;
    }

    /// <summary>
    /// Starts one headless Ruffle game session and keeps it alive until shutdown.
    /// </summary>
    /// <param name="args">Command-line arguments containing runtime and SWF paths.</param>
    /// <returns>A process exit code indicating whether the session started successfully.</returns>
    private static async Task<int> RunAsync(string[] args)
    {
        Stopwatch startupTimer = Stopwatch.StartNew();
        string runtimeDirectory = GetOption(args, "--runtime-dir") ?? GetDefaultRuntimeDirectory();
        string swfPath = GetOption(args, "--swf") ?? Path.Combine(runtimeDirectory, "game", "skua.swf");
        using CancellationTokenSource cancellationTokenSource = new();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            await using RuffleRuntime runtime = new(runtimeDirectory);
            using RuffleFlashUtil flash = new(runtime);
            HeadlessSessionState sessionState = new();
            TaskCompletionSource swfLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HeadlessLogService? diagnosticLogs = null;
            int loginAcceptedLogged = 0;

            flash.FlashCall += (name, values) =>
            {
                sessionState.Observe(name, values);

                if (name.Equals("packetFromServer", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("packet", StringComparison.OrdinalIgnoreCase))
                {
                    string direction = name.Equals("packetFromServer", StringComparison.OrdinalIgnoreCase) ? "server" : "client";
                    foreach (string packet in values.OfType<string>())
                    {
                        string? packetType = GetPacketType(packet);
                        if (!string.IsNullOrWhiteSpace(packetType)
                            && LoggedPacketTypes.TryAdd($"{direction}:{packetType}", 0))
                        {
                            string packetMessage = $"AQW {direction} packet type: {packetType} ({System.Text.Encoding.UTF8.GetByteCount(packet)} bytes).";
                            if (diagnosticLogs is not null)
                                diagnosticLogs.DebugLog(packetMessage);
                            else
                                HeadlessConsole.Debug(packetMessage);
                        }

                        if (direction == "client" && TryGetTransferMap(packet, out string? targetMap)
                            && LoggedPacketTypes.TryAdd($"client:transfer:{targetMap}", 0))
                        {
                            string transferMessage = $"AQW map transfer packet emitted: {targetMap}.";
                            if (diagnosticLogs is not null)
                                diagnosticLogs.DebugLog(transferMessage);
                            else
                                HeadlessConsole.Info(transferMessage);
                        }
                    }
                }
                if ((string.Equals(name, "packetFromServer", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "pext", StringComparison.OrdinalIgnoreCase))
                    && values.OfType<string>().Any(value => value.Contains("loginResponse", StringComparison.OrdinalIgnoreCase)))
                    HeadlessConsole.Debug($"AQW loginResponse packet received through {name}.");

                (bool loginAccepted, string? acceptedAccount) = sessionState.GetSnapshot();

                if (loginAccepted && Interlocked.CompareExchange(ref loginAcceptedLogged, 1, 0) == 0)
                    HeadlessConsole.Success($"AQW accepted the game login{(string.IsNullOrWhiteSpace(acceptedAccount) ? string.Empty : $" for {acceptedAccount}")}.");
                else if (string.Equals(name, "connectionLost", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Exchange(ref loginAcceptedLogged, 0);

                if (!HeadlessConsole.Quiet
                    && !name.Equals("packetFromServer", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("pext", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("debug", StringComparison.OrdinalIgnoreCase))
                    HeadlessConsole.Debug($"[AS3 -> Host] {name}");

                if (string.Equals(name, "loaded", StringComparison.OrdinalIgnoreCase))
                {
                    swfLoaded.TrySetResult();
                    return;
                }

                // The SWF asks its host to begin loading AQW after Externalizer initializes.
                // Invoke loadClient only after the browser callback has returned; calling back
                // into the same Playwright page inline can deadlock the exposed binding.
                if (string.Equals(name, "requestLoadGame", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // The Skua bootstrap defaults to hideme.swf, which AQW no longer serves.
                            // Set a valid built-in title background before loadClient copies sBG into game.params.
                            flash.Call("setBackgroundValues", "Generic2.swf", string.Empty);
                            HeadlessConsole.Info("Using Generic2.swf for the AQW title background.");

                            HeadlessConsole.Debug("Queuing the SWF loadClient callback...");
                            flash.Dispatch("loadClient");
                            HeadlessConsole.Success("AQW game-load callback queued in Ruffle.");
                        }
                        catch (Exception exception)
                        {
                            HeadlessConsole.Error($"Unable to dispatch the AQW game-load request: {exception.Message}");
                        }
                    });
                }
            };

            await runtime.StartAsync(swfPath, cancellationTokenSource.Token);
            HeadlessConsole.Info($"Ruffle browser started in {startupTimer.Elapsed.TotalSeconds:F1}s.");
            HeadlessConsole.Info("Waiting for the Skua ActionScript client to initialize...");
            await swfLoaded.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationTokenSource.Token);
            HeadlessConsole.Success($"Skua ActionScript client initialized in {startupTimer.Elapsed.TotalSeconds:F1}s.");

            ServiceCollection services = new();
            services.AddHeadlessSkua(flash);

            ServiceProvider serviceProvider = services.BuildServiceProvider();
            try
            {
                Ioc.Default.ConfigureServices(serviceProvider);
                _ = typeof(System.Windows.Forms.HeadlessCompatibilityMarker).Assembly;
                Skua.Core.Models.ClientFileSources.EnsureInitialized();

                IPluginManager pluginManager = serviceProvider.GetRequiredService<IPluginManager>();

                ISettingsService settings = serviceProvider.GetRequiredService<ISettingsService>();
                settings.SetApplicationVersion();

                // Resolve services without starting their background polling or loading plugins.
                // AQW creates world.myAvatar and its inventory asynchronously after loginResponse.
                // Starting Skua's 20 ms ScriptTimer or plugins before that point causes null-object
                // callbacks (inventoryBagUsedSlots, HidePlayers, and map response handlers).
                IScriptInterface scriptInterface = serviceProvider.GetRequiredService<IScriptInterface>();
                IScriptInterfaceManager scriptInterfaceManager = serviceProvider.GetRequiredService<IScriptInterfaceManager>();

                HeadlessLogService headlessLogs = serviceProvider.GetRequiredService<HeadlessLogService>();
                diagnosticLogs = headlessLogs;
                flash.DiagnosticLogged += headlessLogs.DebugLog;

                IScriptManager scriptManager = serviceProvider.GetRequiredService<IScriptManager>();

                await using RemotePanelHost? remotePanel = args.Contains("--no-panel", StringComparer.OrdinalIgnoreCase)
                    ? null
                    : new RemotePanelHost(
                        scriptInterface,
                        scriptManager,
                        pluginManager,
                        sessionState,
                        headlessLogs,
                        args.Contains("--remote", StringComparer.OrdinalIgnoreCase),
                        GetPanelPort(args));

                if (remotePanel is not null)
                    await remotePanel.StartAsync(cancellationTokenSource.Token);
                HeadlessConsole.Info($"Headless startup completed in {startupTimer.Elapsed.TotalSeconds:F1}s. Waiting for account login.");

                if (!args.Contains("--skip-login", StringComparer.OrdinalIgnoreCase)
                    && !await HeadlessLoginFlow.LoginAsync(scriptInterface, sessionState, cancellationTokenSource.Token))
                    return 4;

                string? butlerTarget = GetOption(args, "--butler");
                string? scriptPath = GetOption(args, "--script");
                if (!args.Contains("--skip-login", StringComparer.OrdinalIgnoreCase))
                {
                    if (!await HeadlessLoginFlow.WaitForAutomationReadyAsync(
                        scriptInterface,
                        TimeSpan.FromSeconds(60),
                        cancellationTokenSource.Token))
                        return 4;
                }

                // Only start Skua's recurring game-state work and load plugins after AQW has
                // fully created the character, inventory, and player assets.
                scriptInterfaceManager.Initialize();
                pluginManager.Initialize();
                HeadlessConsole.Success($"Skua game-state services initialized after player readiness in {startupTimer.Elapsed.TotalSeconds:F1}s.");

                if (!string.IsNullOrWhiteSpace(butlerTarget))
                    return await HeadlessButlerCommand.RunAsync(pluginManager, butlerTarget, GetOption(args, "--duration"), cancellationTokenSource.Token);

                if (!string.IsNullOrWhiteSpace(scriptPath))
                {
                    string fullScriptPath = Path.GetFullPath(scriptPath);
                    if (!File.Exists(fullScriptPath))
                        throw new FileNotFoundException("The requested Skua script was not found.", fullScriptPath);

                    HeadlessConsole.Info($"Starting script: {fullScriptPath}");
                    scriptManager.SetLoadedScript(fullScriptPath);

                    scriptManager.SilentConfig = true;
                    Trace.Listeners.Add(new ConsoleTraceListener());
                    Trace.AutoFlush = true;

                    Exception? scriptException = await scriptManager.StartScript();

                    if (scriptException is not null)
                    {
                        Console.Error.WriteLine($"Script failed to start: {scriptException}");
                        return 3;
                    }

                    while (scriptManager.ScriptRunning)
                    {
                        if (cancellationTokenSource.IsCancellationRequested)
                        {
                            try
                            {
                                scriptManager.ScriptCts?.Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }

                        await Task.Delay(250);
                    }

                    Console.WriteLine("Script execution stopped.");
                    return 0;
                }

                HeadlessConsole.Success("Headless game runtime ready. Press Ctrl+C to stop.");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationTokenSource.Token);
                return 0;
            }
            finally
            {
                try
                {
                    await serviceProvider.DisposeAsync();
                }
                catch (OperationCanceledException exception)
                {
                    HeadlessConsole.Debug($"Skua background services canceled during shutdown: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            HeadlessConsole.Warn("Stopping Headless Skua...");
            return 0;
        }
        catch (Exception exception)
        {
            HeadlessConsole.Error($"Headless Skua failed: {exception.GetType().Name}: {exception.Message}");
            HeadlessConsole.Debug(exception.ToString());
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Extracts the SmartFox packet command without exposing packet contents or account data.
    /// </summary>
    /// <param name="packet">The packet trace emitted by the AQW client.</param>
    /// <returns>The packet command name, or null when it cannot be identified.</returns>
    private static string? GetPacketType(string packet)
    {
        int marker = packet.IndexOf("%xt%", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            string[] fields = packet[(marker + 1)..].Split('%');
            return fields.Length > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1].Trim() : null;
        }

        int separator = packet.IndexOf('%');
        return separator > 0 ? packet[..separator].Trim() : null;
    }

    /// <summary>
    /// Extracts a map transfer target without logging account names, room numbers, or packet contents.
    /// </summary>
    /// <param name="packet">The outbound AQW packet.</param>
    /// <param name="targetMap">The normalized map name, when the packet is a transfer.</param>
    /// <returns>True when the packet requests a map transfer.</returns>
    private static bool TryGetTransferMap(string packet, out string? targetMap)
    {
        targetMap = null;
        string[] fields = packet.Split('%');
        if (fields.Length < 9
            || !string.Equals(fields[1], "xt", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields[2], "zm", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields[3], "cmd", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields[5], "tfer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(fields[7]))
            return false;

        string map = fields[7];
        int suffix = map.LastIndexOf('-');
        if (suffix > 0 && int.TryParse(map[(suffix + 1)..], out _))
            map = map[..suffix];

        targetMap = map;
        return true;
    }

    /// <summary>
    /// Returns the value following a command-line option, when supplied.
    /// </summary>
    /// <param name="args">All command-line arguments.</param>
    /// <param name="option">Option name to locate.</param>
    /// <returns>The option value, or null when it was not supplied.</returns>
    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];

        return null;
    }

    /// <summary>
    /// Resolves the bundled Ruffle runtime beside the executable, with a repository-root fallback for development runs.
    /// </summary>
    /// <returns>The available runtime directory.</returns>
    private static string GetDefaultRuntimeDirectory()
    {
        foreach (string startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "runtime", "ruffle-host");
                if (File.Exists(Path.Combine(candidate, "index.html")))
                    return candidate;

                directory = directory.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "runtime", "ruffle-host");
    }

    /// <summary>
    /// Reads and validates the optional remote panel port.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The configured panel port, or 8788 when omitted.</returns>
    private static int GetPanelPort(string[] args)
    {
        string? value = GetOption(args, "--panel-port");
        if (string.IsNullOrWhiteSpace(value))
            return 8788;

        if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException("--panel-port", "The panel port must be between 1 and 65535.");

        return port;
    }

    /// <summary>
    /// Prints supported commands and options.
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine($"""
            Headless Skua {Version}

            Usage:
              HeadlessSkua [command] [options]

            Commands:
              doctor             Check local runtime prerequisites.
              run                Start the headless game runtime.
              -butler <user> [duration]
                                 Follow a player with ButlerV5; no duration means run until stopped.
              -butler stop        Stop the active headless ButlerV5 session.

            Run options:
              --swf <path>       Path to the Skua SWF (defaults to runtime game/skua.swf).
              --runtime-dir <path>
                                 Ruffle host directory (defaults to runtime/ruffle-host).
              --script <path>   Existing Skua C# script to execute after client initialization.
              --remote          Bind the web panel to 0.0.0.0 for trusted LAN/VPN access.
              --panel-port <port>
                                 Web panel port (default: 8788).
              --no-panel        Disable the web panel.
              --theme <name>    Console theme: midnight, ember, or neon.
              --quiet           Hide Ruffle/browser and ActionScript trace noise.
              --no-color        Disable terminal colours.

            Remote panel:
              Open http://127.0.0.1:8788 locally, or use --remote for LAN/VPN access.
              Remote control has no login in this version. Do not port-forward the panel port.

            Options:
              -h, --help         Show this help.
              -v, --version      Show the application version.
            """);
    }

    /// <summary>
    /// Reports basic host prerequisites without starting a game session.
    /// </summary>
    private static void PrintDiagnostics()
    {
        Console.WriteLine($"Headless Skua {Version}");
        Console.WriteLine($"Operating system : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture     : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($".NET runtime     : {Environment.Version}");
        Console.WriteLine($"Base directory   : {AppContext.BaseDirectory}");
        string runtimeDirectory = GetDefaultRuntimeDirectory();
        Console.WriteLine($"Ruffle directory : {runtimeDirectory}");
        Console.WriteLine($"Ruffle JS        : {File.Exists(Path.Combine(runtimeDirectory, "ruffle", "ruffle.js"))}");
        Console.WriteLine($"Skua SWF         : {File.Exists(Path.Combine(runtimeDirectory, "game", "skua.swf"))}");
        Console.WriteLine($"Remote panel     : {File.Exists(Path.Combine(AppContext.BaseDirectory, "panel", "index.html"))}");
        Console.WriteLine($"Playwright Node  : {File.Exists(Path.Combine(AppContext.BaseDirectory, ".playwright", "node", "win32_x64", "node.exe"))}");
        Console.WriteLine($"Playwright CLI   : {File.Exists(Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "cli.js"))}");
    }
}
