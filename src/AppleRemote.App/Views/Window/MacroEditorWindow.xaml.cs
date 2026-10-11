using System.Windows;
using AppleRemote.App.Models;

namespace AppleRemote.App.Views;

/// <summary>Éditeur de macro par blocs en fenêtre (ouvert depuis le sélecteur d'action d'un bouton).
/// Valider enregistre le fichier de la macro ; fermer ou annuler laisse <see cref="SavedName"/> à null.</summary>
public partial class MacroEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly HashSet<string> _taken;
    readonly string? _original; // nom d'origine d'une macro existante (null pour une nouvelle)

    public string? SavedName { get; private set; }

    /// <param name="existing">Macro à modifier (renommable, supprimable), null pour une nouvelle.</param>
    /// <param name="software">Logiciels proposés par le bloc Logiciel.</param>
    /// <param name="existingNames">Noms déjà pris (une macro ne peut pas en écraser une autre).</param>
    public MacroEditorWindow(Macro? existing, IEnumerable<string> software, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        _taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        _original = existing?.Name;
        Editor.Software = software;
        Editor.Load(existing?.Workspace);
        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            DeleteMacroButton.Visibility = Visibility.Visible;
        }
    }

    void Editor_Failed(object? sender, EventArgs e) => Close();

    void Refresh(object? sender, EventArgs e)
    {
        if (ValidateButton is null) return;
        string name = Macro.Sanitize(NameBox.Text);
        ValidateButton.IsEnabled = name.Length > 0 && Editor.HasBlocks
            && !(!name.Equals(_original, StringComparison.OrdinalIgnoreCase) && _taken.Contains(name));
    }

    void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Macro.ConfirmDelete", _original!), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Macro.Delete(_original!); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    async void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var macro = new Macro { Name = Macro.Sanitize(NameBox.Text), Workspace = await Editor.GetWorkspaceAsync() };
            macro.SaveAs(_original);
            SavedName = macro.Name;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
