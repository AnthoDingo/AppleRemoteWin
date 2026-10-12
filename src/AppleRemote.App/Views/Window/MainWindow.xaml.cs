// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using AppleRemote.App.Models;
using AppleRemote.App.ViewModels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Abstractions;

namespace AppleRemote.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly MainViewModel _viewModel;
    bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
#if !DEBUG_PAGE
        RootNavigation.FooterMenuItems.Remove(DebugItem);
#endif
        _viewModel = new MainViewModel(new Dialogs(this));
        TrayMenu.DataContext = _viewModel.Remote;   // the notification-area menu doesn't inherit the window's DataContext
        // Each page gets its own view-model and keeps its state between visits.
        RootNavigation.SetPageProviderService(new PageProvider(_viewModel));
        Application.Current.SessionEnding += (_, _) => _quitting = true;   // Windows shutdown: don't hold back the close
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RemotePage));
        _viewModel.Start();
        // Bluetooth adapter plugged in / removed: WM_DEVICECHANGE reaches top-level windows, hidden ones too (tray).
        // Windows sends a burst of them per device: handled once things settle.
        var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        settle.Tick += async (_, _) =>
        {
            settle.Stop();
            await _viewModel.Preferences.OnDevicesChanged();
        };
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x0219 && wParam == 0x0007)   // WM_DEVICECHANGE, DBT_DEVNODES_CHANGED
            {
                settle.Stop();
                settle.Start();
            }
            return IntPtr.Zero;
        });
        if (Environment.GetCommandLineArgs().Contains("--tray")) Hide();   // launched with Windows: straight to the notification area
    }

    // Closing the window minimises it to the notification area; only "Quit" in the context menu exits the app.
    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_quitting && !_viewModel.CloseQuits)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _viewModel.Dispose();
    }

    void Tray_Show(object sender, RoutedEventArgs e) => ShowFromTray();

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    void Tray_Quit(object sender, RoutedEventArgs e)
    {
        _quitting = true;
        Close();
    }

    /// <summary>Creates each page only once, with its view-model as DataContext.</summary>
    sealed class PageProvider(MainViewModel viewModel) : INavigationViewPageProvider
    {
        readonly Dictionary<Type, FrameworkElement> _pages = new();

        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                page.DataContext = page switch
                {
                    SettingsPage => viewModel.Preferences,
                    DebugPage => viewModel.Debug,
                    MacrosPage => viewModel.Macros,
                    _ => viewModel.Remote, // Remote, and the guide's remote drawings
                };
                _pages[pageType] = page;
            }
            return page;
        }
    }
}
