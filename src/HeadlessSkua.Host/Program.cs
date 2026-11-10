using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        if (args.Contains("run", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The headless game runtime is not available yet. See docs/roadmap.md for the current milestone.");
            return 2;
        }

        Console.Error.WriteLine($"Unknown command: {args[0]}");
        PrintHelp();
        return 1;
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
              doctor             Check the local runtime prerequisites.
              run                Start a game session (runtime prototype pending).

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
        Console.WriteLine($"Operating system : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture     : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($".NET runtime     : {Environment.Version}");
        Console.WriteLine($"Base directory   : {AppContext.BaseDirectory}");
        Console.WriteLine("Game runtime     : Not installed");
        Console.WriteLine("Session status   : Not started");
    }
}
