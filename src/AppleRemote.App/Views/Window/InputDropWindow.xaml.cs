using System.Windows;

namespace AppleRemote.App.Views;

/// <summary>Demande un fichier ou dossier déposé par glisser-déposer. <see cref="Result"/> : le chemin validé, null si la fenêtre est fermée sans valider.</summary>
public partial class InputDropWindow : Wpf.Ui.Controls.FluentWindow
{
    string _path = "";
    public string? Result { get; private set; }
    string Current => _path;

    public InputDropWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = Bar.Title = title;
        PromptText.Text = prompt;
    }

    void Zone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Zone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        _path = files[0];
        DropText.Text = _path;
        DropText.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorPrimaryBrush");
        ValidateButton.IsEnabled = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Result = Current;
        Close();
    }
}
