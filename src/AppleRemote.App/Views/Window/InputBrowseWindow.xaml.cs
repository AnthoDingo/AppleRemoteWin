using System.Windows;

namespace AppleRemote.App.Views;

/// <summary>Demande un fichier via « Parcourir ». <see cref="Result"/> : le chemin validé, null si la fenêtre est fermée sans valider.</summary>
public partial class InputBrowseWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly string _filter;
    public string? Result { get; private set; }
    string Current => PathBox.Text;

    /// <param name="filter">Filtre de la boîte de dialogue, ex. « Images|*.png;*.jpg ».</param>
    public InputBrowseWindow(string title, string prompt, string filter = "*.*|*.*")
    {
        InitializeComponent();
        Title = Bar.Title = title;
        PromptText.Text = prompt;
        _filter = filter;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = _filter };
        if (dlg.ShowDialog(this) != true) return;
        PathBox.Text = dlg.FileName;
        ValidateButton.IsEnabled = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Result = Current;
        Close();
    }
}
