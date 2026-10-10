using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AppleRemote.Windows;

namespace AppleRemote.App.Models;

/// <summary>
/// Installer/retirer WinUSB demande les droits administrateur alors que l'application tourne « asInvoker » :
/// elle se relance élevée avec <c>--driver install|restore</c> (voir <see cref="App.OnStartup"/>), le résultat revient par code de sortie.
/// </summary>
public static class DriverCommand
{
    public const int Ok = 0, Failed = 1, RebootNeeded = 3010;

    static string LogPath => Path.Combine(Path.GetTempPath(), "AppleRemoteDriver.log");

    /// <summary>Côté processus élevé : exécute l'opération et retourne le code de sortie.</summary>
    public static int Run(string verb)
    {
        try
        {
            File.Delete(LogPath);
            switch (verb)
            {
                case "install": return WinUsbDriver.Install() ? RebootNeeded : Ok;
                case "restore": WinUsbDriver.Restore(); return Ok;
                default: throw new ArgumentException(Loc.T("Driver.UnknownVerb", verb));
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(LogPath, e.Message);
            return Failed;
        }
    }

    const string InstallerName = "AppleRemote.DriverInstaller.exe";

    /// <summary>
    /// Le programme d'installation du filtre : à côté de l'exe, sinon sa dernière compilation (Release puis Debug).
    /// Il porte la logique d'installation et demande lui-même l'élévation (manifeste requireAdministrator).
    /// </summary>
    static string FindInstaller()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidates = new List<string> { Path.Combine(dir.FullName, InstallerName) };
            foreach (var config in new[] { "Release", "Debug" })
                candidates.Add(Path.Combine(dir.FullName, "src", "AppleRemote.DriverInstaller", "bin", config, "net10.0-windows10.0.19041.0", InstallerName));
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return found;
        }
        throw new FileNotFoundException(Loc.T("Driver.InstallerMissing", InstallerName));
    }

    /// <summary>Côté application : relance élevée et attente. Retourne (code de sortie, message d'erreur éventuel).</summary>
    public static async Task<(int Code, string? Error)> RunElevatedAsync(string verb)
    {
        try
        {
            // Filtre Bluetooth : DriverInstaller (install|uninstall) ; WinUSB : l'application se relance elle-même élevée.
            var info = verb.StartsWith("filter-")
                ? new ProcessStartInfo(FindInstaller(), $"{verb["filter-".Length..]} --quiet") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }
                : new ProcessStartInfo(Environment.ProcessPath!, $"--driver {verb}") { UseShellExecute = true, Verb = "runas" };
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            string? error = process.ExitCode == Failed && File.Exists(LogPath) ? File.ReadAllText(LogPath) : null;
            return (process.ExitCode, error);
        }
        catch (FileNotFoundException e)
        {
            return (Failed, e.Message);
        }
        catch (Win32Exception) // UAC refusée
        {
            return (Failed, Loc.T("Driver.ElevationDenied"));
        }
    }
}
