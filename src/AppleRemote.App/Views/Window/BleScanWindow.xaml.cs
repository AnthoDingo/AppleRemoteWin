using AppleRemote.App.Models;
using AppleRemote.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace AppleRemote.App.Views;

public partial class BleScanWindow : Wpf.Ui.Controls.FluentWindow
{
    public BleScanViewModel ViewModel { get; }

    public BleScanWindow()
    {
        InitializeComponent();
        DataContext = ViewModel = new BleScanViewModel(new Dialogs(this));
    }

    void Window_Loaded(object sender, RoutedEventArgs e) => ViewModel.Start();

    void Window_Closed(object? sender, EventArgs e) => ViewModel.Dispose();

    void Row_DoubleClick(object sender, MouseButtonEventArgs e) => ViewModel.PairCommand.Execute(null);
}
