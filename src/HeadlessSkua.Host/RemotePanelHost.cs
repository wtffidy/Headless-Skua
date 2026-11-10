using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Headless.UI;

namespace HeadlessSkua;

/// <summary>
/// Hosts the local dashboard and exposes a small control API over the active Skua services.
/// </summary>
internal sealed class RemotePanelHost : IAsyncDisposable
{
    private readonly IScriptInterface _bot;
    private readonly IScriptManager _scriptManager;
    private readonly IPluginManager _pluginManager;
    private readonly HeadlessLogService _logs;
    private readonly bool _remoteEnabled;
    private readonly int _port;
    private readonly string _panelDirectory;
    private readonly object _butlerSync = new();
    private WebApplication? _application;
    private CancellationTokenSource? _butlerCancellation;
    private Task? _butlerTask;

    /// <summary>
    /// Creates the dashboard host. It binds to loopback unless remote access is explicitly enabled.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <param name="scriptManager">The active Skua script manager.</param>
    /// <param name="pluginManager">The initialized Skua plugin manager.</param>
    /// <param name="logs">The shared headless log history.</param>
    /// <param name="remoteEnabled">Whether to listen on all network interfaces.</param>
    /// <param name="port">The TCP port used by the dashboard.</param>
    public RemotePanelHost(IScriptInterface bot, IScriptManager scriptManager, IPluginManager pluginManager, HeadlessLogService logs, bool remoteEnabled, int port)
    {
        _bot = bot;
        _scriptManager = scriptManager;
        _pluginManager = pluginManager;
        _logs = logs;
        _remoteEnabled = remoteEnabled;
        _port = port is >= 1 and <= 65535 ? port : throw new ArgumentOutOfRangeException(nameof(port));
        _panelDirectory = ResolvePanelDirectory();
    }

    /// <summary>
    /// Resolves panel assets beside the actual executable before checking runtime extraction and development paths.
    /// </summary>
    /// <returns>The first panel directory containing index.html, or the executable-relative default.</returns>
    private static string ResolvePanelDirectory()
    {
        string? executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string[] candidates = new[]
        {
            string.IsNullOrWhiteSpace(executableDirectory) ? null : Path.Combine(executableDirectory, "panel"),
            Path.Combine(AppContext.BaseDirectory, "panel"),
            Path.Combine(Environment.CurrentDirectory, "panel")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()!;

        foreach (string candidate in candidates)
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;

        return candidates.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "panel");
    }

    /// <summary>
    /// Starts the dashboard web server and prints its available addresses.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel web-host startup.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(_panelDirectory, "index.html");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("The remote panel files were not published.", indexPath);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _panelDirectory,
            Args = Array.Empty<string>()
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(_remoteEnabled ? IPAddress.Any : IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();

        _application = builder.Build();

        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".js"] = "text/javascript";
        contentTypes.Mappings[".css"] = "text/css";

