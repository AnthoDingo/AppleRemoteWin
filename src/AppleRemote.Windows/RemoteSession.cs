namespace AppleRemote.Windows;

/// <summary>
/// Orchestre pilote HID, décodeur, gestes et clavier virtuel. Les événements sont levés depuis des threads du pool :
/// l'interface doit les marshaler (Dispatcher). Toute la chaîne est sérialisée par un verrou.
/// </summary>
public sealed class RemoteSession : IDisposable
{
    readonly object _gate = new();
    readonly RemotePipeline _pipeline = new();
    readonly KeyboardInjector _injector = new();
    readonly KeyBinder _binder;
    bool _keyboardEnabled;

    CancellationTokenSource? _cts;
    Task? _driverTask, _tickTask;

    public event Action<RemoteEvent>? Event;
    public event Action<GestureEvent>? Gesture;
    public event Action<string>? Status;

    /// <summary>Niveau de batterie en % (Bluetooth direct seulement), levé depuis un thread du pool.</summary>
    public event Action<int>? Battery;

    public RemoteSession(KeyMap? map = null)
    {
        _binder = new KeyBinder(map ?? KeyMap.Default(), _injector);
        _pipeline.Event += ev =>
        {
            if (_keyboardEnabled) _binder.OnEvent(ev);
            Event?.Invoke(ev);
        };
        _pipeline.Gesture += g =>
        {
            if (_keyboardEnabled) _binder.OnGesture(g);
            Gesture?.Invoke(g);
        };
    }

    public KeyboardInjector Injector => _injector;

    public bool KeyboardEnabled
    {
        get => _keyboardEnabled;
        set
        {
            lock (_gate)
            {
                if (_keyboardEnabled && !value) _binder.ReleaseAll();
                _keyboardEnabled = value;
            }
        }
    }

    public KeyMap KeyMap
    {
        get => _binder.Map;
        set { lock (_gate) _binder.Map = value; }
    }

    public bool IsRunning => _cts is not null;

    /// <summary>Clés d'appairage du Bluetooth direct.</summary>
    public string BondsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "bonds.json");

    /// <summary>Vrai si la session en cours pilote l'adaptateur en direct (WinUSB) plutôt que le HID de Windows.</summary>
    public bool UsesBluetoothDirect { get; private set; }

    /// <summary>Oublie l'appairage du Bluetooth direct : au prochain démarrage, recherche d'une télécommande en mode appairage.</summary>
    public void ForgetPairing() => new BondStore(BondsPath).Clear();

    /// <summary>
    /// Démarre la lecture de la vraie télécommande : Bluetooth direct si un adaptateur Bluetooth USB est sous WinUSB
    /// (la pile Bluetooth de Windows ne garde pas la télécommande, cf. CLAUDE.md), sinon le HID de Windows.
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        UsbHci? hci = null;
        try { hci = UsbHci.Open(); }
        catch (Exception e) { Status?.Invoke(Loc.T("Status.BtUnavailable", e.Message)); }

        UsesBluetoothDirect = hci is not null;
        if (hci is not null)
        {
            hci.Failed += s => Status?.Invoke(s);
            var driver = new SiriRemoteBle(hci.Host, new BondStore(BondsPath), Feed, s => Status?.Invoke(s), l => Battery?.Invoke(l));
            _driverTask = Task.Run(async () =>
            {
                try { await driver.RunAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested) { Status?.Invoke(Loc.T("Status.BtError", e.Message)); }
                finally { hci.Dispose(); }
            });
        }
        else
        {
            var driver = new SiriRemoteDriver(Feed, s => Status?.Invoke(s));
            _driverTask = Task.Run(() => driver.RunAsync(ct));
        }
        _tickTask = Task.Run(() => TickLoopAsync(ct));
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try { Task.WaitAll(new[] { _driverTask, _tickTask }.Where(t => t is not null).Cast<Task>().ToArray(), 2000); }
        catch (AggregateException) { }
        cts.Dispose();
        _cts = null;
        _driverTask = _tickTask = null;
        lock (_gate) _binder.ReleaseAll();
        Status?.Invoke(Loc.T("Status.Stopped"));
    }

    void Feed(double hostTime, byte[]? report)
    {
        lock (_gate) _pipeline.Feed(hostTime, report);
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                lock (_gate) _pipeline.Feed(SiriRemoteDriver.HostNow, null);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Stop();
}
