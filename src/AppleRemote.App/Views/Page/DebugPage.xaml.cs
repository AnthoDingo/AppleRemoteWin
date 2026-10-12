// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Windows.Controls;

namespace AppleRemote.App.Views;

public partial class DebugPage
{
    public DebugPage() => InitializeComponent();

    // The log always shows its last line.
    void Log_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
