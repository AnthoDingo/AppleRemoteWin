// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
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

    [ObservableProperty] string _updateStatus = "";
    [ObservableProperty] string _lastChecked = "";
    [ObservableProperty] bool _autoUpdate;
    partial void OnAutoUpdateChanged(bool value) { _settings.AutoUpdate = value; _settings.Save(); }
    [ObservableProperty] bool _notifyUpdates;
    partial void OnNotifyUpdatesChanged(bool value) { _settings.NotifyUpdates = value; _settings.Save(); }
    void ShowLastChecked() => LastChecked = _settings.LastUpdateCheck is { } at ? Loc.T("Settings.LastChecked", at.ToString("G")) : "";
    [ObservableProperty] bool _checkingUpdates;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReleaseUrl))] string? _releaseUrl;
    public bool HasReleaseUrl => ReleaseUrl is not null;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    async Task CheckUpdates()
    {
        CheckingUpdates = true;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AppleRemote-UpdateCheck");
            http.Timeout = TimeSpan.FromMinutes(5);
            var release = await UpdateService.LatestAsync(http);
            ReleaseUrl = release.Url;
            UpdateStatus = Loc.T(release.Newer ? "Settings.NewVersion" : "Settings.UpToDate", release.Tag);
            if (release.Newer && AutoUpdate) await UpdateService.InstallAsync(http, release, _log);
            _settings.LastUpdateCheck = DateTime.Now;
            _settings.Save();
            ShowLastChecked();
            await FetchLatestReleases();
        }
        catch (Exception) { ReleaseUrl = null; UpdateStatus = Loc.T("Settings.UpdateFailed"); }
        finally { CheckingUpdates = false; }
    }
    bool CanCheckUpdates() => !CheckingUpdates;
    partial void OnCheckingUpdatesChanged(bool value) => CheckUpdatesCommand.NotifyCanExecuteChanged();

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
        _notifyUpdates = _settings.NotifyUpdates;
        _autoUpdate = _settings.AutoUpdate;
        UpdateService.Checked += () => System.Windows.Application.Current?.Dispatcher.InvokeAsync(ShowLastChecked);
        _ = FetchLatestReleases();
        ShowLastChecked();
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DriverIdle)), NotifyPropertyChangedFor(nameof(CanChangeAdapter))] bool _driverBusy;
    public bool DriverIdle => !DriverBusy;
    /// <summary>The adapter is chosen with the session stopped: stop, change, start again.</summary>
    public bool CanChangeAdapter => DriverIdle && !_session.IsRunning;
    /// <summary>The session started or stopped (<see cref="RemoteViewModel"/>).</summary>
    public void SessionChanged() => OnPropertyChanged(nameof(CanChangeAdapter));
    [ObservableProperty] string _filterText = Loc.T("Driver.Checking");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WinUsbButton)), NotifyPropertyChangedFor(nameof(CanToggleFilter)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _winUsbOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(CanToggleFilter))] bool _adapterPresent;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilterButton)), NotifyPropertyChangedFor(nameof(FilterInstalled)), NotifyPropertyChangedFor(nameof(FilterStatus)), NotifyPropertyChangedFor(nameof(CanToggleWinUsb)), NotifyPropertyChangedFor(nameof(AnyOn))] bool _filterOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DongleText)), NotifyPropertyChangedFor(nameof(DongleInstalled)), NotifyPropertyChangedFor(nameof(AnyOn)),
     NotifyPropertyChangedFor(nameof(UpdateAvailable)), NotifyCanExecuteChangedFor(nameof(UpdateDongleCommand))] bool _dongleOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DongleText)), NotifyPropertyChangedFor(nameof(DongleInstalled)), NotifyPropertyChangedFor(nameof(UpdateAvailable)),
     NotifyCanExecuteChangedFor(nameof(UpdateDongleCommand))] string? _dongleFirmware;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DongleText)), NotifyPropertyChangedFor(nameof(UpdateAvailable)),
     NotifyCanExecuteChangedFor(nameof(UpdateDongleCommand))] int? _dongleProgress;   // null: no update running
    public bool AnyOn => WinUsbOn || FilterOn || DongleOn;
    public string DongleText =>
        DongleProgress is { } percent ? Loc.T("Settings.DongleUpdating", percent)
        : !DongleOn ? Loc.T("Settings.DongleAbsent")
        : DongleFirmware is { } version ? Loc.T("Settings.DongleFirmware", version)
        : Loc.T("Settings.DonglePresent");
    public bool CanToggleWinUsb => !FilterOn && AdapterPresent; // mutually exclusive: only one of the two at a time
    public bool CanToggleFilter => !WinUsbOn && (AdapterPresent || FilterOn); // without an adapter, only removal remains possible
    public string WinUsbButton => Loc.T(WinUsbOn ? "Settings.RestoreDriver" : "Settings.InstallWinUsb");
    static readonly string Unknown = "—";
    /// <summary>DriverVer of the package shipped in driver\ next to the exe (re-stamped by every build).</summary>
    static readonly string PackageVersion = ReadPackageVersion();
    /// <summary>Latest filter release on GitHub; the package shipped in driver\ (the one the app installs) wins when present.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilterAvailable))] string? _filterLatest;
    public string FilterAvailable => PackageVersion != Unknown ? PackageVersion : FilterLatest ?? Unknown;
    static string ReadPackageVersion()
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "driver", "AppleRemoteDriver.inf")), @"DriverVer\s*=\s*[^,

]+,\s*([^\s;]+)");
            return m.Success ? m.Groups[1].Value : Unknown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Unknown; }
    }
    public string FilterInstalled => !FilterOn ? Unknown : _settings.FilterVersion ?? "?";
    public string FilterStatus => Loc.T(FilterOn ? "Filter.Installed" : "Filter.NotInstalled");
    public string DongleInstalled => DongleOn ? DongleFirmware ?? "?" : Unknown;
    /// <summary>Latest dongle firmware release on GitHub; the shipped image (the one the app can flash) wins when present.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DongleAvailable))] string? _dongleLatest;
    public string DongleAvailable => BundledFirmware.Value is { } image ? FirmwareImage.Version(image) : DongleLatest ?? Unknown;

    async Task FetchLatestReleases()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AppleRemote-UpdateCheck");
            DongleLatest = await UpdateService.LatestTagAsync(http, "AnthoDingo/AppleRemoteWin-Dongle");
            FilterLatest = await UpdateService.LatestTagAsync(http, "AnthoDingo/AppleRemoteWin-Driver");
        }
        catch (Exception) { }   // offline, rate-limited or no release yet: stays unknown
    }
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
            DongleFirmware = await Task.Run(EspDongle.ReadFirmwareVersion);
            DongleOn = DongleFirmware is not null || await Task.Run(() => EspDongle.Find() is not null);
        }
        catch (Exception) { DongleOn = false; }   // HID enumeration failed: shown as absent
        if (UpdateAvailable && _announcedFirmware != DongleFirmware)
        {
            _announcedFirmware = DongleFirmware;
            _log(Loc.T("Log.FirmwareAvailable", FirmwareImage.Version(BundledFirmware.Value!), DongleFirmware));
        }
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

    /// <summary>
    /// A device was plugged in or removed (UI thread, debounced): refreshes the adapter list and, if the running session
    /// is not on the adapter it would now open, restarts it there.
    /// </summary>
    public async Task OnDevicesChanged()
    {
        if (DriverBusy) return;   // a driver change replugs the adapter; ChangeDriver restarts the session itself
        var before = Adapters.Select(a => (a.InstanceId, a.Driver)).ToHashSet();
        await RefreshDriver();
        if (DriverBusy || before.SetEquals(Adapters.Select(a => (a.InstanceId, a.Driver)))) return;
        _log(Loc.T("Log.AdaptersChanged", Adapters.Count > 0 ? string.Join(", ", Adapters) : "—"));
        Remote.ClearNearby();
        if (!_session.IsRunning || string.Equals(ExpectedAdapter(), _session.AdapterInUse, StringComparison.OrdinalIgnoreCase)) return;
        Remote.StopSession();
        Remote.StartSession();
    }

    /// <summary>
    /// What <see cref="UsbHci.Open"/> would pick: the saved adapter if plugged in (direct Bluetooth only under WinUSB),
    /// otherwise the first under WinUSB. Null: Windows' stack.
    /// </summary>
    string? ExpectedAdapter() =>
        Adapters.FirstOrDefault(a => a.InstanceId.Equals(_settings.Adapter, StringComparison.OrdinalIgnoreCase)) is { } saved
            ? saved.Driver == AdapterDriver.WinUsb ? saved.InstanceId : null
            : Adapters.FirstOrDefault(a => a.Driver == AdapterDriver.WinUsb)?.InstanceId;

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
                if (verb is "filter-install" or "filter-uninstall")
                {
                    _settings.FilterVersion = verb == "filter-install" ? FilterAvailable : null;
                    _settings.Save();
                }
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

    /// <summary>
    /// Signed firmware shipped next to the exe (firmware\AppleRemoteEsp32.bin, downloaded from AppleRemoteWin-Dongle by the Release build): the only update source,
    /// the dongle itself never goes online. Null if missing or damaged.
    /// </summary>
    static readonly Lazy<byte[]?> BundledFirmware = new(() =>
    {
        try
        {
            byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "firmware", FirmwareImage.Project + ".bin"));
            return FirmwareImage.Problem(image) is null ? image : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });

    string? _announcedFirmware;

    /// <summary>The shipped firmware is newer than the dongle's (which still checks signature and version itself).</summary>
    public bool UpdateAvailable => DongleOn && DongleProgress is null && BundledFirmware.Value is { } image
        && FirmwareImage.IsNewer(FirmwareImage.Version(image), DongleFirmware);

    public string DongleUpdateText =>
        BundledFirmware.Value is { } image ? Loc.T("Settings.UpdateDongle", FirmwareImage.Version(image)) : "";

    [RelayCommand(CanExecute = nameof(UpdateAvailable))]
    async Task UpdateDongle()
    {
        byte[] image = BundledFirmware.Value!;
        string version = FirmwareImage.Version(image), title = Loc.T("Firmware.Title");
        if (!await _dialogs.Confirm(title, Loc.T("Firmware.Ask", DongleFirmware, version))) return;

        _log(Loc.T("Log.FirmwareSending", version, Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()));
        DongleProgress = 0;
        try
        {
            await EspDongle.UpdateFirmwareAsync(image, new Progress<int>(p => DongleProgress = p), CancellationToken.None);
            _log(Loc.T("Log.FirmwareUpdated"));
            await Task.Delay(4000);   // the dongle reboots on its new firmware before answering again
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _log(Loc.T("Log.FirmwareFailed", ex.Message));
            await _dialogs.Warn(title, ex.Message);
        }
        finally
        {
            DongleProgress = null;
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
