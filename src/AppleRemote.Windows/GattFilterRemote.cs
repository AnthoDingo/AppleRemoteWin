using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AppleRemote.Windows;

/// <summary>
/// Client GATT user-mode de la télécommande quand le filtre noyau AppleRemoteDriver est installé (même séquence que
/// Jack-R1/SiriRemoteDriver) : le filtre réécrit l'écriture 0xAF du « niveau de batterie » (0x2A19) en déverrouillage HID,
/// son CCCD en abonnement HID, et fait remonter les notifications HID sous « état d'alimentation » (0x2A1A).
/// La télécommande doit déjà être appairée par Windows (Scanner BLE → Appairer).
/// </summary>
public sealed class GattFilterRemote
{
    const byte ReportId = 0xFC; // l'ID n'est pas dans la notification : on le remet en tête (format hid.dll)

    static readonly Guid PowerState = BluetoothUuidHelper.FromShortId(0x2A1A);

    readonly Action<double, byte[]?> _onReport;
    readonly Action<string> _onStatus;
    string? _lastStatus;

    public GattFilterRemote(Action<double, byte[]?> onReport, Action<string> onStatus)
    {
        _onReport = onReport;
        _onStatus = onStatus;
    }

    void Status(string message)
    {
        if (message == _lastStatus) return;
        _lastStatus = message;
        _onStatus(message);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunConnectionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Status(Loc.T("Status.GattRetry", e.Message)); }
            _onReport(SiriRemoteDriver.HostNow, []); // relâche boutons et doigts
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task RunConnectionAsync(CancellationToken ct)
    {
        using var device = await FindRemoteAsync();
        if (device is null) { Status(Loc.T("Status.GattNoRemote")); return; }
        Status(Loc.T("Status.GattConnecting", device.Name));

        // Garde la liaison ouverte : la télécommande coupe une ou deux fois juste après l'appairage.
        using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        session.MaintainConnection = true;

        // Une télécommande appairée ne réémet que quand on appuie sur un bouton : on attend la connexion plutôt que d'échouer en « Unreachable ».
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            Status(Loc.T("Status.GattWake"));
            var connected = new TaskCompletionSource();
            device.ConnectionStatusChanged += (d, _) => { if (d.ConnectionStatus == BluetoothConnectionStatus.Connected) connected.TrySetResult(); };
            try { await connected.Task.WaitAsync(TimeSpan.FromSeconds(90), ct); }
            catch (TimeoutException) { return; }
            Status(Loc.T("Status.GattConnecting", device.Name));
        }

        // Juste après la connexion, la découverte échoue parfois encore (« Unreachable ») : quelques essais rapprochés.
        GattDeviceServicesResult services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        for (int attempt = 1; attempt < 4 && (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0); attempt++)
        {
            await Task.Delay(1000, ct);
            services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        }
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new IOException($"service batterie : {services.Status}");
        var characteristics = await services.Services[0].GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        var level = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == GattCharacteristicUuids.BatteryLevel);
        var power = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == PowerState);
        if (level is null || power is null) throw new IOException("caractéristiques batterie absentes");

        int reports = 0;
        byte[] last = [];
        power.ValueChanged += (_, args) =>
        {
            var value = args.CharacteristicValue.ToArray();
            last = value;
            Interlocked.Increment(ref reports);
            _onReport(SiriRemoteDriver.HostNow, [ReportId, .. value]);
        };
        await Check(power.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
        // CCCD du niveau de batterie : le filtre le redirige vers les notifications HID (handle 0x29 -> 0x24).
        await Check(level.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
        // « Paquet magique » : le filtre le transforme en déverrouillage (0xAF -> handle 0x1d).
        var unlock = await level.WriteValueAsync(new byte[] { 0xAF }.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        Status(Loc.T("Status.GattUnlocked", unlock));

        var started = DateTime.Now;
        int shown = 0;
        while (!ct.IsCancellationRequested && device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            // Le journal montre l'arrivée des rapports (nombre + octets du dernier) : sans cela on ne voit pas si les boutons parlent.
            if (reports != shown)
            {
                shown = reports;
                Status(shown == 1 ? Loc.T("Status.GattReady") : Loc.T("Status.GattReports", shown, Convert.ToHexString(last)));
            }
            await Task.Delay(300, ct);
        }
        Status(Loc.T("Status.GattLost", (DateTime.Now - started).TotalSeconds, reports));
    }

    /// <summary>L'adresse mémorisée à l'appairage ; à défaut, le seul appareil LE appairé ou celui dont le nom est un numéro de série.</summary>
    static async Task<BluetoothLEDevice?> FindRemoteAsync()
    {
        if (ulong.TryParse(File.Exists(BleScanner.RemoteAddressPath) ? File.ReadAllText(BleScanner.RemoteAddressPath) : null, out var address)
            && await BluetoothLEDevice.FromBluetoothAddressAsync(address) is { } known)
            return known;
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        var info = infos.FirstOrDefault(i => BleDevice.LooksLikeSerial(i.Name)) ?? (infos.Count == 1 ? infos[0] : null);
        return info is null ? null : await BluetoothLEDevice.FromIdAsync(info.Id);
    }

    static async Task Check(global::Windows.Foundation.IAsyncOperation<GattCommunicationStatus> write)
    {
        var status = await write;
        if (status != GattCommunicationStatus.Success) throw new IOException($"écriture GATT : {status}");
    }
}
