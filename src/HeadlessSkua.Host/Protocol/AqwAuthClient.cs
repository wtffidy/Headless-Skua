using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Skua.Core.Models.Servers;

namespace HeadlessSkua.Protocol;

/// <summary>
/// Result returned after authenticating with the Artix/AQW web API.
/// </summary>
public sealed record AqwLoginResult(
    bool Success,
    string? Message,
    string? Token,
    int UserId,
    string? Username,
    List<Server> Servers);

/// <summary>
/// Authenticates against the AQW Web API to obtain session tokens and server lists.
/// </summary>
public static class AqwAuthClient
{
    private const string LoginEndpoint = "https://game.aq.com/game/api/login/now";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/javascript, */*; q=0.01");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Origin", "https://game.aq.com");
        client.DefaultRequestHeaders.Add("Referer", "https://game.aq.com/game/");
        return client;
    }

    /// <summary>
    /// Authenticates with the AQW API using username and password.
    /// </summary>
    public static async Task<AqwLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        string url = $"{LoginEndpoint}?ran={Random.Shared.NextDouble()}";
        var parameters = new Dictionary<string, string>
        {
            ["user"] = username,
            ["pass"] = password,
            ["option"] = "1"
        };

        using FormUrlEncodedContent content = new(parameters);
        HttpResponseMessage response = await HttpClient.PostAsync(url, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new AqwLoginResult(
                Success: false,
                Message: $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
                Token: null,
                UserId: 0,
                Username: null,
                Servers: new List<Server>());
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        JsonElement loginNode = root;
        if (root.TryGetProperty("login", out JsonElement loginObj) && loginObj.ValueKind == JsonValueKind.Object)
            loginNode = loginObj;

        int bSuccess = 0;
        if (loginNode.TryGetProperty("bSuccess", out JsonElement sEl))
        {
            if (sEl.ValueKind == JsonValueKind.Number) bSuccess = sEl.GetInt32();
            else if (sEl.ValueKind == JsonValueKind.String && int.TryParse(sEl.GetString(), out int p)) bSuccess = p;
        }
        else if (root.TryGetProperty("bSuccess", out JsonElement rootSEl))
        {
            if (rootSEl.ValueKind == JsonValueKind.Number) bSuccess = rootSEl.GetInt32();
            else if (rootSEl.ValueKind == JsonValueKind.String && int.TryParse(rootSEl.GetString(), out int p)) bSuccess = p;
        }

        string? sMsg = null;
        if (loginNode.TryGetProperty("sMsg", out JsonElement msgEl)) sMsg = msgEl.GetString();
        else if (root.TryGetProperty("sMsg", out JsonElement rootMsgEl)) sMsg = rootMsgEl.GetString();

        if (bSuccess != 1)
        {
            return new AqwLoginResult(
                Success: false,
                Message: sMsg ?? $"Server returned: {json}",
                Token: null,
                UserId: 0,
                Username: null,
                Servers: new List<Server>());
        }

        string? sToken = loginNode.TryGetProperty("sToken", out JsonElement tokenEl) ? tokenEl.GetString()
            : (root.TryGetProperty("sToken", out JsonElement rTokenEl) ? rTokenEl.GetString() : null);

        int userId = 0;
        if (loginNode.TryGetProperty("userid", out JsonElement userEl) && userEl.TryGetInt32(out int id)) userId = id;
        else if (root.TryGetProperty("userid", out JsonElement rUserEl) && rUserEl.TryGetInt32(out int rId)) userId = rId;

        string? unm = loginNode.TryGetProperty("unm", out JsonElement unmEl) ? unmEl.GetString()
            : (root.TryGetProperty("unm", out JsonElement rUnmEl) ? rUnmEl.GetString() : username);

        List<Server> servers = new();
        if (root.TryGetProperty("servers", out JsonElement serversEl) && serversEl.ValueKind == JsonValueKind.Array)
        {
            string serversJson = serversEl.GetRawText();
            servers = JsonConvert.DeserializeObject<List<Server>>(serversJson) ?? new List<Server>();
        }
        else if (loginNode.TryGetProperty("servers", out JsonElement targetServersEl) && targetServersEl.ValueKind == JsonValueKind.Array)
        {
            string serversJson = targetServersEl.GetRawText();
            servers = JsonConvert.DeserializeObject<List<Server>>(serversJson) ?? new List<Server>();
        }

        return new AqwLoginResult(
            Success: true,
            Message: null,
            Token: sToken,
            UserId: userId,
            Username: unm,
            Servers: servers);
    }
}
