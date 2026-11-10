using System.Diagnostics;
using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Servers;
using Skua.Core.Utils;
using Skua.Headless.UI;

namespace HeadlessSkua;

/// <summary>
/// Handles interactive account selection, authentication, and game-server selection.
/// </summary>
internal static class HeadlessLoginFlow
{
    /// <summary>
    /// Authenticates an account and connects it to a server selected in the terminal.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <param name="cancellationToken">Token used to cancel login.</param>
    /// <returns>True when the game world is loaded.</returns>
    public static async Task<bool> LoginAsync(IScriptInterface bot, HeadlessSessionState sessionState, CancellationToken cancellationToken)
    {
        (string username, string password) = SelectAccount(bot);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            HeadlessConsole.Error("A username and password are required.");
            return false;
        }

        bot.Servers.SetLoginInfo(username, password);
        HeadlessConsole.Info($"Authenticating {username}...");
        Stopwatch authenticationTimer = Stopwatch.StartNew();
        bot.Servers.Login(username, password);

        if (!await WaitForServerListAsync(bot, cancellationToken))
        {
            HeadlessConsole.Error("Login did not reach the game server-selection screen. Check the credentials and try again.");
            return false;
        }

        HeadlessConsole.Info($"AQW server list ready in {authenticationTimer.Elapsed.TotalSeconds:F1}s.");
        List<Server> servers = bot.Servers.ServerList.ToList();
        if (servers.Count == 0)
            servers = await bot.Servers.GetServers(true);

