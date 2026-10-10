// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Bluetooth;

namespace AppleRemote.Windows;

/// <summary>
/// User-mode "driver" for the Siri Remote gen 1: finds the HID collection, unlocks it (0xF0 &lt;- 0xAF), reads the 0xFC reports
/// asynchronously, and reconnects by itself after sleep. Windows already provides the kernel driver (HidBthLE, HID over GATT):
/// this code does in user space what BlueZ/hidraw + open-appleremote do on Linux.
/// </summary>
public sealed class SiriRemoteDriver
{
    /// <summary>Silence (ms) after which the link is probed with a new unlock.</summary>
    const int ProbeAfterMs = 8000;

    public static double HostNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    readonly Action<double, byte[]?> _onReport;
    readonly Action<string> _onStatus;
    string? _lastStatus;

    /// <param name="onReport">(host time, report). Empty array = link lost. Called from a pool thread.</param>
    /// <param name="onStatus">Status messages, de-duplicated.</param>
    /// <param name="onBattery">Battery level in %, read over GATT after each (re)unlock. Called from a pool thread.</param>
    public SiriRemoteDriver(Action<double, byte[]?> onReport, Action<string> onStatus, Action<int>? onBattery = null)
    {
        _onReport = onReport;
        _onStatus = onStatus;
        _onBattery = onBattery;
    }

    readonly Action<int>? _onBattery;

    async Task ReportBatteryAsync(BluetoothLEDevice? device)
    {
        if (_onBattery is null || device is null) return;
        try { if (await FilterUnlock.ReadBatteryAsync(device) is { } level) _onBattery(level); }
        catch (Exception) { /* battery is optional: link dropped or WinRT refused */ }
    }

    void Status(string message)
    {
        if (message == _lastStatus) return;
        _lastStatus = message;
        _onStatus(message);
    }

    /// <summary>Connection loop: only returns on cancellation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = HidEnumerator.FindRemote();
                if (info is null)
                {
                    Status(Loc.T("Status.WaitHid"));
                    await Task.Delay(1500, ct);
                    continue;
                }
                await RunConnectionAsync(info, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Status(Loc.T("Status.Error", e.Message));
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// 0xF0 = 0xAF through HidD_SetFeature; Windows sometimes refuses it (error 87): falls back to the filter's GATT unlock.
    /// Also returns the Windows error of the first attempt, for the status message.
    /// </summary>
    static async Task<(bool Ok, int Error)> UnlockAsync(HidDeviceInfo info, SafeFileHandle handle, BluetoothLEDevice? device)
    {
        if (HidEnumerator.Unlock(handle, info.FeatureLength)) return (true, 0);
        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (device is null) return (false, error);
        try { return (await FilterUnlock.TryAsync(device), error); }
        catch (Exception) { return (false, error); } // WinRT unavailable or remote gone: handled like a refusal
    }

    async Task RunConnectionAsync(HidDeviceInfo info, CancellationToken ct)
    {
        using SafeFileHandle handle = HidEnumerator.Open(info.Path);
        if (handle.IsInvalid)
        {
            Status(Loc.T("Status.CannotOpen", System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            await Task.Delay(2000, ct);
            return;
        }

        // LE device kept open for the whole connection: fallback unlock over GATT, and wake-up reported by Windows.
        using var device = await FilterUnlock.OpenAsync(info.Path);

        // The HID node can exist while the remote sleeps: the unlock only goes through if the link is alive.
        bool unlocked = false;
        int error = 0;
        for (int attempt = 0; attempt < 3 && !unlocked && !ct.IsCancellationRequested; attempt++)
        {
            (unlocked, error) = await UnlockAsync(info, handle, device);
            if (!unlocked) await Task.Delay(300, ct);
        }
        if (!unlocked)
        {
            Status(Loc.T("Status.Unreachable", error));
            // Short: the remote only stays awake a few seconds after a press, so it must be caught quickly.
            await Task.Delay(500, ct);
            return;
        }

        Status(Loc.T("Status.ConnectedHid", string.IsNullOrWhiteSpace(info.Product) ? "Siri Remote" : info.Product));

        _ = ReportBatteryAsync(device);

        int length = Math.Max(info.InputLength, 21);
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long lastReport = Environment.TickCount64;

        // The remote often falls asleep then reconnects, and stays mute until it is unlocked again:
        // we redo it as soon as Windows signals the reconnection, without waiting for the probe (otherwise the first presses are lost).
        int relocking = 0;
        async void OnConnectionChanged(BluetoothLEDevice sender, object _)
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected || Interlocked.Exchange(ref relocking, 1) == 1) return;
            try
            {
                for (int attempt = 0; attempt < 10 && !linkCts.IsCancellationRequested; attempt++)
                {
                    if ((await UnlockAsync(info, handle, sender)).Ok) { Status(Loc.T("Status.Relocked")); _ = ReportBatteryAsync(sender); break; }
                    await Task.Delay(300);
                }
            }
            catch (Exception) { /* link dropped during the attempt: the next reconnection will retry */ }
            finally { Volatile.Write(ref relocking, 0); }
        }
        if (device is not null) device.ConnectionStatusChanged += OnConnectionChanged;

        // Probe: if nothing has arrived for a while, a failing unlock = link dropped -> reconnect.
        var probe = Task.Run(async () =>
        {
            try
            {
                while (!linkCts.IsCancellationRequested)
                {
                    await Task.Delay(ProbeAfterMs, linkCts.Token);
                    if (Environment.TickCount64 - Volatile.Read(ref lastReport) < ProbeAfterMs) continue;
                    if (!(await UnlockAsync(info, handle, device)).Ok)
                    {
                        linkCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);

        try
        {
            using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
            var buffer = new byte[length];
            while (!linkCts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, length), linkCts.Token);
                if (n <= 0) throw new IOException("end of HID stream");
                Volatile.Write(ref lastReport, Environment.TickCount64);
                _onReport(HostNow, buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // device gone (sleep, disconnect): treated as a lost link
        }
        finally
        {
            if (device is not null) device.ConnectionStatusChanged -= OnConnectionChanged;
            linkCts.Cancel();
            try { await probe; } catch { /* probe already stopped */ }
        }

        _onReport(HostNow, Array.Empty<byte>());   // releases buttons and fingers
        if (!ct.IsCancellationRequested)
        {
            Status(Loc.T("Status.Disconnected"));
            await Task.Delay(1000, ct);
        }
    }
}
