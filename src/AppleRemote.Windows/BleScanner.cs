// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AppleRemote.Windows;

/// <summary>A Bluetooth LE device seen advertising, aggregated by address.</summary>
public sealed record BleDevice(
    ulong Address, string Name, short Rssi, IReadOnlyList<ushort> Companies, bool HidService, DateTime LastSeen)
{
    public const ushort AppleCompanyId = 0x004C;

    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((Address >> (8 * i)) & 0xFF).ToString("X2")));

    public bool IsApple => Companies.Contains(AppleCompanyId);

    /// <summary>Apple serial numbers (the name broadcast by the Siri Remote): long, uppercase letters and digits.</summary>
    public bool NameLooksLikeSerial =>
        Name.Length >= 8 && Name.All(char.IsAsciiLetterOrDigit) && Name.Any(char.IsAsciiDigit) && Name.Any(char.IsAsciiLetterUpper);

    /// <summary>Likely candidate: Apple manufacturer, HID service (0x1812) or a serial-number-shaped name.</summary>
    public bool Likely => IsApple || HidService || NameLooksLikeSerial;

    /// <summary>"Apple HID" filter: Apple manufacturer data and HID service, like a Siri Remote in pairing mode.</summary>
    public bool AppleHid => IsApple && HidService;

    public string CompaniesText => Companies.Count == 0
        ? ""
        : string.Join(", ", Companies.Select(c => c == AppleCompanyId ? "0x004C (Apple)" : $"0x{c:X4}"));

    public string Note
    {
        get
        {
            var parts = new List<string>();
            if (IsApple) parts.Add("Apple");
            if (HidService) parts.Add("HID");
            if (NameLooksLikeSerial) parts.Add(Loc.T("Ble.NoteSerial"));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Listens to Bluetooth LE advertisements around the PC (<see cref="BluetoothLEAdvertisementWatcher"/>), without pairing.
/// Diagnostic aid: a Siri Remote in pairing mode (Menu + Volume + ~5 s) must show up here,
/// with the Apple manufacturer (0x004C) and/or the HID service, even if Windows' "Add a device" list doesn't show it.
/// </summary>
public sealed class BleScanner : IDisposable
{
    static readonly Guid HidServiceUuid = new("00001812-0000-1000-8000-00805f9b34fb");

    readonly object _gate = new();
    readonly Dictionary<ulong, BleDevice> _devices = new();
    BluetoothLEAdvertisementWatcher? _watcher;

    /// <summary>Raised (pool thread) if the scan stops by itself: Bluetooth off, no LE adapter, etc.</summary>
    public event Action<string>? Failed;

    public bool IsRunning => _watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created };

    public void Start()
    {
        if (_watcher is not null) return;

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        _watcher = watcher;
        try
        {
            watcher.Start();
        }
        catch (Exception e)
        {
            _watcher = null;
            Failed?.Invoke($"Scan impossible : {e.Message}");
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        if (watcher is null) return;
        _watcher = null;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try { watcher.Stop(); } catch { /* already stopped */ }
    }

    public void Clear()
    {
        lock (_gate) _devices.Clear();
    }

    /// <summary>Snapshot: likely candidates first, then by decreasing signal.</summary>
    public IReadOnlyList<BleDevice> Snapshot()
    {
        lock (_gate)
            return _devices.Values.OrderByDescending(d => d.Likely).ThenByDescending(d => d.Rssi).ToList();
    }

    void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var ad = args.Advertisement;
        string name = ad.LocalName ?? "";
        var companies = ad.ManufacturerData.Select(m => m.CompanyId).Distinct().ToList();
        bool hid = ad.ServiceUuids.Contains(HidServiceUuid);

        lock (_gate)
        {
            // The name often arrives in the scan response, separately from the rest: merged by address.
            if (_devices.TryGetValue(args.BluetoothAddress, out var old))
            {
                if (name.Length == 0) name = old.Name;
                companies = old.Companies.Union(companies).ToList();
                hid |= old.HidService;
            }
            _devices[args.BluetoothAddress] = new BleDevice(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, companies, hid, DateTime.Now);
        }
    }

    void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == BluetoothError.Success) return;   // stop requested
        _watcher = null;
        Failed?.Invoke(args.Error switch
        {
            BluetoothError.RadioNotAvailable => Loc.T("Ble.ScanNoRadio"),
            BluetoothError.NotSupported => Loc.T("Ble.ScanNoLe"),
            BluetoothError.DisabledByUser => Loc.T("Ble.ScanOffUser"),
            BluetoothError.DisabledByPolicy => Loc.T("Ble.ScanOffPolicy"),
            var other => Loc.T("Ble.ScanStopped", other),
        });
    }

    /// <summary>
    /// Pairs the LE device at this address without going through Settings; it must be in pairing mode.
    /// Windows then loads the HID driver (HID over GATT) itself. Returns the status and a message to display.
    /// <paramref name="system"/> : ceremony handled by Windows (the Settings one) instead of ours, for comparison.
    /// </summary>
    public static async Task<(DevicePairingResultStatus Status, string Message)> PairAsync(ulong address, bool system = false)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return (DevicePairingResultStatus.Failed, Loc.T("Ble.DeviceNotFound"));

        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) return (DevicePairingResultStatus.AlreadyPaired, Loc.T("Ble.AlreadyPaired"));

        // No input possible here: accept anything that doesn't require typing a code.
        DevicePairingKinds? asked = null;
        void OnRequested(DeviceInformationCustomPairing _, DevicePairingRequestedEventArgs e)
        {
            asked = e.PairingKind;
            e.Accept();
        }

        var custom = pairing.Custom;
        custom.PairingRequested += OnRequested;
        try
        {
            var started = DateTime.Now;
            var result = system
                ? await pairing.PairAsync()
                : await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin);
            // A duration close to 30 s = SMP timeout: the pairing dragged on.
            string kind = Loc.T("Ble.PairKind", Loc.T(system ? "Ble.KindSystem" : "Ble.KindApp"), (DateTime.Now - started).TotalSeconds)
                          + (asked is null ? ")" : Loc.T("Ble.PairKindMethod", asked));
            if (result.Status == DevicePairingResultStatus.Paired)
                return (result.Status, Loc.T("Ble.Paired", kind, await DiscoverServicesAsync(device)));
            return (result.Status, result.Status switch
            {
                DevicePairingResultStatus.AlreadyPaired => Loc.T("Ble.AlreadyPaired"),
                var status => Loc.T("Ble.PairFailedKind", status, kind),
            });
        }
        finally
        {
            custom.PairingRequested -= OnRequested;
        }
    }

    /// <summary>
    /// Forces GATT discovery while the pairing link is open. Without it, Windows doesn't know the device
    /// is HID: it creates no HID collection and never reconnects to it (paired but "empty").
    /// </summary>
    static async Task<string> DiscoverServicesAsync(BluetoothLEDevice device)
    {
        var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            return Loc.T("Ble.DiscoverFailed", result.Status);
        int count = result.Services.Count;
        foreach (var service in result.Services) service.Dispose();
        // Windows may hide the HID service (0x1812) from applications: we can't require seeing it here.
        return Loc.T("Ble.Discovered", count);
    }

    /// <summary>Removes this device's Windows pairing (stale link: paired on the PC side, no longer on the remote side).</summary>
    public static async Task<DeviceUnpairingResultStatus> UnpairAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return DeviceUnpairingResultStatus.Failed;
        var result = await device.DeviceInformation.Pairing.UnpairAsync();
        return result.Status;
    }

    public void Dispose() => Stop();
}
