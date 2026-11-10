using System.Collections.Concurrent;
using System.Dynamic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Headless.UI;

namespace HeadlessSkua;

/// <summary>
/// Adapts the headless Ruffle runtime to Skua's existing synchronous IFlashUtil contract.
/// </summary>
internal sealed class RuffleFlashUtil : IFlashUtil
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RuffleRuntime _runtime;
    private readonly ConcurrentDictionary<string, DeferredModuleCall> _deferredModuleCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Function, object[] Arguments)> _hostCalls = new();
    private readonly ConcurrentDictionary<string, CachedCall> _readCallCache = new(StringComparer.Ordinal);
    private int _flushingDeferredModuleCalls;
    private int _processingHostCalls;

    /// <inheritdoc />
    public event FlashCallHandler? FlashCall;

    /// <summary>
    /// Reports safe bridge diagnostics to the console and the remote panel log service.
    /// </summary>
    public event Action<string>? DiagnosticLogged;

    /// <summary>
    /// Writes a map-operation diagnostic without exposing packet payloads or player data.
    /// </summary>
    /// <param name="message">The safe diagnostic text.</param>
    /// <param name="isError">Whether the operation failed.</param>
    private void LogMapDiagnostic(string message, bool isError = false)
    {
        if (isError)
            HeadlessConsole.Error(message);
        else
            HeadlessConsole.Info(message);

        DiagnosticLogged?.Invoke(message);
    }

    /// <summary>
    /// Creates the compatibility adapter for an already-started Ruffle runtime.
    /// </summary>
    /// <param name="runtime">The active Ruffle runtime.</param>
    public RuffleFlashUtil(RuffleRuntime runtime)
    {
        _runtime = runtime;
        _runtime.HostCallReceived += OnHostCallReceived;
    }

    /// <inheritdoc />
    public void InitializeFlash()
    {
        // The headless host initializes Ruffle before exposing this adapter.
    }

    /// <inheritdoc />
    public string? Call(string function, params object[] args)
    {
        object? result = Call(function, typeof(string), args);
        return result as string;
    }

    /// <inheritdoc />
    public T? Call<T>(string function, params object[] args)
    {
        object? result = Call(function, typeof(T), args);
        return result is T typed ? typed : default;
    }

    /// <summary>
    /// Queues a void ActionScript callback without waiting for its asynchronous work.
    /// </summary>
    /// <param name="function">The registered ActionScript callback name.</param>
    public void Dispatch(string function)
    {
        try
        {
            _runtime.DispatchActionScriptAsync(function).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Ruffle Dispatch] {function}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    /// <summary>
    /// Queues an ActionScript callback with arguments and returns without waiting for the callback to finish.
    /// </summary>
    /// <param name="function">The registered ActionScript callback name.</param>
    /// <param name="args">Arguments passed to the callback.</param>
    public void DispatchActionScript(string function, params object[] args)
    {
        try
        {
            _runtime.DispatchActionScriptAsync(function, args).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Ruffle Dispatch] {function}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public object? Call(string function, Type type, params object[] args)
    {
        string? outboundOperation = DescribeOutboundMapOperation(function, args);
        Stopwatch? outboundTimer = outboundOperation is null ? null : Stopwatch.StartNew();

        try
        {
            if (ShouldDeferModuleCall(function, args) || ShouldSkipUnavailableGameCall(function) || ShouldSkipNullTarget(function, args))
                return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (outboundOperation is not null)
                LogMapDiagnostic($"AQW {outboundOperation} entered the ActionScript bridge.");

            string? cacheKey = GetReadCallCacheKey(function, type, args, out long cacheDurationMilliseconds);
            long now = Environment.TickCount64;
            if (cacheKey is not null
                && _readCallCache.TryGetValue(cacheKey, out CachedCall cachedCall)
                && now < cachedCall.ExpiresAt)
                return cachedCall.Value;

            JsonElement? result = _runtime.CallActionScriptAsync(function, args).GetAwaiter().GetResult();
            object? converted = ConvertResult(result, type);
            if (cacheKey is not null)
                _readCallCache[cacheKey] = new CachedCall(Environment.TickCount64 + cacheDurationMilliseconds, converted);

            if (outboundOperation is not null)
                LogMapDiagnostic($"AQW {outboundOperation} returned from ActionScript in {outboundTimer?.Elapsed.TotalMilliseconds:F0}ms.");

            FlushDeferredModuleCalls();
            return converted;
        }
        catch (Exception exception)
        {
            if (outboundOperation is not null)
                LogMapDiagnostic($"AQW {outboundOperation} bridge call failed after {outboundTimer?.Elapsed.TotalMilliseconds:F0}ms: {exception.Message}", isError: true);

            Console.Error.WriteLine($"[Ruffle Call] {function}: {exception.GetType().Name}: {exception.Message}");
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>
    /// Describes only map-changing packets sent through the Skua-to-ActionScript bridge.
    /// Packet payloads, player names, room identifiers, and credentials are never logged.
    /// </summary>
    /// <param name="function">The ActionScript bridge callback name.</param>
    /// <param name="args">Arguments passed to the callback.</param>
    /// <returns>A safe operation description, or null for unrelated calls.</returns>
    private static string? DescribeOutboundMapOperation(string function, object[] args)
    {
        if (function == "callGameFunction"
            && args.Length >= 2
            && string.Equals(args[0] as string, "sfc.sendString", StringComparison.Ordinal)
            && args[1] is string packet)
        {
            string[] fields = packet.Split('%');
            if (fields.Length > 3
                && string.Equals(fields[1], "xt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(fields[2], "zm", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(fields[3], "house", StringComparison.OrdinalIgnoreCase))
                    return "house transfer request";

                if (fields.Length > 7
                    && string.Equals(fields[3], "cmd", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fields[5], "tfer", StringComparison.OrdinalIgnoreCase))
                {
                    string map = fields[7];
                    int suffix = map.LastIndexOf('-');
                    if (suffix > 0 && int.TryParse(map[(suffix + 1)..], out _))
                        map = map[..suffix];

                    return $"map transfer to {map}";
                }
            }
        }

        if (function == "sendClientPacket"
            && args.Length >= 2
            && args[0] is string clientPacket
            && string.Equals(args[1] as string, "json", StringComparison.OrdinalIgnoreCase)
            && clientPacket.Contains("\"cmd\":\"moveToArea\"", StringComparison.OrdinalIgnoreCase))
            return "client map-entry packet";

        return null;
    }

    /// <summary>
    /// Defers module toggles until both the game world and its modal UI are available.
    /// Module frame callbacks will then apply the already-recorded enabled state.
    /// </summary>
    /// <param name="function">The ActionScript module callback.</param>
    /// <param name="args">Arguments supplied to the callback.</param>
    /// <returns>True when the module call has been queued.</returns>
    private bool ShouldDeferModuleCall(string function, object[] args)
    {
        if (function is not ("modEnable" or "modDisable") || args.Length == 0 || args[0] is not string moduleName)
            return false;

        if (IsModulesReady())
            return false;

        _deferredModuleCalls[moduleName] = new DeferredModuleCall(function, moduleName);
        return true;
    }

    /// <summary>
    /// Applies module toggles that arrived before Skua finished creating the game world and UI.
    /// </summary>
    private void FlushDeferredModuleCalls()
    {
        if (_deferredModuleCalls.IsEmpty || !IsModulesReady() || Interlocked.Exchange(ref _flushingDeferredModuleCalls, 1) != 0)
            return;

        try
        {
            foreach (KeyValuePair<string, DeferredModuleCall> entry in _deferredModuleCalls.ToArray())
            {
                if (!_deferredModuleCalls.TryRemove(entry.Key, out DeferredModuleCall deferredCall))
                    continue;

                try
                {
                    _runtime.CallActionScriptAsync(deferredCall.Function, new object[] { deferredCall.ModuleName }).GetAwaiter().GetResult();
                }
                catch
                {
                    _deferredModuleCalls.TryAdd(entry.Key, deferredCall);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _flushingDeferredModuleCalls, 0);
        }
    }

    /// <summary>
    /// Checks that Skua has created the world avatar collection and modal UI before enabling UI modules.
    /// </summary>
    /// <returns>True when module callbacks can safely access their game objects.</returns>
    private bool IsModulesReady()
    {
        if (IsNullPath("world.avatars")
            || IsNullPath("ui.ModalStack")
            || IsNullPath("world.myAvatar"))
            return false;

        JsonElement? itemCountResult = _runtime.CallActionScriptAsync(
            "getGameObject",
            new object[] { "world.myAvatar.items.length" }).GetAwaiter().GetResult();
        int itemCount = ConvertResult(itemCountResult, typeof(int)) is int count ? count : 0;
        if (itemCount <= 0)
            return false;

        JsonElement? mapLoadingResult = _runtime.CallActionScriptAsync(
            "getGameObject",
            new object[] { "world.mapLoadInProgress" }).GetAwaiter().GetResult();
        if (ConvertResult(mapLoadingResult, typeof(bool)) is true)
            return false;

        JsonElement? artLoadedResult = _runtime.CallActionScriptAsync(
            "callGameFunction0",
            new object[] { "world.myAvatar.pMC.artLoaded" }).GetAwaiter().GetResult();
        return ConvertResult(artLoadedResult, typeof(bool)) is true;
    }

    /// <summary>
    /// Skips gameplay-only callbacks until their underlying AQW objects have been constructed.
    /// </summary>
    /// <param name="function">The ActionScript callback name.</param>
    /// <returns>True when the required game object is not available.</returns>
    private bool ShouldSkipUnavailableGameCall(string function)
    {
        string? requiredPath = function switch
        {
            "skipCutscenes" or "killLag" => "world.myAvatar",
            "magnetize" => "world.myAvatar",
            "infiniteRange" => "world.actions.active",
            "hidePlayers" or "disableFX" => "world.avatars",
            "jumpCorrectRoom" => "world",
            _ => null
        };

        return !string.IsNullOrWhiteSpace(requiredPath) && IsNullPath(requiredPath);
    }

    /// <summary>
    /// Tests an ActionScript object path without throwing when the game has not created it yet.
    /// </summary>
    /// <param name="path">The object path to check.</param>
    /// <returns>True when the path resolves to null or is unavailable.</returns>
    private bool IsNullPath(string path)
    {
        JsonElement? result = _runtime.CallActionScriptAsync("isNull", new object[] { path }).GetAwaiter().GetResult();
        return ConvertResult(result, typeof(bool)) is true;
    }

    /// <summary>
    /// Avoids ActionScript null-reference errors when Skua writes to UI objects that have not been created yet.
    /// </summary>
    /// <param name="function">The ActionScript function being called.</param>
    /// <param name="args">Arguments supplied to the function.</param>
    /// <returns>True when the target object path is currently unavailable.</returns>
    private bool ShouldSkipNullTarget(string function, object[] args)
    {
        if (args.Length == 0 || args[0] is not string path)
            return false;

        string? targetPath = function switch
        {
            "setGameObject" => path[..Math.Max(0, path.LastIndexOf('.'))],
            "setGameObjectKey" or "setArrayObject" => path,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(targetPath) && IsNullPath(targetPath);
    }

    /// <summary>
    /// Builds a short-lived cache key for read-only game-state calls made repeatedly by Skua's
    /// 20 ms background timer. Only stable status paths are cached; gameplay commands are never cached.
    /// </summary>
    /// <param name="function">The ActionScript bridge function.</param>
    /// <param name="type">The requested result type.</param>
    /// <param name="args">Arguments passed to ActionScript.</param>
    /// <param name="durationMilliseconds">Cache duration in milliseconds.</param>
    /// <returns>A cache key, or null when this call must always reach ActionScript.</returns>
    private static string? GetReadCallCacheKey(string function, Type type, object[] args, out long durationMilliseconds)
    {
        durationMilliseconds = 0;
        string? path = args.Length > 0 ? args[0] as string : null;

        if (function == "isLoggedIn" && args.Length == 0)
            durationMilliseconds = 75;
        else if (function == "isKicked" && args.Length == 0)
            durationMilliseconds = 200;
        else if (function == "isNull" && path is "world" or "world.myAvatar" or "mcLogin.warning" or "mcConnDetail.stage" or "mcLogin.sl.iList")
            durationMilliseconds = 75;
        else if (function == "getGameObjectS" && path == "serverIP")
            durationMilliseconds = 75;
        else if (function == "getGameObject" && path is
            "mcLogin.visible"
            or "mcLogin.sl.iList.numChildren"
            or "mcConnDetail.txtDetail.text"
            or "world.mapLoadInProgress"
            or "world.myAvatar.items.length"
            or "world.myAvatar.dataLeaf.intState")
            durationMilliseconds = 75;
        else if (function == "callGameFunction0" && path == "world.myAvatar.pMC.artLoaded")
            durationMilliseconds = 250;

        return durationMilliseconds > 0
            ? $"{function}\u001f{type.AssemblyQualifiedName}\u001f{path}"
            : null;
    }

    private readonly record struct CachedCall(long ExpiresAt, object? Value);

    private readonly record struct DeferredModuleCall(string Function, string ModuleName);

    /// <inheritdoc />
    public object FromFlashXml(XElement element)
    {
        return element.Name.LocalName switch
        {
            "number" => ParseNumber(element.Value),
            "true" => true,
            "false" => false,
            "null" => null!,
            "array" => element.Elements().Select(FromFlashXml).ToArray(),
            "object" => ParseObject(element),
            _ => element.Value
        };
    }

    /// <inheritdoc />
    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runtime.HostCallReceived -= OnHostCallReceived;
        _readCallCache.Clear();
    }

    /// <summary>
    /// Converts the ActionScript return value into the requested .NET type.
    /// </summary>
    /// <param name="result">The JSON value returned by Ruffle.</param>
    /// <param name="targetType">The expected .NET type.</param>
    /// <returns>The converted value, or the target type's default value.</returns>
    private static object? ConvertResult(JsonElement? result, Type targetType)
    {
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

        JsonElement value = result.Value;

        if (targetType == typeof(string))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

        if (targetType == typeof(object))
            return ConvertJsonElement(value);

        if (targetType == typeof(bool))
            return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed);

        if (targetType.IsEnum)
            return Enum.Parse(targetType, value.ToString(), true);

        return JsonSerializer.Deserialize(value.GetRawText(), targetType, JsonOptions);
    }

    /// <summary>
    /// Converts a JSON element into ordinary CLR values for Skua callback handlers.
    /// </summary>
    /// <param name="element">The JSON value to convert.</param>
    /// <returns>A CLR scalar, array, dictionary, or null.</returns>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ConvertJsonElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    /// <summary>
    /// Parses a Flash XML number using invariant culture.
    /// </summary>
    /// <param name="value">The number text.</param>
    /// <returns>An integer or floating-point value.</returns>
    private static object ParseNumber(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
            return integer;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }

    /// <summary>
    /// Converts a Flash XML object into an ExpandoObject.
    /// </summary>
    /// <param name="element">The object XML element.</param>
    /// <returns>The converted dynamic object.</returns>
    private object ParseObject(XElement element)
    {
        IDictionary<string, object?> values = new ExpandoObject();

        foreach (XElement property in element.Elements())
        {
            string? name = property.Attribute("id")?.Value;
            XElement? value = property.Elements().FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(name) && value is not null)
                values[name] = FromFlashXml(value);
        }

        return (ExpandoObject)values;
    }

    /// <summary>
    /// Forwards ActionScript-to-host calls through Skua's existing FlashCall event.
    /// </summary>
    /// <param name="function">The callback name.</param>
    /// <param name="arguments">The callback arguments as JSON.</param>
    private void OnHostCallReceived(string function, JsonElement arguments)
    {
        if (function.Equals("connectionLost", StringComparison.OrdinalIgnoreCase))
            _readCallCache.Clear();

        // Forward the early "loaded" signal to Program so startup can continue, but do not
        // initialize VibeSkua's ScriptTimer here. Program's handler only completes its
        // bootstrap wait; IScriptInterfaceManager is initialized after AQW's avatar is ready.
        object[] values = arguments.ValueKind == JsonValueKind.Array
            ? arguments.EnumerateArray().Select(value => ConvertJsonElement(value)!).ToArray()
            : Array.Empty<object>();

        // Never run Skua event handlers inside Playwright's exposed-binding callback.
        // A handler may call back into Flash, which would re-enter the same browser page
        // while Chromium is still servicing this host binding and deadlock the bridge.
        _hostCalls.Enqueue((function, values));
        if (Interlocked.Exchange(ref _processingHostCalls, 1) == 0)
            _ = Task.Run(ProcessHostCalls);
    }

    /// <summary>
    /// Processes ActionScript callbacks sequentially after the Playwright binding has returned.
    /// </summary>
    private void ProcessHostCalls()
    {
        do
        {
            while (_hostCalls.TryDequeue(out (string Function, object[] Arguments) call))
            {
                try
                {
                    FlashCall?.Invoke(call.Function, call.Arguments);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Ruffle Host Callback] {call.Function}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            Volatile.Write(ref _processingHostCalls, 0);
        }
        while (!_hostCalls.IsEmpty && Interlocked.Exchange(ref _processingHostCalls, 1) == 0);
    }
}
