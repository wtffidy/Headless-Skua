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
    public static async Task<bool> LoginAsync(IScriptInterface bot, CancellationToken cancellationToken)
    {
        (string username, string password) = SelectAccount(bot);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            HeadlessConsole.Error("A username and password are required.");
            return false;
        }

        bot.Servers.SetLoginInfo(username, password);
        HeadlessConsole.Info($"Authenticating {username}...");
        bot.Servers.Login(username, password);

        if (!await WaitForServerListAsync(bot, cancellationToken))
        {
            HeadlessConsole.Error("Login did not reach the game server-selection screen. Check the credentials and try again.");
            return false;
        }

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

        int serverPort = selectedServer.Port > 0 ? selectedServer.Port : 5588;
        HeadlessConsole.Info($"Preparing the {selectedServer.Name} connection ({selectedServer.IP}:{serverPort})...");

        // AQW requires the server's XML socket policy before opening its game connection.
        try
        {
            bot.Flash.Call("loadPolicyFile", selectedServer.IP, serverPort);
            bot.Flash.Call("killLoginModals");
        }
        catch (Exception exception)
        {
            HeadlessConsole.Warn($"Could not prepare the server connection policy: {exception.Message}");
        }

        // Use Skua's normal server-list click path first. It selects the actual row in
        // mcLogin and lets the game client perform its own connection handshake.
        bool selectedInClient = false;
        try
        {
            selectedInClient = bot.Flash.Call<bool>("clickServer", selectedServer.Name);
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Server-list click was unavailable: {exception.Message}");
        }

        if (selectedInClient)
            HeadlessConsole.Info($"Selected {selectedServer.Name} through the AQW server list.");
        else
        {
            HeadlessConsole.Warn("The AQW server-list entry could not be clicked. Trying Skua's direct server bridge...");

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

            try
            {
                selectedInClient = bot.Flash.Call<bool>("connectToServer", serverData);
            }
            catch (Exception exception)
            {
                HeadlessConsole.Debug($"Direct server bridge was unavailable: {exception.Message}");
            }
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

        HeadlessConsole.Info($"Connection request accepted. Waiting for {selectedServer.Name} to finish loading...");
        bool connected = await WaitForWorldLoadedAsync(bot, TimeSpan.FromSeconds(60), cancellationToken);
        if (!connected)
        {
            string? modalMessage = GetActiveModalMessage(bot);
            string connectionDetail = GetConnectionDetail(bot);
            HeadlessConsole.Error($"The connection to {selectedServer.Name} did not reach a loaded game world.");
            HeadlessConsole.Error($"Connection state: Playing={bot.Player.Playing}, WorldLoaded={bot.Flash.IsWorldLoaded}.");
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
    private static async Task<bool> WaitForWorldLoadedAsync(IScriptInterface bot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (bot.Player.Playing && bot.Flash.IsWorldLoaded)
                return true;

            await Task.Delay(250, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bot.Player.Playing && bot.Flash.IsWorldLoaded;
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

            bool loginVisible = bot.Flash.GetGameObject<bool>("mcLogin.visible", false);
            bool serverListExists = !bot.Flash.IsNull("mcLogin.sl.iList");
            int serverEntries = bot.Flash.GetGameObject<int>("mcLogin.sl.iList.numChildren", 0);
            bool hasServerData = bot.Servers.ServerList.Any(server =>
                !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.IP));

            if (loginVisible && serverListExists && serverEntries > 0 && hasServerData)
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

            await Task.Delay(250, cancellationToken);
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
