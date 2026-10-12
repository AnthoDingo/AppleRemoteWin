// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>
/// ESP32-S3 dongle (src/AppleRemoteEsp32): it keeps the remote itself (BLE central, bond in its flash, unlock on every
/// connection) and forwards it over USB HID, vendor collection 0xFF52 under Espressif's VID. Report 1 (in): remote report
/// in hid.dll format (0xFC…), report 2 (in): state, battery %, last disconnect reason, feature 3: command.
/// </summary>
public sealed class EspDongle(Action<double, byte[]?> onReport, Action<string> onStatus, Action<int>? onBattery = null)
{
    public const ushort VendorId = 0x303A, UsagePage = 0xFF52;
    public const byte CommandStatus = 0, CommandPair = 1, CommandForget = 2;
    const byte ReportRemote = 1, ReportState = 2, ReportCommand = 3;
    const byte StateIdle = 0, StatePairing = 1, StateConnected = 3;

    SafeFileHandle? _handle;
    string? _lastStatus;

    void Status(string message)
    {
        if (message == _lastStatus) return;
        _lastStatus = message;
        onStatus(message);
    }

    public static HidDeviceInfo? Find() =>
        HidEnumerator.ListAll().FirstOrDefault(d => d.VendorId == VendorId && d.UsagePage == UsagePage);

    /// <summary>Sends a command to the dongle; false if it is not open or refused it.</summary>
    public bool Send(byte command)
    {
        try { return _handle is { IsInvalid: false } h && HidD_SetFeature(h, [ReportCommand, command], 2); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Read loop: only returns on cancellation; waits for the dongle if it is unplugged.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Find() is { } info) await ReadAsync(info, ct);
                else Status(Loc.T("Status.WaitDongle"));
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException)
            {
                Status(Loc.T("Status.Error", e.Message));   // unplugged while reading
            }
            finally
            {
                onReport(SiriRemoteDriver.HostNow, []);   // releases buttons and fingers
            }
        }
    }

    async Task ReadAsync(HidDeviceInfo info, CancellationToken ct)
    {
        using SafeFileHandle handle = HidEnumerator.Open(info.Path);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        _handle = handle;
        try
        {
            Send(CommandStatus);   // the dongle answers with a state report
            using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
            var buffer = new byte[info.InputLength];
            while (true)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) throw new IOException("end of HID stream");
                if (buffer[0] == ReportRemote) onReport(SiriRemoteDriver.HostNow, buffer[1..n]);
                else if (buffer[0] == ReportState && n >= 4) OnState(buffer[1], buffer[2], buffer[3]);
            }
        }
        finally
        {
            _handle = null;
        }
    }

    void OnState(byte state, byte battery, byte reason)
    {
        if (battery is > 0 and <= 100) onBattery?.Invoke(battery);
        if (state != StateConnected) onReport(SiriRemoteDriver.HostNow, []);
        Status(state switch
        {
            StateConnected => Loc.T("Status.ConnectedDongle"),
            StateIdle => Loc.T("Status.DongleIdle"),
            StatePairing => Loc.T("Status.WaitPairing"),
            _ => reason != 0 ? Loc.T("Status.DisconnectedReason", reason) : Loc.T("Status.WaitBonded"),
        });
    }
}
