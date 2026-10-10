// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>Windows key: virtual-key code (VK_*) and "extended" flag (arrows, navigation).</summary>
public readonly record struct KeyCode(ushort Vk, bool Extended = false);

/// <summary>Key sink (SendInput on Windows, fake receiver in tests).</summary>
public interface IKeySink
{
    void Key(KeyCode key, bool down);
}

/// <summary>Key names accepted in the JSON mapping file. Portable table (VK_* values).</summary>
public static class Keys
{
    public static readonly IReadOnlyDictionary<string, KeyCode> Names = Build();

    /// <summary>Key name for display (the first declared: "Escape" before "Esc"), null if not in the table.</summary>
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
/// Mapping remote input -> key. Inputs: up, down, left, right, click, menu, tv,
/// playpause, volup, voldown, siri. A missing input is disabled.
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

    /// <summary>Assigns a key to an input; null disables it.</summary>
    public void Set(string input, KeyCode? key)
    {
        if (key is { } k) _map[input] = k;
        else _map.Remove(input);
    }

    /// <summary>JSON readable by <see cref="FromJson"/>: all inputs, null if disabled.</summary>
    public string ToJson()
    {
        var o = new Dictionary<string, object?>();
        foreach (var input in Inputs)
            o[input] = _map.TryGetValue(input, out var k) ? (Keys.NameOf(k) ?? (object)k.Vk) : null;
        return System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Applies on top of the default mapping a JSON of the form
    /// <c>{"click": "Space", "siri": "C", "tv": null, "volup": 175}</c> : key name, VK code (1-254) or null to disable.
    /// Throws <see cref="ArgumentException"/> if an input or a key is unknown.
    /// </summary>
    public static KeyMap FromJson(string json)
    {
        var m = Default();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("The mapping must be a JSON object.");

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string input = p.Name.ToLowerInvariant();
            if (!Inputs.Contains(input))
                throw new ArgumentException($"Unknown input: \"{p.Name}\" (valid: {string.Join(", ", Inputs)}).");

            var v = p.Value;
            switch (v.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Null:
                    m._map.Remove(input);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    string name = v.GetString()!;
                    if (!Keys.Names.TryGetValue(name, out var key))
                        throw new ArgumentException($"Unknown key: \"{name}\" for \"{p.Name}\".");
                    m._map[input] = key;
                    break;
                case System.Text.Json.JsonValueKind.Number when v.TryGetInt32(out int vk) && vk is >= 1 and <= 254:
                    m._map[input] = new KeyCode((ushort)vk);
                    break;
                default:
                    throw new ArgumentException($"Invalid value for \"{p.Name}\": {v}.");
            }
        }
        return m;
    }
}

/// <summary>
/// Virtual keyboard: translates buttons and focus steps into keys. Buttons: press = key down, release = key up.
/// Focus steps (drag/flick): one keystroke. Releases come before presses within the same event.
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

    /// <summary>Releases every key still held (disable, stop, link lost).</summary>
    public void ReleaseAll()
    {
        foreach (var name in _held.ToArray())
            if (_map.TryGet(name, out var key))
                _sink.Key(key, false);
        _held.Clear();
    }
}
