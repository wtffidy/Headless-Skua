using Skua.Core.Interfaces;

namespace HeadlessSkua;

/// <summary>
/// Supplies the Skua plugin contract without exposing desktop menu controls.
/// </summary>
internal sealed class HeadlessPluginHelper : IPluginHelper
{
    /// <inheritdoc />
    public IReadOnlyList<Skua.Core.Interfaces.ViewModels.IMainMenuItemViewModel> MenuButtons => Array.Empty<Skua.Core.Interfaces.ViewModels.IMainMenuItemViewModel>();

    /// <inheritdoc />
    public void AddMenuButton(string text, Action action)
    {
        Console.WriteLine($"[Plugin] Ignoring desktop menu button '{text}' in headless mode.");
    }

    /// <inheritdoc />
    public void RemoveMenuButton(string text)
    {
    }
}
