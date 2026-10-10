using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AppleRemote.Windows;

/// <summary>Un appareil Bluetooth LE vu en publicité (advertising), agrégé par adresse.</summary>
public sealed record BleDevice(
    ulong Address, string Name, short Rssi, IReadOnlyList<ushort> Companies, bool HidService, DateTime LastSeen)
{
    public const ushort AppleCompanyId = 0x004C;

    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((Address >> (8 * i)) & 0xFF).ToString("X2")));

    public bool IsApple => Companies.Contains(AppleCompanyId);

    /// <summary>Les numéros de série Apple (nom diffusé par la Siri Remote) : lettres majuscules et chiffres, longs.</summary>
    public bool NameLooksLikeSerial => LooksLikeSerial(Name);

    public static bool LooksLikeSerial(string name) =>
        name.Length >= 8 && name.All(char.IsAsciiLetterOrDigit) && name.Any(char.IsAsciiDigit) && name.Any(char.IsAsciiLetterUpper);

    /// <summary>Candidat probable : fabricant Apple, service HID (0x1812) ou nom en forme de numéro de série.</summary>
    public bool Likely => IsApple || HidService || NameLooksLikeSerial;

    /// <summary>Filtre « Apple HID » : données constructeur Apple et service HID, comme une Siri Remote en mode appairage.</summary>
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
/// Écoute les publicités Bluetooth LE autour du PC (<see cref="BluetoothLEAdvertisementWatcher"/>), sans appairage.
/// Sert au diagnostic : une Siri Remote en mode appairage (Menu + Volume + ~5 s) doit apparaître ici,
/// avec le fabricant Apple (0x004C) et/ou le service HID, même si la liste « Ajouter un appareil » de Windows ne l'affiche pas.
/// </summary>
public sealed class BleScanner : IDisposable
{
    static readonly Guid HidServiceUuid = new("00001812-0000-1000-8000-00805f9b34fb");

    readonly object _gate = new();
    readonly Dictionary<ulong, BleDevice> _devices = new();
    BluetoothLEAdvertisementWatcher? _watcher;

    /// <summary>Levé (thread du pool) si le scan s'arrête de lui-même : Bluetooth coupé, pas d'adaptateur LE, etc.</summary>
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
        try { watcher.Stop(); } catch { /* déjà arrêté */ }
    }

    public void Clear()
    {
        lock (_gate) _devices.Clear();
    }

    /// <summary>Instantané : candidats probables d'abord, puis par signal décroissant.</summary>
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
            // Le nom arrive souvent dans la réponse de scan, séparément du reste : on fusionne par adresse.
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
        if (args.Error == BluetoothError.Success) return;   // arrêt demandé
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
    /// Appaire l'appareil LE à cette adresse sans passer par les Paramètres ; il doit être en mode appairage.
    /// Windows charge ensuite lui-même le pilote HID (HID over GATT). Renvoie le statut et un message à afficher.
    /// <paramref name="system"/> : cérémonie gérée par Windows (celle des Paramètres) au lieu de la nôtre, pour comparer.
    /// </summary>
    public static async Task<(DevicePairingResultStatus Status, string Message)> PairAsync(ulong address, bool system = false)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return (DevicePairingResultStatus.Failed, Loc.T("Ble.DeviceNotFound"));

        var pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired) { SaveRemote(address); return (DevicePairingResultStatus.AlreadyPaired, Loc.T("Ble.AlreadyPaired")); }

        // Pas de saisie possible ici : on accepte tout ce qui ne demande pas de taper un code.
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
            // Une durée proche de 30 s = délai d'expiration SMP : l'appairage a traîné.
            string kind = Loc.T("Ble.PairKind", Loc.T(system ? "Ble.KindSystem" : "Ble.KindApp"), (DateTime.Now - started).TotalSeconds)
                          + (asked is null ? ")" : Loc.T("Ble.PairKindMethod", asked));
            if (result.Status == DevicePairingResultStatus.Paired)
            {
                SaveRemote(address);
                return (result.Status, Loc.T("Ble.Paired", kind, await DiscoverServicesAsync(device)));
            }
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
    /// Force la découverte GATT tant que la liaison d'appairage est ouverte. Sans elle, Windows ne sait pas que l'appareil
    /// est HID : il ne crée pas la collection HID et ne s'y reconnecte jamais (appairé mais « vide »).
    /// </summary>
    static async Task<string> DiscoverServicesAsync(BluetoothLEDevice device)
    {
        var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            return Loc.T("Ble.DiscoverFailed", result.Status);
        int count = result.Services.Count;
        foreach (var service in result.Services) service.Dispose();
        // Windows peut masquer le service HID (0x1812) aux applications : on ne peut pas exiger de le voir ici.
        return Loc.T("Ble.Discovered", count);
    }

    /// <summary>Adresse de la télécommande appairée : Windows l'affiche « Bluetooth LE Device … » sans nom, on ne peut pas la deviner.</summary>
    public static string RemoteAddressPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "remote.txt");

    static void SaveRemote(ulong address)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RemoteAddressPath)!);
            File.WriteAllText(RemoteAddressPath, address.ToString());
        }
        catch (IOException) { } // pas bloquant : le client retombe sur la recherche par nom
    }

    /// <summary>Supprime l'appairage Windows de cet appareil (lien périmé : appairé côté PC, plus côté télécommande).</summary>
    public static async Task<DeviceUnpairingResultStatus> UnpairAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null) return DeviceUnpairingResultStatus.Failed;
        var result = await device.DeviceInformation.Pairing.UnpairAsync();
        return result.Status;
    }

    public void Dispose() => Stop();
}
