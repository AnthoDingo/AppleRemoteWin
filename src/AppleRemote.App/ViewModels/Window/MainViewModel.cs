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

/// <summary>Un doigt sur la surface tactile, en pixels du carré de 360 px (coin haut-gauche du point).</summary>
public sealed partial class FingerViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    [ObservableProperty] bool _isVisible;
    [ObservableProperty] double _left;
    [ObservableProperty] double _top;
}

/// <summary>Touche affectée à une entrée de la télécommande, éditable dans une combobox.</summary>
public sealed partial class KeyChoice(string input, string key, Action<string, string> changed) : ObservableObject
{
    public string Input { get; } = input;
    [ObservableProperty] string _key = key;
    partial void OnKeyChanged(string value) => changed(Input, value);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // Plage tactile observée (≈ 1650–1800 unités par axe, non centrée sur 0) -> carré de 360 px.
    const double PadSize = 360, XMin = -1900, YMin = -1150, Span = 1800, DotSize = 30;

    readonly Settings _settings = Settings.Load();
    readonly RemoteSession _session;
    readonly IDialogs _dialogs;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<Point> _trail = new();
    readonly BleScanner _scanner = new();
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Appareils BLE vus en publicité avec fabricant Apple + service HID (« Apple HID »).</summary>
    [ObservableProperty] IReadOnlyList<string> _nearbyDevices = [];
    bool _wasDown;

    static readonly string NoKey = Loc.T("NoKey");
    public IReadOnlyList<string> KeyChoices { get; } = [NoKey, .. Keys.Names.Keys.Where(n => n != "Esc")];
    public Dictionary<string, KeyChoice> Bindings { get; } = new();
    /// <summary>Chemins des logiciels proposés dans le menu Software : détectés + ajoutés par Browse.</summary>
    public ObservableCollection<string> SoftwareChoices { get; } = new();
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

    /// <summary>Lancement à l'ouverture de session Windows (clé Run de l'utilisateur ; l'état vit dans le registre).</summary>
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

    /// <summary>Appliquée en direct : textes XAML (<see cref="LocSource"/>) et propriétés du modèle réévalués.</summary>
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
    int? _batteryLevel;

    public string BatteryPercent => BatteryLevel is { } level ? $"{level} %" : "—";

    /// <summary>Dernier niveau connu (Bluetooth direct) ; « — » tant qu'aucune télécommande ne l'a donné.</summary>
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

        _session = new RemoteSession(map);
        _session.Event += ev => _ui.InvokeAsync(() => OnEvent(ev), DispatcherPriority.Render);
        _session.Gesture += g => _ui.InvokeAsync(() => OnGesture(g), DispatcherPriority.Render);
        _session.Status += s => _ui.InvokeAsync(() => OnStatus(s));
        _session.Battery += level => _ui.InvokeAsync(() => BatteryLevel = level);
        foreach (var p in SoftwareApps.Detect().Concat(_settings.Apps).Where(File.Exists))
            if (!SoftwareChoices.Contains(p, StringComparer.OrdinalIgnoreCase)) SoftwareChoices.Add(p);
        foreach (var input in KeyMap.Inputs) Bindings[input] = new KeyChoice(input, NoKey, OnBindingChanged);
        SyncBindings();

        _scanTimer.Tick += (_, _) =>
            NearbyDevices = _scanner.Snapshot().Where(d => d.AppleHid)
                .Select(d => $"Apple Siri Remote (1st)   {d.AddressText}   {d.Rssi} dBm").ToList();
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
    }

    public void Dispose()
    {
        _scanTimer.Stop();
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

    // ---- affectation des touches ---------------------------------------------------------

    void SyncBindings()
    {
        _syncingBindings = true;
        foreach (var (input, choice) in Bindings)
            choice.Key = _settings.Software.TryGetValue(input, out var app) ? SoftwareApps.Prefix + app
                : _settings.Combos.TryGetValue(input, out var combo) ? combo
                : _session.KeyMap.TryGet(input, out var k) ? Keys.NameOf(k) ?? NoKey : NoKey;
        _syncingBindings = false;
    }

    void OnBindingChanged(string input, string name)
    {
        if (_syncingBindings) return;
        var map = _session.KeyMap;
        _settings.Combos.Remove(input);
        if (name.StartsWith(KeyCombos.Prefix))
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
        _session.KeyMap = map; // relâche les touches tenues
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "keymap.json");
            File.WriteAllText(file, map.ToJson());
            _settings.KeyMapPath = file;
            _settings.Save();
        }
        catch (Exception e) { AddLog(Loc.T("Log.MapNotSaved", e.Message)); }
    }

    // ---- événements de la télécommande ---------------------------------------------------

    /// <summary>Lance le logiciel ou la combinaison affectés à l'entrée, s'il y en a un et si le clavier est activé.</summary>
    void RunBound(string input)
    {
        if (!KeyboardEnabled) return;
        if (_settings.Software.TryGetValue(input, out var app)) SoftwareApps.Launch(app);
        else if (_settings.Combos.TryGetValue(input, out var combo)) KeyCombos.Run(_session.Injector, combo);
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
                break; // audio : non décodable via HID, on l'ignore
        }
    }

    // Masques de l'octet 2 du rapport 0xFC ; le clic de la surface tactile ne fait que la teinter.
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
            _trail.Add(new Point(PadX(t.X), PadY(t.Y)));
            if (_trail.Count > 80) _trail.RemoveAt(0);
            TrailPoints = new PointCollection(_trail);
        }
        _wasDown = anyDown;
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
        StatusLevel = ok ? StatusLevel.Ok : warn ? StatusLevel.Warn : StatusLevel.Idle;
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

    [RelayCommand]
    void Pair()
    {
        if (_session.UsesBluetoothDirect)
        {
            // Bluetooth direct : on oublie l'appairage et on relance ; la session cherche alors une télécommande en mode appairage.
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

    // ---- pilote de l'adaptateur (équivalent de Zadig) --------------------------------------

    [ObservableProperty] string _driverText = Loc.T("Driver.Checking");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DriverIdle))] bool _driverBusy;
    public bool DriverIdle => !DriverBusy;
    [ObservableProperty] string _filterText = Loc.T("Driver.Checking");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WinUsbButton)), NotifyPropertyChangedFor(nameof(CanToggleFilter)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _winUsbOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(CanToggleFilter))] bool _adapterPresent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilterButton)), NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _filterOn;
    public bool AnyOn => WinUsbOn || FilterOn;
    public bool CanToggleWinUsb => !FilterOn && AdapterPresent; // exclusifs : un seul des deux à la fois
    public bool CanToggleFilter => !WinUsbOn && (AdapterPresent || FilterOn); // sans adaptateur, seul le retrait reste possible
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
            if (_session.IsRunning) { _session.Stop(); RefreshButtons(); } // libère l'adaptateur
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
        // Le pilote attend la télécommande et la déverrouille seul dès que Windows expose sa collection HID :
        // il suffit que la session tourne.
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
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 300) Log.RemoveAt(0);
    }
}
