// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

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

/// <summary>Key assigned to a remote input, editable in a combobox.</summary>
public sealed partial class KeyChoice(string input, string key, Action<string, string> changed) : ObservableObject
{
    public string Input { get; } = input;
    [ObservableProperty] string _key = key;
    partial void OnKeyChanged(string value) => changed(Input, value);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // Observed touch range (≈ 1650–1800 units per axis, not centred on 0) -> 360 px square.
    const double PadSize = 360, XMin = -1900, YMin = -1150, Span = 1800, DotSize = 30;

    readonly Settings _settings = Settings.Load();
    UpdateService? _updates;
    readonly RemoteSession _session;
    readonly IDialogs _dialogs;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    const long TrailLifeMs = 400;
    readonly List<(Point Point, long Time)> _trail = new();
    readonly BleScanner _scanner = new();
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _trailTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    /// <summary>BLE devices seen advertising with Apple manufacturer data + HID service ("Apple HID").</summary>
    [ObservableProperty] IReadOnlyList<string> _nearbyDevices = [];
    public string AppVersion { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    bool _wasDown;

    static readonly string NoKey = Loc.T("NoKey");
    public IReadOnlyList<string> KeyChoices { get; } = [NoKey, .. Keys.Names.Keys.Where(n => n != "Esc")];
    public Dictionary<string, KeyChoice> Bindings { get; } = new();
    /// <summary>Chemins des logiciels proposés dans le menu Software : détectés + ajoutés par Browse.</summary>
    public ObservableCollection<string> SoftwareChoices { get; } = new();
    /// <summary>Noms des macros enregistrées (un fichier chacune).</summary>
    public ObservableCollection<string> MacroChoices { get; } = new(Macro.Names());
    CancellationTokenSource? _macroCts;
    bool _syncingBindings;

    public ObservableCollection<string> Log { get; } = new();
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

    [ObservableProperty] bool _keyboardEnabled;

    [ObservableProperty] bool _closeToTray;

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Launch at Windows sign-in (user's Run key; the state lives in the registry).</summary>
    public bool RunAtStartup
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("AppleRemote") is not null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("AppleRemote", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("AppleRemote", false);
            OnPropertyChanged();
        }
    }

    public bool CloseQuits => !CloseToTray;

    public sealed record LanguageChoice(string Code, string Name);
    public IReadOnlyList<LanguageChoice> Languages { get; } =
        [new("auto", Loc.T("Language.Auto")), new("fr", Loc.T("Language.Fr")), new("en", Loc.T("Language.En"))];

    /// <summary>Applied live: XAML texts (<see cref="LocSource"/>) and model properties are re-evaluated.</summary>
    [ObservableProperty] string _language = "auto";

    partial void OnLanguageChanged(string value)
    {
        _settings.Language = value;
        _settings.Save();
        Loc.SetLanguage(value);
        LocSource.Instance.Refresh();
        OnPropertyChanged(string.Empty);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    [NotifyPropertyChangedFor(nameof(BatteryPercent))]
    [NotifyPropertyChangedFor(nameof(BatteryGlyph))]
    int? _batteryLevel;

    /// <summary>Segoe MDL2 battery glyph in steps of 10 % (Battery0 = E850 … Battery9 = E858, Battery10 = E83F); E996 = unknown.</summary>
    public string BatteryGlyph => BatteryLevel is { } level
        ? char.ConvertFromUtf32(Math.Clamp((level + 5) / 10, 0, 10) switch { 10 => 0xE83F, var step => 0xE850 + step })
        : "";

    public string BatteryPercent => BatteryLevel is { } level ? $"{level} %" : "—";

    /// <summary>Last known level (direct Bluetooth); "—" until a remote has reported one.</summary>
    public string BatteryText => BatteryLevel is { } level ? Loc.T("Remote.Battery", level) : Loc.T("Remote.BatteryUnknown");

    public string StartStopText => Loc.T(_session.IsRunning ? "Remote.Stop" : "Remote.Start");

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;

        KeyMap map = KeyMap.Default();
        if (_settings.KeyMapPath is { Length: > 0 } path && File.Exists(path))
        {
            try { map = KeyMap.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { AddLog(Loc.T("Log.MapIgnored", e.Message)); }
        }

        Macro.Renamed += OnMacroRenamed;
        _session = new RemoteSession(map);
        _session.Event += ev => _ui.InvokeAsync(() => OnEvent(ev), DispatcherPriority.Render);
        _session.Gesture += g => _ui.InvokeAsync(() => OnGesture(g), DispatcherPriority.Render);
        _session.Status += s => _ui.InvokeAsync(() => OnStatus(s));
        _session.Battery += level => _ui.InvokeAsync(() => BatteryLevel = level);
        foreach (var p in SoftwareApps.Detect().Concat(_settings.Apps).Where(File.Exists))
            if (!SoftwareChoices.Contains(p, StringComparer.OrdinalIgnoreCase)) SoftwareChoices.Add(p);
        foreach (var input in KeyMap.Inputs) Bindings[input] = new KeyChoice(input, NoKey, OnBindingChanged);
        SyncBindings();

        _trailTimer.Tick += (_, _) => FadeTrail();
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
        _keyboardEnabled = _settings.KeyboardEnabled;
        _language = _settings.Language;
        _closeToTray = _settings.CloseToTray;
        _session.KeyboardEnabled = _settings.KeyboardEnabled;
        _ = RefreshDriver();
    }

    // ---- cycle de vie --------------------------------------------------------------------

    public void Start()
    {
        _session.Start();
        RefreshButtons();
        _scanner.Start();
        _scanTimer.Start();
        (_updates = new UpdateService(_settings, s => _ui.InvokeAsync(() => AddLog(s)))).Start();
    }

    public void Dispose()
    {
        Macro.Renamed -= OnMacroRenamed;
        _scanTimer.Stop();
        _updates?.Dispose();
        _scanner.Dispose();
        _settings.Save();
        _session.Dispose();
    }

    partial void OnKeyboardEnabledChanged(bool value)
    {
        _session.KeyboardEnabled = value;
        _settings.KeyboardEnabled = value;
        _settings.Save();
        AddLog(Loc.T(value ? "Log.KeyboardOn" : "Log.KeyboardOff"));
    }

    // ---- key assignment ---------------------------------------------------------

    void SyncBindings()
    {
        _syncingBindings = true;
        foreach (var (input, choice) in Bindings)
            choice.Key = _settings.Software.TryGetValue(input, out var app) ? SoftwareApps.Prefix + app
                : _settings.Combos.TryGetValue(input, out var combo) ? combo
                : _settings.Macros.TryGetValue(input, out var macro) ? Macro.Prefix + macro
                : _session.KeyMap.TryGet(input, out var k) ? Keys.NameOf(k) ?? NoKey : NoKey;
        _syncingBindings = false;
    }

    void OnBindingChanged(string input, string name)
    {
        if (_syncingBindings) return;
        var map = _session.KeyMap;
        _settings.Combos.Remove(input);
        _settings.Macros.Remove(input);
        if (name.StartsWith(Macro.Prefix))
        {
            string macroName = name[Macro.Prefix.Length..];
            _settings.Software.Remove(input);
            _settings.Macros[input] = macroName;
            if (!MacroChoices.Contains(macroName)) MacroChoices.Add(macroName);
            map.Set(input, null);
        }
        else if (name.StartsWith(KeyCombos.Prefix))
        {
            _settings.Software.Remove(input);
            _settings.Combos[input] = name;
            map.Set(input, null);
        }
        else if (name.StartsWith(SoftwareApps.Prefix))
        {
            string app = name[SoftwareApps.Prefix.Length..];
            _settings.Software[input] = app;
            if (!SoftwareChoices.Contains(app, StringComparer.OrdinalIgnoreCase))
            {
                SoftwareChoices.Add(app);
                _settings.Apps.Add(app);
            }
            map.Set(input, null);
        }
        else
        {
            _settings.Software.Remove(input);
            map.Set(input, Keys.Names.TryGetValue(name, out var k) ? k : null);
        }
        _session.KeyMap = map; // releases held keys
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "keymap.json");
            File.WriteAllText(file, map.ToJson());
            _settings.KeyMapPath = file;
            _settings.Save();
        }
        catch (Exception e) { AddLog(Loc.T("Log.MapNotSaved", e.Message)); }
    }

