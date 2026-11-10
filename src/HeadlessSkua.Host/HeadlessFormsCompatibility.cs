namespace System.Windows.Forms;

/// <summary>
/// Supplies the namespace expected by Skua's shared script compiler without adding a desktop UI dependency.
/// Headless scripts should use Bot.ShowMessageBox or the terminal instead of Windows Forms controls.
/// </summary>
public static class HeadlessCompatibilityMarker
{
}
