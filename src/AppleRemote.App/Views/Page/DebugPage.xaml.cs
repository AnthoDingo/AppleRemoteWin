// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.Specialized;
using AppleRemote.App.ViewModels;

namespace AppleRemote.App.Views;

public partial class DebugPage
{
    public DebugPage()
    {
        InitializeComponent();

        // Log: one line per entry, always scrolled to the last one (300 lines max, rebuilt on every change).
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not MainViewModel vm) return;
            void Refresh()
            {
                LogBox.Text = string.Join(Environment.NewLine, vm.Log);
                LogBox.ScrollToEnd();
            }
            ((INotifyCollectionChanged)vm.Log).CollectionChanged += (_, _) => Refresh();
            Refresh();
        };
    }
}
