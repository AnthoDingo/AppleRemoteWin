// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace AppleRemote.App;

/// <summary>Binding source for texts: <see cref="Refresh"/> re-evaluates all of them (live language change).</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Loc.T(key);
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Translated text in XAML: <c>Text="{l:T Key}"</c>. Bound to <see cref="LocSource"/>: follows the language live.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
