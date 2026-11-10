using Skua.Core.Interfaces;

namespace HeadlessSkua;

/// <summary>
/// Executes plugin dispatcher callbacks synchronously because the headless host has no UI thread.
/// </summary>
internal sealed class HeadlessDispatcherService : IDispatcherService
{
    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
