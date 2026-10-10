// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Windows;
using AppleRemote.App.Models;

namespace AppleRemote.App;

public partial class App : Application
{
    // Single instance: a second launch wakes the first one (minimised to the notification area) and exits.
    Mutex? _instance;
    EventWaitHandle? _showRequest;

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.SetLanguage(Settings.Load().Language);   // before any text, including the elevated instance's
        // Elevated instance launched by DriverCommand: no window, just the operation.
        if (e.Args is ["--driver", var verb]) Environment.Exit(DriverCommand.Run(verb));

        _instance = new Mutex(true, @"Local\AppleRemote.Instance", out bool first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AppleRemote.Show");
        if (!first)
        {
            _showRequest.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var request = _showRequest;
        new Thread(() =>
        {
            while (request.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as Views.MainWindow)?.ShowFromTray());
        }) { IsBackground = true }.Start();
    }
}
