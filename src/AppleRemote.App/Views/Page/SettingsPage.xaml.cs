// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote.App.Views;

public partial class SettingsPage
{
    public SettingsPage() => InitializeComponent();

    void Components_Click(object sender, System.Windows.RoutedEventArgs e) =>
        new ComponentsWindow { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();
}
