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
    private readonly HeadlessSessionState _sessionState;
    private readonly HeadlessLogService _logs;
    private readonly bool _remoteEnabled;
    private readonly int _port;
    private readonly string _panelDirectory;
    private readonly object _butlerSync = new();
    private readonly object _statusSync = new();
    private WebApplication? _application;
    private CancellationTokenSource? _butlerCancellation;
    private Task? _butlerTask;
    private CancellationTokenSource? _statusMonitorCancellation;
    private Task? _statusMonitorTask;
    private GameStatusSnapshot _statusSnapshot = new(false, false, false, null, null);

    /// <summary>
    /// Creates the dashboard host. It binds to loopback unless remote access is explicitly enabled.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <param name="scriptManager">The active Skua script manager.</param>
    /// <param name="pluginManager">The initialized Skua plugin manager.</param>
    /// <param name="sessionState">AQW authentication state received directly from the game server.</param>
    /// <param name="logs">The shared headless log history.</param>
    /// <param name="remoteEnabled">Whether to listen on all network interfaces.</param>
    /// <param name="port">The TCP port used by the dashboard.</param>
    public RemotePanelHost(IScriptInterface bot, IScriptManager scriptManager, IPluginManager pluginManager, HeadlessSessionState sessionState, HeadlessLogService logs, bool remoteEnabled, int port)
    {
        _bot = bot;
        _scriptManager = scriptManager;
        _pluginManager = pluginManager;
        _sessionState = sessionState;
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
        List<string> candidates = new();

        if (!string.IsNullOrWhiteSpace(executableDirectory))
            candidates.Add(Path.Combine(executableDirectory, "panel"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "panel"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "panel"));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;

        return candidates[0];
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

        _statusMonitorCancellation = new CancellationTokenSource();
        _statusMonitorTask = Task.Run(() => RefreshStatusLoopAsync(_statusMonitorCancellation.Token));

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
        app.MapGet("/api/scripts", (string? path) => GetScripts(path));
        app.MapGet("/api/logs", GetLogs);
        app.MapPost("/api/script/start", StartScriptAsync);
        app.MapPost("/api/script/stop", StopScript);
        app.MapPost("/api/butler/start", StartButler);
        app.MapPost("/api/butler/stop", StopButler);
    }

    private IResult GetStatus()
    {
        (bool loginAccepted, string? acceptedAccount) = _sessionState.GetSnapshot();
        GameStatusSnapshot snapshot = GetStatusSnapshot();
        bool loggedIn = loginAccepted || snapshot.LoggedIn;
        bool worldLoaded = loggedIn && snapshot.WorldLoaded;
        bool alive = worldLoaded && snapshot.Alive;
        bool connected = loggedIn && worldLoaded && alive;
        string? account = string.IsNullOrWhiteSpace(acceptedAccount) ? snapshot.Account : acceptedAccount;
        string runtime = !loggedIn
            ? "Not logged in"
            : !worldLoaded
                ? "Login accepted · loading world"
                : !alive
                    ? "World loaded · character not alive"
                    : "Connected";
        string connectionMessage = !loggedIn
            ? "The panel API is online. Waiting for AQW to confirm the game login."
            : !worldLoaded
                ? $"AQW accepted the login{(string.IsNullOrWhiteSpace(account) ? string.Empty : $" for {account}")}. The game world is still loading; automation unlocks when the character is ready."
                : !alive
                    ? "The AQW world is loaded, but the character is not alive yet. Automation will unlock when the character is alive."
                    : $"AQW is connected{(string.IsNullOrWhiteSpace(account) ? string.Empty : $" as {account}")} and ready for scripts and ButlerV5.";

        string? butlerBlockReason = HeadlessButlerCommand.GetStartBlockReason(_pluginManager);

        return Results.Ok(new
        {
            connected,
            loggedIn,
            loginState = loggedIn ? "Accepted" : "Waiting",
            loginSource = loginAccepted ? "AQW login response" : "Skua player state",
            account,
            alive,
            worldLoaded,
            worldState = worldLoaded ? "Loaded" : loggedIn ? "Loading" : "Waiting",
            runtime,
            connectionMessage,
            server = snapshot.Server,
            scriptRunning = _scriptManager.ScriptRunning,
            butlerRunning = _butlerTask is { IsCompleted: false },
            butlerAvailable = butlerBlockReason is null,
            butlerBlockReason
        });
    }

    /// <summary>
    /// Returns the latest sampled game state without invoking Ruffle from an HTTP request.
    /// </summary>
    private GameStatusSnapshot GetStatusSnapshot()
    {
        lock (_statusSync)
            return _statusSnapshot;
    }

    /// <summary>
    /// Samples the Ruffle-backed player state in the background so slow Flash calls never
    /// prevent the panel's status endpoint from responding.
    /// </summary>
    private async Task RefreshStatusLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                (bool loginAccepted, string? acceptedAccount) = _sessionState.GetSnapshot();
                bool loggedIn = loginAccepted || _bot.Player.LoggedIn;
                bool worldLoaded = loggedIn && _bot.Flash.IsWorldLoaded;
                bool alive = worldLoaded && _bot.Player.Alive;
                string? account = !string.IsNullOrWhiteSpace(acceptedAccount)
                    ? acceptedAccount
                    : worldLoaded ? _bot.Player.Username : null;
                string? server = _bot.Servers.LastName;

                lock (_statusSync)
                    _statusSnapshot = new GameStatusSnapshot(loggedIn, worldLoaded, alive, account, server);
            }
            catch (Exception exception)
            {
                HeadlessConsole.Debug($"Could not refresh the cached panel game state: {exception.GetType().Name}: {exception.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private IResult GetScripts(string? relativeDirectory)
    {
        string scriptsRoot = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR);
        Directory.CreateDirectory(scriptsRoot);

        string requestedPath = (relativeDirectory ?? string.Empty).Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(relativeDirectory ?? string.Empty)
            || requestedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
                segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return Results.BadRequest(new { error = "The explorer can only browse inside the VibeSkua Scripts folder." });

        try
        {
            string currentDirectory = requestedPath.Length == 0
                ? scriptsRoot
                : Path.GetFullPath(Path.Combine(scriptsRoot, requestedPath.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = Path.EndsInDirectorySeparator(scriptsRoot) ? scriptsRoot : scriptsRoot + Path.DirectorySeparatorChar;

            if (!string.Equals(currentDirectory, scriptsRoot, StringComparison.OrdinalIgnoreCase)
                && !currentDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "The explorer path is outside the VibeSkua Scripts folder." });

            string cursor = scriptsRoot;
            foreach (string segment in requestedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, segment);
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    return Results.BadRequest(new { error = "The explorer cannot follow symbolic links or junctions." });
            }

            if (!Directory.Exists(currentDirectory))
                return Results.NotFound(new { error = "That folder no longer exists." });

            DirectoryInfo directory = new(currentDirectory);
            object[] folders = directory.EnumerateDirectories()
                .Where(folder => (folder.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(folder => (object)new
                {
                    name = folder.Name,
                    path = string.IsNullOrEmpty(requestedPath) ? folder.Name : $"{requestedPath}/{folder.Name}"
                })
                .ToArray();

            object[] files = directory.EnumerateFiles("*.cs")
                .Where(file => (file.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .Select(file => (object)new
                {
                    name = file.Name,
                    path = string.IsNullOrEmpty(requestedPath) ? file.Name : $"{requestedPath}/{file.Name}"
                })
                .ToArray();

            string? parentPath = string.IsNullOrEmpty(requestedPath)
                ? null
                : Path.GetDirectoryName(requestedPath.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;

            return Results.Ok(new
            {
                root = "Scripts",
                directory = requestedPath,
                parentPath,
                folders,
                files
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            HeadlessConsole.Error($"Could not browse the VibeSkua Scripts folder: {exception.Message}");
            return Results.Problem("The VibeSkua Scripts folder could not be browsed.");
        }
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

    /// <summary>
    /// Checks the authoritative AQW login response and Skua's world/avatar readiness flags.
    /// </summary>
    /// <returns>True when the character is loaded and alive.</returns>
    private bool IsGameReady()
    {
        (bool loginAccepted, _) = _sessionState.GetSnapshot();
        GameStatusSnapshot snapshot = GetStatusSnapshot();
        return (loginAccepted || snapshot.LoggedIn) && snapshot.WorldLoaded && snapshot.Alive;
    }

    private async Task<IResult> StartScriptAsync(ScriptStartRequest request)
    {
        if (!IsGameReady())
            return Results.Conflict(new { error = "AQW is not ready. Finish login, wait for the game world, and make sure your character is alive." });
        if (_scriptManager.ScriptRunning)
            return Results.Conflict(new { error = "A script is already running." });
        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { error = "Choose a .cs script from the Scripts explorer first." });

        string scriptsRoot = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR);
        string relativePath = request.Path.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(request.Path)
            || relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
                segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            || !relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Choose a C# script from inside the VibeSkua Scripts folder." });

        string fullPath = Path.GetFullPath(Path.Combine(scriptsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = Path.EndsInDirectorySeparator(scriptsRoot) ? scriptsRoot : scriptsRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "The selected script is outside the VibeSkua Scripts folder." });

        try
        {
            string cursor = scriptsRoot;
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                cursor = Path.Combine(cursor, segments[index]);
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return Results.BadRequest(new { error = "Scripts inside symbolic links or junctions cannot be run from the panel." });
                if (index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                    return Results.BadRequest(new { error = "The selected script folder is invalid." });
            }
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new { error = "The selected script no longer exists." });
        }
        catch (DirectoryNotFoundException)
        {
            return Results.NotFound(new { error = "The selected script folder no longer exists." });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }

        if (!File.Exists(fullPath))
            return Results.NotFound(new { error = "The selected script was not found in the VibeSkua Scripts folder." });

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
        if (!IsGameReady())
            return Results.Conflict(new { error = "AQW is not ready. Finish login, wait for the game world, and make sure your character is alive." });
        if (string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new { error = "Enter the player name Butler should follow." });
        if (!HeadlessButlerCommand.TryParseDuration(request.Duration, out TimeSpan? duration))
            return Results.BadRequest(new { error = "Invalid duration. Use 30m, 2h, 1d, or HH:MM:SS." });

        lock (_butlerSync)
        {
            if (_butlerTask is { IsCompleted: false })
                return Results.Conflict(new { error = "A Butler session is already running." });

            if (!HeadlessButlerCommand.TryStartFollow(_pluginManager, request.Username, out ButlerFollowSession? session, out string? error)
                || session is null)
                return Results.Conflict(new { error = error ?? "ButlerV5 could not start." });

            _butlerCancellation?.Dispose();
            _butlerCancellation = new CancellationTokenSource();
            CancellationToken token = _butlerCancellation.Token;
            string username = request.Username.Trim();

            _butlerTask = Task.Run(async () =>
            {
                try
                {
                    await HeadlessButlerCommand.WaitForSessionAsync(
                        session,
                        duration,
                        token,
                        useNamedPipe: false,
                        unloadPlugin: false,
                        _pluginManager);
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

        HeadlessConsole.Success($"Remote panel started ButlerV5 following {request.Username.Trim()}.");
        return Results.Accepted(value: new { message = $"ButlerV5 is now following {request.Username.Trim()}." });
    }

    private IResult StopButler()
    {
        lock (_butlerSync)
        {
            if (_butlerTask is not { IsCompleted: false } || _butlerCancellation is null)
                return Results.Conflict(new { error = "No Butler session is currently running." });

            _butlerCancellation.Cancel();
        }

        HeadlessConsole.Info("Butler stop requested from the remote panel.");
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
        _statusMonitorCancellation?.Cancel();
        if (_statusMonitorTask is not null)
        {
            try
            {
                await _statusMonitorTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                HeadlessConsole.Debug("The panel status monitor did not stop before shutdown.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        _statusMonitorCancellation?.Dispose();

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
            catch (OperationCanceledException)
            {
            }
        }

        _butlerCancellation?.Dispose();

        if (_application is not null)
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed record GameStatusSnapshot(bool LoggedIn, bool WorldLoaded, bool Alive, string? Account, string? Server);
    private sealed record ScriptStartRequest(string? Path);
    private sealed record ButlerStartRequest(string? Username, string? Duration);
}
