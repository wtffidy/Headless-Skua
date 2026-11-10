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

    /// <summary>
    /// Starts the Headless Skua host or handles a command-line information request.
    /// </summary>
    /// <param name="args">Command-line arguments supplied by the process.</param>
    /// <returns>A process exit code indicating success or failure.</returns>
    private static async Task<int> Main(string[] args)
    {
        HeadlessConsole.Init(args);
        HeadlessConsole.Banner(Version);

        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
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

            return await RunAsync(butlerArguments.ToArray());
        }

        if (args.Contains("run", StringComparer.OrdinalIgnoreCase))
            return await RunAsync(args);

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        PrintHelp();
        return 1;
    }

    /// <summary>
    /// Starts one headless Ruffle game session and keeps it alive until shutdown.
    /// </summary>
    /// <param name="args">Command-line arguments containing runtime and SWF paths.</param>
    /// <returns>A process exit code indicating whether the session started successfully.</returns>
    private static async Task<int> RunAsync(string[] args)
    {
        string runtimeDirectory = GetOption(args, "--runtime-dir") ?? Path.Combine("runtime", "ruffle-host");
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
            TaskCompletionSource swfLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

            flash.FlashCall += (name, values) =>
            {
                Console.WriteLine($"[AS3 -> Host] {name}: {string.Join(", ", values.Select(value => value?.ToString() ?? "null"))}");

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
                            flash.Call("loadClient");
                            HeadlessConsole.Success("AQW game-load request dispatched to the SWF.");
                        }
                        catch (Exception exception)
                        {
                            HeadlessConsole.Error($"Unable to dispatch the AQW game-load request: {exception.Message}");
                        }
                    });
                }
            };

            await runtime.StartAsync(swfPath, cancellationTokenSource.Token);
            HeadlessConsole.Info("Waiting for the Skua ActionScript client to initialize...");
            await swfLoaded.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationTokenSource.Token);
            HeadlessConsole.Success("Skua ActionScript client initialized.");

            ServiceCollection services = new();
            services.AddHeadlessSkua(flash);

            await using ServiceProvider serviceProvider = services.BuildServiceProvider();
            Ioc.Default.ConfigureServices(serviceProvider);
            Skua.Core.Models.ClientFileSources.EnsureInitialized();

            IPluginManager pluginManager = serviceProvider.GetRequiredService<IPluginManager>();
            pluginManager.Initialize();

            ISettingsService settings = serviceProvider.GetRequiredService<ISettingsService>();
            settings.SetApplicationVersion();

            // Resolve the real Skua script interface now so missing desktop dependencies fail at startup,
            // rather than halfway through an unattended script.
            IScriptInterface scriptInterface = serviceProvider.GetRequiredService<IScriptInterface>();
            IScriptManager scriptManager = serviceProvider.GetRequiredService<IScriptManager>();

            if (!args.Contains("--skip-login", StringComparer.OrdinalIgnoreCase)
                && !await HeadlessLoginFlow.LoginAsync(scriptInterface, cancellationTokenSource.Token))
                return 4;

            string? butlerTarget = GetOption(args, "--butler");
            if (!string.IsNullOrWhiteSpace(butlerTarget))
                return await HeadlessButlerCommand.RunAsync(pluginManager, butlerTarget, GetOption(args, "--duration"), cancellationTokenSource.Token);

            string? scriptPath = GetOption(args, "--script");
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
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            HeadlessConsole.Warn("Stopping Headless Skua...");
            return 0;
        }
        catch (Exception exception)
        {
            HeadlessConsole.Error($"Headless Skua failed: {exception.Message}");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
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
              --theme <name>    Console theme: midnight, ember, or neon.
              --no-color        Disable terminal colours.

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
        Console.WriteLine($"Ruffle JS        : {File.Exists(Path.Combine("runtime", "ruffle-host", "ruffle", "ruffle.js"))}");
        Console.WriteLine($"Skua SWF         : {File.Exists(Path.Combine("runtime", "ruffle-host", "game", "skua.swf"))}");
    }
}
