using System.Windows;
using AppleRemote.App.Views;

namespace AppleRemote.App.Models;

/// <summary>Ce dont les view-models ont besoin de l'interface (boîtes de dialogue, presse-papiers, fenêtres).</summary>
public interface IDialogs
{
    Task<bool> Confirm(string title, string message);
    Task Warn(string title, string message);
    void CopyText(string text);
    void ShowBleScan(Action onPaired);
}

public sealed class Dialogs(Window owner) : IDialogs
{
    public async Task<bool> Confirm(string title, string message) =>
        await new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner, Title = title, Content = message,
            PrimaryButtonText = "Oui", CloseButtonText = "Non",
        }.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;

    public async Task Warn(string title, string message) =>
        await new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner, Title = title, Content = message, CloseButtonText = "OK",
        }.ShowDialogAsync();

    public void CopyText(string text) => Clipboard.SetText(text);

    public void ShowBleScan(Action onPaired)
    {
        var window = new BleScanWindow { Owner = owner };
        window.ViewModel.Paired += onPaired;
        window.Show();
    }
}
