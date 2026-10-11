// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.IO;
using AppleRemote.App.Models;
using AppleRemote.App.Views;

namespace AppleRemote.MacroEditor;

public partial class MainWindow
{
    public MainWindow(IMacroLibrary library)
    {
        InitializeComponent();
        Page.DataContext = library; // a Frame does not pass its DataContext down to its page
    }
}

/// <summary>Macros and programs the page offers, without the app's view-model (no remote session).</summary>
sealed class MacroLibrary : IMacroLibrary
{
    public ObservableCollection<string> MacroChoices { get; } = new(Macro.Names());

    public IEnumerable<string> SoftwareChoices { get; } =
        SoftwareApps.Detect().Concat(Settings.Load().Apps).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public MacroLibrary() => Macro.Renamed += (old, @new) =>
    {
        MacroChoices.Remove(old);
        if (@new is not null && !MacroChoices.Contains(@new)) MacroChoices.Add(@new);
    };
}
