using System.Windows;
using AppleRemote.App.Models;

namespace AppleRemote.App;

public partial class App : Application
{
    // Une seule instance : la suivante réveille la première (réduite dans la zone de notification) puis s'arrête.
    Mutex? _instance;
    EventWaitHandle? _showRequest;

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.SetLanguage(Settings.Load().Language);   // avant tout texte, y compris celui de l'instance élevée
        // Instance élevée lancée par DriverCommand : pas de fenêtre, juste l'opération.
        if (e.Args is ["--driver", var verb]) Environment.Exit(DriverCommand.Run(verb));

        _instance = new Mutex(true, @"Local\AppleRemote.Instance", out bool first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AppleRemote.Show");
        if (!first)
        {
            _showRequest.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var request = _showRequest;
        new Thread(() =>
        {
            while (request.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as Views.MainWindow)?.ShowFromTray());
        }) { IsBackground = true }.Start();
    }
}
