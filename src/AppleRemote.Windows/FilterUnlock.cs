// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AppleRemote.Windows;

/// <summary>
/// Unlocks the remote over GATT when HidD_SetFeature(0xF0) is refused (error 87 on Windows): 0xAF is written to the
/// "battery level" (0x2A19) without response; the AppleRemoteDriver filter rewrites it as a write of the 0xF0 report (handle 0x28 -> 0x1d).
/// Report reading remains that of Windows' HID collection.
/// </summary>
static partial class FilterUnlock
{
    /// <summary>The Bluetooth address is the last element of the HID path: ..._rev&amp;0000_149d9926bfcc#...</summary>
    [GeneratedRegex("_([0-9a-f]{12})#", RegexOptions.IgnoreCase)]
    private static partial Regex AddressInPath();

    /// <summary>The LE device behind a HID collection, or null (path without an address, WinRT unavailable).</summary>
    public static async Task<BluetoothLEDevice?> OpenAsync(string hidPath)
    {
        var match = AddressInPath().Match(hidPath);
        if (!match.Success) return null;
        try { return await BluetoothLEDevice.FromBluetoothAddressAsync(Convert.ToUInt64(match.Groups[1].Value, 16)); }
        catch (Exception) { return null; }
    }

    public static async Task<bool> TryAsync(BluetoothLEDevice device)
    {
        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) return false;

        // Cache first: an "Uncached" discovery on every unlock is heavy and can make Windows recreate the HID collection.
        var level = await FindBatteryLevelAsync(device, BluetoothCacheMode.Cached)
                    ?? await FindBatteryLevelAsync(device, BluetoothCacheMode.Uncached);
        if (level is null) return false;

        // Notification subscription: the filter redirects this write of the battery-level CCCD (0x29) to the HID reports one (0x24).
        // Without it the remote, even unlocked, notifies nothing.
        try { await level.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify); }
        catch (Exception) { /* Notify property missing on the Windows side: try the unlock anyway */ }

        var result = await level.WriteValueAsync(new byte[] { 0xAF }.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        return result == GattCommunicationStatus.Success;
    }

    /// <summary>Battery level in % read over GATT (reads are not redirected by the filter). Throws with the reason on failure.</summary>
    public static async Task<int> ReadBatteryAsync(BluetoothLEDevice device)
    {
        var level = await FindBatteryLevelAsync(device, BluetoothCacheMode.Cached)
                    ?? await FindBatteryLevelAsync(device, BluetoothCacheMode.Uncached)
                    ?? throw new InvalidOperationException("battery level 0x180F/0x2A19 not found");
        var read = await level.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0)
            throw new InvalidOperationException($"battery level read: {read.Status}, ATT error {read.ProtocolError?.ToString("X2") ?? "none"}");
        return global::Windows.Storage.Streams.DataReader.FromBuffer(read.Value).ReadByte();
    }

    static async Task<global::Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic?> FindBatteryLevelAsync(BluetoothLEDevice device, BluetoothCacheMode mode)
    {
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, mode);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return null;
        var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, mode);
        return characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0
            ? characteristics.Characteristics[0]
            : null;
    }
}
