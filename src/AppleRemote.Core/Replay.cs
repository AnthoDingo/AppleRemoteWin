using System.Text.Json;

namespace AppleRemote;

/// <summary>Capture JSONL (format de <c>monitor --raw</c> du projet open-appleremote) : lecture et écriture.</summary>
public static class Replay
{
    /// <summary>
    /// Lit une capture : lignes <c>{"kind":"report","t":..,"hex":".."}</c> (les autres sont ignorées). Donne les ticks d'inactivité
    /// qu'aurait une télécommande réelle (pour l'inertie des gestes) ; <paramref name="realtime"/> respecte aussi le rythme.
    /// Les silences sont coupés à 1 s.
    /// </summary>
    public static IEnumerable<(double Time, byte[]? Report)> Read(string path, bool realtime = false, double idle = 0.01, CancellationToken ct = default)
    {
        double? now = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "report") continue;

            double t = root.GetProperty("t").GetDouble();
            byte[] bytes = Convert.FromHexString(root.GetProperty("hex").GetString()!);

            if (now is double n)
            {
                double end = Math.Min(t, n + 1);
                while (n + idle < end)
                {
                    if (ct.IsCancellationRequested) yield break;
                    n += idle;
                    if (realtime) Thread.Sleep((int)(idle * 1000));
                    yield return (n, null);
                }
                if (realtime && end == t) Thread.Sleep((int)(Math.Max(0.0, t - n) * 1000));
            }
            if (ct.IsCancellationRequested) yield break;
            now = t;
            yield return (t, bytes);
        }

        double tail = now ?? 0.0;
        for (int i = 0; i < (int)(1 / idle); i++)
        {
            if (ct.IsCancellationRequested) yield break;
            tail += idle;
            if (realtime) Thread.Sleep((int)(idle * 1000));
            yield return (tail, null);
        }
    }

    /// <summary>Une ligne de capture pour un report.</summary>
    public static string Line(double t, byte[] report) =>
        JsonSerializer.Serialize(new { kind = "report", t = Math.Round(t, 4), hex = Convert.ToHexString(report).ToLowerInvariant() });
}
