using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Skua.Core.Models.Servers;
using Skua.Headless.UI;

namespace HeadlessSkua.Protocol;

/// <summary>
/// Manages the AQW SmartFoxServer game session, handling the initial login handshake,
/// packet multiplexing, and translating gameplay commands into raw network messages.
/// </summary>
public sealed class AqwProtocolSession : IAsyncDisposable
{
    private readonly SfsClient _client = new();
    private readonly ProtocolGameState _state = new();
    private readonly TaskCompletionSource<bool> _apiOkTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loginOkTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loginResponseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SfsClient Client => _client;
    public ProtocolGameState State => _state;

    public event Action<string, object[]>? FlashEventReceived;

    public AqwProtocolSession()
    {
        _client.MessageReceived += OnSfsMessageReceived;
        _client.Disconnected += OnSfsDisconnected;
    }

    /// <summary>
    /// Connects to the AQW server, executes the SmartFoxServer handshake, and logs in.
    /// </summary>
    public async Task<bool> ConnectAndLoginAsync(
        Server server,
        string token,
        string username,
        CancellationToken cancellationToken = default)
    {
        _state.ServerName = server.Name;
        _state.ServerIP = server.IP;
        _state.Account = username;
        _state.Username = username;

        int port = server.Port > 0 ? server.Port : 5588;
        HeadlessConsole.Info($"[Protocol] Connecting to SFS TCP socket {server.IP}:{port}...");

        await _client.ConnectAsync(server.IP, port, cancellationToken);
        HeadlessConsole.Success("[Protocol] SFS TCP connection established.");

        // 1. Send API version check
        HeadlessConsole.Debug("[Protocol] Sending SFS verChk...");
        await _client.SendAsync("<msg t='sys'><body action='verChk' r='0'><ver v='166' /></body></msg>", cancellationToken);

        await _apiOkTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        HeadlessConsole.Debug("[Protocol] SFS apiOK received.");

        // 2. Send SFS Login
        HeadlessConsole.Debug("[Protocol] Sending SFS login...");
        string loginXml = $"<msg t='sys'><body action='login' r='0'><login z='zone_master'><nick><![CDATA[SPIDER#0001~{username}~5.32]]></nick><pword><![CDATA[{token}]]></pword></login></body></msg>";
        await _client.SendAsync(loginXml, cancellationToken);

        // 3. Await character profile (loginResponse)
        HeadlessConsole.Info("[Protocol] Awaiting character profile (loginResponse)...");
        await _loginResponseTcs.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
        HeadlessConsole.Success($"[Protocol] Character '{_state.Username}' fully loaded into world! (Level {_state.Level}, Gold: {_state.Gold:N0})");

        return true;
    }

    /// <summary>
    /// Sends a raw game packet string to the SFS server.
    /// </summary>
    public async Task SendPacketAsync(string packet, CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected)
            return;

