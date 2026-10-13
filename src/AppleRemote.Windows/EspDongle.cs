// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>
/// ESP32-S3 dongle (src/AppleRemoteEsp32): it keeps the remote itself (BLE central, bond in its flash, unlock on every
/// connection) and forwards it over USB HID, vendor collection 0xFF52 under Espressif's VID. Report 1 (in): remote report
/// in hid.dll format (0xFC…), report 2 (in): state, battery %, last disconnect reason, feature 3: command,
/// feature 4: firmware update (<see cref="UpdateFirmwareAsync"/>).
/// </summary>
public sealed class EspDongle(Action<double, byte[]?> onReport, Action<string> onStatus, Action<int>? onBattery = null)
{
    public const ushort VendorId = 0x303A, UsagePage = 0xFF52;
    public const byte CommandStatus = 0, CommandPair = 1, CommandForget = 2;
    const byte ReportRemote = 1, ReportState = 2, ReportCommand = 3;
    const byte StateIdle = 0, StatePairing = 1, StateConnected = 3;

    SafeFileHandle? _handle;
    int _featureLength = 2;
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
        // Windows wants a buffer of the collection's FeatureReportByteLength (the OTA report sets it), zero-padded.
        var report = new byte[_featureLength];
        report[0] = ReportCommand;
        report[1] = command;
        try { return _handle is { IsInvalid: false } h && HidD_SetFeature(h, report, report.Length); }
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
        _featureLength = Math.Max(info.FeatureLength, 2);
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

    // ---- firmware update (protocol mirrored in src/AppleRemoteEsp32/main/ota.c) ----------------

    const byte ReportOta = 4, OtaBegin = 1, OtaData = 2, OtaEnd = 3, OtaAbort = 4;
    const byte OtaReceiving = 1, OtaVerifying = 2, OtaDone = 3;
    const int OtaChunk = 57;   // 63-byte payload - op, offset, length

    /// <summary>Version of the firmware running on the dongle; null without a dongle or if it does not answer.</summary>
    public static string? ReadFirmwareVersion()
    {
        if (Find() is not { } info) return null;
        using SafeFileHandle handle = HidEnumerator.Open(info.Path);
        try { return handle.IsInvalid ? null : GetOta(handle, new byte[info.FeatureLength]).Version; }
        catch (Win32Exception) { return null; }   // firmware without the update report
    }

    /// <summary>
    /// Sends <paramref name="image"/> to the dongle, which writes it to its inactive slot, checks its signature and version,
    /// then reboots on it. Throws with a user message if the dongle refuses it (it then keeps its current firmware).
    /// </summary>
    /// <param name="progress">Percentage sent.</param>
    public static Task UpdateFirmwareAsync(byte[] image, IProgress<int>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (FirmwareImage.Problem(image) is { } problem) throw new InvalidDataException(Loc.T(problem));
        HidDeviceInfo info = Find() ?? throw new IOException(Loc.T("Firmware.NoDongle"));
        using SafeFileHandle handle = HidEnumerator.Open(info.Path);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var report = new byte[info.FeatureLength];
        try
        {
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)image.Length);
            SetOta(handle, report, [OtaBegin, .. size]);
            Expect(handle, report, OtaReceiving);

            var offset = new byte[4];
            for (int sent = 0, chunk = 0; sent < image.Length; sent += OtaChunk, chunk++)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(OtaChunk, image.Length - sent);
                BinaryPrimitives.WriteUInt32LittleEndian(offset, (uint)sent);
                SetOta(handle, report, [OtaData, .. offset, (byte)n, .. image.AsSpan(sent, n)]);
                if (chunk % 256 == 0)   // a refused chunk stops everything on the dongle: no point sending the rest
                {
                    Expect(handle, report, OtaReceiving);
                    progress?.Report(sent * 100 / image.Length);
                }
            }
            SetOta(handle, report, [OtaEnd]);

            // Hash and signature check on the dongle, then it reboots by itself on the new firmware.
            for (var clock = Stopwatch.StartNew(); clock.Elapsed < TimeSpan.FromSeconds(30); Thread.Sleep(200))
            {
                byte status = GetOta(handle, report).Status;
                if (status == OtaDone) { progress?.Report(100); return; }
                if (status != OtaVerifying) throw Refused(status);
            }
            throw new TimeoutException(Loc.T("Firmware.Timeout"));
        }
        catch (Exception) when (!handle.IsClosed)
        {
            try { SetOta(handle, report, [OtaAbort]); } catch (Exception) { /* dongle gone */ }
            throw;
        }
    }, ct);

    static void SetOta(SafeFileHandle handle, byte[] report, ReadOnlySpan<byte> payload)
    {
        Array.Clear(report);
        report[0] = ReportOta;
        payload.CopyTo(report.AsSpan(1));
        if (!HidD_SetFeature(handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    static (byte Status, string Version) GetOta(SafeFileHandle handle, byte[] report)
    {
        Array.Clear(report);
        report[0] = ReportOta;
        if (!HidD_GetFeature(handle, report, report.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // [ID, status, bytes written (u32), version (32, NUL-padded)]
        return (report[1], Encoding.ASCII.GetString(report, 6, Math.Min(32, report.Length - 6)).TrimEnd('\0'));
    }

    static void Expect(SafeFileHandle handle, byte[] report, byte expected)
    {
        byte status = GetOta(handle, report).Status;
        if (status != expected) throw Refused(status);
    }

    static InvalidDataException Refused(byte status) => new(status switch
    {
        0x82 => Loc.T("Firmware.BadSize"),
        0x86 => Loc.T("Firmware.BadSignature"),
        0x87 => Loc.T("Firmware.Downgrade"),
        0x88 => Loc.T("Firmware.WrongProject"),
        _ => Loc.T("Firmware.Failed", status),
    });
}
