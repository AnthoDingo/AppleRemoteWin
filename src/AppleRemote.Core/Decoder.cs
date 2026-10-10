namespace AppleRemote;

/// <summary>
/// Décode les reports 0xFC de la Siri Remote gen 1 (A1513/A1962). Port de appleremote/decode.py.
/// <code>
/// fc 00 BB                                boutons seuls
/// fc NN BB 32 CC CC SS + NN x 7 octets    NN doigts ; CC CC = horloge de la télécommande (2000 ticks/s)
/// fc 10 BB ...                            audio pendant Siri
/// fc e0 BB 60 02                          réponse au déverrouillage
/// </code>
/// Sous Windows, ReadFile renvoie toujours InputReportByteLength octets (rembourrés de zéros) :
/// le décodeur n'utilise que les octets annoncés par l'octet 1.
/// </summary>
public sealed class Decoder
{
    public const int TicksPerSecond = 2000;

    static readonly (int Mask, string Name)[] ButtonNames =
    {
        (0x01, "tv"), (0x02, "volup"), (0x04, "voldown"), (0x08, "playpause"),
        (0x10, "siri"), (0x20, "menu"), (0x80, "click"),
    };

    double _t;
    int? _clock;
    int _mask;
    TouchEvent? _touch;

    static int S12(int v) => v >= 2048 ? v - 4096 : v;

    public static IReadOnlyList<string> Names(int bits)
    {
        var list = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            if ((bits >> i & 1) == 0) continue;
            int m = 1 << i;
            string? name = null;
            foreach (var (mask, n) in ButtonNames)
                if (mask == m) { name = n; break; }
            list.Add(name ?? $"0x{m:x2}");
        }
        return list;
    }

    /// <summary>
    /// Décode un report. <c>null</c> = pas de report (rien). Tableau vide = lien coupé : tout ce qui était tenu est relâché.
    /// </summary>
    public List<RemoteEvent> Decode(byte[]? r)
    {
        var output = new List<RemoteEvent>();
        if (r is null) return output;

        if (r.Length == 0)
        {
            if (_mask != 0) output.Add(SetButtons(0, r));
            if (_touch is { Down: true } last)
                output.Add(last with { Down = false, Fingers = Array.Empty<Finger>(), Raw = r });
            _touch = null;
            return output;
        }

        if (r.Length < 3 || r[0] != 0xFC) return output;

        if (r[2] != _mask) output.Add(SetButtons(r[2], r));

        if (r[1] == 0x10)
        {
            output.Add(new VoiceChunkEvent(r));
        }
        else if (r[1] != 0 && r.Length >= 14 && r[3] == 0x32)
        {
            int clock = r[4] | r[5] << 8;
            if (_clock is int prev)
                _t += ((clock - prev) & 0xFFFF) / (double)TicksPerSecond; // 16 bits : 32,768 s par tour
            _clock = clock;

            int n = Math.Min((int)r[1], (r.Length - 7) / 7);
            if (n <= 0) return output;
            var fingers = new Finger[n];
            for (int k = 0; k < n; k++)
            {
                int o = 7 + 7 * k;
                fingers[k] = new Finger(
                    S12(r[o] | (r[o + 1] & 15) << 8),
                    S12(r[o + 1] >> 4 | r[o + 2] << 4),
                    r[o + 6] & 7);
            }
            var f0 = fingers[0];
            _touch = new TouchEvent(Math.Round(_t, 4), f0.X, f0.Y, f0.State is >= 3 and <= 6, fingers, r);
            output.Add(_touch);
        }
        return output;
    }

    ButtonsEvent SetButtons(int mask, byte[] raw)
    {
        int old = _mask;
        _mask = mask;
        return new ButtonsEvent(mask, Names(mask & ~old), Names(old & ~mask), raw);
    }
}
