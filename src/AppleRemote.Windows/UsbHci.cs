using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static AppleRemote.Windows.Native.NativeMethods;

namespace AppleRemote.Windows;

/// <summary>
/// Contrôleur Bluetooth piloté directement en USB (transport HCI « H2 ») via WinUSB, sans la pile Bluetooth de Windows :
/// commandes par transfert de contrôle, événements par le point d'accès interruption, données ACL par les points bulk.
/// L'adaptateur doit avoir le pilote WinUSB (Zadig) ; Windows ne peut alors plus s'en servir.
/// </summary>
public sealed class UsbHci : IDisposable
{
    readonly SafeFileHandle _file;
    readonly IntPtr _usb;
    readonly byte _eventPipe, _aclInPipe, _aclOutPipe;
    readonly Thread _eventReader, _aclReader;
    volatile bool _disposed;

    public HciHost Host { get; }

    /// <summary>Erreur de lecture USB (adaptateur retiré, pilote changé…). Levé depuis un thread de lecture.</summary>
    public event Action<string>? Failed;

    UsbHci(SafeFileHandle file, IntPtr usb)
    {
        _file = file;
        _usb = usb;
        (_eventPipe, _aclInPipe, _aclOutPipe) = FindPipes(usb);
        Host = new HciHost(SendCommand, SendAcl);
        _eventReader = StartReader(_eventPipe, 260, Host.ReceiveEventBytes, "événements");
        _aclReader = StartReader(_aclInPipe, 1024, Host.ReceiveAclBytes, "ACL");
    }

    /// <summary>Identifiant USB (VID/PID) d'un adaptateur.</summary>
    public readonly record struct UsbId(ushort Vid, ushort Pid)
    {
        public override string ToString() => $"{Vid:X4}:{Pid:X4}";
    }

    /// <summary>Chemins des périphériques USB présents.</summary>
    static IEnumerable<string> EnumerateUsbPaths()
    {
        Guid guid = GUID_DEVINTERFACE_USB_DEVICE;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs a échoué");
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) yield break;
                string? path = HidEnumerator.GetInterfacePath(set, ref data);
                if (path is not null) yield return path;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    static readonly System.Text.RegularExpressions.Regex IdPattern =
        new(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Ouvre le premier périphérique USB sous WinUSB dont l'interface 0 est un contrôleur Bluetooth
    /// (classe 0xE0, sous-classe 1, protocole 1), quels que soient son VID/PID.
    /// </summary>
    static bool TryOpenAdapter(out SafeFileHandle file, out IntPtr usb, out UsbId id)
    {
        foreach (string path in EnumerateUsbPaths())
        {
            var m = IdPattern.Match(path);
            if (!m.Success) continue;
            var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            if (WinUsb_Initialize(handle, out IntPtr h)
                && WinUsb_QueryInterfaceSettings(h, 0, out var d)
                && d.bInterfaceClass == 0xE0 && d.bInterfaceSubClass == 1 && d.bInterfaceProtocol == 1)
            {
                file = handle;
                usb = h;
                id = new UsbId(Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16));
                return true;
            }
            if (h != IntPtr.Zero) WinUsb_Free(h);
            handle.Dispose();
        }
        file = null!;
        usb = IntPtr.Zero;
        id = default;
        return false;
    }

    /// <summary>Adaptateur Bluetooth déjà sous WinUSB, ou null.</summary>
    public static UsbId? FindWinUsbAdapter()
    {
        if (!TryOpenAdapter(out var file, out var usb, out var id)) return null;
        WinUsb_Free(usb);
        file.Dispose();
        return id;
    }

    /// <summary>Ouvre le premier adaptateur Bluetooth USB sous WinUSB.</summary>
    public static UsbHci Open()
    {
        if (!TryOpenAdapter(out var file, out var usb, out _))
            throw new InvalidOperationException(
                "Aucun adaptateur Bluetooth USB sous WinUSB (pilote WinUSB installé, PC redémarré depuis, adaptateur pas déjà utilisé ?).");
        return new UsbHci(file, usb);
    }

    static (byte Event, byte AclIn, byte AclOut) FindPipes(IntPtr usb)
    {
        byte evt = 0, aclIn = 0, aclOut = 0;
        if (!WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Lecture de l'interface USB impossible");
        for (byte i = 0; i < descriptor.bNumEndpoints; i++)
        {
            if (!WinUsb_QueryPipe(usb, 0, i, out var pipe)) continue;
            bool input = (pipe.PipeId & 0x80) != 0;
            if (pipe.PipeType == UsbdPipeTypeInterrupt && input) evt = pipe.PipeId;
            else if (pipe.PipeType == UsbdPipeTypeBulk && input) aclIn = pipe.PipeId;
            else if (pipe.PipeType == UsbdPipeTypeBulk) aclOut = pipe.PipeId;
        }
        if (evt == 0 || aclIn == 0 || aclOut == 0)
            throw new InvalidOperationException("Ce périphérique USB n'a pas les points d'accès d'un contrôleur Bluetooth.");
        return (evt, aclIn, aclOut);
    }

    void SendCommand(byte[] packet)
    {
        // Commande HCI : requête de classe vers le périphérique (bmRequestType 0x20, bRequest 0).
        var setup = new WINUSB_SETUP_PACKET { RequestType = 0x20, Length = (ushort)packet.Length };
        if (!WinUsb_ControlTransfer(_usb, setup, packet, (uint)packet.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Envoi de la commande HCI impossible");
    }

    void SendAcl(byte[] packet)
    {
        if (!WinUsb_WritePipe(_usb, _aclOutPipe, packet, (uint)packet.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Envoi ACL impossible");
    }

    Thread StartReader(byte pipe, int size, SpanAction receive, string name)
    {
        var thread = new Thread(() =>
        {
            var buffer = new byte[size];
            while (!_disposed)
            {
                if (WinUsb_ReadPipe(_usb, pipe, buffer, (uint)size, out uint read, IntPtr.Zero))
                {
                    if (read > 0) receive(buffer.AsSpan(0, (int)read));
                }
                else if (!_disposed)
                {
                    Failed?.Invoke($"Lecture USB ({name}) interrompue : {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    return;
                }
            }
        }) { IsBackground = true, Name = $"UsbHci {name}" };
        thread.Start();
        return thread;
    }

    delegate void SpanAction(ReadOnlySpan<byte> data);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinUsb_AbortPipe(_usb, _eventPipe);   // débloque les lectures en cours
        WinUsb_AbortPipe(_usb, _aclInPipe);
        _eventReader.Join(1000);
        _aclReader.Join(1000);
        WinUsb_Free(_usb);
        _file.Dispose();
    }
}
