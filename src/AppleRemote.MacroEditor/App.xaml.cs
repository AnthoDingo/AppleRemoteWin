// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Windows;
using AppleRemote.App.Models;

namespace AppleRemote.MacroEditor;

/// <summary>Opens the app's Macros page on its own: list, block editor, save.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loc.SetLanguage(Settings.Load().Language);
        new MainWindow().ShowDialog();
        Shutdown();
    }
}
