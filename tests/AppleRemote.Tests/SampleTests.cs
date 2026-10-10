using System.Text.Json;
using Xunit;

namespace AppleRemote.Tests;

/// <summary>
/// Rejoue sample.jsonl (vraie capture d'une A1962) dans la chaîne complète, comme <c>pump()</c> du projet Python,
/// et compare le résultat à golden.json : la sortie du code Python de référence (decode.py + gestures.py) sur la même capture.
/// </summary>
public class SampleTests
{
    static string Local(string name) => Path.Combine(AppContext.BaseDirectory, name);

    /// <summary>(étape guidée, événement) dans l'ordre d'arrivée.</summary>
    static List<(int Step, object Ev)> Run(out List<(double T, int Step)> marks)
    {
        var m = new List<(double T, int Step)>();
        foreach (var line in File.ReadLines(Local("sample.jsonl")))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("kind").GetString() == "mark")
                m.Add((doc.RootElement.GetProperty("t").GetDouble(),
                       int.Parse(doc.RootElement.GetProperty("step").GetString()!.Split(':')[0])));
        }
        marks = m;

        double now = 0;
        var list = new List<(int, object)>();
        int StepNow() => m.Where(x => x.T <= now).Max(x => x.Step);

        var pipe = new RemotePipeline();
        pipe.Event += ev => { if (ev is not VoiceChunkEvent) list.Add((StepNow(), ev)); };
        pipe.Gesture += g => list.Add((StepNow(), g));

        foreach (var (t, report) in Replay.Read(Local("sample.jsonl")))
        {
            now = t;
            pipe.Feed(t, report);
        }
        return list;
    }

    [Fact]
    public void Buttons_per_guided_step()
    {
        var events = Run(out _);
        List<string> Pressed(int step) => events
            .Where(e => e.Step == step && e.Ev is ButtonsEvent)
            .SelectMany(e => ((ButtonsEvent)e.Ev).Pressed)
            .ToList();

        Assert.Equal(new[] { "volup", "menu" }, Pressed(0).Take(2));   // la réponse au déverrouillage porte les boutons tenus
        Assert.Equal(Enumerable.Repeat("click", 3), Pressed(10));
        Assert.Equal(Enumerable.Repeat("menu", 3), Pressed(11));
        Assert.Equal(Enumerable.Repeat("tv", 3), Pressed(12));
        Assert.Equal(Enumerable.Repeat("playpause", 5), Pressed(13));
        Assert.Equal(Enumerable.Repeat("volup", 3), Pressed(14));
        Assert.Equal(Enumerable.Repeat("voldown", 5), Pressed(15));
        Assert.Equal(Enumerable.Repeat("siri", 2), Pressed(16));
    }

    [Fact]
    public void Touches_per_step_and_remote_clock_wrap()
    {
        var touches = Run(out _).Where(e => e.Ev is TouchEvent).Select(e => (e.Step, T: (TouchEvent)e.Ev)).ToList();

        // l'horloge 16 bits fait un tour dans la capture, et t ne fait que croître
        var clocks = touches.Select(x => x.T.Raw[4] | x.T.Raw[5] << 8).ToList();
        Assert.Contains(clocks.Zip(clocks.Skip(1)), p => p.Second < p.First);
        Assert.All(touches.Zip(touches.Skip(1)), p => Assert.True(p.Second.T.T > p.First.T.T));

        var downs = new SortedDictionary<int, int>();
        bool was = false;
        foreach (var (step, t) in touches)
        {
            if (t.Down && !was) downs[step] = downs.GetValueOrDefault(step) + 1;
            was = t.Down;
        }
        // 3 swipes haut, 9 bas, 2 petits touchers + 5 flicks, 5 taps, un frôlement + 3 clics, 4 touchers à deux doigts
        Assert.Equal(new SortedDictionary<int, int> { [6] = 3, [7] = 9, [8] = 7, [9] = 5, [10] = 4, [17] = 4 }, downs);
    }

    [Fact]
    public void Gestures_per_step()
    {
        var moves = new Dictionary<int, List<(Direction Dir, MoveSource Src)>>();
        int step = 0;
        foreach (var (s, ev) in Run(out _))
        {
            if (ev is TouchGesture { Down: true }) step = s;   // un mouvement compte dans l'étape où son toucher a commencé
            if (ev is MoveGesture m)
            {
                if (!moves.TryGetValue(step, out var l)) moves[step] = l = new();
                l.Add((m.Dir, m.Src));
            }
        }

        Assert.All(moves[6], m => Assert.Equal(Direction.Up, m.Dir));
        Assert.All(moves[7], m => Assert.Equal(Direction.Down, m.Dir));
        Assert.All(moves[8], m => Assert.Equal(Direction.Right, m.Dir));
        Assert.InRange(moves[6].Count, 1, 2 * 3);
        Assert.InRange(moves[7].Count, 5, 2 * 9);
        Assert.InRange(moves[8].Count, 5, 5 * (1 + Gestures.CoastMax));
        Assert.DoesNotContain(9, moves.Keys);    // les taps ne bougent pas le focus
        Assert.DoesNotContain(10, moves.Keys);   // ni les clics
    }

    [Fact]
    public void Whole_pipeline_matches_the_python_reference()
    {
        var gold = JsonDocument.Parse(File.ReadAllText(Local("golden.json"))).RootElement.GetProperty("events");
        var actual = Run(out _);

        Assert.Equal(gold.GetArrayLength(), actual.Count);

        int i = 0;
        foreach (var g in gold.EnumerateArray())
        {
            var (step, ev) = actual[i];
            string where = $"événement {i} (étape {step}) : {ev}";
            Assert.True(g[0].GetInt32() == step, where);

            switch (g[1].GetString())
            {
                case "touch":
                    var t = Assert.IsType<TouchEvent>(ev);
                    Assert.True(Math.Abs(g[2].GetDouble() - t.T) < 1e-9, where);
                    Assert.True(g[3].GetInt32() == t.X && g[4].GetInt32() == t.Y && g[5].GetBoolean() == t.Down, where);
                    var fingers = g[6].EnumerateArray().Select(f => new Finger(f[0].GetInt32(), f[1].GetInt32(), f[2].GetInt32())).ToArray();
                    Assert.True(fingers.SequenceEqual(t.Fingers), where);
                    break;
                case "buttons":
                    var b = Assert.IsType<ButtonsEvent>(ev);
                    Assert.True(g[2].GetInt32() == b.Mask, where);
                    Assert.True(g[3].EnumerateArray().Select(x => x.GetString()).SequenceEqual(b.Pressed), where);
                    Assert.True(g[4].EnumerateArray().Select(x => x.GetString()).SequenceEqual(b.Released), where);
                    break;
                case "gtouch":
                    Assert.True(Assert.IsType<TouchGesture>(ev).Down == g[2].GetBoolean(), where);
                    break;
                case "move":
                    var m = Assert.IsType<MoveGesture>(ev);
                    Assert.True(m.Dir.ToString().ToLowerInvariant() == g[2].GetString(), where);
                    Assert.True(m.Src.ToString().ToLowerInvariant() == g[3].GetString(), where);
                    break;
                case "tilt":
                    var tl = Assert.IsType<TiltGesture>(ev);
                    Assert.True(Math.Abs(tl.X - g[2].GetDouble()) <= 0.0011 && Math.Abs(tl.Y - g[3].GetDouble()) <= 0.0011, where);
                    break;
                default:
                    Assert.Fail($"type de référence inconnu : {g[1]}");
                    break;
            }
            i++;
        }
    }
}
