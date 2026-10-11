using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AppleRemote.Windows;

namespace AppleRemote.App.Models;

/// <summary>Étape d'une ancienne macro (liste à plat), relue seulement pour convertir les fichiers existants en blocs.
/// <see cref="Type"/> : combo (« Win+D »), app (chemin de l'exe), wait (ms), text, askText / askFile / askDrop (consigne).</summary>
public sealed class MacroStep
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Macro = espace de travail Blockly (blocs, boucles, conditions, variables), enregistrée dans son propre fichier
/// %AppData%\AppleRemote\macros\&lt;nom&gt;.json. Une valeur « macro » d'une affectation s'écrit <see cref="Prefix"/> + nom.</summary>
public sealed class Macro
{
    public const string Prefix = "$";

    // Insensible à la casse : les anciens fichiers (liste d'étapes) ont été écrits en « Name » / « Steps ».
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true };

    public string Name { get; set; } = "";

    /// <summary>Sérialisation Blockly : <c>{"blocks":{"languageVersion":0,"blocks":[…]},"variables":[…]}</c>.</summary>
    [JsonPropertyName("workspace")]
    public JsonNode? Workspace { get; set; }

    /// <summary>Ancien format (liste d'étapes) : converti en <see cref="Workspace"/> au chargement, jamais réécrit.</summary>
    [JsonPropertyName("steps")]
    public List<MacroStep>? Steps { get; set; }

    /// <summary>Levé après un renommage (ancien, nouveau) ou une suppression (ancien, null) : les affectations doivent suivre.</summary>
    public static event Action<string, string?>? Renamed;

    public static string Dir => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "macros");

    static string FileOf(string name) => Path.Combine(Dir, name + ".json");

    /// <summary>Nom utilisable comme nom de fichier (caractères interdits retirés).</summary>
    public static string Sanitize(string name) =>
        string.Concat(name.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

    public static List<string> Names() =>
        Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList() : [];

    public static Macro? Load(string name)
    {
        try
        {
            var macro = JsonSerializer.Deserialize<Macro>(File.ReadAllText(FileOf(name)), Json);
            if (macro is { Workspace: null, Steps: { } steps })
            {
                macro.Workspace = MacroBlocks.FromSteps(steps);
                macro.Steps = null;
            }
            return macro;
        }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FileOf(Name), JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Enregistre sous <see cref="Name"/> ; si <paramref name="oldName"/> diffère, l'ancien fichier est supprimé.</summary>
    public void SaveAs(string? oldName)
    {
        Save();
        if (oldName is null || oldName == Name) return;
        if (!oldName.Equals(Name, StringComparison.OrdinalIgnoreCase)) File.Delete(FileOf(oldName)); // même fichier si seule la casse change
        Renamed?.Invoke(oldName, Name);
    }

    /// <summary>Copie la macro dans un nouveau fichier (« nom (copie) », puis « nom (copie) 2 »…) ; renvoie le nouveau nom, null si échec.</summary>
    public static string? Duplicate(string name)
    {
        if (Load(name) is not { } macro) return null;
        var taken = Names();
        string copy = Loc.T("Macro.Copy", name);
        for (int i = 2; taken.Contains(copy, StringComparer.OrdinalIgnoreCase); i++) copy = Loc.T("Macro.Copy", name) + " " + i;
        macro.Name = Sanitize(copy);
        try { macro.Save(); }
        catch { return null; }
        return macro.Name;
    }

    public static void Delete(string name)
    {
        File.Delete(FileOf(name));
        Renamed?.Invoke(name, null);
    }

    /// <summary>Exécute les blocs (thread de l'interface : les fenêtres de saisie s'y ouvrent). Annulable ; s'arrête aussi si une fenêtre de saisie est fermée.</summary>
    public Task RunAsync(KeyboardInjector injector, CancellationToken ct) => new MacroInterpreter(injector, ct).RunAsync(Workspace);
}
