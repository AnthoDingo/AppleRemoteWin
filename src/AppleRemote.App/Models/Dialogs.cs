// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Windows;
using AppleRemote.App.Views;

namespace AppleRemote.App.Models;

/// <summary>What the view-models need from the UI (dialogs, clipboard, windows).</summary>
public interface IDialogs
{
    Task<bool> Confirm(string title, string message);
    Task Warn(string title, string message);
    void CopyText(string text);
    void ShowBleScan(Action onPaired);
    void ShowComponents();
}

public sealed class Dialogs(Window owner) : IDialogs
{
    public async Task<bool> Confirm(string title, string message) =>
        await new Wpf.Ui.Controls.MessageBox
        {
            Owner = owner, Title = title, Content = message,
            PrimaryButtonText = Loc.T("Dialog.Yes"), CloseButtonText = Loc.T("Dialog.No"),
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

    public void ShowComponents() => new ComponentsWindow { Owner = owner }.ShowDialog();
}
