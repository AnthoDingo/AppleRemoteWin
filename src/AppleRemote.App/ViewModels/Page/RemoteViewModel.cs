// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleRemote.App.ViewModels;

public enum StatusLevel { Idle, Ok, Warn }

/// <summary>One finger on the touch surface, in pixels of the 360 px square (top-left corner of the dot).</summary>
public sealed partial class FingerViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    [ObservableProperty] bool _isVisible;
    [ObservableProperty] double _left;
    [ObservableProperty] double _top;
}

/// <summary>Remote page (and the remote drawing of the other pages): connection status, live buttons, touch and gestures.</summary>
public sealed partial class RemoteViewModel : ObservableObject, IDisposable
{
    // Observed touch range (≈ 1650–1800 units per axis, not centred on 0) -> 360 px square.
    const double PadSize = 360, XMin = -1900, YMin = -1150, Span = 1800, DotSize = 30;
    const long TrailLifeMs = 1000;

    readonly RemoteSession _session;
    readonly Action<string> _log;
    readonly Action<string> _runBound;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<(Point Point, long Time)> _trail = new();
    readonly BleScanner _scanner = new();
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _trailTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly DispatcherTimer _gestureTimer = new() { Interval = TimeSpan.FromMilliseconds(TrailLifeMs) };
    bool _wasDown;

    /// <summary>BLE devices seen advertising with Apple manufacturer data + HID service ("Apple HID").</summary>
    [ObservableProperty] IReadOnlyList<string> _nearbyDevices = [];

    public FingerViewModel[] Fingers { get; } = [new(0), new(1)];

