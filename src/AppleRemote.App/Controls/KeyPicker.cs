using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppleRemote.App.Models;

namespace AppleRemote.App.Controls;

/// <summary>Sélecteur d'action : un bouton qui ouvre un menu Key (sous-menu des touches) / Key Combo / Software / Macro.</summary>
public sealed class KeyPicker : Button
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key), typeof(string), typeof(KeyPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((KeyPicker)d).Content = Display((string)e.NewValue)));
    public static readonly DependencyProperty ChoicesProperty = DependencyProperty.Register(nameof(Choices), typeof(IEnumerable<string>), typeof(KeyPicker));

    public static readonly DependencyProperty SoftwareProperty = DependencyProperty.Register(nameof(Software), typeof(IEnumerable<string>), typeof(KeyPicker));
    public IEnumerable<string> Software { get => (IEnumerable<string>)GetValue(SoftwareProperty); set => SetValue(SoftwareProperty, value); }

    static string Display(string key) => key.StartsWith(SoftwareApps.Prefix) ? SoftwareApps.NameOf(key[1..]) : key;

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public IEnumerable<string> Choices { get => (IEnumerable<string>)GetValue(ChoicesProperty); set => SetValue(ChoicesProperty, value); }

    public KeyPicker()
    {
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Click += (_, _) => Open();
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
        menu.Items.Add(new MenuItem { Header = Loc.T("Pick.KeyCombo"), IsEnabled = false });
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
        // Pas encore implémenté : visible mais grisé.
        foreach (var k in new[] { "Pick.Macro" })
            menu.Items.Add(new MenuItem { Header = Loc.T(k), IsEnabled = false });
        menu.IsOpen = true;
    }
}