        FlashEventReceived?.Invoke("packet", new object[] { packet });
        await _client.SendAsync(packet, cancellationToken);
    }

    /// <summary>
    /// Processes inbound null-delimited SFS protocol messages.
    /// </summary>
    private void OnSfsMessageReceived(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (message.StartsWith("Z"))
        {
            string? decompressed = TryDecompressSfsMessage(message);
            if (!string.IsNullOrWhiteSpace(decompressed))
            {
                HeadlessConsole.Debug($"[SFS Z-DECOMPRESSED] {decompressed}");
                message = decompressed;
            }
        }
        else
        {
            HeadlessConsole.Debug($"[SFS IN] {message}");
        }

        // XML System Messages
        if (message.StartsWith("<"))
        {
            HandleXmlMessage(message);
            return;
        }

        // XT Pipe-delimited messages: %xt%...%
        if (message.StartsWith("%xt%"))
        {
            HandleXtMessage(message);
            return;
        }

        // JSON Extension messages: {t:"xt", ...}
        if (message.StartsWith("{"))
        {
            HandleJsonMessage(message);
            return;
        }
    }

    private static string? TryDecompressSfsMessage(string message)
    {
        if (!message.StartsWith("Z") || message.Length < 2)
            return null;

        try
        {
            string base64Payload = message.Substring(1);
            int padding = -base64Payload.Length % 4;
            if (padding < 0) padding += 4;
            if (padding > 0)
                base64Payload += new string('=', padding);

            byte[] compressedBytes = Convert.FromBase64String(base64Payload);
            using MemoryStream compressedStream = new(compressedBytes);
            using ZLibStream zlib = new(compressedStream, CompressionMode.Decompress);
            using MemoryStream decompressedStream = new();
            zlib.CopyTo(decompressedStream);
            return Encoding.UTF8.GetString(decompressedStream.ToArray());
        }
        catch (Exception ex)
        {
            HeadlessConsole.Debug($"[Protocol] Failed to decompress Z-packet: {ex.Message}");
            return null;
        }
    }

    private void HandleXmlMessage(string xml)
    {
        try
        {
            XDocument doc = XDocument.Parse(xml);
            XElement? body = doc.Root?.Element("body");
            if (body is null)
                return;

            string action = body.Attribute("action")?.Value ?? string.Empty;

            if (action == "apiOK")
            {
                _apiOkTcs.TrySetResult(true);
            }
            else if (action == "logOK")
            {
                XElement? loginEl = body.Element("login");
                if (loginEl is not null && int.TryParse(loginEl.Attribute("id")?.Value, out int uid))
                {
                    _state.UserId = uid;
                }
                _loginOkTcs.TrySetResult(true);
            }
            else if (action == "logKO")
            {
                HeadlessConsole.Error($"[Protocol] Server rejected login token: {xml}");
                _loginOkTcs.TrySetException(new InvalidOperationException("AQW server rejected the login token."));
            }
        }
        catch (Exception ex)
        {
            HeadlessConsole.Warn($"[Protocol] Could not parse XML message: {ex.Message}");
        }
    }

    private void HandleXtMessage(string message)
    {
        string[] rawTokens = message.Split('%');
        if (rawTokens.Length < 3 || rawTokens[1] != "xt")
            return;

        FlashEventReceived?.Invoke("packetFromServer", new object[] { message });

        string cmd;
        string[] dataObj;

        if (string.Equals(rawTokens[2], "zm", StringComparison.OrdinalIgnoreCase))
        {
            // Format: %xt%zm%<cmd>%<roomId>%...%
            cmd = rawTokens.Length > 3 ? rawTokens[3] : string.Empty;
            int takeCount = Math.Max(0, rawTokens.Length - 4);
            dataObj = rawTokens.Skip(3).Take(takeCount).ToArray();
        }
        else
        {
            // Format: %xt%<cmd>%<roomId>%...% (e.g. loginResponse)
            cmd = rawTokens[2];
            int takeCount = Math.Max(0, rawTokens.Length - 3);
            dataObj = rawTokens.Skip(2).Take(takeCount).ToArray();
        }

        // Forward to Skua's pext handler
        JObject pextEnvelope = new()
        {
            ["type"] = "str",
            ["params"] = new JObject
            {
                ["type"] = "str",
                ["dataObj"] = new JArray(dataObj)
            }
        };

        FlashEventReceived?.Invoke("pext", new object[] { pextEnvelope.ToString() });

        if (string.Equals(cmd, "warning", StringComparison.OrdinalIgnoreCase))
        {
            HeadlessConsole.Warn($"[Protocol Warning] {message}");
        }
        else if (string.Equals(cmd, "logoutWarning", StringComparison.OrdinalIgnoreCase))
        {
            HeadlessConsole.Warn($"[Protocol Logout Warning] {message}");
        }
        else if (string.Equals(cmd, "loginResponse", StringComparison.OrdinalIgnoreCase))
        {
            HandleXtLoginResponse(dataObj);
        }
        else if (string.Equals(cmd, "uotls", StringComparison.OrdinalIgnoreCase))
        {
            // %xt%uotls%-1%username%strFrame:r2,strPad:Left,...%
            if (dataObj.Length > 2 && string.Equals(dataObj[2], _state.Username, StringComparison.OrdinalIgnoreCase))
            {
                if (dataObj.Length > 3)
                {
                    string kvs = dataObj[3];
                    foreach (var pair in kvs.Split(','))
                    {
                        var kv = pair.Split(':');
                        if (kv.Length == 2)
                        {
                            if (kv[0] == "strFrame") _state.Cell = kv[1];
                            else if (kv[0] == "strPad") _state.Pad = kv[1];
                            else if (kv[0] == "afk" && bool.TryParse(kv[1], out bool afk)) _state.IsAfk = afk;
                        }
                    }
                }
            }
        }
        else if (string.Equals(cmd, "respawnMon", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in dataObj.Skip(1))
            {
                if (int.TryParse(token, out int monMapId) && monMapId > 0)
                {
                    var mon = _state.MapMonsters.FirstOrDefault(m => m.MapID == monMapId);
                    if (mon is not null)
                    {
                        mon.HP = mon.MaxHP;
                        mon.State = 1;
                        break;
                    }
                }
            }
        }
    }

    private void HandleXtLoginResponse(string[] dataObj)
    {
        try
        {
            // In ActionScript Game.as:
            // dataObj[0] = "loginResponse"
            // dataObj[1] = roomId (-1 or 1)
            // dataObj[2] = success (1, "true", "True")
            // dataObj[3] = userId (Number)
            // dataObj[4] = userName (String)
            // dataObj[5] = errorMessage
            // dataObj[6] = serverTime (String)
            // dataObj[7] = vars (comma-separated variables)
            bool success = dataObj.Length > 2 && (dataObj[2] == "1" || dataObj[2].Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!success)
            {
                string errMsg = dataObj.Length > 5 && !string.IsNullOrWhiteSpace(dataObj[5]) ? dataObj[5] : "Login failed";
                HeadlessConsole.Error($"[Protocol] Server rejected login: {errMsg}");
                _loginResponseTcs.TrySetException(new InvalidOperationException($"AQW server rejected login: {errMsg}"));
                return;
            }

            if (dataObj.Length > 3 && int.TryParse(dataObj[3], out int uid))
                _state.UserId = uid;

            if (dataObj.Length > 4 && !string.IsNullOrWhiteSpace(dataObj[4]))
                _state.Username = dataObj[4];

            if (dataObj.Length > 7)
            {
                string varsStr = dataObj[7];
                foreach (string pair in varsStr.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = pair[..eq].Trim();
                        string v = pair[(eq + 1)..].Trim();
                        if (k == "iMaxBagSlots" && int.TryParse(v, out int bag)) _state.BagSlots = bag;
                        else if (k == "iMaxBankSlots" && int.TryParse(v, out int bank)) _state.BankSlots = bank;
                    }
                }
            }

            _state.IsLoggedIn = true;
            _state.WorldLoaded = true;

            HeadlessConsole.Debug("[Protocol] loginResponse accepted. Sending firstJoin...");
            _ = _client.SendAsync("%xt%zm%firstJoin%1%");

            _loginResponseTcs.TrySetResult(true);
            FlashEventReceived?.Invoke("loaded", Array.Empty<object>());
        }
        catch (Exception ex)
        {
            HeadlessConsole.Error($"[Protocol] Error processing loginResponse: {ex.Message}");
            _loginResponseTcs.TrySetException(ex);
        }
    }

    private void HandleJsonMessage(string json)
    {
        try
        {
            JObject obj = JObject.Parse(json);
            JToken? b = obj["b"];
            if (b is not JObject body)
                return;

            JToken? o = body["o"];
            if (o is not JObject data)
                return;

            string cmd = data["cmd"]?.Value<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(cmd))
                return;

            // Update local game state
            _state.HandlePacket(cmd, data);

            if (cmd == "loginResponse")
            {
                _state.IsLoggedIn = true;
                _state.WorldLoaded = true;
                _ = _client.SendAsync("%xt%zm%firstJoin%1%");
                _loginResponseTcs.TrySetResult(true);
                FlashEventReceived?.Invoke("loaded", Array.Empty<object>());
            }
            else if (cmd == "moveToArea")
            {
                _state.WorldLoaded = true;
                if (_state.UserId > 0 && _state.Inventory.Count == 0)
                {
                    _ = _client.SendAsync($"%xt%zm%retrieveInventory%{_state.RoomID}%{_state.UserId}%");
                    _ = _client.SendAsync($"%xt%zm%retrieveUserData%{_state.RoomID}%{_state.UserId}%");
                }
            }
            else if (cmd == "initUserData" || cmd == "initUserDatas")
            {
                if (_state.UserId > 0 && _state.Inventory.Count == 0)
                    _ = _client.SendAsync($"%xt%zm%retrieveInventory%{_state.RoomID}%{_state.UserId}%");
            }
            else if (cmd == "loadInventoryBig" || cmd == "loadInventory")
            {
                HeadlessConsole.Success($"[Protocol] Inventory loaded ({_state.Inventory.Count} items). Player.Loaded is active.");
            }

            // Wrap into Skua's pext format: { params: { type: "json", dataObj: data } }
            JObject pextEnvelope = new()
            {
                ["params"] = new JObject
                {
                    ["type"] = "json",
                    ["dataObj"] = data
                }
            };

            FlashEventReceived?.Invoke("pext", new object[] { pextEnvelope.ToString() });
        }
        catch (Exception ex)
        {
            HeadlessConsole.Warn($"[Protocol] Error handling JSON message: {ex.Message}");
        }
    }

    private void OnSfsDisconnected()
    {
        _state.IsLoggedIn = false;
        _state.WorldLoaded = false;
        FlashEventReceived?.Invoke("connectionLost", Array.Empty<object>());
        HeadlessConsole.Warn("[Protocol] Disconnected from SmartFoxServer.");
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}
