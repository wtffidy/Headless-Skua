using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skua.Core.Utils;
using Skua.Headless.UI;
using TerminalLogLevel = Skua.Headless.UI.LogLevel;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Playwright;

namespace HeadlessSkua;

/// <summary>
/// Hosts Ruffle inside an isolated, headless Chromium page and exposes its bridge to .NET.
/// </summary>
internal sealed class RuffleRuntime : IAsyncDisposable
{
    private static readonly Dictionary<string, int[]> FallbackSocketHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sock7.aq.com"] = new[] { 5588, 5589, 5591, 5592, 5593, 5594, 843 },
        ["sock8.aq.com"] = new[] { 5588, 5589, 5590, 5591, 5593, 843 },
        ["euro.aqw.artix.com"] = new[] { 5588, 843 },
        ["asia.game.artix.com"] = new[] { 5588, 843 }
    };

    private readonly string _runtimeDirectory;
    private readonly SemaphoreSlim _pageLock = new(1, 1);
    private readonly HashSet<SocketTarget> _socketTargets = new();
    private WebApplication? _webApplication;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;
    private IPage? _page;
    private int _disposeState;

    /// <summary>
    /// Raised when the SWF invokes one of the registered ExternalInterface functions.
    /// </summary>
    public event Action<string, JsonElement>? HostCallReceived;

    /// <summary>
    /// Creates a Ruffle runtime using the supplied self-hosted player directory.
    /// </summary>
    /// <param name="runtimeDirectory">Directory containing index.html, ruffle/, and game/.</param>
    public RuffleRuntime(string runtimeDirectory)
    {
        _runtimeDirectory = Path.GetFullPath(runtimeDirectory);
    }

    /// <summary>
    /// Starts the local static host, Chromium, and the Ruffle player.
    /// </summary>
    /// <param name="swfPath">Path to the Skua bootstrap SWF that should be loaded.</param>
    /// <param name="gameClientFileName">AQW game client filename to serve when the bootstrap requests it.</param>
    /// <param name="cancellationToken">Token used to cancel runtime startup.</param>
    public async Task StartAsync(string swfPath, string gameClientFileName, CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(_runtimeDirectory, "index.html");
        string rufflePath = Path.Combine(_runtimeDirectory, "ruffle", "ruffle.js");
        string fullSwfPath = Path.GetFullPath(swfPath);

        if (!File.Exists(indexPath))
            throw new FileNotFoundException("Ruffle host index.html was not found.", indexPath);
        if (!File.Exists(rufflePath))
            throw new FileNotFoundException("Ruffle is not installed. Run Install-Runtime.ps1 first.", rufflePath);

        string ruffleVersionPath = Path.Combine(_runtimeDirectory, "ruffle-version.txt");
        if (File.Exists(ruffleVersionPath))
        {
            string ruffleVersion = (await File.ReadAllTextAsync(ruffleVersionPath, cancellationToken)).Trim();
            if (!string.IsNullOrWhiteSpace(ruffleVersion))
                HeadlessConsole.Info($"Using Ruffle web package {ruffleVersion}.");
        }
        if (!File.Exists(fullSwfPath))
            throw new FileNotFoundException("The Skua SWF was not found. Supply --swf or install it into runtime/game/skua.swf.", fullSwfPath);

        await LoadSocketTargetsAsync(cancellationToken);
        await StartStaticHostAsync(cancellationToken);
        await StartBrowserAsync(cancellationToken);
        await ConfigureLocalGameClientRouteAsync(gameClientFileName, cancellationToken);

        string address = GetServerAddress();
        string localSwfPath = Path.Combine(_runtimeDirectory, "game", "skua.swf");
        Directory.CreateDirectory(Path.GetDirectoryName(localSwfPath)!);

        if (!string.Equals(fullSwfPath, Path.GetFullPath(localSwfPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(fullSwfPath, localSwfPath, true);

        string playerQuery = HeadlessConsole.Quiet
            ? "?swf=game/skua.swf&quiet=1"
            : "?swf=game/skua.swf";
        Uri playerUri = new(new Uri(address), playerQuery);

        HeadlessConsole.Debug($"Runtime page: {playerUri}");
        await _page!.GotoAsync(playerUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000
        });

        await _page.WaitForFunctionAsync(
            "() => window.__headlessSkua && window.__headlessSkua.getStatus().status === 'loaded'",
            null,
            new PageWaitForFunctionOptions { Timeout = 120_000 });

        HeadlessConsole.Success("Ruffle reports that the SWF has loaded.");
    }

    /// <summary>
    /// Loads live AQW socket endpoints and merges in a small fallback list for server-list outages.
    /// Each endpoint gets a port 843 route for Flash cross-domain policy requests.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the server-list request.</param>
    private async Task LoadSocketTargetsAsync(CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, int[]> fallback in FallbackSocketHosts)
            foreach (int port in fallback.Value)
                AddSocketTarget(fallback.Key, port);

        try
        {
            // Use the same authenticated AQW content client and endpoint as VibeSkua's
            // ScriptServers.GetServers implementation instead of the newer game.aq.com route.
            using HttpResponseMessage response = await HttpClients.GetGHClient().GetAsync(
                "http://content.aq.com/game/api/data/servers",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            JsonElement servers = document.RootElement;

            if (servers.ValueKind == JsonValueKind.Object)
            {
                if (servers.TryGetProperty("servers", out JsonElement nestedServers)
                    || servers.TryGetProperty("data", out nestedServers))
                    servers = nestedServers;
            }

            if (servers.ValueKind != JsonValueKind.Array)
                throw new JsonException("The AQW server-list response was not an array.");

            int serverCount = 0;
            foreach (JsonElement server in servers.EnumerateArray())
            {
                if (!server.TryGetProperty("sIP", out JsonElement hostElement)
                    || !server.TryGetProperty("iPort", out JsonElement portElement))
                    continue;

                string? host = hostElement.ValueKind == JsonValueKind.String ? hostElement.GetString() : null;
                int port = portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out int numericPort)
                    ? numericPort
                    : int.TryParse(portElement.ToString(), out int parsedPort) ? parsedPort : 0;

                if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
                    continue;

                AddSocketTarget(host, port);
                AddSocketTarget(host, 843);
                serverCount++;
            }

            HeadlessConsole.Info($"Loaded {serverCount} AQW servers and prepared {_socketTargets.Count} WebSocket proxy routes.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            HeadlessConsole.Warn($"Could not refresh the AQW server list; using bundled socket endpoints. {exception.Message}");
        }

        if (_socketTargets.Count == 0)
            throw new InvalidOperationException("No AQW socket proxy endpoints could be prepared.");
    }

    /// <summary>
    /// Adds an Artix/AQW socket endpoint to the loopback-only proxy allowlist.
    /// </summary>
    /// <param name="host">The socket hostname supplied by the AQW server list.</param>
    /// <param name="port">The TCP destination port.</param>
    private void AddSocketTarget(string host, int port)
    {
        string normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        bool isArtixHost = normalizedHost.EndsWith(".aq.com", StringComparison.Ordinal)
            || normalizedHost.EndsWith(".artix.com", StringComparison.Ordinal);

        if (!isArtixHost || port is < 1 or > 65535)
            return;

        _socketTargets.Add(new SocketTarget(normalizedHost, port));
    }

    /// <summary>
    /// Handles one Ruffle WebSocket connection and relays its binary frames to the matching AQW TCP socket.
    /// </summary>
    /// <param name="context">The local WebSocket request.</param>
    private async Task HandleSocketProxyAsync(HttpContext context)
    {
        string host = context.Request.Query["host"].ToString().Trim().TrimEnd('.').ToLowerInvariant();
        if (!int.TryParse(context.Request.Query["port"], out int port)
            || !_socketTargets.Contains(new SocketTarget(host, port)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using TcpClient tcpClient = new() { NoDelay = true };
        try
        {
            await tcpClient.ConnectAsync(host, port, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            HeadlessConsole.Warn($"AQW socket proxy could not connect to {host}:{port}: {exception.Message}");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
        using CancellationTokenSource relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        NetworkStream networkStream = tcpClient.GetStream();

        HeadlessConsole.Success($"AQW socket tunnel connected: {host}:{port}.");

        Task clientToServer = RelayWebSocketToTcpAsync(webSocket, networkStream, relayCancellation.Token);
        Task serverToClient = RelayTcpToWebSocketAsync(networkStream, webSocket, relayCancellation.Token);

        await Task.WhenAny(clientToServer, serverToClient);
        relayCancellation.Cancel();
        tcpClient.Close();

        await IgnoreRelayFailureAsync(clientToServer);
        await IgnoreRelayFailureAsync(serverToClient);

        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "TCP connection closed", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
    }

    /// <summary>
    /// Copies binary WebSocket payloads to the AQW TCP stream.
    /// </summary>
    private static async Task RelayWebSocketToTcpAsync(WebSocket webSocket, NetworkStream networkStream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        bool firstPayload = true;

        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            if (firstPayload && result.Count > 0)
            {
                HeadlessConsole.Debug($"AQW tunnel sent its first client payload ({result.Count} bytes).");
                firstPayload = false;
            }

            await networkStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
    }

    /// <summary>
    /// Copies AQW TCP payloads to binary WebSocket messages.
    /// </summary>
    private static async Task RelayTcpToWebSocketAsync(NetworkStream networkStream, WebSocket webSocket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        bool firstPayload = true;

        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            int bytesRead = await networkStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
                return;

            if (firstPayload)
            {
                HeadlessConsole.Debug($"AQW tunnel received its first server payload ({bytesRead} bytes).");
                firstPayload = false;
            }

            await webSocket.SendAsync(
                new ArraySegment<byte>(buffer, 0, bytesRead),
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
        }
    }

    /// <summary>
    /// Suppresses expected cancellation and disconnect exceptions when either side of a socket tunnel closes.
    /// </summary>
    private static async Task IgnoreRelayFailureAsync(Task relayTask)
    {
        try
        {
            await relayTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private readonly record struct SocketTarget(string Host, int Port);

    /// <summary>
    /// Serves the selected AQW game client locally when the Skua bootstrap requests it.
    /// </summary>
    /// <param name="gameClientFileName">The selected AQW game client filename.</param>
    /// <param name="cancellationToken">Token used to cancel loading the local game client.</param>
    private async Task ConfigureLocalGameClientRouteAsync(string gameClientFileName, CancellationToken cancellationToken)
    {
        string localGameClientPath = Path.Combine(_runtimeDirectory, "game", gameClientFileName);
        if (!File.Exists(localGameClientPath))
        {
            HeadlessConsole.Warn($"No local {gameClientFileName} was found; AQW will use its online game client.");
            return;
        }

        byte[] localGameClient = await File.ReadAllBytesAsync(localGameClientPath, cancellationToken);
        await _page!.RouteAsync("**/gamefiles/**", async route =>
        {
            string requestedFileName = Path.GetFileName(new Uri(route.Request.Url).AbsolutePath);
            bool isAqwGameClient = requestedFileName.StartsWith("Game4000r", StringComparison.OrdinalIgnoreCase)
                && requestedFileName.EndsWith(".swf", StringComparison.OrdinalIgnoreCase);
            if (!isAqwGameClient)
            {
                await route.ContinueAsync();
                return;
            }

            // The Skua bootstrap can request the game-client filename compiled into
            // its own build (currently R8), regardless of the version selected here.
            // Replace any versioned Game4000 SWF request with the selected local build.
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/x-shockwave-flash",
                BodyBytes = localGameClient
            });

            HeadlessConsole.Success($"Serving selected {gameClientFileName} for AQW request {requestedFileName}.");
        });

        HeadlessConsole.Info($"Local AQW game-client override is ready for {gameClientFileName}.");
    }

    /// <summary>
    /// Queues an ActionScript ExternalInterface callback after the current browser evaluation returns.
    /// Calls are ignored once shutdown begins because late Skua callbacks can outlive the browser page.
    /// </summary>
    /// <param name="functionName">Callback name registered by ActionScript.</param>
    /// <param name="arguments">Arguments passed to the callback.</param>
    public async Task DispatchActionScriptAsync(string functionName, object?[]? arguments = null)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return;

        IPage? page = _page;
        if (page is null)
            throw new InvalidOperationException("The Ruffle runtime has not started.");

        await _pageLock.WaitAsync();

        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
                return;

            await page.EvaluateAsync<bool>(
                "payload => window.__headlessSkua.dispatchActionScript(payload.name, payload.args)",
                new { name = functionName, args = arguments ?? Array.Empty<object>() });
        }
        finally
        {
            _pageLock.Release();
        }
    }

    /// <summary>
    /// Invokes an ActionScript ExternalInterface callback registered by the SWF.
    /// Calls are ignored once shutdown begins because late Skua callbacks can outlive the browser page.
    /// </summary>
    /// <param name="functionName">Callback name registered by ActionScript.</param>
    /// <param name="arguments">Arguments passed to the callback.</param>
    /// <returns>The callback result as JSON, when one is returned.</returns>
    public async Task<JsonElement?> CallActionScriptAsync(string functionName, object?[]? arguments = null)
    {
        if (Volatile.Read(ref _disposeState) != 0)
            return null;

        IPage? page = _page;
        if (page is null)
            throw new InvalidOperationException("The Ruffle runtime has not started.");

        await _pageLock.WaitAsync();

        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
                return null;

            return await page.EvaluateAsync<JsonElement?>(
                "payload => window.__headlessSkua.callActionScript(payload.name, payload.args)",
                new { name = functionName, args = arguments ?? Array.Empty<object>() });
        }
        finally
        {
            _pageLock.Release();
        }
    }

    /// <summary>
    /// Starts a localhost-only static file server for the Ruffle web player.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel server startup.</param>
    private async Task StartStaticHostAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _runtimeDirectory,
            Args = Array.Empty<string>()
        });

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _webApplication = builder.Build();

        _webApplication.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".wasm"] = "application/wasm";
        contentTypes.Mappings[".swf"] = "application/x-shockwave-flash";

        _webApplication.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = new PhysicalFileProvider(_runtimeDirectory)
        });
        _webApplication.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(_runtimeDirectory),
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = false
        });

        _webApplication.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        _webApplication.MapGet("/socket-proxy-config", (HttpContext context) =>
        {
            string scheme = context.Request.IsHttps ? "wss" : "ws";
            object[] endpoints = _socketTargets
                .OrderBy(target => target.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.Port)
                .Select(target => new
                {
                    host = target.Host,
                    port = target.Port,
                    proxyUrl = $"{scheme}://{context.Request.Host}/socket-proxy?host={Uri.EscapeDataString(target.Host)}&port={target.Port}"
                })
                .Cast<object>()
                .ToArray();

            return Results.Json(endpoints);
        });
        _webApplication.Map("/socket-proxy", branch => branch.Run(HandleSocketProxyAsync));

        await _webApplication.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Launches Chromium in headless mode and installs the ActionScript bridge binding.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel browser startup.</param>
    private async Task StartBrowserAsync(CancellationToken cancellationToken)
    {
        // Single-file publishing bundles Microsoft.Playwright.dll, so Playwright cannot
        // infer the external driver location from Assembly.Location. The build copies
        // .playwright beside HeadlessSkua.exe; explicitly point Playwright there.
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", AppContext.BaseDirectory);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // AQW's gameversion endpoint does not send CORS headers for the local
            // Ruffle origin. This browser is isolated from the user's profile and
            // only hosts the bundled Skua runtime, so disable web security here to
            // allow the legacy SWF loader to fetch AQW's game assets.
            Args = new[]
            {
                "--disable-dev-shm-usage",
                "--disable-web-security",
                "--enable-unsafe-swiftshader",
                "--log-level=3"
            }
        });

        _browserContext = await _browser.NewContextAsync();
        _page = await _browserContext.NewPageAsync();
        _page.SetDefaultTimeout(30_000);

        _page.Console += (_, message) =>
        {
            bool actionableInfo = message.Text.Contains("[Game] ERR", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("Error #", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("BitmapData.draw", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("panic", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("exception", StringComparison.OrdinalIgnoreCase);

            if (HeadlessConsole.Quiet
                || ((message.Type is "log" or "debug" or "info") && !actionableInfo)
                || message.Text.Contains("GPU stall due to ReadPixels", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("Failed to load resource: the server responded with a status of 404", StringComparison.OrdinalIgnoreCase))
                return;

            TerminalLogLevel level = message.Type == "error" || actionableInfo
                ? TerminalLogLevel.Error
                : TerminalLogLevel.Warn;
            string cleanMessage = System.Text.RegularExpressions.Regex.Replace(message.Text, @"%c[^%]*%c", string.Empty);
            cleanMessage = System.Text.RegularExpressions.Regex.Replace(cleanMessage, @"\s*(color|background|font-style):\s*[^;]+;?", string.Empty);
            HeadlessConsole.Log(level, $"[Browser] {cleanMessage.Trim()}");
        };
        _page.PageError += (_, message) =>
        {
            if (!HeadlessConsole.Quiet)
                HeadlessConsole.Error($"[Browser] {message}");
        };
        _page.RequestFailed += (_, request) =>
        {
            if (!HeadlessConsole.Quiet)
                HeadlessConsole.Warn($"[Network] {request.Method} {request.Url}: {request.Failure}");
        };
        _page.Response += (_, response) =>
        {
            if (response.Status == 404 && !HeadlessConsole.Quiet)
                HeadlessConsole.Warn($"[Network] Missing resource (404): {response.Request.Method} {response.Url}");
            else if (response.Status >= 500 && !HeadlessConsole.Quiet)
                HeadlessConsole.Warn($"[Network] Server error ({response.Status}): {response.Request.Method} {response.Url}");
        };

        await _page.ExposeBindingAsync<string, JsonElement, object?>(
            "headlessSkuaHostCall",
            (source, name, values) =>
            {
                if (Volatile.Read(ref _disposeState) == 0)
                    HostCallReceived?.Invoke(name, values.Clone());

                return Task.FromResult<object?>(null);
            });
    }

    /// <summary>
    /// Returns the dynamically assigned localhost URL for the static file server.
    /// </summary>
    /// <returns>The server base URL.</returns>
    private string GetServerAddress()
    {
        IServer server = _webApplication!.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
        string? address = addresses?.Addresses.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("The local Ruffle web server did not publish an address.");

        return address.EndsWith('/') ? address : $"{address}/";
    }

    /// <summary>
    /// Stops accepting bridge calls, waits for any active browser evaluation, and then releases runtime resources.
    /// The semaphore is intentionally left undisposed because late Skua callbacks may still reach this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        HostCallReceived = null;

        await _pageLock.WaitAsync();

        try
        {
            _page = null;

            if (_browser is not null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }

            _browserContext = null;

            _playwright?.Dispose();
            _playwright = null;

            if (_webApplication is not null)
            {
                await _webApplication.StopAsync();
                await _webApplication.DisposeAsync();
                _webApplication = null;
            }
        }
        finally
        {
            _pageLock.Release();
        }
    }
}
