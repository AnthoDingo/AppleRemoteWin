// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>
/// Full chain: raw report -> <see cref="Decoder"/> -> <see cref="Gestures"/>. No clock of its own:
/// the host time is the one passed to <see cref="Feed"/>, which makes replays deterministic.
/// Not thread-safe: the caller serialises the calls.
/// </summary>
public sealed class RemotePipeline
{
    readonly Decoder _decoder = new();
    readonly Gestures _gestures;
    double _now;

    public event Action<RemoteEvent>? Event;
    public event Action<GestureEvent>? Gesture;

    public RemotePipeline()
    {
        _gestures = new Gestures(g => Gesture?.Invoke(g), () => _now);
    }

    /// <summary>
    /// Feeds a report at host time <paramref name="hostTime"/> (seconds). <c>null</c> = plain tick (flick inertia),
    /// empty array = link lost.
    /// </summary>
    public void Feed(double hostTime, byte[]? report)
    {
        _now = hostTime;
        foreach (var ev in _decoder.Decode(report))
        {
            Event?.Invoke(ev);
            if (ev is TouchEvent t) _gestures.Frame(t.T, t.X, t.Y, t.Down);
        }
        _gestures.Tick();
    }
}