    // ---- remote events ---------------------------------------------------

    /// <summary>Lance le logiciel ou la combinaison affectés à l'entrée, s'il y en a un et si le clavier est activé.</summary>
    void RunBound(string input)
    {
        if (!KeyboardEnabled) return;
        if (_settings.Software.TryGetValue(input, out var app)) SoftwareApps.Launch(app);
        else if (_settings.Combos.TryGetValue(input, out var combo)) KeyCombos.Run(_session.Injector, combo);
        else if (_settings.Macros.TryGetValue(input, out var macroName)) _ = RunMacro(macroName);
    }

    /// <summary>Une macro a été renommée (nouveau nom) ou supprimée (null) : les boutons qui l'utilisent suivent.</summary>
    void OnMacroRenamed(string old, string? @new)
    {
        foreach (var input in _settings.Macros.Where(p => p.Value.Equals(old, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
            if (@new is null) _settings.Macros.Remove(input);
            else _settings.Macros[input] = @new;
        MacroChoices.Remove(old);
        if (@new is not null && !MacroChoices.Contains(@new)) MacroChoices.Add(@new);
        _settings.Save();
        SyncBindings();
    }

    /// <summary>Exécute la macro ; un appui pendant qu'une macro tourne l'arrête (une boucle peut ne jamais finir).</summary>
    async Task RunMacro(string name)
    {
        if (_macroCts is { } running)
        {
            running.Cancel();
            AddLog(Loc.T("Log.MacroStopped", name));
            return;
        }
        if (Macro.Load(name) is not { } macro)
        {
            AddLog(Loc.T("Log.MacroMissing", name));
            return;
        }
        using var cts = _macroCts = new CancellationTokenSource();
        try { await macro.RunAsync(_session.Injector, cts.Token); }
        finally { _macroCts = null; }
    }

    void OnEvent(RemoteEvent ev)
    {
        switch (ev)
        {
            case TouchEvent t:
                ShowTouch(t);
                break;
            case ButtonsEvent b:
                ShowButtons(b.Mask);
                foreach (var name in b.Pressed) RunBound(name);
                if (b.Pressed.Count > 0) AddLog("▼ " + string.Join(" + ", b.Pressed));
                if (b.Released.Count > 0) AddLog("▲ " + string.Join(" + ", b.Released));
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

    void OnGesture(GestureEvent g)
    {
        switch (g)
        {
            case TiltGesture tilt:
                TileShiftX = tilt.X * 18;
                TileShiftY = -tilt.Y * 18;
                break;
            case MoveGesture m:
                RunBound(m.Dir.ToString().ToLowerInvariant());
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
                AddLog(Loc.T("Log.Focus", dir, src));
                break;
            case TouchGesture { Down: true }:
                TileShiftX = TileShiftY = 0;
                break;
        }
    }

    void OnStatus(string message)
    {
        StatusText = message;
        bool ok = new[] { "Status.ConnectedHid", "Status.ConnectedDirect" }.Any(k => Loc.StartsWith(message, k));
        bool warn = new[] { "Status.WaitHid", "Status.WaitBonded", "Status.WaitPairing", "Status.Unreachable", "Status.Disconnected", "Status.DisconnectedReason" }
            .Any(k => Loc.StartsWith(message, k));
        // "Stopped" only if the session is really stopped: an unknown message keeps the current level.
        StatusLevel = ok ? StatusLevel.Ok : warn ? StatusLevel.Warn : _session.IsRunning ? StatusLevel : StatusLevel.Idle;
        AddLog(message);
    }

    // ---- commandes -----------------------------------------------------------------------

    [RelayCommand]
    void StartStop()
    {
        if (_session.IsRunning) _session.Stop();
        else _session.Start();
        RefreshButtons();
    }

    void RefreshButtons()
    {
        OnPropertyChanged(nameof(StartStopText));
        if (!_session.IsRunning)
        {
            foreach (var f in Fingers) f.IsVisible = false;
            ShowButtons(0);
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
            _session.Start();
            RefreshButtons();
            AddLog(Loc.T("Log.PairDirect"));
            return;
        }

        AddLog(Loc.T("Log.PairSettings"));
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { AddLog(Loc.T("Log.OpenBtFailed", ex.Message)); }
    }

    // ---- adapter driver (Zadig equivalent) --------------------------------------

    [ObservableProperty] string _driverText = Loc.T("Driver.Checking");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DriverIdle))] bool _driverBusy;
    public bool DriverIdle => !DriverBusy;
    [ObservableProperty] string _filterText = Loc.T("Driver.Checking");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WinUsbButton)), NotifyPropertyChangedFor(nameof(CanToggleFilter)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _winUsbOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(CanToggleFilter))] bool _adapterPresent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilterButton)), NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _filterOn;
    public bool AnyOn => WinUsbOn || FilterOn;
    public bool CanToggleWinUsb => !FilterOn && AdapterPresent; // mutually exclusive: only one of the two at a time
    public bool CanToggleFilter => !WinUsbOn && (AdapterPresent || FilterOn); // without an adapter, only removal remains possible
    public string WinUsbButton => Loc.T(WinUsbOn ? "Settings.RestoreDriver" : "Settings.InstallWinUsb");
    public string FilterButton => Loc.T(FilterOn ? "Settings.UninstallFilter" : "Settings.InstallFilter");

    [RelayCommand]
    async Task RefreshDriver()
    {
        try
        {
            FilterOn = await Task.Run(() => FilterInstaller.IsInstalled());
            FilterText = Loc.T(FilterOn ? "Filter.Installed" : "Filter.NotInstalled");
        }
        catch (Exception ex) { FilterText = Loc.T("Driver.Unknown", ex.Message); }
        try
        {
            var state = await Task.Run(() => WinUsbDriver.GetState());
            AdapterPresent = state != AdapterDriver.Absent;
            WinUsbOn = state == AdapterDriver.WinUsb;
            DriverText = state switch
            {
                AdapterDriver.Absent => Loc.T("Driver.Absent"),
                AdapterDriver.WinUsb => Loc.T("Driver.WinUsb"),
                _ => Loc.T("Driver.Original"),
            };
        }
        catch (Exception ex) { DriverText = Loc.T("Driver.Unknown", ex.Message); }
    }

    [RelayCommand]
    Task ToggleWinUsb() => WinUsbOn
        ? ChangeDriver("restore", Loc.T("Driver.RestoreAsk"))
        : ChangeDriver("install", Loc.T("Driver.InstallAsk"));

    [RelayCommand]
    Task ToggleFilter() => FilterOn
        ? ChangeDriver("filter-uninstall", Loc.T("Driver.FilterRemoveAsk"))
        : ChangeDriver("filter-install", Loc.T("Driver.FilterInstallAsk"));

    async Task ChangeDriver(string verb, string question)
    {
        if (DriverBusy || !await _dialogs.Confirm(Loc.T("Driver.DialogTitle"), question)) return;
        DriverBusy = true;
        try
        {
            if (_session.IsRunning) { _session.Stop(); RefreshButtons(); } // releases the adapter
            var (code, error) = await DriverCommand.RunElevatedAsync(verb);
            if (code is DriverCommand.Ok or DriverCommand.RebootNeeded)
            {
                AddLog(Loc.T(code == DriverCommand.RebootNeeded
                    ? "Log.DriverReboot"
                    : verb switch
                    {
                        "install" => "Log.DriverInstalled",
                        "filter-install" => "Log.FilterInstalled",
                        "filter-uninstall" => "Log.FilterRemoved",
                        _ => "Log.DriverRestored",
                    }));
                if (code == DriverCommand.Ok) _session.Start(); // WinUSB -> Bluetooth direct, sinon HID de Windows
                RefreshButtons();
            }
            else await _dialogs.Warn(Loc.T("Driver.DialogTitle"), error ?? Loc.T("Driver.Failed", code));
        }
        finally
        {
            DriverBusy = false;
            await RefreshDriver();
        }
    }

    [RelayCommand]
    async Task ListDevices()
    {
        try
        {
            var devices = await Task.Run(() => HidEnumerator.ListApple());
            if (devices.Count == 0)
            {
                AddLog(Loc.T("Log.NoHid"));
                return;
            }
            AddLog(Loc.T("Log.HidCount", devices.Count));
            foreach (var d in devices) AddLog("  " + d + (d.IsSiriRemoteGen1 ? Loc.T("Log.HidGen1") : ""));
        }
        catch (Exception ex)
        {
            AddLog(Loc.T("Log.EnumFailed", ex.Message));
        }
    }

    [RelayCommand]
    void BleScan() =>
        // The driver waits for the remote and unlocks it by itself as soon as Windows exposes its HID collection:
        // the session just has to be running.
        _dialogs.ShowBleScan(() =>
        {
            AddLog(Loc.T("Log.BlePaired"));
            if (_session.IsRunning) return;
            _session.Start();
            RefreshButtons();
        });

    [RelayCommand]
    void ResetMap()
    {
        _session.KeyMap = KeyMap.Default();
        _settings.Software.Clear();
        _settings.Combos.Clear();
        _settings.Macros.Clear();
        SyncBindings();
        _settings.KeyMapPath = null;
        _settings.Save();
        AddLog(Loc.T("Log.DefaultMap"));
    }

    [RelayCommand]
    void ClearLog() => Log.Clear();

    // ---- journal -------------------------------------------------------------------------

    void AddLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        Log.Add(stamped);
        while (Log.Count > 300) Log.RemoveAt(0);
        // Copy to %TEMP%\AppleRemote.log: readable without the UI (remote diagnosis, VM test session).
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "AppleRemote.log"), stamped + Environment.NewLine); }
        catch (IOException) { }
    }
}
