// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using AppleRemote.App.ViewModels;
using System.Windows.Input;

namespace AppleRemote.App.Views;

public partial class RemotePage
{
    public RemotePage() => InitializeComponent();

    void Row_DoubleClick(object sender, MouseButtonEventArgs e) => ((RemoteViewModel)DataContext).PairDeviceCommand.Execute(null);
}
