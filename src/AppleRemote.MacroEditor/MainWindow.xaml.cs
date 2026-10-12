// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.IO;
using AppleRemote.App.Models;
using AppleRemote.App.ViewModels;

namespace AppleRemote.MacroEditor;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Macros and programs the page offers, without the app's view-model (no remote session).
        var macros = new ObservableCollection<string>(Macro.Names());
        Macro.Renamed += (old, @new) =>
        {
            macros.Remove(old);
            if (@new is not null && !macros.Contains(@new)) macros.Add(@new);
        };
        var software = SoftwareApps.Detect().Concat(Settings.Load().Apps).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Page.DataContext = new MacrosViewModel(macros, software, new Dialogs(this)); // a Frame does not pass its DataContext down to its page
    }
}
