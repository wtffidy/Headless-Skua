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

        if (!string.Equals(function, "packetFromServer", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (object? argument in arguments)
        {
            if (argument is string packet)
                ObservePacket(packet);
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
    /// Parses AQW's %xt%loginResponse packet without depending on avatar initialization.
    /// </summary>
    /// <param name="packet">The raw packet received from AQW.</param>
    private void ObservePacket(string packet)
    {
        int responseIndex = packet.IndexOf("%xt%loginResponse%", StringComparison.OrdinalIgnoreCase);
        if (responseIndex < 0)
            return;

        string[] fields = packet[responseIndex..].Split('%');
        if (fields.Length < 7 || !bool.TryParse(fields[4], out bool accepted))
            return;

        string? account = accepted && !string.IsNullOrWhiteSpace(fields[6])
            ? fields[6].Trim()
            : null;

        lock (_sync)
        {
            _loginAccepted = accepted;
            _account = account;
        }
    }
}
