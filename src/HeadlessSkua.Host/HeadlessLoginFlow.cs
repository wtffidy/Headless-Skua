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
            HeadlessConsole.Error("The game server list did not appear. Check the credentials and try again.");
            return false;
        }

        List<Server> servers = bot.Servers.ServerList.Count > 0
            ? bot.Servers.ServerList.ToList()
            : await bot.Servers.GetServers(true);

        if (servers.Count == 0)
        {
            HeadlessConsole.Error("The game did not provide a server list.");
            return false;
        }

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
            selectedInClient = bot.Flash.Call<bool>("clickServer", selectedServer.Name);
        }
        catch (Exception exception)
        {
            HeadlessConsole.Debug($"Server-list selection was unavailable: {exception.Message}");
        }

        if (!selectedInClient)
        {
            try
            {
                int port = selectedServer.Port > 0 ? selectedServer.Port : 5588;
                bot.Servers.ConnectIP(selectedServer.IP, port);
            }
            catch (Exception exception)
            {
                HeadlessConsole.Error($"Could not connect to {selectedServer.Name}: {exception.Message}");
                return false;
            }
        }

        bool connected = await WaitForWorldAsync(bot, cancellationToken);
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
                    string password = account.Password.StartsWith("enc:", StringComparison.Ordinal)
                        ? CryptoUtils.Decrypt(account.Password)
                        : account.Password;
                    return (account.Username, password);
                }

                HeadlessConsole.Warn("Choose an account number or M for manual login.");
            }
        }

        string? username = HeadlessConsole.Prompt("Username");
        string passwordInput = HeadlessConsole.ReadSecret("Password");
        return (username?.Trim() ?? string.Empty, passwordInput);
    }

    private static async Task<bool> WaitForServerListAsync(IScriptInterface bot, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (bot.Servers.ServerList.Count > 0)
                return true;

            string? modal = null;
            try
            {
                modal = bot.Flash.Call<string>("getActiveModalMessage");
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(modal) && !string.Equals(modal, "null", StringComparison.OrdinalIgnoreCase))
            {
                HeadlessConsole.Error($"Login rejected: {modal.Trim()}");
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> WaitForWorldAsync(IScriptInterface bot, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (bot.Player.Playing && bot.Flash.IsWorldLoaded)
                return true;

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }
}
