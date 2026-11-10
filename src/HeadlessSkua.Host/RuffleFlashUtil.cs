using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;

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

    /// <inheritdoc />
    public event FlashCallHandler? FlashCall;

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

    /// <inheritdoc />
    public object? Call(string function, Type type, params object[] args)
    {
        try
        {
            if (ShouldSkipNullTarget(function, args))
                return type.IsValueType ? Activator.CreateInstance(type) : null;

            JsonElement? result = _runtime.CallActionScriptAsync(function, args).GetAwaiter().GetResult();
            return ConvertResult(result, type);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Ruffle Call] {function}: {exception.GetType().Name}: {exception.Message}");
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
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

        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        JsonElement? result = _runtime.CallActionScriptAsync("isNull", new object[] { targetPath }).GetAwaiter().GetResult();
        return ConvertResult(result, typeof(bool)) is true;
    }

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
        object[] values = arguments.ValueKind == JsonValueKind.Array
            ? arguments.EnumerateArray().Select(value => ConvertJsonElement(value)!).ToArray()
            : Array.Empty<object>();

        FlashCall?.Invoke(function, values);
    }
}
