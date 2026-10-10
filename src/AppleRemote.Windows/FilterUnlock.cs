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

    public static async Task<bool> TryAsync(string hidPath)
    {
        var match = AddressInPath().Match(hidPath);
        if (!match.Success) return false;
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(Convert.ToUInt64(match.Groups[1].Value, 16));
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return false;

        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) return false;
        var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
        if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0) return false;

        var level = characteristics.Characteristics[0];
        // Abonnement aux notifications : le filtre redirige cette écriture du CCCD du niveau de batterie (0x29) vers celui des rapports HID (0x24).
        // Sans cela la télécommande, même déverrouillée, ne notifie rien.
        try { await level.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify); }
        catch (Exception) { /* propriété Notify absente côté Windows : on tente quand même le déverrouillage */ }

        var result = await level.WriteValueAsync(new byte[] { 0xAF }.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        return result == GattCommunicationStatus.Success;
    }
}
