using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace HeadlessSkua;

/// <summary>
/// Uses Skua's existing settings storage without depending on the desktop application.
/// </summary>
internal sealed class HeadlessSettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _settings = new();

    /// <summary>
    /// Initializes the shared Skua settings store for a client session.
    /// </summary>
    public HeadlessSettingsService()
    {
        _settings.Initialize(AppRole.Client);
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value) => _settings.Set(key, value);

    /// <inheritdoc />
    public T? Get<T>(string key) => _settings.Get<T>(key);

    /// <inheritdoc />
    public T Get<T>(string key, T defaultValue) => _settings.Get(key, defaultValue);

    /// <inheritdoc />
    public void Initialize(AppRole role) => _settings.Initialize(role);

    /// <inheritdoc />
    public SharedSettings GetShared() => _settings.GetShared();

    /// <inheritdoc />
    public ClientSettings GetClient() => _settings.GetClient();

    /// <inheritdoc />
    public ManagerSettings GetManager() => _settings.GetManager();

    /// <inheritdoc />
    public void SetApplicationVersion() => _settings.SetApplicationVersion();

    /// <inheritdoc />
    public void ReloadSettings() => _settings.ReloadSettings();

    /// <inheritdoc />
    public bool TryReloadSettings() => _settings.TryReloadSettings();

    /// <inheritdoc />
    public bool TryUpdate<T>(string key, T defaultValue, Func<T, T> change, out T result) =>
        _settings.TryUpdate(key, defaultValue, change, out result);
}
