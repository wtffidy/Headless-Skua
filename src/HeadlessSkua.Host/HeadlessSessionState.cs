using System.Text.Json;

namespace HeadlessSkua;

/// <summary>
/// Tracks authentication reported directly by AQW before Skua has constructed the player avatar.
/// </summary>
internal sealed class HeadlessSessionState
{
    private readonly object _sync = new();
    private bool _loginAccepted;
    private string? _account;

    /// <summary>
    /// Observes ActionScript callbacks for the AQW login response.
    /// </summary>
    /// <param name="function">The callback name.</param>
    /// <param name="arguments">The callback arguments.</param>
    public void Observe(string function, object[] arguments)
    {
        if (string.Equals(function, "connectionLost", StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            return;
        }

        if (string.Equals(function, "userIntentionalAction", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(argument => argument is string action
                && action.Contains("logout", StringComparison.OrdinalIgnoreCase)))
        {
            Reset();
            return;
        }

        if (string.Equals(function, "packetFromServer", StringComparison.OrdinalIgnoreCase))
        {
            foreach (object? argument in arguments)
                if (argument is string packet)
                    ObservePacket(packet);

            return;
        }

        if (string.Equals(function, "pext", StringComparison.OrdinalIgnoreCase))
            foreach (object? argument in arguments)
                if (argument is string json)
                {
                    if (json.Contains("loginResponse%", StringComparison.OrdinalIgnoreCase))
                        ObservePacket(json);
                    else
                        ObserveExtensionResponse(json);
                }
    }

    /// <summary>
    /// Returns a consistent snapshot of AQW's latest login response.
    /// </summary>
    /// <returns>The accepted state and account name reported by AQW.</returns>
    public (bool LoginAccepted, string? Account) GetSnapshot()
    {
        lock (_sync)
            return (_loginAccepted, _account);
    }

    /// <summary>
    /// Clears the tracked session after a disconnect or logout action.
    /// </summary>
    private void Reset()
    {
        lock (_sync)
        {
            _loginAccepted = false;
            _account = null;
        }
    }

    /// <summary>
    /// Reads a parsed SmartFox extension response. AQW/Ruffle may serialize packet fields
    /// as strings, booleans, or numbers, so parsing must not assume every field is a string.
    /// </summary>
    /// <param name="json">The serialized extension response.</param>
    private void ObserveExtensionResponse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                ObservePacket(root.GetString() ?? string.Empty);
                return;
            }

            JsonElement data;
            if (TryGetPropertyIgnoreCase(root, "params", out JsonElement parameters)
                && TryGetPropertyIgnoreCase(parameters, "dataObj", out data))
            {
                ObserveLoginResponse(data);
                return;
            }

            if (TryGetPropertyIgnoreCase(root, "dataObj", out data) || root.ValueKind == JsonValueKind.Array)
                ObserveLoginResponse(root.ValueKind == JsonValueKind.Array ? root : data);
        }
        catch (JsonException)
        {
            // Non-JSON extension events are not login responses.
        }
    }

    /// <summary>
    /// Extracts AQW's login response from the SmartFox dataObj array.
    /// </summary>
    /// <param name="data">The extension response data.</param>
    private void ObserveLoginResponse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() < 5
            || !string.Equals(GetScalarText(data[0]), "loginResponse", StringComparison.OrdinalIgnoreCase)
            || !bool.TryParse(GetScalarText(data[2]), out bool accepted))
            return;

        string? account = accepted ? GetScalarText(data[4])?.Trim() : null;
        SetLoginState(accepted, account);
    }

    /// <summary>
    /// Parses AQW's raw loginResponse packet independently of avatar initialization.
    /// Supports both full %xt% packets and the shortened loginResponse% form.
    /// </summary>
    /// <param name="packet">The raw packet received from AQW.</param>
    private void ObservePacket(string packet)
    {
        int responseIndex = packet.IndexOf("loginResponse%", StringComparison.OrdinalIgnoreCase);
        if (responseIndex < 0)
            return;

        string[] fields = packet[responseIndex..].Split('%');
        if (fields.Length < 5 || !bool.TryParse(fields[2], out bool accepted))
            return;

        string? account = accepted ? fields[4].Trim() : null;
        SetLoginState(accepted, account);
    }

    /// <summary>
    /// Stores an accepted or rejected login response atomically.
    /// </summary>
    /// <param name="accepted">Whether AQW accepted the login.</param>
    /// <param name="account">The account name returned by AQW.</param>
    private void SetLoginState(bool accepted, string? account)
    {
        lock (_sync)
        {
            _loginAccepted = accepted;
            _account = string.IsNullOrWhiteSpace(account) ? null : account;
        }
    }

    /// <summary>
    /// Reads a JSON object property without depending on the serializer's property casing.
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in element.EnumerateObject())
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

        value = default;
        return false;
    }

    /// <summary>
    /// Converts a scalar JSON value into text without throwing on boolean or numeric fields.
    /// </summary>
    private static string? GetScalarText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object or JsonValueKind.Array => null,
            _ => value.ToString()
        };
    }
}
