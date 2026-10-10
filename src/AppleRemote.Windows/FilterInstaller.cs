using System.Runtime.InteropServices;
using System.Text;

namespace AppleRemote.Windows;

/// <summary>
/// Détection (lecture seule, sans droits administrateur) du filtre noyau AppleRemoteDriver.
/// L'installation et la désinstallation, qui demandent l'élévation, sont dans AppleRemote.DriverInstaller.
/// </summary>
public static class FilterInstaller
{
    const string ServiceName = "AppleRemoteDriver";

    static Guid _bluetoothClass = new("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}");

    /// <summary>Vrai si le filtre est dans les LowerFilters d'un adaptateur Bluetooth présent.</summary>
    public static bool IsInstalled()
    {
        IntPtr set = SetupDiGetClassDevs(ref _bluetoothClass, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == (IntPtr)(-1)) return false;
        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = (uint)Marshal.SizeOf(data);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!IsAdapter(set, ref data)) continue;
                var filters = ReadMultiSz(set, ref data, SPDRP_LOWERFILTERS);
                if (Array.Exists(filters, f => f.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Adaptateur (USB, PCI…) et non périphérique enfant : les énumérateurs « BTH* » (BTHENUM, BTHLE…) sont des enfants.</summary>
    static bool IsAdapter(IntPtr set, ref SP_DEVINFO_DATA data) =>
        ReadMultiSz(set, ref data, SPDRP_ENUMERATOR_NAME) is [var enumerator, ..]
        && !enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

    static string[] ReadMultiSz(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        byte[] buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out uint size) || size < 2)
            return [];
        return Encoding.Unicode.GetString(buffer, 0, (int)size - 2).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    const uint DIGCF_PRESENT = 0x00000002;
    const uint SPDRP_LOWERFILTERS = 0x00000012;
    const uint SPDRP_ENUMERATOR_NAME = 0x00000016;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // Unicode obligatoire : sans cela, setupapi renvoie les chaînes en ANSI et le décodage UTF-16 donne du bruit.
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
