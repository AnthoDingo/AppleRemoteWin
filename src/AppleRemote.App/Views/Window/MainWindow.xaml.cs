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
        TrayMenu.DataContext = _viewModel;   // le menu de la zone de notification n'hérite pas du DataContext de la fenêtre
        // Les pages (Télécommande, Settings, Debug) partagent le view-model et gardent leur état d'une visite à l'autre.
        RootNavigation.SetPageProviderService(new PageProvider(_viewModel));
        Application.Current.SessionEnding += (_, _) => _quitting = true;   // arrêt de Windows : on ne retient pas la fermeture
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RemotePage));
        _viewModel.Start();
        if (Environment.GetCommandLineArgs().Contains("--tray")) Hide();   // lancée avec Windows : directement dans la zone de notification
    }

    // Fermer la fenêtre la réduit dans la zone de notification ; seul « Quitter » du menu contextuel arrête l'app.
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

    /// <summary>Crée chaque page une seule fois, avec le view-model de la fenêtre comme DataContext.</summary>
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