        _application.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(_panelDirectory)
        });
        _application.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_panelDirectory),
            ContentTypeProvider = contentTypes
        });

        MapApi(_application);
        await _application.StartAsync(cancellationToken);

        HeadlessConsole.Success($"Remote panel listening on port {_port}.");
        HeadlessConsole.Panel("Remote Panel",
            ("Local URL", $"http://127.0.0.1:{_port}"),
            ("Binding", _remoteEnabled ? "0.0.0.0 (LAN/VPN)" : "127.0.0.1 (local only)"));

        if (!_remoteEnabled)
            return;

        foreach (string address in GetNetworkAddresses())
            HeadlessConsole.Info($"Panel address: http://{address}:{_port}");

        HeadlessConsole.Warn("Remote panel control has no login in this version. Only enable it on a trusted LAN/VPN, and do not port-forward this port.");
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/status", GetStatus);
        app.MapGet("/api/logs", GetLogs);
        app.MapPost("/api/script/start", StartScriptAsync);
        app.MapPost("/api/script/stop", StopScript);
        app.MapPost("/api/butler/start", StartButler);
        app.MapPost("/api/butler/stop", StopButler);
    }

    private IResult GetStatus()
    {
        bool worldLoaded = _bot.Flash.IsWorldLoaded;
        return Results.Ok(new
        {
            connected = _bot.Player.Playing && worldLoaded,
            runtime = worldLoaded ? "World loaded" : _bot.Player.Playing ? "Connecting" : "Not connected",
            server = _bot.Servers.LastName,
            scriptRunning = _scriptManager.ScriptRunning,
            butlerRunning = _butlerTask is { IsCompleted: false }
        });
    }

    private IResult GetLogs()
    {
        return Results.Ok(new
        {
            debug = Tail(_logs.GetLogs(LogType.Debug), 150),
            script = Tail(_logs.GetLogs(LogType.Script), 150),
            flash = Tail(_logs.GetLogs(LogType.Flash), 150)
        });
    }

    private async Task<IResult> StartScriptAsync(ScriptStartRequest request)
    {
        if (!_bot.Player.Playing || !_bot.Flash.IsWorldLoaded)
            return Results.Conflict(new { error = "Connect to a game server before starting a script." });
        if (_scriptManager.ScriptRunning)
            return Results.Conflict(new { error = "A script is already running." });
        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { error = "Enter the full path to a script on the Headless Skua computer." });

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(request.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Results.BadRequest(new { error = "The supplied script path is invalid." });
        }

        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "The script file was not found on the Headless Skua computer." });

        try
        {
            _scriptManager.SetLoadedScript(fullPath);
            _scriptManager.SilentConfig = true;
            Exception? exception = await _scriptManager.StartScript();
            if (exception is not null)
                return Results.Problem($"The script failed to start: {exception.Message}");

            HeadlessConsole.Info($"Remote panel started script: {fullPath}");
            return Results.Ok(new { message = "Script started.", path = fullPath });
        }
        catch (Exception exception)
        {
            HeadlessConsole.Error($"Remote script start failed: {exception.Message}");
            return Results.Problem($"The script could not be started: {exception.Message}");
        }
    }

    private IResult StopScript()
    {
        if (!_scriptManager.ScriptRunning)
            return Results.Conflict(new { error = "No script is currently running." });

        _scriptManager.ScriptCts?.Cancel();
        HeadlessConsole.Warn("Script stop requested from the remote panel.");
        return Results.Ok(new { message = "Script stop requested." });
    }

    private IResult StartButler(ButlerStartRequest request)
    {
        if (!_bot.Player.Playing || !_bot.Flash.IsWorldLoaded)
            return Results.Conflict(new { error = "Connect to a game server before starting Butler." });
        if (string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new { error = "Enter the player name Butler should follow." });

        lock (_butlerSync)
        {
            if (_butlerTask is { IsCompleted: false })
                return Results.Conflict(new { error = "A Butler session is already running." });

            _butlerCancellation?.Dispose();
            _butlerCancellation = new CancellationTokenSource();
            CancellationToken token = _butlerCancellation.Token;
            string username = request.Username.Trim();
            string? duration = string.IsNullOrWhiteSpace(request.Duration) ? null : request.Duration.Trim();

            _butlerTask = Task.Run(async () =>
            {
                try
                {
                    int result = await HeadlessButlerCommand.RunAsync(_pluginManager, username, duration, token);
                    if (result != 0)
                        HeadlessConsole.Error($"Remote Butler session ended with exit code {result}.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    HeadlessConsole.Error($"Remote Butler session failed: {exception.Message}");
                }
            });
        }

        HeadlessConsole.Info($"Remote panel requested Butler to follow {request.Username.Trim()}.");
        return Results.Accepted(value: new { message = $"Butler is starting to follow {request.Username.Trim()}." });
    }

    private IResult StopButler()
    {
        lock (_butlerSync)
        {
            if (_butlerTask is not { IsCompleted: false } || _butlerCancellation is null)
                return Results.Conflict(new { error = "No Butler session is currently running." });

            _butlerCancellation.Cancel();
        }

        HeadlessConsole.Warn("Butler stop requested from the remote panel.");
        return Results.Ok(new { message = "Butler stop requested." });
    }

    private static IEnumerable<string> GetNetworkAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] Tail(List<string> entries, int count)
    {
        int skip = Math.Max(0, entries.Count - count);
        return entries.Skip(skip).ToArray();
    }

    /// <summary>
    /// Cancels remote Butler work and shuts down the web server.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_butlerSync)
            _butlerCancellation?.Cancel();

        if (_butlerTask is not null)
        {
            try
            {
                await _butlerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                HeadlessConsole.Warn("Butler did not stop before the remote panel shut down.");
            }
        }

        _butlerCancellation?.Dispose();

        if (_application is not null)
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed record ScriptStartRequest(string Path);
    private sealed record ButlerStartRequest(string Username, string? Duration);
}
