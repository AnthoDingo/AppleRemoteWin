using Xunit;

namespace AppleRemote.Tests;

/// <summary>Port de _selftest() de gestures.py : mêmes trajectoires, mêmes attentes.</summary>
public class GesturesTests
{
    sealed class Rig
    {
        public readonly List<GestureEvent> Out = new();
        public readonly Gestures G;
        public double Now = 100.0, T = 0.0;

        public Rig() => G = new Gestures(Out.Add, () => Now);

        public void Touch(IEnumerable<(double X, double Y)> path, double dt = .01)
        {
            foreach (var (x, y) in path)
            {
                G.Frame(T, x, y, true);
                T += dt;
                Now += dt;
            }
            G.Frame(T, 0, 0, false);
        }

        public List<Direction> Moves() => Out.OfType<MoveGesture>().Select(m => m.Dir).ToList();
    }

    [Fact]
    public void Resting_finger_jitter_moves_nothing_and_does_not_tilt()
    {
        var r = new Rig();
        r.Touch(Enumerable.Range(0, 120).Select(i => (5 * Math.Sin(i), 4 * Math.Cos(i * 1.3))));
        Assert.Empty(r.Moves());
        Assert.DoesNotContain(r.Out.OfType<TiltGesture>(), t => t.X != 0 || t.Y != 0);
    }

    [Fact]
    public void Landing_jump_is_ignored()
    {
        var r = new Rig();
        var path = new[] { (0.0, 0.0) }
            .Concat(Enumerable.Repeat((400.0, 0.0), 2))
            .Concat(Enumerable.Range(0, 40).Select(i => (400.0 + 8 * i, 0.0)));
        r.Touch(path);
        Assert.Empty(r.Moves());
    }

    [Fact]
    public void Slow_slide_steps_three_times_per_surface()
    {
        var r = new Rig();
        r.Touch(Enumerable.Range(0, 200).Select(i => (9.0 * i, 0.0)));   // 1800 unités à 900 u/s
        Assert.Equal(new[] { Direction.Right, Direction.Right, Direction.Right }, r.Moves());
    }

    [Fact]
    public void Coming_back_needs_extra_travel_so_no_ping_pong()
    {
        var r = new Rig();
        r.Touch(Enumerable.Range(0, 70).Select(i => (9.0 * i, 0.0))
            .Concat(Enumerable.Range(0, 40).Select(i => (630.0 - 9 * i, 0.0))));
        Assert.Equal(new[] { Direction.Right }, r.Moves());
    }

    [Fact]
    public void Short_fast_flick_coasts_with_friction_and_next_touch_stops_it()
    {
        var r = new Rig();
        r.Touch(Enumerable.Range(0, 14).Select(i => (60.0 * i, 0.0)));   // 6000 u/s, trop court pour glisser
        Assert.Empty(r.Moves());
        Assert.InRange(r.G.PendingCoast, 1, Gestures.CoastMax);

        // on laisse passer le temps : les pas arrivent, tous en flick vers la droite
        int pending = r.G.PendingCoast;
        r.Now += 5;
        r.G.Tick();
        var flicks = r.Out.OfType<MoveGesture>().ToList();
        Assert.Equal(pending, flicks.Count);
        Assert.All(flicks, m =>
        {
            Assert.Equal(Direction.Right, m.Dir);
            Assert.Equal(MoveSource.Flick, m.Src);
        });
        Assert.Equal(0, r.G.PendingCoast);

        // un toucher arrête l'inertie en cours
        var r2 = new Rig();
        r2.Touch(Enumerable.Range(0, 14).Select(i => (60.0 * i, 0.0)));
        Assert.True(r2.G.PendingCoast > 0);
        r2.G.Frame(r2.T, 0, 0, true);
        Assert.Equal(0, r2.G.PendingCoast);
    }
}
