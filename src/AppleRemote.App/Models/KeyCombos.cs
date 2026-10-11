namespace AppleRemote.App.Models;

/// <summary>Combinaison de touches (Win+D, Alt+Tab…) lançable depuis un bouton. Une valeur « combo » d'une
/// affectation s'écrit <see cref="Prefix"/> + noms séparés par « + » (modificateurs d'abord, puis la touche).</summary>
public static class KeyCombos
{
    public const string Prefix = "#";

    /// <summary>Modificateurs, dans l'ordre d'affichage : nom -> VK générique (gauche/droite confondus).</summary>
    static readonly (string Name, ushort Vk)[] Modifiers = [("Ctrl", 0x11), ("Alt", 0x12), ("Shift", 0x10), ("Win", 0x5B)];

    /// <summary>Nom du modificateur pour un VK gauche/droite, null si ce n'en est pas un.</summary>
    public static string? ModifierName(ushort vk) => vk switch
    {
        0xA0 or 0xA1 or 0x10 => "Shift",
        0xA2 or 0xA3 or 0x11 => "Ctrl",
        0xA4 or 0xA5 or 0x12 => "Alt",
        0x5B or 0x5C => "Win",
        _ => null,
    };

    /// <summary>Nom de la touche principale (table de <see cref="Keys"/>), null si hors table.</summary>
    public static string? KeyName(ushort vk) => Keys.Names.FirstOrDefault(p => p.Value.Vk == vk).Key;

    /// <summary>Valeur stockée à partir des modificateurs tenus et de la touche principale (peut être null).</summary>
    public static string Build(IEnumerable<string> modifiers, string? key)
    {
        var parts = Modifiers.Select(m => m.Name).Where(modifiers.Contains).Concat(key is null ? [] : [key]);
        return Prefix + string.Join("+", parts);
    }

    public static string Display(string combo) => combo[Prefix.Length..].Replace("+", " + ");

    /// <summary>Appuie la combinaison (modificateurs enfoncés, touche, tout relâché).</summary>
    public static void Run(IKeySink sink, string combo)
    {
        var keys = new List<KeyCode>();
        foreach (var name in combo[Prefix.Length..].Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Modifiers.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } m) keys.Add(new KeyCode(m.Vk));
            else if (Keys.Names.TryGetValue(name, out var k)) keys.Add(k);
        }
        foreach (var k in keys) sink.Key(k, true);
        for (int i = keys.Count - 1; i >= 0; i--) sink.Key(keys[i], false);
    }
}
