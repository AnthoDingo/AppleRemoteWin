using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppleRemote.App.Models;
using Microsoft.Web.WebView2.Core;

namespace AppleRemote.App.Views;

/// <summary>Éditeur de macro par blocs (Blockly dans un WebView2, fichiers du dossier Blockly à côté de l'exe).
/// Valider enregistre le fichier de la macro ; fermer ou annuler laisse <see cref="SavedName"/> à null.</summary>
public partial class MacroEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly Macro? _existing;
    readonly IEnumerable<string> _software;
    readonly HashSet<string> _taken;
    readonly string? _original; // nom d'origine d'une macro existante (null pour une nouvelle)
    bool _hasBlocks;

    public string? SavedName { get; private set; }

    /// <param name="existing">Macro à modifier (renommable, supprimable), null pour une nouvelle.</param>
    /// <param name="software">Logiciels proposés par le bloc Logiciel.</param>
    /// <param name="existingNames">Noms déjà pris (une macro ne peut pas en écraser une autre).</param>
    public MacroEditorWindow(Macro? existing, IEnumerable<string> software, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        _existing = existing;
        _software = software;
        _taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        _original = existing?.Name;
        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            DeleteMacroButton.Visibility = Visibility.Visible;
        }
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("blockly.local", Path.Combine(AppContext.BaseDirectory, "Blockly"), CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += OnMessage;
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";
            core.Navigate($"https://blockly.local/index.html?lang={lang}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Macro.WebViewError", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        switch (msg.GetProperty("type").GetString())
        {
            case "ready":
                await Run($"loadWorkspace({_existing?.Workspace?.ToJsonString() ?? "null"})");
                break;
            case "change":
                _hasBlocks = msg.GetProperty("hasBlocks").GetBoolean();
                Refresh(null, null!);
                break;
            case "pickCombo":
                var dlg = new KeyComboWindow { Owner = this };
                dlg.ShowDialog();
                if (dlg.Combo is { } combo) await SetField(msg.GetProperty("id").GetString()!, "COMBO", combo[KeyCombos.Prefix.Length..]);
                break;
            case "pickApp":
                PickApp(msg.GetProperty("id").GetString()!);
                break;
        }
    }

    Task<string> Run(string script) => Web.CoreWebView2.ExecuteScriptAsync(script);

    Task SetField(string id, string field, string value) =>
        Run($"setField({JsonSerializer.Serialize(id)},{JsonSerializer.Serialize(field)},{JsonSerializer.Serialize(value)})");

    /// <summary>Menu des logiciels connus + Parcourir… ; le choix remplit le champ du bloc.</summary>
    void PickApp(string blockId)
    {
        var menu = new ContextMenu { PlacementTarget = Web, Placement = PlacementMode.MousePoint };
        foreach (var path in _software)
        {
            var item = new MenuItem { Header = SoftwareApps.NameOf(path) };
            item.Click += async (_, _) => await SetField(blockId, "PATH", path);
            menu.Items.Add(item);
        }
        var browse = new MenuItem { Header = Loc.T("Pick.Browse") };
        browse.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "*.exe|*.exe" };
            if (dlg.ShowDialog(this) == true) await SetField(blockId, "PATH", dlg.FileName);
        };
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    void Refresh(object? sender, EventArgs e)
    {
        if (ValidateButton is null) return;
        string name = Macro.Sanitize(NameBox.Text);
        ValidateButton.IsEnabled = name.Length > 0 && _hasBlocks
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
            string json = JsonSerializer.Deserialize<string>(await Run("getWorkspace()"))!;
            var macro = new Macro { Name = Macro.Sanitize(NameBox.Text), Workspace = JsonNode.Parse(json) };
            macro.SaveAs(_original);
            SavedName = macro.Name;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
