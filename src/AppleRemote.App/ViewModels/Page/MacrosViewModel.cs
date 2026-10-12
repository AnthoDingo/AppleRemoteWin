// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleRemote.App.ViewModels;

/// <summary>What the view-model needs from the block editor (the WebView2 control).</summary>
public interface IMacroWorkspace
{
    void Load(JsonNode? workspace);
    Task<JsonNode?> GetWorkspaceAsync();
}

/// <summary>Macros page: pick a macro in the list (or start a new one), edit its blocks, save.</summary>
/// <param name="macros">Saved macro names; the owner keeps it in step with <see cref="Macro.Renamed"/> (renames and deletions).</param>
/// <param name="software">Software offered by the Software block.</param>
public sealed partial class MacrosViewModel(ObservableCollection<string> macros, IEnumerable<string> software, IDialogs dialogs) : ObservableObject
{
    CancellationTokenSource? _testCts; // test run in progress

    public ObservableCollection<string> Macros { get; } = macros;
    public IEnumerable<string> Software { get; } = software;

    /// <summary>Set by the page once its editor exists.</summary>
    public IMacroWorkspace? Editor { get; set; }

    [ObservableProperty] string? _selectedMacro;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] string _name = "";

    /// <summary>The editor shows blocks (updated by the page on each change).</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyCanExecuteChangedFor(nameof(TestCommand))] bool _hasBlocks;

    /// <summary>Name of the macro being edited (null: new one).</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyCanExecuteChangedFor(nameof(DuplicateCommand)), NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    string? _original;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestCommand))] bool _testing;

    bool IsSaved => Original is not null;

    partial void OnSelectedMacroChanged(string? value)
    {
        if (value is null || value == Original) return; // re-selected after a save: already shown
        if (Macro.Load(value) is { } macro) Edit(macro);
        else _ = dialogs.Warn(Loc.T("Macro.Title"), Loc.T("Log.MacroMissing", value));
    }

    void Edit(Macro? macro)
    {
        Original = macro?.Name;
        Name = macro?.Name ?? "";
        Editor?.Load(macro?.Workspace);
    }

    [RelayCommand]
    void New()
    {
        SelectedMacro = null;
        Edit(null);
    }

    [RelayCommand(CanExecute = nameof(IsSaved))]
    void Duplicate()
    {
        if (Macro.Duplicate(Original!) is not { } copy) return;
        Macros.Add(copy);
        SelectedMacro = copy;
    }

    [RelayCommand(CanExecute = nameof(IsSaved))]
    async Task Delete()
    {
        if (!await dialogs.Confirm(Loc.T("Macro.DeleteMacro"), Loc.T("Macro.ConfirmDelete", Original!))) return;
        try { Macro.Delete(Original!); } // the owner drops it from the list and from the buttons using it
        catch (Exception ex) { await dialogs.Warn(Loc.T("Macro.Title"), ex.Message); return; }
        New();
    }

    bool CanTest() => HasBlocks || Testing;

    /// <summary>Runs the blocks as currently shown (saved or not); a second click stops the run (a loop may never end).</summary>
    [RelayCommand(CanExecute = nameof(CanTest), AllowConcurrentExecutions = true)]
    async Task Test()
    {
        if (_testCts is { } running) { running.Cancel(); return; }
        if (Editor is null) return;
        using var cts = _testCts = new CancellationTokenSource();
        Testing = true;
        try { await new Macro { Workspace = await Editor.GetWorkspaceAsync() }.RunAsync(new KeyboardInjector(), cts.Token); }
        catch (Exception ex) { await dialogs.Warn(Loc.T("Macro.Title"), ex.Message); }
        finally
        {
            _testCts = null;
            Testing = false;
        }
    }

    bool CanSave()
    {
        string name = Macro.Sanitize(Name);
        return name.Length > 0 && HasBlocks
            && (name.Equals(Original, StringComparison.OrdinalIgnoreCase) || !Macros.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task Save()
    {
        if (Editor is null) return;
        try
        {
            var macro = new Macro { Name = Macro.Sanitize(Name), Workspace = await Editor.GetWorkspaceAsync() };
            macro.SaveAs(Original); // a rename updates the list and the buttons through Macro.Renamed
            if (!Macros.Contains(macro.Name)) Macros.Add(macro.Name);
            Original = macro.Name;
            Name = macro.Name;
            SelectedMacro = macro.Name;
        }
        catch (Exception ex) { await dialogs.Warn(Loc.T("Macro.Title"), ex.Message); }
    }
}
