using System.Diagnostics;
using System.Reflection;
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
    private readonly object _scriptStopSync = new();
    private readonly object _statusSync = new();
    private static int _traceListenerAttached;
    private WebApplication? _application;
    private CancellationTokenSource? _butlerCancellation;
    private Task? _butlerTask;
    private Task? _scriptStopTask;
    private CancellationTokenSource? _statusMonitorCancellation;
    private Task? _statusMonitorTask;
    private GameStatusSnapshot _statusSnapshot = new(false, false, false, false, false, null, null, null, false, false, null, null);

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

        CancellationTokenSource statusMonitorCancellation = new();
        _statusMonitorCancellation = statusMonitorCancellation;
        _statusMonitorTask = Task.Run(() => RefreshStatusLoopAsync(statusMonitorCancellation.Token));

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
        app.MapGet("/api/scripts/search", (string? q) => SearchScripts(q));
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
        bool avatarLoaded = worldLoaded && snapshot.AvatarLoaded;
        bool playerLoaded = avatarLoaded && snapshot.PlayerLoaded;
        bool alive = avatarLoaded && snapshot.Alive;
        bool connected = loggedIn && worldLoaded && avatarLoaded && playerLoaded && alive;
        string? account = string.IsNullOrWhiteSpace(acceptedAccount) ? snapshot.Account : acceptedAccount;
        string? server = string.IsNullOrWhiteSpace(_bot.Servers.LastName) ? snapshot.Server : _bot.Servers.LastName;
        string runtime = !loggedIn
            ? "Not logged in"
            : !worldLoaded
                ? "Login accepted · connecting to game world"
                : !avatarLoaded
                    ? "World loaded · waiting for character"
                    : !playerLoaded
                        ? "Character found · finishing initialization"
                        : !alive
                            ? "Character loaded · not alive"
                            : "Connected";
        string connectionMessage = !loggedIn
            ? "The panel API is online. Waiting for AQW to confirm the game login."
            : !worldLoaded
                ? $"AQW accepted the login{(string.IsNullOrWhiteSpace(account) ? string.Empty : $" for {account}")}{(string.IsNullOrWhiteSpace(server) ? string.Empty : $" and selected {server}")}. Waiting for the game world to initialize."
                : !avatarLoaded
                    ? $"The {server ?? "AQW"} world is loaded, but the character avatar has not initialized yet."
                    : !playerLoaded
                        ? "The character avatar exists, but VibeSkua is still waiting for inventory data, map loading, and character assets. Scripts will unlock when Player.Loaded is true."
                        : !alive
                            ? "The AQW character is loaded, but not alive yet. Automation will unlock when the character is alive."
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
            avatarLoaded,
            playerLoaded,
            worldLoaded,
            worldState = worldLoaded ? "Loaded" : loggedIn && !string.IsNullOrWhiteSpace(server) ? "Connecting" : loggedIn ? "Loading" : "Waiting",
            runtime,
            connectionMessage,
            server,
            map = snapshot.Map,
            mapLoaded = snapshot.MapLoaded,
            mapLoading = snapshot.MapLoading,
            level = snapshot.Level,
            gold = snapshot.Gold,
            scriptRunning = _scriptManager.ScriptRunning,
            scriptStopping = IsScriptStopping(),
            scriptDiagnostics = GetScriptDiagnostics(),
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
        HashSet<string> warnedStatusFields = new(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            (bool loginAccepted, string? acceptedAccount) = _sessionState.GetSnapshot();
            bool loggedIn = loginAccepted || TryReadStatusValue("player login", () => _bot.Player.LoggedIn, false, warnedStatusFields);
            string? server = TryReadStatusValue<string?>("server", () => _bot.Servers.LastName, null, warnedStatusFields);
            bool worldLoaded = loggedIn && TryReadStatusValue("game world", () => _bot.Flash.IsWorldLoaded, false, warnedStatusFields);
            bool avatarLoaded = worldLoaded && TryReadStatusValue("character avatar", IsLocalAvatarPresent, false, warnedStatusFields);
            string? account = !string.IsNullOrWhiteSpace(acceptedAccount)
                ? acceptedAccount
                : worldLoaded ? TryReadStatusValue<string?>("account", () => _bot.Player.Username, null, warnedStatusFields) : null;

            // Publish the authoritative login/world/avatar milestones before querying
            // inventory, assets, health, or map details. Those deeper reads can lag behind
            // avatar creation and must not leave the panel stuck on "Character: Pending".
            GameStatusSnapshot baseSnapshot = new(
                loggedIn,
                worldLoaded,
                avatarLoaded,
                false,
                false,
                account,
                server,
                null,
                false,
                false,
                null,
                null);

            lock (_statusSync)
                _statusSnapshot = baseSnapshot;

            if (avatarLoaded)
            {
                bool playerLoaded = TryReadStatusValue("player readiness", () => _bot.Player.Loaded, false, warnedStatusFields);
                bool alive = TryReadStatusValue("player alive state", () => _bot.Player.Alive, false, warnedStatusFields);
                string? map = TryReadStatusValue<string?>("map name", () => _bot.Map.Name, null, warnedStatusFields);
                bool mapLoading = TryReadStatusValue("map loading state", () => _bot.Map.Loading, false, warnedStatusFields);
                bool mapLoaded = TryReadStatusValue("map loaded state", () => _bot.Map.Loaded, false, warnedStatusFields);
                int? level = TryReadStatusValue<int?>("player level", () => _bot.Player.Level, null, warnedStatusFields);
                int? gold = TryReadStatusValue<int?>("player gold", () => _bot.Player.Gold, null, warnedStatusFields);

                lock (_statusSync)
                    _statusSnapshot = new GameStatusSnapshot(
                        loggedIn,
                        worldLoaded,
                        avatarLoaded,
                        playerLoaded,
                        alive,
                        account,
                        server,
                        map,
                        mapLoaded,
                        mapLoading,
                        level,
                        gold);
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

    /// <summary>
    /// Detects the local character through world.myAvatar, with a fallback to the
    /// isMyAvatar marker when AQW populates world.avatars before assigning myAvatar.
    /// </summary>
    /// <returns>True when the local character exists in the current world.</returns>
    private bool IsLocalAvatarPresent()
    {
        if (!_bot.Flash.IsNull("world.myAvatar"))
            return true;

        int avatarCount = _bot.Flash.GetGameObject<int>("world.avatars.length", 0);
        int scanCount = Math.Min(Math.Max(avatarCount, 0), 256);
        for (int index = 0; index < scanCount; index++)
            if (_bot.Flash.GetGameObject<bool>($"world.avatars.{index}.isMyAvatar", false))
                return true;

        return false;
    }

    /// <summary>
    /// Reads one panel status value without allowing an unavailable AQW property to block
    /// publication of the other independently sampled readiness milestones.
    /// </summary>
    /// <typeparam name="T">The expected status value type.</typeparam>
    /// <param name="field">A short diagnostic field name.</param>
    /// <param name="read">The AQW/VibeSkua property accessor.</param>
    /// <param name="fallback">The safe value to publish when the accessor fails.</param>
    /// <param name="warnedFields">Fields that have already produced a diagnostic.</param>
    /// <returns>The sampled value or its fallback.</returns>
    private static T TryReadStatusValue<T>(string field, Func<T> read, T fallback, HashSet<string> warnedFields)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            if (warnedFields.Add(field))
                HeadlessConsole.Debug($"Could not sample panel {field}: {exception.GetType().Name}: {exception.Message}");

            return fallback;
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

    /// <summary>
    /// Searches script names and relative paths throughout the Scripts folder.
    /// </summary>
    /// <param name="query">Text to match against script names or paths.</param>
    /// <returns>Matching C# scripts with paths relative to the Scripts root.</returns>
    private IResult SearchScripts(string? query)
    {
        string searchTerm = query?.Trim() ?? string.Empty;
        if (searchTerm.Length < 2)
            return Results.Ok(new { query = searchTerm, files = Array.Empty<object>() });

        string scriptsRoot = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR);
        Directory.CreateDirectory(scriptsRoot);

        try
        {
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
            };

            string[] matches = Directory.EnumerateFiles(scriptsRoot, "*.cs", options)
                .Select(file => new
                {
                    FullPath = file,
                    RelativePath = Path.GetRelativePath(scriptsRoot, file).Replace(Path.DirectorySeparatorChar, '/')
                })
                .Where(file => file.RelativePath.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(file => file.RelativePath)
                .ToArray();

            object[] files = matches.Select(relativePath => (object)new
            {
                name = Path.GetFileName(relativePath),
                path = relativePath
            }).ToArray();

            return Results.Ok(new { query = searchTerm, files });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            HeadlessConsole.Error($"Could not search the VibeSkua Scripts folder: {exception.Message}");
            return Results.Problem("The VibeSkua Scripts folder could not be searched.");
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
        return (loginAccepted || snapshot.LoggedIn) && snapshot.WorldLoaded && snapshot.AvatarLoaded && snapshot.PlayerLoaded && snapshot.Alive;
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
            EnsureTraceListener();
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

    /// <summary>
    /// Makes asynchronous script-thread exceptions visible in the headless console.
    /// </summary>
    private static void EnsureTraceListener()
    {
        if (Interlocked.Exchange(ref _traceListenerAttached, 1) != 0)
            return;

        Trace.Listeners.Add(new ConsoleTraceListener());
        Trace.AutoFlush = true;
    }

    /// <summary>
    /// Captures the active VibeSkua script thread and its current managed stack.
    /// </summary>
    /// <returns>Current worker-thread diagnostics, or a clear unavailable state.</returns>
    private object GetScriptDiagnostics()
    {
        if (!_scriptManager.ScriptRunning)
            return new { threadState = "Stopped", threadId = (int?)null, stackTrace = (string?)null };

        try
        {
            FieldInfo? threadField = _scriptManager.GetType().GetField(
                "_currentScriptThread",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Thread? scriptThread = threadField?.GetValue(_scriptManager) as Thread;
            if (scriptThread is null)
                return new { threadState = "Starting", threadId = (int?)null, stackTrace = "VibeSkua has not published its script worker thread yet." };

            bool cancellationRequested = _scriptManager.ScriptCts?.IsCancellationRequested ?? false;
            string diagnostics = "Cross-thread managed stack capture is not supported by .NET 10. Use the Script log and AQW bridge trace below to identify the current operation.";

            return new
            {
                threadState = scriptThread.ThreadState.ToString(),
                threadId = (int?)scriptThread.ManagedThreadId,
                cancellationRequested,
                stackTrace = diagnostics
            };
        }
        catch (Exception exception)
        {
            return new
            {
                threadState = "Unavailable",
                threadId = (int?)null,
                stackTrace = $"Could not inspect the VibeSkua script worker: {exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    /// <summary>
    /// Reports whether VibeSkua is currently executing its script stop sequence.
    /// </summary>
    private bool IsScriptStopping()
    {
        lock (_scriptStopSync)
            return _scriptStopTask is { IsCompleted: false };
    }

    /// <summary>
    /// Requests VibeSkua's complete script shutdown sequence without blocking the HTTP request.
    /// </summary>
    private IResult StopScript()
    {
        if (!_scriptManager.ScriptRunning)
            return Results.Conflict(new { error = "No script is currently running." });

        lock (_scriptStopSync)
        {
            if (_scriptStopTask is { IsCompleted: false })
                return Results.Accepted(value: new { message = "The script is already stopping.", stopping = true });

            _scriptStopTask = Task.Run(StopScriptCoreAsync);
        }

        HeadlessConsole.Warn("Full script stop requested from the remote panel.");
        return Results.Accepted(value: new { message = "Stop requested. Waiting for the script to shut down...", stopping = true });
    }

    /// <summary>
    /// Invokes VibeSkua's own stop routine, which cancels the script and interrupts its thread if it does not exit.
    /// </summary>
    private async Task StopScriptCoreAsync()
    {
        try
        {
            MethodInfo? stopMethod = _scriptManager.GetType().GetMethod(
                "StopScript",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null);

            object? stopResult = stopMethod?.Invoke(_scriptManager, new object[] { true });
            if (stopResult is ValueTask stopValueTask)
                await stopValueTask.ConfigureAwait(false);
            else if (stopResult is Task stopTask)
                await stopTask.ConfigureAwait(false);
            else
            {
                HeadlessConsole.Warn("VibeSkua stop routine is unavailable. Falling back to script cancellation.");
                _scriptManager.ScriptCts?.Cancel();
            }

            HeadlessConsole.Success("VibeSkua script stop sequence finished.");
        }
        catch (Exception exception)
        {
            Exception actualException = exception is TargetInvocationException invocationException
                ? invocationException.InnerException ?? exception
                : exception;
            HeadlessConsole.Error($"VibeSkua script stop failed: {actualException.Message}");
            _scriptManager.ScriptCts?.Cancel();
        }
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

    private sealed record GameStatusSnapshot(bool LoggedIn, bool WorldLoaded, bool AvatarLoaded, bool PlayerLoaded, bool Alive, string? Account, string? Server, string? Map, bool MapLoaded, bool MapLoading, int? Level, int? Gold);
    private sealed record ScriptStartRequest(string? Path);
    private sealed record ButlerStartRequest(string? Username, string? Duration);
}