    [ObservableProperty] string _statusText = Loc.T("Status.Stopped");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTitle))]
    StatusLevel _statusLevel;

    public string StatusTitle => Loc.T("Remote.Level." + StatusLevel);
    [ObservableProperty] PointCollection _trailPoints = new();
    [ObservableProperty] double _tileShiftX;
    [ObservableProperty] double _tileShiftY;
    [ObservableProperty] string _moveArrow = "·";
    [ObservableProperty] string _gestureText = "—";

    [ObservableProperty] bool _menuPressed;
    [ObservableProperty] bool _tvPressed;
    [ObservableProperty] bool _siriPressed;
    [ObservableProperty] bool _playPressed;
    [ObservableProperty] bool _volUpPressed;
    [ObservableProperty] bool _volDownPressed;
    [ObservableProperty] bool _touchPressed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    [NotifyPropertyChangedFor(nameof(BatteryPercent))]
    [NotifyPropertyChangedFor(nameof(BatteryGlyph))]
    int? _batteryLevel;

    /// <summary>Segoe MDL2 battery glyph in steps of 10 % (Battery0 = E850 … Battery9 = E858, Battery10 = E83F); E996 = unknown.</summary>
    public string BatteryGlyph => BatteryLevel is { } level
        ? char.ConvertFromUtf32(Math.Clamp((level + 5) / 10, 0, 10) switch { 10 => 0xE83F, var step => 0xE850 + step })
        : "";

    public string BatteryPercent => BatteryLevel is { } level ? $"{level} %" : "—";

    /// <summary>Last known level (direct Bluetooth); "—" until a remote has reported one.</summary>
    public string BatteryText => BatteryLevel is { } level ? Loc.T("Remote.Battery", level) : Loc.T("Remote.BatteryUnknown");

    public string StartStopText => Loc.T(_session.IsRunning ? "Remote.Stop" : "Remote.Start");

    /// <param name="runBound">Runs what is assigned to a remote input (key, software, combo, macro).</param>
    public RemoteViewModel(RemoteSession session, Action<string> log, Action<string> runBound)
    {
        _session = session;
        _log = log;
        _runBound = runBound;
        _session.Event += ev => _ui.InvokeAsync(() => OnEvent(ev), DispatcherPriority.Render);
        _session.Gesture += g => _ui.InvokeAsync(() => OnGesture(g), DispatcherPriority.Render);
        _session.Status += s => _ui.InvokeAsync(() => OnStatus(s));
        _session.Battery += level => _ui.InvokeAsync(() => BatteryLevel = level);

        _trailTimer.Tick += (_, _) => FadeTrail();
        _gestureTimer.Tick += (_, _) => ResetGesture();
        _scanTimer.Tick += async (_, _) =>
        {
            var hid = await Task.Run(() =>
            {
                try { return HidEnumerator.ListApple(); }
                catch { return []; }
            });
            // A single remote exposes several HID collections: one line per product.
            var connected = hid.DistinctBy(d => (d.VendorId, d.ProductId))
                .Select(d => $"{(d.IsSiriRemoteGen1 ? "Siri Remote (1st)" : d.Product.Length > 0 ? d.Product : $"HID {d.VendorId:X4}:{d.ProductId:X4}")}   {Loc.T("Remote.Connected")}");
            var nearby = _scanner.Snapshot().Where(d => d.AppleHid)
                .Select(d => $"Siri Remote (1st)   {d.AddressText}   {d.Rssi} dBm");
            NearbyDevices = connected.Concat(nearby).ToList();
        };
    }

    public void Start()
    {
        StartSession();
        _scanner.Start();
        _scanTimer.Start();
    }

    public void Dispose()
    {
        _scanTimer.Stop();
        _scanner.Dispose();
    }

    /// <summary>The language changed: texts computed here are re-read.</summary>
    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    public void StartSession()
    {
        _session.Start();
        RefreshButtons();
    }

    public void StopSession()
    {
        _session.Stop();
        RefreshButtons();
    }

    // ---- remote events ---------------------------------------------------

    void OnEvent(RemoteEvent ev)
    {
        switch (ev)
        {
            case TouchEvent t:
                ShowTouch(t);
                break;
            case ButtonsEvent b:
                ShowButtons(b.Mask);
                foreach (var name in b.Pressed) _runBound(name);
                if (b.Pressed.Count > 0) _log("▼ " + string.Join(" + ", b.Pressed));
                if (b.Released.Count > 0) _log("▲ " + string.Join(" + ", b.Released));
                break;
            case VoiceChunkEvent:
                break; // audio: not decodable over HID, ignored
        }
    }

    // Masks of byte 2 of the 0xFC report; the touch-surface click only tints it.
    void ShowButtons(int mask)
    {
        MenuPressed = (mask & 0x20) != 0;
        TvPressed = (mask & 0x01) != 0;
        SiriPressed = (mask & 0x10) != 0;
        PlayPressed = (mask & 0x08) != 0;
        VolUpPressed = (mask & 0x02) != 0;
        VolDownPressed = (mask & 0x04) != 0;
        TouchPressed = (mask & 0x80) != 0;
    }

    void ShowTouch(TouchEvent t)
    {
        bool anyDown = false;
        for (int i = 0; i < Fingers.Length; i++)
        {
            bool visible = i < t.Fingers.Count && t.Fingers[i].State is >= 3 and <= 6;
            Fingers[i].IsVisible = visible;
            if (!visible) continue;
            anyDown = true;
            var f = t.Fingers[i];
            Fingers[i].Left = PadX(f.X) - DotSize / 2;
            Fingers[i].Top = PadY(f.Y) - DotSize / 2;
        }

        if (anyDown)
        {
            if (!_wasDown) _trail.Clear();
            _trail.Add((new Point(PadX(t.X), PadY(t.Y)), Environment.TickCount64));
            TrailPoints = new PointCollection(_trail.Select(p => p.Point));
            _trailTimer.Start();
        }
        _wasDown = anyDown;
    }

    /// <summary>Trail: each point fades <see cref="TrailLifeMs"/> after being drawn, so the tail shortens by itself.</summary>
    void FadeTrail()
    {
        long limit = Environment.TickCount64 - TrailLifeMs;
        int expired = _trail.FindIndex(p => p.Time > limit);
        if (expired < 0) expired = _trail.Count;
        if (expired == 0) return;
        _trail.RemoveRange(0, expired);
        TrailPoints = new PointCollection(_trail.Select(p => p.Point));
        if (_trail.Count == 0) _trailTimer.Stop();
    }

    static double PadX(int x) => Math.Clamp((x - XMin) / Span, 0, 1) * PadSize;

    static double PadY(int y) => (1 - Math.Clamp((y - YMin) / Span, 0, 1)) * PadSize;

    /// <summary>Gesture card back to idle: arrow, text and tilt shift.</summary>
    void ResetGesture()
    {
        _gestureTimer.Stop();
        MoveArrow = "·";
        GestureText = "—";
        TileShiftX = TileShiftY = 0;
    }

    void OnGesture(GestureEvent g)
    {
        _gestureTimer.Stop();
        _gestureTimer.Start();   // the card fades with the trail: idle TrailLifeMs after the last gesture
        switch (g)
        {
            case TiltGesture tilt:
                TileShiftX = tilt.X * 18;
                TileShiftY = -tilt.Y * 18;
                break;
            case MoveGesture m:
                _runBound(m.Dir.ToString().ToLowerInvariant());
                MoveArrow = m.Dir switch
                {
                    Direction.Up => "↑",
                    Direction.Down => "↓",
                    Direction.Left => "←",
                    _ => "→",
                };
                string src = Loc.T(m.Src == MoveSource.Flick ? "Src.Flick" : "Src.Glide");
                string dir = Loc.T("Dir." + m.Dir);
                GestureText = $"{dir} ({src})";
                _log(Loc.T("Log.Focus", dir, src));
                break;
            case TouchGesture { Down: true }:
                TileShiftX = TileShiftY = 0;
                break;
        }
    }

    void OnStatus(string message)
    {
        // Battery is optional: its failure is logged but must not hide the connection status.
        if (Loc.StartsWith(message, "Status.BatteryUnreadable")) { _log(message); return; }
        StatusText = message;
        bool ok = new[] { "Status.ConnectedHid", "Status.ConnectedDirect" }.Any(k => Loc.StartsWith(message, k));
        bool warn = new[] { "Status.WaitHid", "Status.WaitBonded", "Status.WaitPairing", "Status.Unreachable", "Status.Disconnected", "Status.DisconnectedReason" }
            .Any(k => Loc.StartsWith(message, k));
        // "Stopped" only if the session is really stopped: an unknown message keeps the current level.
        StatusLevel = ok ? StatusLevel.Ok : warn ? StatusLevel.Warn : _session.IsRunning ? StatusLevel : StatusLevel.Idle;
        _log(message);
    }

    // ---- commands -----------------------------------------------------------------------

    [RelayCommand]
    void StartStop()
    {
        if (_session.IsRunning) StopSession();
        else StartSession();
    }

    void RefreshButtons()
    {
        OnPropertyChanged(nameof(StartStopText));
        if (!_session.IsRunning)
        {
            foreach (var f in Fingers) f.IsVisible = false;
            ShowButtons(0);
            ResetGesture();
        }
    }

    // Double-click on a row of the nearby list: only the rows that are not connected start pairing.
    [RelayCommand]
    void PairDevice(string line)
    {
        if (!line.EndsWith(Loc.T("Remote.Connected"))) Pair();
    }

    [RelayCommand]
    void Pair()
    {
        if (_session.UsesBluetoothDirect)
        {
            // Direct Bluetooth: forget the bond and restart; the session then looks for a remote in pairing mode.
            _session.Stop();
            _session.ForgetPairing();
            StartSession();
            _log(Loc.T("Log.PairDirect"));
            return;
        }

        _log(Loc.T("Log.PairSettings"));
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { _log(Loc.T("Log.OpenBtFailed", ex.Message)); }
    }
}
