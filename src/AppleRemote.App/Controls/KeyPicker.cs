using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AppleRemote.App.Models;

namespace AppleRemote.App.Controls;

/// <summary>Sélecteur d'action : un bouton qui ouvre un menu Key (sous-menu des touches) / Key Combo / Software / Macro.</summary>
public sealed class KeyPicker : Button
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(string), typeof(KeyPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((KeyPicker)d)._label.Text = Display((string)e.NewValue)));
    public static readonly DependencyProperty ChoicesProperty = DependencyProperty.Register(nameof(Choices), typeof(IEnumerable<string>), typeof(KeyPicker));

    public static readonly DependencyProperty MacrosProperty = DependencyProperty.Register(nameof(Macros), typeof(IEnumerable<string>), typeof(KeyPicker));
    public IEnumerable<string> Macros { get => (IEnumerable<string>)GetValue(MacrosProperty); set => SetValue(MacrosProperty, value); }

    public static readonly DependencyProperty SoftwareProperty = DependencyProperty.Register(nameof(Software), typeof(IEnumerable<string>), typeof(KeyPicker));
    public IEnumerable<string> Software { get => (IEnumerable<string>)GetValue(SoftwareProperty); set => SetValue(SoftwareProperty, value); }

    static string Display(string key) =>
        key.StartsWith(SoftwareApps.Prefix) ? SoftwareApps.NameOf(key[1..])
        : key.StartsWith(KeyCombos.Prefix) ? KeyCombos.Display(key)
        : key.StartsWith(Macro.Prefix) ? key[Macro.Prefix.Length..] : key;

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public IEnumerable<string> Choices { get => (IEnumerable<string>)GetValue(ChoicesProperty); set => SetValue(ChoicesProperty, value); }

    readonly TextBlock _label = new() { Text = Display(""), TextTrimming = TextTrimming.CharacterEllipsis };

    public KeyPicker()
    {
        SetResourceReference(StyleProperty, typeof(Button)); // implicit styles don't reach subclasses: reuse WPF-UI's button look
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(11, 5, 11, 5);
        var chevron = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(_label);
        grid.Children.Add(chevron);
        Content = grid;
        Click += (_, _) => Open();
    }

    /// <summary>Ouvre l'éditeur ; une macro nouvellement enregistrée est affectée au bouton.</summary>
    void EditMacro(Macro? existing)
    {
        var dlg = new Views.MacroEditorWindow(existing, Software ?? [], Macros ?? []) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (existing is null && dlg.SavedName is { } name) Key = Macro.Prefix + name;
    }

    void Open()
    {
        var keys = new MenuItem { Header = Loc.T("Pick.Key") };
        foreach (var name in Choices ?? [])
        {
            var item = new MenuItem { Header = name, IsChecked = name == Key };
            item.Click += (_, _) => Key = name;
            keys.Items.Add(item);
        }
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom, MinWidth = ActualWidth };
        menu.Items.Add(keys);
        var combo = new MenuItem { Header = Loc.T("Pick.KeyCombo"), IsChecked = Key.StartsWith(KeyCombos.Prefix) };
        combo.Click += (_, _) =>
        {
            var dlg = new Views.KeyComboWindow { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (dlg.Combo is { } c) Key = c; // fermée sans valider : on garde la sélection précédente
        };
        menu.Items.Add(combo);
        var soft = new MenuItem { Header = Loc.T("Pick.Software") };
        foreach (var path in Software ?? [])
        {
            var item = new MenuItem { Header = SoftwareApps.NameOf(path), IsChecked = Key == SoftwareApps.Prefix + path };
            item.Click += (_, _) => Key = SoftwareApps.Prefix + path;
            soft.Items.Add(item);
        }
        if (soft.Items.Count > 0) soft.Items.Add(new Separator());
        var browse = new MenuItem { Header = Loc.T("Pick.Browse") };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "*.exe|*.exe" };
            if (dlg.ShowDialog() == true) Key = SoftwareApps.Prefix + dlg.FileName;
        };
        soft.Items.Add(browse);
        menu.Items.Add(soft);
        var macro = new MenuItem { Header = Loc.T("Pick.Macro") };
        foreach (var name in Macros ?? [])
        {
            var item = new MenuItem { Header = name, IsChecked = Key == Macro.Prefix + name };
            item.Click += (_, _) => Key = Macro.Prefix + name;
            macro.Items.Add(item);
        }
        if (macro.Items.Count > 0) macro.Items.Add(new Separator());
        var create = new MenuItem { Header = Loc.T("Pick.NewMacro") };
        create.Click += (_, _) => EditMacro(null);
        macro.Items.Add(create);
        if (Key.StartsWith(Macro.Prefix))
        {
            var edit = new MenuItem { Header = Loc.T("Pick.EditMacro") };
            edit.Click += (_, _) => EditMacro(Macro.Load(Key[Macro.Prefix.Length..]));
            macro.Items.Add(edit);
            var dup = new MenuItem { Header = Loc.T("Pick.DuplicateMacro") };
            dup.Click += (_, _) =>
            {
                if (Macro.Duplicate(Key[Macro.Prefix.Length..]) is { } copy) Key = Macro.Prefix + copy;
            };
            macro.Items.Add(dup);
        }
        menu.Items.Add(macro);
        menu.IsOpen = true;
    }
}
