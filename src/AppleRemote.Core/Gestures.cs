// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo
// Port of open-appleremote (MIT, Copyright (c) 2026 open-appleremote contributors): see LICENSE-MIT-open-appleremote.

namespace AppleRemote;

/// <summary>
/// Touch frames -> smoothed tvOS-style focus gestures. Port of appleremote/gestures.py.
/// <list type="bullet">
/// <item>a resting finger that moves a little tilts the tile (parallax), the focus doesn't move;</item>
/// <item>a drag advances the focus along a locked axis, one step per <see cref="Step"/> of travel, paced;</item>
/// <item>a flick finishes its travel with friction; the next touch stops it;</item>
/// <item>the click (button) is handled by the caller.</item>
/// </list>
/// Units: the remote's touch units (~1650 across the surface). The times passed to
/// <see cref="Frame"/> are the remote's; <c>clock</c> is the host clock (pacing, inertia).
/// Not thread-safe: the caller serialises <see cref="Frame"/> et <see cref="Tick"/>.
/// </summary>
public sealed class Gestures
{
    // Calibration constants (tuned by feel on a gen 1).
    public const double Step = 560;       // travel per focus step (~3 across the surface)
    public const double Lock = 140;       // travel before locking the axis...
    public const double LockRatio = 1.5;  // ...and by how much the major axis must win
    public const double Turn = 1.25;      // travel (in Step) on the other axis that changes the locked axis
    public const double Back = 1.4;       // travel (in Step) to go back
    public const double Pace = .12;       // s between two drag steps at the fastest
    public const int Settle = 3;          // frames ignored after the finger lands
    public const int Guard = 2;           // frames held back then dropped before lift-off
    public const double Flick = 4200;     // release speed (u/s) that gives inertia
    public const double FlickMin = 200;   // minimum travel on the axis for a flick
    public const int CoastMax = 4;
    public const double CoastFirst = .11, Friction = 1.3;
    public const double TiltHz = 20, TiltEps = .05, TiltDead = .06;

    readonly Action<GestureEvent> _emit;
    readonly Func<double> _clock;

    bool _down;
    readonly List<(double Due, Direction Dir)> _coast = new();
    (double X, double Y) _tilt = (0.0, 0.0);
    double _tiltAt;

    int _n;
    List<(double T, double X, double Y)> _buf = new();
    List<(double T, double X, double Y)> _hist = new();
    OneEuro _fx = new(), _fy = new();
    double[]? _anchor;
    char _axis;               // '\0' until locked, then 'x' or 'y'
    Direction? _last;
    int _steps;
    double _nextAt;

    public Gestures(Action<GestureEvent> emit, Func<double>? clock = null)
    {
        _emit = emit;
        _clock = clock ?? DefaultClock;
    }

    static double DefaultClock() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Pending coast steps (for tests).</summary>
    public int PendingCoast => _coast.Count;

    public void Frame(double t, double x, double y, bool down)
    {
        if (down && !_down)
        {
            _coast.Clear();
            _down = true;
            _n = 0;
            _buf = new();
            _hist = new();
            _fx = new OneEuro();
            _fy = new OneEuro();
            _anchor = null;
            _axis = '\0';
            _last = null;
            _steps = 0;
            _nextAt = 0.0;
            _emit(new TouchGesture(true));
        }
        if (!down)
        {
            if (_down) Release();
            return;
        }

        _n++;
        if (_n <= Settle) return;

        _buf.Add((t, x, y));              // holds back Guard frames: they may be the roll-off before lift-off
        if (_buf.Count <= Guard) return;
        var (bt, bx, by) = _buf[0];
        _buf.RemoveAt(0);
        Move(bt, _fx.Filter(bt, bx), _fy.Filter(bt, by));
    }

