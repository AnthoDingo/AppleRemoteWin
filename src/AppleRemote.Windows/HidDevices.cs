using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AppleRemote.Windows.Native;
using Microsoft.Win32.SafeHandles;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>Une collection HID exposée par Windows (un « top-level collection » = un chemin d'interface).</summary>
public sealed record HidDeviceInfo(
    string Path, ushort VendorId, ushort ProductId, string Product,
    ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int FeatureLength)
{
    public bool IsSiriRemoteGen1 =>
        ProductId == HidEnumerator.RemoteProductId &&
        (VendorId == HidEnumerator.AppleBluetoothVendorId || Path.Contains("004c_pid&026d", StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"{(string.IsNullOrWhiteSpace(Product) ? "(sans nom)" : Product)}  VID {VendorId:X4} PID {ProductId:X4}  " +
        $"usage {UsagePage:X4}:{Usage:X4}  in {InputLength} out {OutputLength} feat {FeatureLength}\n    {Path}";
}

/// <summary>Énumération des périphériques HID via SetupAPI + hid.dll. Aucun pilote noyau : Windows fournit HidBthLE.</summary>
public static class HidEnumerator
{
    /// <summary>ID fournisseur Bluetooth SIG d'Apple (0x004C) ; produit 0x026D = Siri Remote gen 1 (A1513/A1962).</summary>
    public const ushort AppleBluetoothVendorId = 0x004C;
    public const ushort AppleUsbVendorId = 0x05AC;
    public const ushort RemoteProductId = 0x026D;

    /// <summary>Tous les périphériques HID présents (collections de premier niveau).</summary>
    public static IReadOnlyList<HidDeviceInfo> ListAll()
    {
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs a échoué");

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;

                string? path = GetInterfacePath(set, ref data);
                if (path is null) continue;

                var info = Query(path);
                if (info is not null) result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>Les périphériques HID Apple (diagnostic : « la télécommande est-elle vue par Windows ? »).</summary>
    public static IReadOnlyList<HidDeviceInfo> ListApple() =>
        ListAll().Where(d => d.VendorId is AppleBluetoothVendorId or AppleUsbVendorId || d.IsSiriRemoteGen1).ToList();

    /// <summary>
    /// La collection de la Siri Remote gen 1 qui porte le report d'entrée 0xFC et le report de fonctionnalité 0xF0
    /// (usage constructeur, longueurs d'entrée et de fonctionnalité non nulles).
    /// </summary>
    public static HidDeviceInfo? FindRemote()
    {
        var candidates = ListAll().Where(d => d.IsSiriRemoteGen1).ToList();
        return candidates.FirstOrDefault(d => d.UsagePage >= 0xFF00 && d.InputLength > 0 && d.FeatureLength > 0)
            ?? candidates.FirstOrDefault(d => d.InputLength >= 20 && d.FeatureLength > 0)
            ?? candidates.FirstOrDefault();
    }

    internal static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero); // 1er appel : taille requise
        if (size <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits, 6 en 32 bits (DWORD + 1 WCHAR).
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static HidDeviceInfo? Query(string path)
    {
        // Accès 0 : suffisant pour interroger les attributs, et ne gêne pas les autres applications.
        using SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return null;

        string product = "";
        var sb = new byte[256];
        if (HidD_GetProductString(h, sb, sb.Length))
            product = Encoding.Unicode.GetString(sb).TrimEnd('\0');

        ushort usagePage = 0, usage = 0, inLen = 0, outLen = 0, featLen = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pp))
        {
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) >= 0)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(pp);
            }
        }
        return new HidDeviceInfo(path, attr.VendorID, attr.ProductID, product, usagePage, usage, inLen, outLen, featLen);
    }

    /// <summary>Ouvre une collection en lecture/écriture, E/S asynchrones (overlapped).</summary>
    public static SafeFileHandle Open(string path) =>
        CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED, IntPtr.Zero);

    /// <summary>
    /// Déverrouille la télécommande : report de fonctionnalité 0xF0 = 0xAF. Sous Windows, HidD_SetFeature exige un tampon de
    /// FeatureReportByteLength octets (zéros derrière) ; si le pilote refuse, on retente avec les 2 octets nus (comme Linux).
    /// </summary>
    public static bool Unlock(SafeFileHandle handle, int featureLength)
    {
        var padded = new byte[Math.Max(featureLength, 2)];
        padded[0] = 0xF0;
        padded[1] = 0xAF;
        if (HidD_SetFeature(handle, padded, padded.Length)) return true;
        if (padded.Length == 2) return false;
        return HidD_SetFeature(handle, new byte[] { 0xF0, 0xAF }, 2);
    }
}
