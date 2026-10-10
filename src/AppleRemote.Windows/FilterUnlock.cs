using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AppleRemote.Windows;

/// <summary>
/// Déverrouille la télécommande par GATT quand HidD_SetFeature(0xF0) est refusé (erreur 87 sous Windows) : on écrit 0xAF sur le
/// « niveau de batterie » (0x2A19) sans réponse ; le filtre AppleRemoteDriver le réécrit en écriture du rapport 0xF0 (handle 0x28 -> 0x1d).
/// La lecture des rapports reste celle de la collection HID de Windows.
/// </summary>
static partial class FilterUnlock
{
    /// <summary>L'adresse Bluetooth est le dernier élément du chemin HID : ..._rev&amp;0000_149d9926bfcc#...</summary>
    [GeneratedRegex("_([0-9a-f]{12})#", RegexOptions.IgnoreCase)]
    private static partial Regex AddressInPath();

    /// <summary>Le périphérique LE derrière une collection HID, ou null (chemin sans adresse, WinRT indisponible).</summary>
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

        // Cache d'abord : une découverte « Uncached » à chaque déverrouillage est lourde et peut faire recréer la collection HID par Windows.
        var level = await FindBatteryLevelAsync(device, BluetoothCacheMode.Cached)
                    ?? await FindBatteryLevelAsync(device, BluetoothCacheMode.Uncached);
        if (level is null) return false;

        // Abonnement aux notifications : le filtre redirige cette écriture du CCCD du niveau de batterie (0x29) vers celui des rapports HID (0x24).
        // Sans cela la télécommande, même déverrouillée, ne notifie rien.
        try { await level.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify); }
        catch (Exception) { /* propriété Notify absente côté Windows : on tente quand même le déverrouillage */ }

        var result = await level.WriteValueAsync(new byte[] { 0xAF }.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        return result == GattCommunicationStatus.Success;
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
