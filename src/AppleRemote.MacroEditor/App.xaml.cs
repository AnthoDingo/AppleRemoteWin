using System.Windows;
using AppleRemote.App.Models;
using AppleRemote.App.Views;

namespace AppleRemote.MacroEditor;

/// <summary>Ouvre directement l'éditeur de macro. Argument facultatif : nom d'une macro à modifier (sinon, nouvelle macro).</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Loc.SetLanguage(Settings.Load().Language);

        var software = SoftwareApps.Detect().Concat(Settings.Load().Apps.Where(System.IO.File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = e.Args is [var name] ? Macro.Load(name) : null;
        new MacroEditorWindow(existing, software, Macro.Names()) { WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog();
        Shutdown();
    }
}
