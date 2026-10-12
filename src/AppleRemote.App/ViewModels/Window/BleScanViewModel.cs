// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text;
using System.Windows.Threading;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Enumeration;

namespace AppleRemote.App.ViewModels;

public sealed partial class BleScanViewModel : ObservableObject, IDisposable
{
    /// <summary>Scan source: Windows' radio (<see cref="AdapterId"/> null) or an adapter under WinUSB.</summary>
    public sealed record Source(string Name, string? AdapterId)
    {
        public override string ToString() => Name;
    }

    public sealed record Row(string NameText, string Address, short Rssi, string Companies, string Note, bool AppleHid, bool AppleNoHid, ulong Key);

    readonly BleScanner _scanner = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly IDialogs _dialogs;
    bool _paused;

    [ObservableProperty] IReadOnlyList<Source> _sources = [new(Loc.T("Ble.SourceWindows"), null)];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PairCommand))] Source? _selectedSource;
    [ObservableProperty] IReadOnlyList<Row> _rows = [];
    [ObservableProperty] Row? _selected;
    [ObservableProperty] string _toggleText = Loc.T("Ble.Pause");
    [ObservableProperty] string _countText = "";
    [ObservableProperty] string _pairText = "";
    [ObservableProperty] bool _onlyLikely;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    bool _isPairing;

    /// <summary>Raised (UI thread) after a successful pairing or if the device was already paired.</summary>
    public event Action? Paired;

    public BleScanViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        _scanner.Failed += message => _ui.InvokeAsync(() =>
        {
            CountText = message;
            ToggleText = Loc.T("Ble.Relaunch");
            _paused = true;
        });
        _timer.Tick += (_, _) => Refresh();
    }

    public async void Start()
    {
        SelectedSource = Sources[0];
        _timer.Start();
        // Adapters under their original driver are all behind Windows' single radio: only WinUSB ones are a choice.
        try
        {
            var usb = await Task.Run(() => WinUsbDriver.List().Where(a => a.Driver == AdapterDriver.WinUsb).ToList());
            Sources = [Sources[0], .. usb.Select(a => new Source($"{a.Name} ({a.Id}, WinUSB)", a.InstanceId))];
            SelectedSource = Sources[0];   // the ComboBox clears its selection when the list is replaced
        }
        catch (Exception) { /* listing failed: Windows' radio only */ }
    }

    Source? _scanned;

    partial void OnSelectedSourceChanged(Source? value)
    {
        if (value is null || value == _scanned) return;   // null: transient, while the list is replaced
        _scanned = value;
        _scanner.Clear();
        _scanner.Start(value.AdapterId);
        _paused = false;
        ToggleText = Loc.T("Ble.Pause");
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
        _scanner.Dispose();
    }

    partial void OnOnlyLikelyChanged(bool value) => Refresh();

    List<Row> BuildRows()
    {
        var devices = _scanner.Snapshot();
        if (OnlyLikely) devices = devices.Where(d => d.AppleHid).ToList();
        return devices
            .Select(d => new Row(d.Name.Length > 0 ? d.Name : Loc.T("Ble.Unnamed"), d.AddressText, d.Rssi, d.CompaniesText, d.Note, d.AppleHid, d.IsApple && !d.HidService, d.Address))
            .ToList();
    }

    void Refresh()
    {
        var rows = BuildRows();
        // The list is rebuilt on every tick: the selection is kept by address.
        ulong? selected = Selected?.Key;
        Rows = rows;
        Selected = rows.FirstOrDefault(r => r.Key == selected);
        CountText = Loc.T("Ble.Count", rows.Count, rows.Count(r => r.AppleHid));
    }

    [RelayCommand]
    void Toggle()
    {
        if (_paused)
        {
            _scanner.Start(SelectedSource?.AdapterId);
            _timer.Start();
            ToggleText = Loc.T("Ble.Pause");
        }
        else
        {
            _scanner.Stop();
            ToggleText = Loc.T("Ble.Resume");
        }
        _paused = !_paused;
    }

    // Pairing goes through Windows: not for a device seen by an adapter under WinUSB (the Remote page pairs those).
    bool CanPair() => !IsPairing && SelectedSource?.AdapterId is null;

    [RelayCommand(CanExecute = nameof(CanPair))]
    Task Pair() => PairSelected(system: false);

    async Task PairSelected(bool system)
    {
        if (Selected is not { } row)
        {
            PairText = Loc.T("Ble.SelectRow");
            return;
        }

        IsPairing = true;
        // Scanning while connecting upsets some adapters: the scan is suspended.
        bool resume = !_paused;
        _scanner.Stop();
        try
        {
            if (await PairAsync(row.Key, row.NameText, row.Address, _dialogs, text => PairText = text, system)) Paired?.Invoke();
        }
        finally
        {
            IsPairing = false;
            if (resume) _scanner.Start(SelectedSource?.AdapterId);
        }
    }

    /// <summary>
    /// Pairs a device through Windows (also used by the Remote page). Offers to drop a stale Windows pairing first.
    /// <paramref name="progress"/> receives each step's text. True if paired (or already paired).
    /// </summary>
    public static async Task<bool> PairAsync(ulong key, string name, string address, IDialogs dialogs, Action<string> progress, bool system = false)
    {
        progress(Loc.T("Ble.Pairing", name, address));
        try
        {
            var (status, message) = await BleScanner.PairAsync(key, system);
            // Paired on the Windows side while the device advertises in pairing mode: stale link, no GATT services (hence no HID).
            if (status == DevicePairingResultStatus.AlreadyPaired && await dialogs.Confirm(Loc.T("Ble.AlreadyTitle"),
                    Loc.T("Ble.AlreadyAsk", name, address)))
            {
                progress(Loc.T("Ble.Unpairing"));
                var unpaired = await BleScanner.UnpairAsync(key);
                if (unpaired is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                {
                    progress(Loc.T("Ble.UnpairFailed", unpaired));
                    return false;
                }
                progress(Loc.T("Ble.Pairing", name, address));
                (status, message) = await BleScanner.PairAsync(key, system);
            }
            progress(message);
            return status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
        }
        catch (Exception ex)
        {
            progress(Loc.T("Ble.PairFailed", ex.Message));
            return false;
        }
    }

    [RelayCommand]
    void Clear()
    {
        _scanner.Clear();
        Refresh();
    }

    [RelayCommand]
    void Copy()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Ble.TableHeader"));
        foreach (var r in BuildRows())
            sb.AppendLine($"{r.NameText} | {r.Address} | {r.Rssi} | {r.Companies} | {r.Note}");
        try
        {
            _dialogs.CopyText(sb.ToString());
            CountText = Loc.T("Ble.Copied");
        }
        catch (Exception ex)
        {
            CountText = Loc.T("Ble.CopyFailed", ex.Message);
        }
    }
}
