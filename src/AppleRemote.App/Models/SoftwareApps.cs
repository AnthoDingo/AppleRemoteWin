using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AppleRemote.App.Models;

/// <summary>Logiciels lançables depuis une touche : détection des plus courants, nom affiché, lancement.
/// Une valeur « logiciel » d'une affectation s'écrit <see cref="Prefix"/> + chemin de l'exe.</summary>
public static class SoftwareApps
{
    public const string Prefix = "@";

    static readonly (string Exe, string Name)[] Common =
        [("chrome.exe", "Chrome"), ("msedge.exe", "Edge"), ("powerpnt.exe", "PowerPoint")];

    /// <summary>Chemins des logiciels courants trouvés via la clé « App Paths » du registre.</summary>
    public static IEnumerable<string> Detect()
    {
        foreach (var (exe, _) in Common)
        {
            string sub = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}";
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var k = hive.OpenSubKey(sub);
                if (k?.GetValue(null) as string is { } p && File.Exists(p = p.Trim('"'))) { yield return p; break; }
            }
        }
    }

    public static string NameOf(string path)
    {
        string file = Path.GetFileName(path);
        foreach (var (exe, name) in Common)
            if (file.Equals(exe, StringComparison.OrdinalIgnoreCase)) return name;
        return Path.GetFileNameWithoutExtension(path);
    }

    public static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* exe disparu ou refusé : on ignore */ }
    }
}
