// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppleRemote.Windows;

public enum AdapterDriver
{
    /// <summary>Adapter missing (unplugged or disabled).</summary>
    Absent,
    /// <summary>Pilote d'origine (pile Bluetooth de Windows).</summary>
    Original,
    /// <summary>WinUSB (installed by this application or by Zadig).</summary>
    WinUsb,
}

/// <summary>
/// Zadig equivalent for the Bluetooth adapter: installs WinUSB (INF generated for the VID/PID, self-signed catalog,
/// trusted certificate), or removes that driver so Windows takes back the original one. <see cref="Install"/> et
/// <see cref="Restore"/> require administrator rights; <see cref="GetState"/> doesn't.
/// </summary>
public static class WinUsbDriver
{
    const string Publisher = "AppleRemoteWin", CertSubject = "CN=AppleRemoteWin WinUSB";

    static string HardwareId(ushort vid, ushort pid) => $@"USB\VID_{vid:X4}&PID_{pid:X4}";

    /// <summary>
    /// First USB Bluetooth adapter present (only one is handled): already under WinUSB, or still under the original driver
    /// (classe Bluetooth). Absent si aucun.
    /// </summary>
    public static (AdapterDriver State, UsbHci.UsbId? Id) Find()
    {
        if (UsbHci.FindWinUsbAdapter() is { } winusb) return (AdapterDriver.WinUsb, winusb);
        string hwid = Ps("Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue | Where-Object InstanceId -like 'USB\\VID_*' | " +
                         "Select-Object -First 1 -ExpandProperty InstanceId; exit 0").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(hwid, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})");
        return m.Success
            ? (AdapterDriver.Original, new UsbHci.UsbId(Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16)))
            : (AdapterDriver.Absent, null);
    }

    public static AdapterDriver GetState() => Find().State;

    /// <summary>Installs WinUSB on the adapter (administrator). Returns true if Windows requests a restart.</summary>
    public static bool Install()
    {
        if (Find().Id is not { } id) throw new InvalidOperationException("No USB Bluetooth adapter found.");
        (ushort vid, ushort pid) = (id.Vid, id.Pid);
        string hwid = HardwareId(vid, pid);
        string root = Path.Combine(Path.GetTempPath(), "AppleRemoteWinUsb-" + Guid.NewGuid().ToString("N"));
        string pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(pkg);
        try
        {
            string inf = Path.Combine(pkg, "AppleRemoteWinUsb.inf");
            File.WriteAllText(inf, BuildInf(hwid), new UTF8Encoding(false));

            // Catalog signed with a self-signed certificate trusted by the machine (what libwdi/Zadig does).
            Ps($$"""
                $c = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq '{{CertSubject}}' | Select-Object -First 1
                if (-not $c) { $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject '{{CertSubject}}' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(10) }
                $cer = Join-Path '{{root}}' 'cert.cer'
                Export-Certificate -Cert $c -FilePath $cer | Out-Null
                foreach ($s in 'Root', 'TrustedPublisher') { Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$s" | Out-Null }
                $cat = Join-Path '{{root}}' 'AppleRemoteWinUsb.cat'
                New-FileCatalog -Path '{{pkg}}' -CatalogFilePath $cat -CatalogVersion 2.0 | Out-Null
                Set-AuthenticodeSignature -FilePath $cat -Certificate $c | Out-Null
                Copy-Item $cat '{{pkg}}'
                """);

            // INSTALLFLAG_FORCE: takes the current driver's place even if it ranks better (like Zadig).
            if (!Native.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hwid, inf, 1, out bool reboot))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install the WinUSB driver");
            return reboot;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Removes the WinUSB driver from the adapter (administrator): the original package, still in the driver store,
    /// is reinstalled by Windows. Returns true if there was nothing to remove.
    /// </summary>
    public static bool Restore()
    {
        if (Find().Id is not { } id) return true;
        (ushort vid, ushort pid) = (id.Vid, id.Pid);
        string inf = Ps($"$d = {FindDevice(vid, pid)}; if ($d -and $d.Service -eq 'WinUSB') " +
                        "{ (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath').Data }").Trim();
        if (!inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) return true; // no third-party WinUSB (or inbox driver)

        string provider = Ps($"$d = {FindDevice(vid, pid)}; (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider').Data").Trim();
        Run("pnputil.exe", $"/delete-driver {inf} /uninstall /force");
        Run("pnputil.exe", "/scan-devices");
        if (provider == Publisher)
            Ps($$"""
                foreach ($s in 'My', 'Root', 'TrustedPublisher') { Get-ChildItem "Cert:\LocalMachine\$s" | Where-Object Subject -eq '{{CertSubject}}' | Remove-Item }
                """);
        return false;
    }

    static string FindDevice(ushort vid, ushort pid) =>
        $"Get-PnpDevice -PresentOnly | Where-Object {{ ($_.HardwareID -join ' ') -like '*VID_{vid:X4}&PID_{pid:X4}*' }} | Select-Object -First 1";

    static string BuildInf(string hwid) => $$"""
        [Version]
        Signature   = "$Windows NT$"
        Class       = USBDevice
        ClassGuid   = {88BAE032-5A81-49f0-BC3D-A4FF138216D6}
        Provider    = %Provider%
        CatalogFile = AppleRemoteWinUsb.cat
        DriverVer   = 01/01/2024,1.0.0.0

        [Manufacturer]
        %Provider% = Devices,NTamd64

        [Devices.NTamd64]
        %DeviceName% = USB_Install, {{hwid}}

        [USB_Install]
        Include = winusb.inf
        Needs   = WINUSB.NT

        [USB_Install.Services]
        Include = winusb.inf
        Needs   = WINUSB.NT.Services

        [USB_Install.HW]
        AddReg = Dev_AddReg

        [Dev_AddReg]
        HKR,,DeviceInterfaceGUIDs,0x10000,"{6B3E8C52-9D41-4F6A-8A1E-5C0D7B2F4E19}"

        [Strings]
        Provider   = "{{Publisher}}"
        DeviceName = "Adaptateur Bluetooth (WinUSB)"
        """;

    static string Ps(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " + script));
        return Run("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}");
    }

    static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string error = "";
        p.ErrorDataReceived += (_, e) => error += e.Data + "\n";
        p.BeginErrorReadLine();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed ({p.ExitCode}) : {(error + output).Trim()}");
        return output;
    }

    static class Native
    {
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr owner, string hardwareId, string infPath, uint flags,
            [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
    }
}
