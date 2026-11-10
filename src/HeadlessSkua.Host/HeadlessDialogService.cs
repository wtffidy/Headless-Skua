using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;

namespace HeadlessSkua;

/// <summary>
/// Replaces desktop dialogs with safe non-interactive behavior for unattended script execution.
/// </summary>
internal sealed class HeadlessDialogService : IDialogService
{
    /// <inheritdoc />
    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        LogUnavailableDialog(viewModel);
        return false;
    }

    /// <inheritdoc />
    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class
    {
        LogUnavailableDialog(viewModel);
        return false;
    }

    /// <inheritdoc />
    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        LogUnavailableDialog(viewModel);
        return false;
    }

    /// <inheritdoc />
    public void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback)
    {
        Console.Error.WriteLine("[Headless Dialog] Script options require a saved configuration; interactive dialogs are unavailable.");
    }

    /// <inheritdoc />
    public void ShowMessageBox(string message, string caption)
    {
        Console.WriteLine($"[{caption}] {message}");
    }

    /// <inheritdoc />
    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        Console.WriteLine($"[{caption}] {message}");
        return false;
    }

    /// <inheritdoc />
    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        Console.WriteLine($"[{caption}] {message}");
        return DialogResult.Cancelled;
    }

    /// <inheritdoc />
    public IInputDialogViewModel CreateInputDialog(string title, string dialogHint)
    {
        return new HeadlessInputDialogViewModel(title, dialogHint);
    }

    /// <summary>
    /// Logs a dialog request that cannot be displayed in a headless session.
    /// </summary>
    /// <param name="viewModel">The requested dialog view model.</param>
    private static void LogUnavailableDialog(object viewModel)
    {
        Console.Error.WriteLine($"[Headless Dialog] {viewModel.GetType().Name} requested; interactive dialogs are unavailable.");
    }

    private sealed class HeadlessInputDialogViewModel(string title, string hint) : IInputDialogViewModel
    {
        public bool NumberOnly => false;
        public string DialogTextInput => string.Empty;
        public string DialogHint => hint;
        public string TextBoxHint => title;
    }
}
