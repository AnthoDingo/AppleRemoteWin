using System.IO;
using System.Text.Json;

namespace AppleRemote.App.Models;

/// <summary>Préférences dans %AppData%\AppleRemote\settings.json.</summary>
public sealed class Settings
{
    public bool KeyboardEnabled { get; set; }
    public string? KeyMapPath { get; set; }
    /// <summary>Fermer la fenêtre la réduit dans la zone de notification (sinon l'app s'arrête).</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>« auto » (langue de Windows), « fr » ou « en » ; appliquée au démarrage.</summary>
    /// <summary>Entrée de la télécommande -> exe à lancer (remplace la touche).</summary>
    public Dictionary<string, string> Software { get; set; } = new();
    /// <summary>Entrée de la télécommande -> combinaison « #Win+D » à envoyer (remplace la touche).</summary>
    public Dictionary<string, string> Combos { get; set; } = new();
    /// <summary>Entrée de la télécommande -> nom de la macro à exécuter (fichier dans le dossier macros).</summary>
    public Dictionary<string, string> Macros { get; set; } = new();
    /// <summary>Logiciels ajoutés par « Browse », conservés dans le menu.</summary>
    public List<string> Apps { get; set; } = new();
    public string Language { get; set; } = "auto";

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // fichier illisible : valeurs par défaut
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // préférences non critiques
        }
    }
}
