using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AppleRemote.App.Views;

/// <summary>Demande une saisie de texte. <see cref="Result"/> : le texte validé, null si la fenêtre est fermée sans valider.</summary>
public partial class InputTextWindow : Wpf.Ui.Controls.FluentWindow
{
    public string? Result { get; private set; }
    string Current => Box.Text;

    public InputTextWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = Bar.Title = title;
        PromptText.Text = prompt;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    void Box_TextChanged(object sender, TextChangedEventArgs e) => ValidateButton.IsEnabled = Box.Text.Length > 0;

    void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ValidateButton.IsEnabled) Validate_Click(sender, e);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Validate_Click(object sender, RoutedEventArgs e)
    {
        Result = Current;
        Close();
    }
}
