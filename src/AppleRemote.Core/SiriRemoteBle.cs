// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;

namespace AppleRemote;

/// <summary>
/// Siri Remote gen 1 driver over a directly driven Bluetooth controller (HCI), without the Windows Bluetooth stack:
/// paired remote (address from the <see cref="BondStore"/>) or, failing that, a search for a remote in pairing mode;
/// connection, encryption with the stored keys (or pairing if there are none or they are rejected), HID service
/// (subscription to input reports, 0xF0 &lt;- 0xAF unlock), battery level, then waiting for sleep
/// and reconnection. Reports come out in hid.dll format (ID first: 0xFC…), like those of <c>SiriRemoteDriver</c>:
/// same decoder, same session.
/// </summary>
/// <param name="onBattery">Battery level in %: on each connection, then on each change. Transport thread.</param>
public sealed class SiriRemoteBle(HciHost hci, BondStore bonds,
    Action<double, byte[]?> onReport, Action<string> onStatus, Action<int>? onBattery = null)
{
    const ushort HidService = 0x1812, Report = 0x2A4D, ReportReference = 0x2908, ClientConfiguration = 0x2902,
        BatteryService = 0x180F, BatteryLevel = 0x2A19;
    const byte InputReport = 1, FeatureReport = 3, UnlockReportId = 0xF0, UnlockValue = 0xAF;

    static double HostNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// Advertisement of a Siri Remote in pairing mode: HID service (0x1812) announced and Apple manufacturer data (0x004C).
    /// </summary>
    public static bool IsSiriRemoteAdvertisement(byte[] data)
    {
        bool hid = false, apple = false;
        for (int i = 0; i + 1 < data.Length && data[i] > 0; i += data[i] + 1)
        {
            int length = data[i], type = data[i + 1];
            if (i + 1 + length > data.Length) break;
            if (type is 0x02 or 0x03)   // list (partial or complete) of 16-bit services
                for (int j = i + 2; j + 1 <= i + length; j += 2)
                    hid |= (data[j] | data[j + 1] << 8) == HidService;
            if (type == 0xFF && length >= 3)
                apple |= data[i + 2] == 0x4C && data[i + 3] == 0x00;
        }
        return hid && apple;
    }

    /// <summary>Connection loop: only returns on cancellation (the current connection is then closed).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var central = new LeCentral(hci);
        TaskCompletionSource<byte>? gone = null;
        central.Disconnected += (_, reason) => gone?.TrySetResult(reason);
        await central.InitializeAsync();

        while (!ct.IsCancellationRequested)
        {
            gone = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            string address;
            ushort handle;
            try
            {
                if (bonds.Addresses.FirstOrDefault() is { } known)
                {
                    address = known;
                    onStatus(Loc.T("Status.WaitBonded"));
                }
                else
                {
                    onStatus(Loc.T("Status.WaitPairing"));
                    address = await central.ScanAsync(IsSiriRemoteAdvertisement, ct);
                    onStatus(Loc.T("Status.Found", address));
                }
                handle = await central.ConnectAsync(address, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            GattClient? gatt = null;
            try
            {
                await SecureAsync(central, handle, address);

                gatt = new GattClient(central, handle);
                var inputs = new Dictionary<ushort, byte>();   // handle de valeur -> ID du rapport
                ushort battery = 0;                             // value handle of the battery level (0: none)
                gatt.Notification += (valueHandle, value) =>
                {
                    if (inputs.TryGetValue(valueHandle, out byte id)) onReport(HostNow, [id, .. value]);
                    else if (valueHandle == battery && value.Length > 0) onBattery?.Invoke(value[0]);
                };
                List<GattService> services = await gatt.DiscoverPrimaryServicesAsync();
                await SetUpHidAsync(gatt, services, inputs);
                battery = await SetUpBatteryAsync(gatt, services);
                onStatus(Loc.T("Status.ConnectedDirect"));

                byte reason = await gone.Task.WaitAsync(ct);
                onStatus(Loc.T("Status.DisconnectedReason", reason));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { await central.DisconnectAsync(handle); } catch { /* already gone */ }
            }
            catch (Exception e)
            {
                onStatus(Loc.T("Status.Error", e.Message));
                if (!gone.Task.IsCompleted)
                    try { await central.DisconnectAsync(handle); await gone.Task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            finally
            {
                gatt?.Detach();
                onReport(HostNow, []);   // releases buttons and fingers
            }
        }
    }

    /// <summary>
    /// Encrypts with the stored keys; without keys, or if the remote rejects them (it was paired elsewhere meanwhile),
    /// pairs "Just Works" on the same connection and stores the new LTK.
    /// </summary>
    async Task SecureAsync(LeCentral central, ushort handle, string address)
    {
        if (bonds.Load(address) is { } bond)
        {
            try
            {
                await central.EncryptAsync(handle, bond);
                return;
            }
            // Only 0x06 (key missing on the remote) and 0x05 (authentication failure) mean our keys are no longer valid.
            // Anything else (0x3E: link lost…) bubbles up: we retry with the same keys on the next connection.
            catch (HciException e) when (e.Status is 0x05 or 0x06)
            {
                onStatus(Loc.T("Status.KeysRefused", e.Status));
            }
        }
        else
        {
            onStatus(Loc.T("Status.PairingFirst"));
        }
        BondKeys keys = await SmpPairing.PairAsync(central, handle, central.LocalAddress, BdAddr.Parse(address));
        bonds.Save(address, keys);
    }

    /// <summary>Finds the HID reports through their Report Reference descriptor, subscribes to the inputs then unlocks.</summary>
    static async Task SetUpHidAsync(GattClient gatt, List<GattService> services, Dictionary<ushort, byte> inputs)
    {
        GattService hid = services.FirstOrDefault(s => GattClient.Is(s.Uuid, HidService))
            ?? throw new InvalidOperationException("Service HID introuvable.");
        List<GattCharacteristic> characteristics = await gatt.DiscoverCharacteristicsAsync(hid);

        var subscriptions = new List<ushort>();
        ushort? unlock = null;
        for (int i = 0; i < characteristics.Count; i++)
        {
            GattCharacteristic c = characteristics[i];
            ushort end = i + 1 < characteristics.Count ? (ushort)(characteristics[i + 1].Declaration - 1) : hid.End;
            if (!GattClient.Is(c.Uuid, Report) || c.Value >= end) continue;

            var descriptors = await gatt.DiscoverDescriptorsAsync((ushort)(c.Value + 1), end);
            var reference = descriptors.FirstOrDefault(d => GattClient.Is(d.Uuid, ReportReference));
            if (reference.Uuid is null) continue;
            byte[] idAndType = await gatt.ReadAsync(reference.Handle);
            if (idAndType.Length < 2) continue;

            if (idAndType[1] == InputReport)
            {
                inputs[c.Value] = idAndType[0];
                var cccd = descriptors.FirstOrDefault(d => GattClient.Is(d.Uuid, ClientConfiguration));
                if (cccd.Uuid is not null) subscriptions.Add(cccd.Handle);
            }
            else if (idAndType[1] == FeatureReport && idAndType[0] == UnlockReportId)
            {
                unlock = c.Value;
            }
        }

        foreach (ushort cccd in subscriptions) await gatt.WriteAsync(cccd, 0x01, 0x00);   // notifications
        await gatt.WriteAsync(unlock ?? throw new InvalidOperationException("Unlock report 0xF0 not found."), UnlockValue);
    }

    /// <summary>
    /// Battery level (service 0x180F, characteristic 0x2A19, in %): read once, then tracked by notification if the
    /// characteristic allows it. Optional: without it the remote remains usable. Returns the value handle (0: none).
    /// </summary>
    async Task<ushort> SetUpBatteryAsync(GattClient gatt, List<GattService> services)
    {
        if (onBattery is null || services.FirstOrDefault(s => GattClient.Is(s.Uuid, BatteryService)) is not { } service) return 0;
        try
        {
            List<GattCharacteristic> characteristics = await gatt.DiscoverCharacteristicsAsync(service);
            int i = characteristics.FindIndex(c => GattClient.Is(c.Uuid, BatteryLevel));
            if (i < 0) return 0;
            GattCharacteristic level = characteristics[i];

            byte[] value = await gatt.ReadAsync(level.Value);
            if (value.Length > 0) onBattery(value[0]);

            ushort end = i + 1 < characteristics.Count ? (ushort)(characteristics[i + 1].Declaration - 1) : service.End;
            if ((level.Properties & 0x10) != 0 && level.Value < end)   // notification possible
            {
                var cccd = (await gatt.DiscoverDescriptorsAsync((ushort)(level.Value + 1), end))
                    .FirstOrDefault(d => GattClient.Is(d.Uuid, ClientConfiguration));
                if (cccd.Uuid is not null) await gatt.WriteAsync(cccd.Handle, 0x01, 0x00);
            }
            return level.Value;
        }
        catch (AttException e)
        {
            onStatus(Loc.T("Status.BatteryUnreadable", e.Message));
            return 0;
        }
    }
}
