using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace AppleRemote.App;

/// <summary>Source de liaison des textes : <see cref="Refresh"/> les réévalue tous (changement de langue en direct).</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Loc.T(key);
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Texte traduit en XAML : <c>Text="{l:T Clé}"</c>. Lié à <see cref="LocSource"/> : suit la langue en direct.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
