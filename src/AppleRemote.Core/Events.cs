// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>One finger on the touch surface. State: 0 absent, 3 touch-down, 4 contact, 5-6 leaving, 7 lifted.</summary>
public readonly record struct Finger(int X, int Y, int State);

/// <summary>Event decoded from a 0xFC report.</summary>
public abstract record RemoteEvent;

/// <summary>Touch frame. T in seconds on the remote's clock; X/Y/Down = first finger.</summary>
public sealed record TouchEvent(double T, int X, int Y, bool Down, IReadOnlyList<Finger> Fingers, byte[] Raw) : RemoteEvent;

/// <summary>Button state change. Mask = buttons pressed now.</summary>
public sealed record ButtonsEvent(int Mask, IReadOnlyList<string> Pressed, IReadOnlyList<string> Released, byte[] Raw) : RemoteEvent;

/// <summary>Audio frame while Siri is held (not decodable over HID: reserved for research).</summary>
public sealed record VoiceChunkEvent(byte[] Raw) : RemoteEvent;

public enum Direction { Up, Down, Left, Right }

public enum MoveSource { Slide, Flick }

/// <summary>tvOS-style gesture produced by <see cref="Gestures"/>.</summary>
public abstract record GestureEvent;

public sealed record TouchGesture(bool Down) : GestureEvent;

public sealed record MoveGesture(Direction Dir, MoveSource Src) : GestureEvent;

/// <summary>Tilt of the focused tile caused by a resting finger, from -1 to 1.</summary>
public sealed record TiltGesture(double X, double Y) : GestureEvent;
