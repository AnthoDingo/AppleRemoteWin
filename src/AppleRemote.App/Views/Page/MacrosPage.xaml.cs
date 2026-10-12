// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using AppleRemote.App.ViewModels;

namespace AppleRemote.App.Views;

/// <summary>Built-in macro editor (<see cref="MacrosViewModel"/>); the code-behind only hands the block editor to the view-model.</summary>
public partial class MacrosPage
{
    public MacrosPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not MacrosViewModel vm) return;
            vm.Editor = Editor;
            Editor.Software = vm.Software;
            Editor.Changed += (_, _) => vm.HasBlocks = Editor.HasBlocks;
        };
    }

    void New_Click(object sender, System.Windows.RoutedEventArgs e) => NameBox.Focus();
}
