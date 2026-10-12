// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.IO;
using System.Windows.Threading;
using AppleRemote.App.Models;
using AppleRemote.Windows;

namespace AppleRemote.App.ViewModels;

/// <summary>Main window: owns the settings and the remote session, and one view-model per page.</summary>
public sealed class MainViewModel : IDisposable
{
    readonly Settings _settings = Settings.Load();
    readonly RemoteSession _session;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    UpdateService? _updates;

    public DebugViewModel Debug { get; }
    public RemoteViewModel Remote { get; }
    public SettingsViewModel Preferences { get; }
    public MacrosViewModel Macros { get; }

    public MainViewModel(IDialogs dialogs)
    {
        Debug = new DebugViewModel(dialogs, onPaired: () =>
        {
            // The driver waits for the remote and unlocks it by itself as soon as Windows exposes its HID collection:
            // the session just has to be running.
            if (!_session!.IsRunning) Remote!.StartSession(); // both set before any pairing
        });

        KeyMap map = KeyMap.Default();
        if (_settings.KeyMapPath is { Length: > 0 } path && File.Exists(path))
        {
            try { map = KeyMap.FromJson(File.ReadAllText(path)); }
            catch (Exception e) { Debug.Add(Loc.T("Log.MapIgnored", e.Message)); }
        }
        _session = new RemoteSession(map);

        Remote = new RemoteViewModel(_session, dialogs, Debug.Add, input => Preferences!.RunBound(input));
        Preferences = new SettingsViewModel(_settings, _session, dialogs, Debug.Add, Remote);
        Remote.Preferences = Preferences;
        Macros = new MacrosViewModel(Preferences.MacroChoices, Preferences.SoftwareChoices, dialogs);
    }

    public bool CloseQuits => Preferences.CloseQuits;

    public void Start()
    {
        Remote.Start();
        (_updates = new UpdateService(_settings, s => _ui.InvokeAsync(() => Debug.Add(s)))).Start();
    }

    public void Dispose()
    {
        Preferences.Dispose();
        Remote.Dispose();
        _updates?.Dispose();
        _settings.Save();
        _session.Dispose();
    }
}