        List<Server> selectableServers = servers
            .Where(server => !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.IP) && server.Online)
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectableServers.Count == 0)
        {
            HeadlessConsole.Error("No online servers were returned.");
            return false;
        }

        HeadlessConsole.Success($"Authenticated as {username}.");
        HeadlessConsole.Info($"Available servers: {selectableServers.Count}");
        for (int index = 0; index < selectableServers.Count; index++)
        {
            Server server = selectableServers[index];
            string memberOnly = server.Upgrade ? " [MEMBER]" : string.Empty;
            string population = server.MaxPlayers > 0 ? $" [{server.PlayerCount}/{server.MaxPlayers}]" : string.Empty;
            Console.WriteLine($"  {index + 1,2}. {server.Name}{memberOnly}{population}");
        }

        Server? selectedServer = null;
        while (selectedServer is null && !cancellationToken.IsCancellationRequested)
        {
            string? input = HeadlessConsole.Prompt($"Select server [1-{selectableServers.Count}]");
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (int.TryParse(input, out int selection) && selection >= 1 && selection <= selectableServers.Count)
                selectedServer = selectableServers[selection - 1];
            else
                HeadlessConsole.Warn($"Enter a number between 1 and {selectableServers.Count}.");
        }

        if (selectedServer is null)
            return false;

        // Publish the chosen target before invoking ActionScript. The server-list click can
        // begin a long asynchronous game handshake, but the panel should still show the target.
        bot.Servers.LastIP = selectedServer.IP;
        bot.Servers.LastName = selectedServer.Name;
        HeadlessConsole.Info($"Selected server target: {selectedServer.Name} ({selectedServer.IP}).");

        int serverPort = selectedServer.Port > 0 ? selectedServer.Port : 5588;
        HeadlessConsole.Info($"Preparing the {selectedServer.Name} connection ({selectedServer.IP}:{serverPort})...");

        // AQW requires the server's XML socket policy before opening its game connection.
        try
        {
            HeadlessConsole.Debug($"Requesting the XML socket policy from {selectedServer.IP}:{serverPort}...");
            bot.Flash.Call("loadPolicyFile", selectedServer.IP, serverPort);
            HeadlessConsole.Debug("XML socket policy request queued.");
            bot.Flash.Call("killLoginModals");
            HeadlessConsole.Debug("Login modals cleared.");
        }
        catch (Exception exception)
        {
            HeadlessConsole.Warn($"Could not prepare the server connection policy: {exception.Message}");
        }

        // Use Skua's direct ActionScript bridge first. The server-list MouseEvent path
        // can remain inside Ruffle while the game starts its socket handshake, preventing
        // the synchronous ExternalInterface call from returning to the host.
        string serverData = JsonSerializer.Serialize(new
        {
            sName = selectedServer.Name,
            sIP = selectedServer.IP,
            iChat = selectedServer.ChatLevel,
            iPort = serverPort,
            bOnline = selectedServer.Online,
            sLang = selectedServer.Lang,
            iCount = selectedServer.PlayerCount,
            bUpg = selectedServer.Upgrade,
            iMax = selectedServer.MaxPlayers,
            iLevel = selectedServer.Level
        });

        HeadlessConsole.Debug($"Requesting {selectedServer.Name} through Skua's direct server bridge...");
        bool selectedInClient = false;
        try
        {
            if (bot.Flash is RuffleFlashUtil flashUtil)
            {
                // Queue the ActionScript callback and return immediately. A synchronous
                // ExternalInterface call can remain blocked while AQW starts its socket handshake.
                flashUtil.DispatchActionScript("connectToServer", serverData);
                selectedInClient = true;
                HeadlessConsole.Debug("AQW direct server bridge request queued asynchronously.");
            }
            else
            {
                selectedInClient = bot.Flash.Call<bool>("connectToServer", serverData);
                HeadlessConsole.Debug($"AQW direct server bridge returned: {selectedInClient}.");
            }
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Direct server bridge was unavailable: {exception.Message}");
        }
        if (selectedInClient)
            HeadlessConsole.Info($"Server connection request queued for {selectedServer.Name}.");
        else
        {
            HeadlessConsole.Warn("Skua's direct server bridge was unavailable. Trying the AQW server-list click...");
            try
            {
                selectedInClient = bot.Flash.Call<bool>("clickServer", selectedServer.Name);
            }
            catch (Exception exception)
            {
                HeadlessConsole.Debug($"Server-list click was unavailable: {exception.Message}");
            }

            HeadlessConsole.Debug($"AQW server-list selection returned: {selectedInClient}.");
        }

        if (!selectedInClient)
        {
            HeadlessConsole.Warn("Skua's server-selection bridge was unavailable. Falling back to ConnectIP...");
            try
            {
                selectedInClient = bot.Servers.ConnectIP(selectedServer.IP, serverPort);
            }
            catch (Exception exception)
            {
                HeadlessConsole.Error($"Direct IP connection failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (!selectedInClient)
        {
            string? modalMessage = GetActiveModalMessage(bot);
            HeadlessConsole.Error($"The AQW client rejected the connection request for {selectedServer.Name}.");
            if (!string.IsNullOrWhiteSpace(modalMessage))
                HeadlessConsole.Error($"Game message: {modalMessage}");
            return false;
        }

        bot.Servers.LastIP = selectedServer.IP;
        bot.Servers.LastName = selectedServer.Name;
        HeadlessConsole.Info($"Connection request accepted. Waiting for {selectedServer.Name} to finish loading...");
        bool connected = await WaitForWorldLoadedAsync(bot, sessionState, TimeSpan.FromSeconds(60), cancellationToken);
        if (!connected)
        {
            string? modalMessage = GetActiveModalMessage(bot);
            string connectionDetail = GetConnectionDetail(bot);
            HeadlessConsole.Error($"The connection to {selectedServer.Name} did not reach a fully loaded player.");
            bool worldReadyForDiagnostics = bot.Flash.IsWorldLoaded;
            bool avatarReadyForDiagnostics = worldReadyForDiagnostics && !bot.Flash.IsNull("world.myAvatar");
            bool playerReadyForDiagnostics = avatarReadyForDiagnostics && bot.Player.Loaded;
            HeadlessConsole.Error($"Connection state: Playing={bot.Player.Playing}, PlayerLoaded={playerReadyForDiagnostics}, WorldLoaded={worldReadyForDiagnostics}.");
            if (!string.IsNullOrWhiteSpace(modalMessage))
                HeadlessConsole.Error($"Game message: {modalMessage}");
            if (!string.IsNullOrWhiteSpace(connectionDetail) && !string.Equals(connectionDetail, "null", StringComparison.OrdinalIgnoreCase))
                HeadlessConsole.Error($"AQW connection detail: {connectionDetail}");
            return false;
        }

        bot.Servers.LastIP = selectedServer.IP;
        bot.Servers.LastName = selectedServer.Name;
        HeadlessConsole.Success($"Connected to {selectedServer.Name} as {username}.");
        HeadlessConsole.Panel("Session",
            ("Account", username),
            ("Server", selectedServer.Name),
            ("Mode", "Headless"));
        return true;
    }

    /// <summary>
    /// Waits until the selected AQW server has finished loading the game world.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <param name="timeout">Maximum time allowed for world loading.</param>
    /// <param name="cancellationToken">Token used to cancel the connection wait.</param>
    /// <returns>True when the player is playing and the world is loaded.</returns>
    private static async Task<bool> WaitForWorldLoadedAsync(IScriptInterface bot, HeadlessSessionState sessionState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch connectionTimer = Stopwatch.StartNew();
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        DateTime nextDiagnostic = DateTime.UtcNow.AddSeconds(5);
        DateTime nextPlayingCheck = DateTime.MaxValue;
        DateTime nextPlayerLoadedCheck = DateTime.MaxValue;
        DateTime nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
        bool loginResponseReceived = false;
        bool worldReady = false;
        bool avatarReady = false;
        bool playing = false;
        bool playerReady = false;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            // Wait for the host-side login response handler to finish before making
            // synchronous calls back into the Ruffle page.
            if (!loginResponseReceived)
            {
                if (!sessionState.GetSnapshot().LoginAccepted)
                {
                    if (DateTime.UtcNow >= nextHeartbeat)
                    {
                        HeadlessConsole.Debug("Waiting for AQW to accept the game login before querying the Flash bridge.");
                        nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
                    }

                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                loginResponseReceived = true;
                HeadlessConsole.Debug($"AQW accepted the game login after {connectionTimer.Elapsed.TotalSeconds:F1}s.");
                await Task.Delay(100, cancellationToken);
            }

            DateTime now = DateTime.UtcNow;
            if (!worldReady)
            {
                worldReady = bot.Flash.IsWorldLoaded;
                if (worldReady)
                    HeadlessConsole.Debug($"AQW world initialized after {connectionTimer.Elapsed.TotalSeconds:F1}s.");
            }

            if (worldReady && !avatarReady)
            {
                avatarReady = !bot.Flash.IsNull("world.myAvatar");
                if (avatarReady)
                {
                    HeadlessConsole.Debug($"AQW character avatar appeared after {connectionTimer.Elapsed.TotalSeconds:F1}s.");
                    nextPlayingCheck = DateTime.MinValue;
                }
            }

            if (avatarReady && now >= nextPlayingCheck)
            {
                playing = bot.Player.Playing;
                nextPlayingCheck = now.AddMilliseconds(500);
                if (playing)
                    nextPlayerLoadedCheck = DateTime.MinValue;
            }

            if (playing && !playerReady && now >= nextPlayerLoadedCheck)
            {
                playerReady = bot.Player.Loaded;
                nextPlayerLoadedCheck = now.AddMilliseconds(500);
                if (playerReady)
                {
                    HeadlessConsole.Debug($"VibeSkua player fully loaded after {connectionTimer.Elapsed.TotalSeconds:F1}s.");
                    return true;
                }
            }

            if (now >= nextDiagnostic)
            {
                HeadlessConsole.Debug($"AQW initialization: world={worldReady}, avatar={avatarReady}, playing={playing}, playerLoaded={playerReady} ({connectionTimer.Elapsed.TotalSeconds:F1}s).");
                nextDiagnostic = now.AddSeconds(5);
            }

            if (now >= nextHeartbeat)
            {
                HeadlessConsole.Debug("AQW initialization is still waiting for the player.");
                nextHeartbeat = now.AddSeconds(5);
            }

            await Task.Delay(250, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        worldReady = bot.Flash.IsWorldLoaded;
        avatarReady = worldReady && !bot.Flash.IsNull("world.myAvatar");
        playing = avatarReady && bot.Player.Playing;
        playerReady = playing && bot.Player.Loaded;
        return playerReady;
    }

    /// <summary>
    /// Reads the current AQW modal text when the Flash bridge is available.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <returns>The active message, or null when no message is shown.</returns>
    private static string? GetActiveModalMessage(IScriptInterface bot)
    {
        try
        {
            string? message = bot.Flash.Call<string>("getActiveModalMessage");
            return string.IsNullOrWhiteSpace(message) || string.Equals(message, "null", StringComparison.OrdinalIgnoreCase)
                ? null
                : message.Trim();
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Could not read the AQW modal message: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the connection-stage detail shown by the AQW login screen.
    /// </summary>
    /// <param name="bot">The initialized Skua script interface.</param>
    /// <returns>The connection detail text, or null when it is unavailable.</returns>
    private static string GetConnectionDetail(IScriptInterface bot)
    {
        try
        {
            return bot.Flash.IsNull("mcConnDetail.stage")
                ? string.Empty
                : bot.Flash.GetGameObject("mcConnDetail.txtDetail.text", string.Empty) ?? string.Empty;
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Could not read the AQW connection detail: {exception.Message}");
            return string.Empty;
        }
    }

    private static async Task<bool> WaitForServerListAsync(IScriptInterface bot, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        DateTime nextDiagnostic = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            string? modalMessage = null;
            try
            {
                modalMessage = bot.Flash.Call<string>("getActiveModalMessage");
            }
            catch (Exception exception)
            {
                HeadlessConsole.Debug($"Login dialog status is unavailable: {exception.Message}");
            }

            if (!string.IsNullOrWhiteSpace(modalMessage) && !string.Equals(modalMessage, "null", StringComparison.OrdinalIgnoreCase))
            {
                HeadlessConsole.Error($"Login rejected: {modalMessage.Trim()}");
                try
                {
                    bot.Flash.Call("killLoginModals");
                }
                catch (Exception exception)
                {
                    HeadlessConsole.Debug($"Could not dismiss the login dialog: {exception.Message}");
                }

                return false;
            }

            List<Server> serverList = bot.Servers.ServerList;
            bool hasServerData = serverList.Any(server =>
                !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.IP));

            if (hasServerData)
            {
                try
                {
                    bot.Flash.Call("loadPolicyFiles");
                }
                catch (Exception exception)
                {
                    HeadlessConsole.Debug($"Policy file preload was unavailable: {exception.Message}");
                }

                return true;
            }

            if (DateTime.UtcNow >= nextDiagnostic)
            {
                HeadlessConsole.Debug("Waiting for AQW to populate its server list...");
                nextDiagnostic = DateTime.UtcNow.AddSeconds(5);
            }

            await Task.Delay(500, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static (string Username, string Password) SelectAccount(IScriptInterface bot)
    {
        List<ManagedAccount> accounts = bot.Accounts.GetAllAccounts();
        if (accounts.Count > 0)
        {
            HeadlessConsole.Info($"Found {accounts.Count} account(s) saved in VibeSkua Manager.");
            for (int index = 0; index < accounts.Count; index++)
            {
                ManagedAccount account = accounts[index];
                string displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
                Console.WriteLine($"  {index + 1,2}. {displayName} ({account.Username})");
            }

            Console.WriteLine("   M. Enter credentials manually");
            while (true)
            {
                string? choice = HeadlessConsole.Prompt("Choose an account");
                if (string.Equals(choice, "m", StringComparison.OrdinalIgnoreCase))
                    break;

                if (int.TryParse(choice, out int selection) && selection >= 1 && selection <= accounts.Count)
                {
                    ManagedAccount account = accounts[selection - 1];
                    string storedPassword = account.Password ?? string.Empty;
                    string password = storedPassword.StartsWith("enc:", StringComparison.Ordinal)
                        ? CryptoUtils.Decrypt(storedPassword)
                        : storedPassword;
                    return (account.Username ?? string.Empty, password);
                }

                HeadlessConsole.Warn("Choose an account number or M for manual login.");
            }
        }

        string? username = HeadlessConsole.Prompt("Username");
        string passwordInput = HeadlessConsole.ReadSecret("Password");
        return (username?.Trim() ?? string.Empty, passwordInput);
    }

}
