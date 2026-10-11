// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AppleRemote.App.Models;
using AppleRemote.Windows;

namespace AppleRemote.App.Views;

/// <summary>What the macros page binds to: the app's view-model, or the standalone editor's library.
/// <see cref="MacroChoices"/> must follow <see cref="Macro.Renamed"/> (renames and deletions).</summary>
public interface IMacroLibrary
{
    ObservableCollection<string> MacroChoices { get; }
    IEnumerable<string> SoftwareChoices { get; }
}

/// <summary>Built-in macro editor: pick a macro in the list (or start a new one), edit its blocks, save.</summary>
public partial class MacrosPage
{
    string? _original; // name of the macro being edited (null: new one)
    CancellationTokenSource? _testCts; // test run in progress

    public MacrosPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Editor.Software = Vm.SoftwareChoices;
    }

    IMacroLibrary Vm => (IMacroLibrary)DataContext;

    void Edit(Macro? macro)
    {
        _original = macro?.Name;
        NameBox.Text = macro?.Name ?? "";
        Editor.Load(macro?.Workspace);
        DuplicateButton.IsEnabled = DeleteButton.IsEnabled = macro is not null;
        Refresh(null, EventArgs.Empty);
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not string name || name == _original) return; // re-selected after a save: already shown
        if (Macro.Load(name) is { } macro) Edit(macro);
        else MessageBox.Show(Window.GetWindow(this), Loc.T("Log.MacroMissing", name), Loc.T("Macro.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        List.SelectedItem = null;
        Edit(null);
        NameBox.Focus();
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Macro.Duplicate(_original!) is not { } copy) return;
        Vm.MacroChoices.Add(copy);
        List.SelectedItem = copy;
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var dialogs = new Dialogs(Window.GetWindow(this));
        if (!await dialogs.Confirm(Loc.T("Macro.DeleteMacro"), Loc.T("Macro.ConfirmDelete", _original!))) return;
        try { Macro.Delete(_original!); } // the view-model drops it from the list and from the buttons using it
        catch (Exception ex) { await dialogs.Warn(Loc.T("Macro.Title"), ex.Message); return; }
        New_Click(sender, e);
    }

    /// <summary>Runs the blocks as currently shown (saved or not); a second click stops the run (a loop may never end).</summary>
    async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testCts is { } running) { running.Cancel(); return; }
        using var cts = _testCts = new CancellationTokenSource();
        TestButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Stop24);
        TestButton.ToolTip = Loc.T("Macros.StopTest");
        try { await new Macro { Workspace = await Editor.GetWorkspaceAsync() }.RunAsync(new KeyboardInjector(), cts.Token); }
        catch (Exception ex) { await new Dialogs(Window.GetWindow(this)).Warn(Loc.T("Macro.Title"), ex.Message); }
        finally
        {
            _testCts = null;
            TestButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Play24);
            TestButton.ToolTip = Loc.T("Macros.Test");
            Refresh(null, EventArgs.Empty);
        }
    }

    void Refresh(object? sender, EventArgs e)
    {
        if (SaveButton is null) return;
        TestButton.IsEnabled = Editor.HasBlocks || _testCts is not null;
        string name = Macro.Sanitize(NameBox.Text);
        SaveButton.IsEnabled = name.Length > 0 && Editor.HasBlocks
            && (name.Equals(_original, StringComparison.OrdinalIgnoreCase) || !Vm.MacroChoices.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var macro = new Macro { Name = Macro.Sanitize(NameBox.Text), Workspace = await Editor.GetWorkspaceAsync() };
            macro.SaveAs(_original); // a rename updates the list and the buttons through Macro.Renamed
            if (!Vm.MacroChoices.Contains(macro.Name)) Vm.MacroChoices.Add(macro.Name);
            _original = macro.Name;
            NameBox.Text = macro.Name;
            List.SelectedItem = macro.Name;
            DuplicateButton.IsEnabled = DeleteButton.IsEnabled = true;
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("Macro.Title"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
