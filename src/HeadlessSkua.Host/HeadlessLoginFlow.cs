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

        HeadlessConsole.Info($"Connecting to {selectedServer.Name}...");
        bool selectedInClient = false;
        try
        {
            selectedInClient = bot.Servers.Connect(selectedServer);
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Server-list selection was unavailable: {exception.Message}");
        }

        if (!selectedInClient)
        {
            HeadlessConsole.Error($"Could not connect to {selectedServer.Name}.");
            return false;
        }

        bool connected = bot.Player.Playing && bot.Flash.IsWorldLoaded;
        if (!connected)
        {
            HeadlessConsole.Error($"Timed out while connecting to {selectedServer.Name}.");
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

    private static async Task<bool> WaitForServerListAsync(IScriptInterface bot, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            string? modalMessage = bot.Flash.Call<string>("getActiveModalMessage");
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
