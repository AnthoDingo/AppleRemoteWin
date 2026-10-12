// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.IO;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleRemote.App.ViewModels;

/// <summary>Debug page: the app's log (fed by every view-model) and the diagnostic tools.</summary>
/// <param name="onPaired">A remote was paired from the BLE scan window.</param>
public sealed partial class DebugViewModel(IDialogs dialogs, Action onPaired) : ObservableObject
{
    readonly List<string> _log = new();

    /// <summary>Last 300 log lines, one per line.</summary>
    public string LogText => string.Join(Environment.NewLine, _log);

    public void Add(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        _log.Add(stamped);
        if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300);
        OnPropertyChanged(nameof(LogText));
        // Copy to %TEMP%\AppleRemote.log: readable without the UI (remote diagnosis, VM test session).
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "AppleRemote.log"), stamped + Environment.NewLine); }
        catch (IOException) { }
    }

    [RelayCommand]
    void ClearLog()
    {
        _log.Clear();
        OnPropertyChanged(nameof(LogText));
    }

    [RelayCommand]
    async Task ListDevices()
    {
        try
        {
            var devices = await Task.Run(() => HidEnumerator.ListApple());
            if (devices.Count == 0)
            {
                Add(Loc.T("Log.NoHid"));
                return;
            }
            Add(Loc.T("Log.HidCount", devices.Count));
            foreach (var d in devices) Add("  " + d + (d.IsSiriRemoteGen1 ? Loc.T("Log.HidGen1") : ""));
        }
        catch (Exception ex)
        {
            Add(Loc.T("Log.EnumFailed", ex.Message));
        }
    }

    [RelayCommand]
    void BleScan() =>
        dialogs.ShowBleScan(() =>
        {
            Add(Loc.T("Log.BlePaired"));
            onPaired();
        });
}
