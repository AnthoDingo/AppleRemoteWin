// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using AppleRemote.App.Models;
using AppleRemote.App.ViewModels;
using System.Windows;
using Wpf.Ui.Abstractions;

namespace AppleRemote.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly MainViewModel _viewModel;
    bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel = new MainViewModel(new Dialogs(this));
        TrayMenu.DataContext = _viewModel;   // the notification-area menu doesn't inherit the window's DataContext
        // The pages (Remote, Settings, Debug) share the view-model and keep their state between visits.
        RootNavigation.SetPageProviderService(new PageProvider(_viewModel));
        Application.Current.SessionEnding += (_, _) => _quitting = true;   // Windows shutdown: don't hold back the close
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RemotePage));
        _viewModel.Start();
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

    /// <summary>Creates each page only once, with the window's view-model as DataContext.</summary>
    sealed class PageProvider(object dataContext) : INavigationViewPageProvider
    {
        readonly Dictionary<Type, FrameworkElement> _pages = new();

        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                page.DataContext = dataContext;
                _pages[pageType] = page;
            }
            return page;
        }
    }
}
