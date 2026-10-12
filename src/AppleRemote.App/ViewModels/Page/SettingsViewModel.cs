// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AppleRemote.App.ViewModels;

/// <summary>Key assigned to a remote input, editable in a combobox.</summary>
public sealed partial class KeyChoice(string input, string key, Action<string, string> changed) : ObservableObject
{
    public string Input { get; } = input;
    [ObservableProperty] string _key = key;
    partial void OnKeyChanged(string value) => changed(Input, value);
}

/// <summary>Settings page: what each input does, adapter drivers, preferences, import / export.
/// Also runs the actions assigned to the inputs (<see cref="RunBound"/>).</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    readonly Settings _settings;
    readonly RemoteSession _session;
    readonly IDialogs _dialogs;
    readonly Action<string> _log;
    CancellationTokenSource? _macroCts;
    bool _syncingBindings;

    /// <summary>Live remote, for the drawing behind the key pickers.</summary>
    public RemoteViewModel Remote { get; }

    public string AppVersion { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    static readonly string NoKey = Loc.T("NoKey");
    public IReadOnlyList<string> KeyChoices { get; } = [NoKey, .. Keys.Names.Keys.Where(n => n != "Esc")];
    public Dictionary<string, KeyChoice> Bindings { get; } = new();
    /// <summary>Paths of the software offered in the Software menu: detected + added by Browse.</summary>
    public ObservableCollection<string> SoftwareChoices { get; } = new();
    /// <summary>Names of the saved macros (one file each).</summary>
    public ObservableCollection<string> MacroChoices { get; } = new(Macro.Names());

    [ObservableProperty] bool _keyboardEnabled;

    partial void OnKeyboardEnabledChanged(bool value)
    {
        _session.KeyboardEnabled = value;
        _settings.KeyboardEnabled = value;
        _settings.Save();
        _log(Loc.T(value ? "Log.KeyboardOn" : "Log.KeyboardOff"));
    }

    [ObservableProperty] bool _closeToTray;

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        _settings.Save();
    }

    public bool CloseQuits => !CloseToTray;

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
        Remote.RefreshTexts();
    }

    public SettingsViewModel(Settings settings, RemoteSession session, IDialogs dialogs, Action<string> log, RemoteViewModel remote)
    {
        _settings = settings;
        _session = session;
        _dialogs = dialogs;
        _log = log;
        Remote = remote;

        Macro.Renamed += OnMacroRenamed;
        foreach (var p in SoftwareApps.Detect().Concat(_settings.Apps).Where(File.Exists))
            if (!SoftwareChoices.Contains(p, StringComparer.OrdinalIgnoreCase)) SoftwareChoices.Add(p);
        foreach (var input in KeyMap.Inputs) Bindings[input] = new KeyChoice(input, NoKey, OnBindingChanged);
        SyncBindings();

        _keyboardEnabled = _settings.KeyboardEnabled;
        _language = _settings.Language;
        _closeToTray = _settings.CloseToTray;
        _session.KeyboardEnabled = _settings.KeyboardEnabled;
        _session.AdapterId = _settings.Adapter;
        _ = RefreshDriver();
    }

    public void Dispose() => Macro.Renamed -= OnMacroRenamed;

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
        SaveMap(map);
    }

    void SaveMap(KeyMap map)
    {
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "keymap.json");
            File.WriteAllText(file, map.ToJson());
            _settings.KeyMapPath = file;
            _settings.Save();
        }
        catch (Exception e) { _log(Loc.T("Log.MapNotSaved", e.Message)); }
    }

    /// <summary>Launches the software, combination or macro assigned to the input, if any and if the keyboard is enabled.</summary>
    public void RunBound(string input)
    {
        if (!KeyboardEnabled) return;
        if (_settings.Software.TryGetValue(input, out var app)) SoftwareApps.Launch(app);
        else if (_settings.Combos.TryGetValue(input, out var combo)) KeyCombos.Run(_session.Injector, combo);
        else if (_settings.Macros.TryGetValue(input, out var macroName)) _ = RunMacro(macroName);
    }

    /// <summary>A macro was renamed (new name) or deleted (null): the buttons using it follow.</summary>
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

    /// <summary>Runs the macro; a press while a macro runs stops it (a loop may never end).</summary>
    async Task RunMacro(string name)
    {
        if (_macroCts is { } running)
        {
            running.Cancel();
            _log(Loc.T("Log.MacroStopped", name));
            return;
        }
        if (Macro.Load(name) is not { } macro)
        {
            _log(Loc.T("Log.MacroMissing", name));
            return;
        }
        using var cts = _macroCts = new CancellationTokenSource();
        try { await macro.RunAsync(_session.Injector, cts.Token); }
        finally { _macroCts = null; }
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
            Adapters = await Task.Run(() => WinUsbDriver.List());
            // The saved adapter, otherwise the one the session would open (first under WinUSB), otherwise the first one.
            // Not saved: a refresh must not overwrite the saved choice (adapter unplugged for now).
            _refreshingAdapters = true;
            SelectedAdapter = Adapters.FirstOrDefault(a => a.InstanceId.Equals(_settings.Adapter, StringComparison.OrdinalIgnoreCase))
                ?? Adapters.FirstOrDefault(a => a.Driver == AdapterDriver.WinUsb) ?? Adapters.FirstOrDefault();
            _refreshingAdapters = false;
            ShowAdapterState();
        }
        catch (Exception ex) { DriverText = Loc.T("Driver.Unknown", ex.Message); }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SeveralAdapters))] IReadOnlyList<WinUsbDriver.Adapter> _adapters = [];
    public bool SeveralAdapters => Adapters.Count > 1; // a single adapter needs no choice
    [ObservableProperty] WinUsbDriver.Adapter? _selectedAdapter;
    bool _refreshingAdapters;

    partial void OnSelectedAdapterChanged(WinUsbDriver.Adapter? value)
    {
        // null: the ComboBox clears its selection when the list is replaced
        if (value is null || _refreshingAdapters) return;
        _settings.Adapter = _session.AdapterId = value.InstanceId;
        _settings.Save();
        ShowAdapterState();
    }

    void ShowAdapterState()
    {
        AdapterPresent = SelectedAdapter is not null;
        WinUsbOn = SelectedAdapter?.Driver == AdapterDriver.WinUsb;
        DriverText = Loc.T(SelectedAdapter is null ? "Driver.Absent" : WinUsbOn ? "Driver.WinUsb" : "Driver.Original");
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
            if (_session.IsRunning) Remote.StopSession(); // releases the adapter
            var (code, error) = await DriverCommand.RunElevatedAsync(verb, SelectedAdapter?.InstanceId);
            if (code is DriverCommand.Ok or DriverCommand.RebootNeeded)
            {
                _log(Loc.T(code == DriverCommand.RebootNeeded
                    ? "Log.DriverReboot"
                    : verb switch
                    {
                        "install" => "Log.DriverInstalled",
                        "filter-install" => "Log.FilterInstalled",
                        "filter-uninstall" => "Log.FilterRemoved",
                        _ => "Log.DriverRestored",
                    }));
                if (code == DriverCommand.Ok) Remote.StartSession(); // WinUSB -> direct Bluetooth, otherwise Windows' HID
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
    void ShowComponents() => _dialogs.ShowComponents();

    // ---- mapping import / export ----------------------------------------------------

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
        _log(Loc.T("Log.DefaultMap"));
    }

    /// <summary>Whole mapping (keys, software, combos, macros) in one JSON file.</summary>
    [RelayCommand]
    void ExportMap()
    {
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = "mapping.json" };
        if (dlg.ShowDialog() != true) return;
        var o = new JsonObject
        {
            ["keys"] = JsonNode.Parse(_session.KeyMap.ToJson()),
            ["software"] = JsonSerializer.SerializeToNode(_settings.Software),
            ["combos"] = JsonSerializer.SerializeToNode(_settings.Combos),
            ["macros"] = JsonSerializer.SerializeToNode(_settings.Macros),
        };
        try
        {
            File.WriteAllText(dlg.FileName, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _log(Loc.T("Log.MapExported", dlg.FileName));
        }
        catch (Exception e) { _log(Loc.T("Log.MapNotSaved", e.Message)); }
    }

    [RelayCommand]
    void ImportMap()
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        KeyMap map;
        Dictionary<string, string> software, combos, macros;
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(dlg.FileName))!.AsObject();
            map = KeyMap.FromJson(o["keys"]?.ToJsonString() ?? "{}");
            software = o["software"]?.Deserialize<Dictionary<string, string>>() ?? new();
            combos = o["combos"]?.Deserialize<Dictionary<string, string>>() ?? new();
            macros = o["macros"]?.Deserialize<Dictionary<string, string>>() ?? new();
        }
        catch (Exception e)
        {
            _log(Loc.T("Log.MapIgnored", e.Message));
            return;
        }
        ApplyMap(map, software, combos, macros);
        _log(Loc.T("Log.MapImported", dlg.FileName));
    }

    void ApplyMap(KeyMap map, Dictionary<string, string> software, Dictionary<string, string> combos, Dictionary<string, string> macros)
    {
        _session.KeyMap = map;
        _settings.Software = software;
        _settings.Combos = combos;
        _settings.Macros = macros;
        foreach (var app in software.Values.Where(a => !SoftwareChoices.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            SoftwareChoices.Add(app);
            _settings.Apps.Add(app);
        }
        foreach (var m in macros.Values.Where(m => !MacroChoices.Contains(m))) MacroChoices.Add(m);
        SyncBindings();
        SaveMap(map);
    }

    /// <summary>Settings, mapping and macros in one zip. Pairing keys (bonds.json) stay out: they belong to this PC's adapter.</summary>
    [RelayCommand]
    void ExportAll()
    {
        var dlg = new SaveFileDialog { Filter = "Zip|*.zip", FileName = "AppleRemote.zip" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _settings.Save();
            using var zip = new ZipArchive(File.Create(dlg.FileName), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(Settings.FilePath, "settings.json");
            using (var w = new StreamWriter(zip.CreateEntry("keymap.json").Open())) w.Write(_session.KeyMap.ToJson());
            if (Directory.Exists(Macro.Dir))
                foreach (var f in Directory.GetFiles(Macro.Dir, "*.json")) zip.CreateEntryFromFile(f, "macros/" + Path.GetFileName(f));
            _log(Loc.T("Log.MapExported", dlg.FileName));
        }
        catch (Exception e) { _log(Loc.T("Log.MapNotSaved", e.Message)); }
    }

    [RelayCommand]
    void ImportAll()
    {
        var dlg = new OpenFileDialog { Filter = "Zip|*.zip" };
        if (dlg.ShowDialog() != true) return;
        Settings s;
        KeyMap map;
        try
        {
            using var zip = ZipFile.OpenRead(dlg.FileName);
            string Read(string name)
            {
                using var r = new StreamReader((zip.GetEntry(name) ?? throw new InvalidDataException(name + " missing")).Open());
                return r.ReadToEnd();
            }
            s = JsonSerializer.Deserialize<Settings>(Read("settings.json")) ?? throw new InvalidDataException("settings.json");
            map = KeyMap.FromJson(Read("keymap.json"));
            Directory.CreateDirectory(Macro.Dir);
            // File name only: an entry can never land outside the macros folder.
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("macros/") && e.Name.EndsWith(".json")))
                e.ExtractToFile(Path.Combine(Macro.Dir, e.Name), overwrite: true);
        }
        catch (Exception e)
        {
            _log(Loc.T("Log.MapIgnored", e.Message));
            return;
        }
        foreach (var app in s.Apps.Where(File.Exists))
            if (!SoftwareChoices.Contains(app, StringComparer.OrdinalIgnoreCase)) { SoftwareChoices.Add(app); _settings.Apps.Add(app); }
        foreach (var m in Macro.Names().Where(m => !MacroChoices.Contains(m))) MacroChoices.Add(m);
        ApplyMap(map, s.Software, s.Combos, s.Macros);
        KeyboardEnabled = s.KeyboardEnabled;
        CloseToTray = s.CloseToTray;
        Language = s.Language;
        _log(Loc.T("Log.MapImported", dlg.FileName));
    }
}
