namespace AppleRemote;

/// <summary>
/// Chaîne complète : report brut -> <see cref="Decoder"/> -> <see cref="Gestures"/>. Pas d'horloge propre :
/// le temps hôte est celui passé à <see cref="Feed"/>, ce qui rend les rejeux déterministes.
/// Non thread-safe : l'appelant sérialise les appels.
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
    /// Injecte un report à l'instant hôte <paramref name="hostTime"/> (secondes). <c>null</c> = simple tick (inertie des flicks),
    /// tableau vide = lien coupé.
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