    void Move(double t, double x, double y)
    {
        _hist = _hist.Where(h => t - h.T <= .08).ToList();
        _hist.Add((t, x, y));

        if (_anchor is null)
        {
            _anchor = new[] { x, y };
            return;
        }

        double dx = x - _anchor[0], dy = y - _anchor[1];
        if (_axis == '\0' && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= Lock)
        {
            if (Math.Abs(dx) >= LockRatio * Math.Abs(dy)) _axis = 'x';
            else if (Math.Abs(dy) >= LockRatio * Math.Abs(dx)) _axis = 'y';
        }
        else if (_axis == 'x' && Math.Abs(dy) >= Turn * Step)   // deliberate turn
        {
            _axis = 'y';
            _anchor[0] = x;
            _last = null;
        }
        else if (_axis == 'y' && Math.Abs(dx) >= Turn * Step)
        {
            _axis = 'x';
            _anchor[1] = y;
            _last = null;
        }

        double d = _axis == 'x' ? x - _anchor[0] : _axis == 'y' ? y - _anchor[1] : 0.0;
        if (_axis != '\0')
        {
            var name = _axis == 'x' ? (d > 0 ? Direction.Right : Direction.Left) : (d > 0 ? Direction.Up : Direction.Down);
            double need = Step * (_last is Direction l && name != l ? Back : 1);
            double now = _clock();
            if (Math.Abs(d) >= need && now >= _nextAt)
            {
                _emit(new MoveGesture(name, MoveSource.Slide));
                _steps++;
                _last = name;
                _nextAt = now + Pace;
                int i = _axis == 'x' ? 0 : 1;
                // the anchor advances one step, at most one step behind the finger (no stored burst)
                _anchor[i] = (_axis == 'x' ? x : y) - Math.CopySign(Math.Min(Math.Abs(d) - Step, Step * .5), d);
                if (_axis == 'x') _anchor[1] = y; else _anchor[0] = x;
            }
        }
        UpdateTilt(x - _anchor[0], y - _anchor[1], false);
    }

    void UpdateTilt(double dx, double dy, bool force)
    {
        double tx = Math.Max(-1.0, Math.Min(1.0, dx / (Step * .8)));
        double ty = Math.Max(-1.0, Math.Min(1.0, dy / (Step * .8)));
        tx = Math.Abs(tx) < TiltDead ? 0.0 : Math.Round(tx, 3);
        ty = Math.Abs(ty) < TiltDead ? 0.0 : Math.Round(ty, 3);
        double now = _clock();
        if (force || (Math.Abs(tx - _tilt.X) + Math.Abs(ty - _tilt.Y) >= TiltEps && now - _tiltAt >= 1 / TiltHz))
        {
            _tilt = (tx, ty);
            _tiltAt = now;
            _emit(new TiltGesture(tx, ty));
        }
    }

    void Release()
    {
        _down = false;
        _emit(new TouchGesture(false));
        if (_tilt != (0.0, 0.0)) UpdateTilt(0, 0, true);

        var h = _hist;  // the held roll-off frames are dropped; the speed comes from before
        if (_axis != '\0' && _anchor is not null && h.Count >= 3 && h[^1].T > h[0].T)
        {
            bool isX = _axis == 'x';
            double last = isX ? h[^1].X : h[^1].Y;
            double first = isX ? h[0].X : h[0].Y;
            double v = (last - first) / (h[^1].T - h[0].T);
            double anchor = isX ? _anchor[0] : _anchor[1];
            if (Math.Abs(v) >= Flick && Math.Abs(last - anchor) + _steps * Step >= FlickMin)
            {
                var name = isX ? (v > 0 ? Direction.Right : Direction.Left) : (v > 0 ? Direction.Up : Direction.Down);
                if (!(_last is Direction l && name != l))
                {
                    int n = Math.Min(_steps < 3 ? CoastMax : 2, 1 + (int)((Math.Abs(v) - Flick) / 3000));
                    double due = _clock() + CoastFirst, gap = CoastFirst;
                    for (int k = 0; k < n; k++)
                    {
                        _coast.Add((due, name));
                        gap *= Friction;
                        due += gap;
                    }
                }
            }
        }
    }

    /// <summary>Sends the inertia steps due now. Call often (~10 ms).</summary>
    public void Tick()
    {
        double t = _clock();
        while (_coast.Count > 0 && _coast[0].Due <= t)
        {
            var dir = _coast[0].Dir;
            _coast.RemoveAt(0);
            _emit(new MoveGesture(dir, MoveSource.Flick));
        }
    }
}
