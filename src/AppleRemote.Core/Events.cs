namespace AppleRemote;

/// <summary>Un doigt sur la surface tactile. État : 0 absent, 3 pose, 4 contact, 5-6 départ, 7 levé.</summary>
public readonly record struct Finger(int X, int Y, int State);

/// <summary>Événement décodé d'un report 0xFC.</summary>
public abstract record RemoteEvent;

/// <summary>Trame tactile. T en secondes sur l'horloge de la télécommande ; X/Y/Down = premier doigt.</summary>
public sealed record TouchEvent(double T, int X, int Y, bool Down, IReadOnlyList<Finger> Fingers, byte[] Raw) : RemoteEvent;

/// <summary>Changement d'état des boutons. Mask = boutons enfoncés maintenant.</summary>
public sealed record ButtonsEvent(int Mask, IReadOnlyList<string> Pressed, IReadOnlyList<string> Released, byte[] Raw) : RemoteEvent;

/// <summary>Trame audio pendant que Siri est maintenu (non décodable via HID : réservé à la recherche).</summary>
public sealed record VoiceChunkEvent(byte[] Raw) : RemoteEvent;

public enum Direction { Up, Down, Left, Right }

public enum MoveSource { Slide, Flick }

/// <summary>Geste de type tvOS produit par <see cref="Gestures"/>.</summary>
public abstract record GestureEvent;

public sealed record TouchGesture(bool Down) : GestureEvent;

public sealed record MoveGesture(Direction Dir, MoveSource Src) : GestureEvent;

/// <summary>Inclinaison de la tuile focalisée par un doigt au repos, de -1 à 1.</summary>
public sealed record TiltGesture(double X, double Y) : GestureEvent;
