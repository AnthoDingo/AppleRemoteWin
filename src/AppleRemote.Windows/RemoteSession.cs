// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote.Windows;

/// <summary>
/// Orchestrates the HID driver, decoder, gestures and virtual keyboard. Events are raised from pool threads:
/// the UI must marshal them (Dispatcher). The whole chain is serialised by a lock.
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

    /// <summary>Battery level in %, raised from a pool thread.</summary>
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

    /// <summary>Pairing keys for direct Bluetooth.</summary>
    public string BondsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "bonds.json");

    /// <summary>PnP instance ID of the adapter to drive directly when several are under WinUSB (null: the first one).</summary>
    public string? AdapterId { get; set; }

    /// <summary>True if the running session drives the adapter directly (WinUSB) rather than Windows' HID.</summary>
    public bool UsesBluetoothDirect { get; private set; }

    /// <summary>PnP instance ID of the WinUSB adapter the running session drives; null on Windows' HID or when stopped.</summary>
    public string? AdapterInUse { get; private set; }

    /// <summary>Forgets the direct-Bluetooth bond: on next start, looks for a remote in pairing mode.</summary>
    public void ForgetPairing() => new BondStore(BondsPath).Clear();

    /// <summary>
    /// Starts reading the real remote: direct Bluetooth if a USB Bluetooth adapter is under WinUSB
    /// (the Windows Bluetooth stack doesn't keep the remote, see CLAUDE.md), otherwise Windows' HID.
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        UsbHci? hci = null;
        try { hci = UsbHci.Open(AdapterId); }
        catch (Exception e) { Status?.Invoke(Loc.T("Status.BtUnavailable", e.Message)); }

        UsesBluetoothDirect = hci is not null;
        AdapterInUse = hci?.InstanceId;
        if (hci is not null)
        {
            hci.Failed += s => Status?.Invoke(s);
            var driver = new SiriRemoteBle(hci.Host, new BondStore(BondsPath), Feed, s => Status?.Invoke(s), l => Battery?.Invoke(l), hci.Name);
            _driverTask = Task.Run(async () =>
            {
                try { await driver.RunAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested) { Status?.Invoke(Loc.T("Status.BtError", e.Message)); }
                finally { hci.Dispose(); }
            });
        }
        else
        {
            var driver = new SiriRemoteDriver(Feed, s => Status?.Invoke(s), l => Battery?.Invoke(l));
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
        AdapterInUse = null;
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
