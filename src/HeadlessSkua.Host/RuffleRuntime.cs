using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Playwright;

namespace HeadlessSkua;

/// <summary>
/// Hosts Ruffle inside an isolated, headless Chromium page and exposes its bridge to .NET.
/// </summary>
internal sealed class RuffleRuntime : IAsyncDisposable
{
    private readonly string _runtimeDirectory;
    private WebApplication? _webApplication;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _browserContext;
    private IPage? _page;
    private readonly SemaphoreSlim _pageLock = new(1, 1);

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
    /// <param name="swfPath">Path to the SWF that should be loaded.</param>
    /// <param name="cancellationToken">Token used to cancel runtime startup.</param>
    public async Task StartAsync(string swfPath, CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(_runtimeDirectory, "index.html");
        string rufflePath = Path.Combine(_runtimeDirectory, "ruffle", "ruffle.js");
        string fullSwfPath = Path.GetFullPath(swfPath);

        if (!File.Exists(indexPath))
            throw new FileNotFoundException("Ruffle host index.html was not found.", indexPath);
        if (!File.Exists(rufflePath))
            throw new FileNotFoundException("Ruffle is not installed. Run Install-Runtime.ps1 first.", rufflePath);
        if (!File.Exists(fullSwfPath))
            throw new FileNotFoundException("The Skua SWF was not found. Supply --swf or install it into runtime/game/skua.swf.", fullSwfPath);

        await StartStaticHostAsync(cancellationToken);
        await StartBrowserAsync(cancellationToken);

        string address = GetServerAddress();
        string localSwfPath = Path.Combine(_runtimeDirectory, "game", "skua.swf");
        Directory.CreateDirectory(Path.GetDirectoryName(localSwfPath)!);

        if (!string.Equals(fullSwfPath, Path.GetFullPath(localSwfPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(fullSwfPath, localSwfPath, true);

        Uri playerUri = new(new Uri(address), "?swf=game/skua.swf");

        Console.WriteLine($"Runtime page: {playerUri}");
        await _page!.GotoAsync(playerUri.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 60_000
        });

        await _page.WaitForFunctionAsync(
            "() => window.__headlessSkua && window.__headlessSkua.getStatus().status === 'loaded'",
            null,
            new PageWaitForFunctionOptions { Timeout = 120_000 });

        Console.WriteLine("Ruffle reports that the SWF has loaded.");
    }

    /// <summary>
    /// Invokes an ActionScript ExternalInterface callback registered by the SWF.
    /// </summary>
    /// <param name="functionName">Callback name registered by ActionScript.</param>
    /// <param name="arguments">Arguments passed to the callback.</param>
    /// <returns>The callback result as JSON, when one is returned.</returns>
    public async Task<JsonElement?> CallActionScriptAsync(string functionName, object?[]? arguments = null)
    {
        if (_page is null)
            throw new InvalidOperationException("The Ruffle runtime has not started.");

        await _pageLock.WaitAsync();

        try
        {
            return await _page.EvaluateAsync<JsonElement?>(
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

        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(0));
        _webApplication = builder.Build();

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
        await _webApplication.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Launches Chromium in headless mode and installs the ActionScript bridge binding.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel browser startup.</param>
    private async Task StartBrowserAsync(CancellationToken cancellationToken)
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--disable-dev-shm-usage" }
        });

        _browserContext = await _browser.NewContextAsync();
        _page = await _browserContext.NewPageAsync();
        _page.SetDefaultTimeout(30_000);

        _page.Console += (_, message) =>
            Console.WriteLine($"[Browser:{message.Type}] {message.Text}");
        _page.PageError += (_, message) =>
            Console.Error.WriteLine($"[Browser Error] {message}");
        _page.RequestFailed += (_, request) =>
            Console.Error.WriteLine($"[Network Error] {request.Method} {request.Url}: {request.Failure}");

        await _page.ExposeBindingAsync<string, JsonElement, object?>(
            "headlessSkuaHostCall",
            (source, name, values) =>
            {
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
    /// Releases the browser, Playwright process, and local web server.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.CloseAsync();

        if (_playwright is not null)
            _playwright.Dispose();

        if (_webApplication is not null)
            await _webApplication.StopAsync();

        _pageLock.Dispose();
    }
}
