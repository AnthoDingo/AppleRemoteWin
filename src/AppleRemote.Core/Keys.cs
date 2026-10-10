namespace AppleRemote;

/// <summary>Touche Windows : code de touche virtuelle (VK_*) et drapeau « étendue » (flèches, navigation).</summary>
public readonly record struct KeyCode(ushort Vk, bool Extended = false);

/// <summary>Destination des touches (SendInput sous Windows, faux récepteur dans les tests).</summary>
public interface IKeySink
{
    void Key(KeyCode key, bool down);
}

/// <summary>Noms de touches acceptés dans le fichier de mapping JSON. Table portable (valeurs VK_*).</summary>
public static class Keys
{
    public static readonly IReadOnlyDictionary<string, KeyCode> Names = Build();

    /// <summary>Nom de touche pour l'affichage (le premier déclaré : « Escape » avant « Esc »), null si hors table.</summary>
    public static string? NameOf(KeyCode key) => Names.FirstOrDefault(p => p.Value == key).Key;

    static IReadOnlyDictionary<string, KeyCode> Build()
    {
        var d = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase)
        {
            ["Left"] = new(0x25, true), ["Up"] = new(0x26, true), ["Right"] = new(0x27, true), ["Down"] = new(0x28, true),
            ["Enter"] = new(0x0D), ["Escape"] = new(0x1B), ["Esc"] = new(0x1B), ["Space"] = new(0x20),
            ["Tab"] = new(0x09), ["Backspace"] = new(0x08),
            ["Home"] = new(0x24, true), ["End"] = new(0x23, true),
            ["PageUp"] = new(0x21, true), ["PageDown"] = new(0x22, true),
            ["Insert"] = new(0x2D, true), ["Delete"] = new(0x2E, true),
            ["BrowserBack"] = new(0xA6), ["BrowserForward"] = new(0xA7), ["BrowserRefresh"] = new(0xA8),
            ["BrowserHome"] = new(0xAC),
            ["VolumeMute"] = new(0xAD), ["VolumeDown"] = new(0xAE), ["VolumeUp"] = new(0xAF),
            ["MediaNext"] = new(0xB0), ["MediaPrev"] = new(0xB1), ["MediaStop"] = new(0xB2), ["MediaPlayPause"] = new(0xB3),
        };
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = new((ushort)c);
        for (int i = 0; i <= 9; i++) d[$"D{i}"] = new((ushort)('0' + i));
        for (int i = 1; i <= 12; i++) d[$"F{i}"] = new((ushort)(0x6F + i));
        return d;
    }
}

/// <summary>
/// Correspondance entrée de la télécommande -> touche. Entrées : up, down, left, right, click, menu, tv,
/// playpause, volup, voldown, siri. Une entrée absente est désactivée.
/// </summary>
public sealed class KeyMap
{
    public static readonly IReadOnlyList<string> Inputs = new[]
    {
        "up", "down", "left", "right", "click", "menu", "tv", "playpause", "volup", "voldown", "siri",
    };

    readonly Dictionary<string, KeyCode> _map = new();

    public static KeyMap Default()
    {
        var m = new KeyMap();
        m._map["up"] = Keys.Names["Up"];
        m._map["down"] = Keys.Names["Down"];
        m._map["left"] = Keys.Names["Left"];
        m._map["right"] = Keys.Names["Right"];
        m._map["click"] = Keys.Names["Enter"];
        m._map["menu"] = Keys.Names["Escape"];
        m._map["tv"] = Keys.Names["BrowserHome"];
        m._map["playpause"] = Keys.Names["MediaPlayPause"];
        m._map["volup"] = Keys.Names["VolumeUp"];
        m._map["voldown"] = Keys.Names["VolumeDown"];
        return m;
    }

    public bool TryGet(string input, out KeyCode key) => _map.TryGetValue(input, out key);

    public IReadOnlyDictionary<string, KeyCode> Entries => _map;

    /// <summary>Associe une touche à une entrée ; null la désactive.</summary>
    public void Set(string input, KeyCode? key)
    {
        if (key is { } k) _map[input] = k;
        else _map.Remove(input);
    }

    /// <summary>JSON relisible par <see cref="FromJson"/> : toutes les entrées, null si désactivée.</summary>
    public string ToJson()
    {
        var o = new Dictionary<string, object?>();
        foreach (var input in Inputs)
            o[input] = _map.TryGetValue(input, out var k) ? (Keys.NameOf(k) ?? (object)k.Vk) : null;
        return System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Applique par-dessus le mapping par défaut un JSON du type
    /// <c>{"click": "Space", "siri": "C", "tv": null, "volup": 175}</c> : nom de touche, code VK (1-254) ou null pour désactiver.
    /// Lève <see cref="ArgumentException"/> si une entrée ou une touche est inconnue.
    /// </summary>
    public static KeyMap FromJson(string json)
    {
        var m = Default();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("Le mapping doit être un objet JSON.");

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string input = p.Name.ToLowerInvariant();
            if (!Inputs.Contains(input))
                throw new ArgumentException($"Entrée inconnue : « {p.Name} » (valides : {string.Join(", ", Inputs)}).");

            var v = p.Value;
            switch (v.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Null:
                    m._map.Remove(input);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    string name = v.GetString()!;
                    if (!Keys.Names.TryGetValue(name, out var key))
                        throw new ArgumentException($"Touche inconnue : « {name} » pour « {p.Name} ».");
                    m._map[input] = key;
                    break;
                case System.Text.Json.JsonValueKind.Number when v.TryGetInt32(out int vk) && vk is >= 1 and <= 254:
                    m._map[input] = new KeyCode((ushort)vk);
                    break;
                default:
                    throw new ArgumentException($"Valeur invalide pour « {p.Name} » : {v}.");
            }
        }
        return m;
    }
}

/// <summary>
/// Clavier virtuel : traduit boutons et pas de focus en touches. Boutons : appui = touche enfoncée, relâche = touche levée.
/// Pas de focus (glisser/flick) : une frappe. Les relâches passent avant les appuis dans un même événement.
/// </summary>
public sealed class KeyBinder
{
    readonly IKeySink _sink;
    readonly HashSet<string> _held = new();
    KeyMap _map;

    public KeyBinder(KeyMap map, IKeySink sink)
    {
        _map = map;
        _sink = sink;
    }

    public KeyMap Map
    {
        get => _map;
        set { ReleaseAll(); _map = value; }
    }

    public void OnEvent(RemoteEvent ev)
    {
        if (ev is not ButtonsEvent b) return;
        foreach (var name in b.Released)
            if (_held.Remove(name) && _map.TryGet(name, out var up))
                _sink.Key(up, false);
        foreach (var name in b.Pressed)
            if (_map.TryGet(name, out var down) && _held.Add(name))
                _sink.Key(down, true);
    }

    public void OnGesture(GestureEvent g)
    {
        if (g is not MoveGesture m) return;
        if (!_map.TryGet(m.Dir.ToString().ToLowerInvariant(), out var key)) return;
        _sink.Key(key, true);
        _sink.Key(key, false);
    }

    /// <summary>Relâche toute touche encore enfoncée (désactivation, arrêt, lien coupé).</summary>
    public void ReleaseAll()
    {
        foreach (var name in _held.ToArray())
            if (_map.TryGet(name, out var key))
                _sink.Key(key, false);
        _held.Clear();
    }
}
