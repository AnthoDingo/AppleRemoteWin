// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Diagnostics;
using System.Text;
using AppleRemote.Windows;
using Microsoft.Win32;

namespace AppleRemote.DriverInstaller;

/// <summary>
/// Installs / removes the AppleRemoteDriver kernel filter through its package (extension INF + catalog), like `pnputil`: the Qualcomm
/// driver and its BtFilter stay in place, Windows manages the driver store. Requires administrator rights and test-signing mode.
/// </summary>
static class FilterPackage
{
    public const int Ok = 0, RebootNeeded = 3010;

    const string InfName = "AppleRemoteDriver.inf";
    const string CertName = "WDRLocalTestCert.cer";
    const string AdapterHardwareId = @"USB\VID_0CF3&PID_E007"; // QCA61x4A, the only adapter targeted by the INF

    public static int Install(string? packageDir)
    {
        RequireTestSigning();
        string dir = FindPackage(packageDir);
        Say($"Package: {dir}");

        // The catalog is signed with a test certificate: it must be trusted for pnputil to accept the package.
        string cer = Path.Combine(dir, CertName);
        if (!File.Exists(cer)) throw new FileNotFoundException($"{CertName} not found in {dir}.");
        foreach (string store in new[] { "Root", "TrustedPublisher" })
            Check(Run("certutil", $"-addstore -f {store} \"{cer}\""), $"certutil -addstore {store}");

        // Update over the old package without uninstalling it first: removing a loaded filter leaves the adapter
        // "pending PC restart", and Windows then refuses to restart it. The newest package wins.
        string[] previous = PublishedPackages();
        int code = Run("pnputil", $"/add-driver \"{Path.Combine(dir, InfName)}\" /install").ExitCode;
        const int AlreadyInstalled = 259; // ERROR_NO_MORE_ITEMS: package already in the store and already bound to the adapter
        if (code == AlreadyInstalled) code = 0;
        if (code is not (0 or RebootNeeded))
            throw new InvalidOperationException($"pnputil /add-driver failed (code {code}). Is adapter {AdapterHardwareId} present?");

        if (!RestartAdapter())
            return RebootNeeded; // the package is in place: it will load on next boot
        RepairEnumerators();

        // Old packages are no longer used since the adapter restart: removed from the store (without /uninstall,
        // so without touching the device). A failure is harmless: they just stay unused.
        string[] current = PublishedPackages();
        foreach (string oem in previous.Intersect(current).Where(o => current.Except(previous).Any()))
        {
            var removed = Run("pnputil", $"/delete-driver {oem}");
            Say(removed.ExitCode == 0 ? $"Old package {oem} removed." : $"Old package {oem} kept (code {removed.ExitCode}).");
        }
        if (!FilterInstaller.IsInstalled())
            throw new InvalidOperationException("Package installed, but the filter is not in the adapter's LowerFilters.");
        // pnputil answers 3010 as soon as a package file was loaded; the device restart above reloaded the driver.
        // A PC reboot is only requested if the filter service is not actually running.
        if (!WaitForService("Running", 5))
            return RebootNeeded;
        if (code == RebootNeeded) Say("pnputil asked for a reboot: not needed, the driver is already loaded.");
        return Ok;
    }

    /// <summary>Removes the package (if installed) and puts the adapter back without the filter.</summary>
    public static int Uninstall()
    {
        string[] published = PublishedPackages();
        foreach (string oem in published)
        {
            Say($"Removing package {oem}");
            Check(Run("pnputil", $"/delete-driver {oem} /uninstall /force"), $"pnputil /delete-driver {oem}");
        }
        if (published.Length == 0) return Ok;
        if (!RestartAdapter()) return RebootNeeded;
        RepairEnumerators();
        if (FilterInstaller.IsInstalled())
            throw new InvalidOperationException("The filter is still in the LowerFilters (installed by an older method?).");
        // The filter left the stack: its service should stop after the device restart. Only otherwise, PC reboot.
        return WaitForService("Stopped", 8) ? Ok : RebootNeeded;
    }

    /// <summary>AppleRemoteDriver packages present in the driver store (oemNN.inf).</summary>
    static string[] PublishedPackages() =>
        Ps("(Get-WindowsDriver -Online | Where-Object OriginalFileName -like '*" + InfName + "').Driver")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Waits for the AppleRemoteDriver service to reach the wanted state ("Running", "Stopped"); a missing service counts as stopped.</summary>
    static bool WaitForService(string wanted, int seconds)
    {
        for (int i = 0; i <= seconds; i++)
        {
            string state = Ps("(Get-Service AppleRemoteDriver -ErrorAction SilentlyContinue).Status").Trim();
            if (state.Equals(wanted, StringComparison.OrdinalIgnoreCase) || (state.Length == 0 && wanted == "Stopped")) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    static void RequireTestSigning()
    {
        string options = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "SystemStartOptions", "") as string ?? "";
        if (!options.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test-signing mode is disabled: the test driver would not load. " +
                "Run \"bcdedit /set testsigning on\" then reboot.");
    }

    /// <summary>The folder containing the INF: the requested one, the exe's, otherwise the latest build of src\AppleRemoteDriver.</summary>
    static string FindPackage(string? requested)
    {
        var candidates = new List<string>();
        if (requested is not null) candidates.Add(requested);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "driver")); // publish: <application folder>\driver\
            candidates.Add(dir.FullName);
            foreach (string config in new[] { "release", "debug" })
                candidates.Add(Path.Combine(dir.FullName, "src", "AppleRemoteDriver", "target", config, "AppleRemoteDriver_package"));
        }
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, InfName)))
               ?? throw new FileNotFoundException($"{InfName} not found: build AppleRemoteDriver with \"cargo make --profile release\".");
    }

    /// <summary>
    /// Restarts the adapter to (un)load the filter. False if Windows refuses (adapter pending a PC restart,
    /// or a handle held open by an application blocking the stack teardown): the PC must then be rebooted.
    /// </summary>
    static bool RestartAdapter()
    {
        string id = Ps($"(Get-PnpDevice -PresentOnly | Where-Object InstanceId -like '{AdapterHardwareId}*' | Select-Object -First 1).InstanceId").Trim();
        if (id.Length == 0) throw new InvalidOperationException($"Adapter {AdapterHardwareId} not found.");
        // Windows Bluetooth services keep the adapter open and veto its removal (Kernel-PnP event 225); pnputil
        // then answers 3010 and Windows marks the adapter "restart required" until the next boot. So they are taken
        // down BEFORE the adapter restart. Stopping is not enough: bthserv is trigger-started and Windows relaunches it
        // during the restart, so each one is disabled for the duration, then its startup type and state are restored.
        string[] holders = ["bthserv", "DeviceAssociationService"];
        var saved = holders
            .Select(s => (Name: s, State: Ps($"(Get-Service {s} -ErrorAction SilentlyContinue).Status").Trim(),
                          StartType: Ps($"(Get-Service {s} -ErrorAction SilentlyContinue).StartType").Trim()))
            .Where(s => s.StartType.Length > 0)
            .ToArray();
        (int ExitCode, string Output) result;
        try
        {
            foreach (var s in saved)
                Ps($"Set-Service {s.Name} -StartupType Disabled -ErrorAction SilentlyContinue; Stop-Service {s.Name} -Force -ErrorAction SilentlyContinue");
            result = Run("pnputil", $"/restart-device \"{id}\"");
        }
        finally
        {
            foreach (var s in saved)
            {
                Ps($"Set-Service {s.Name} -StartupType {s.StartType} -ErrorAction SilentlyContinue");
                if (s.State == "Running") Ps($"Start-Service {s.Name} -ErrorAction SilentlyContinue");
            }
        }
        if (result.ExitCode != 0) // 3010 included: the stack was not reloaded
        {
            Say($"Adapter restart refused (code {result.ExitCode}): {result.Output.Trim()}");
            return false;
        }
        Thread.Sleep(3000);
        return true;
    }

    /// <summary>
    /// A device restart sometimes leaves the Bluetooth enumerators (BTH\MS_BTHLE, MS_BTHBRB, MS_RFCOMM) in error
    /// (code 19, 0xC0000034): the BLE scanner no longer starts. Removing them then rescanning recreates them.
    /// </summary>
    static void RepairEnumerators()
    {
        const string failed = "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\\MS_*' -and $_.Status -eq 'Error' }";
        string ids = Ps($"({failed}).InstanceId").Trim();
        if (ids.Length == 0) return;
        foreach (string id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Say($"Bluetooth enumerator in error, recreating: {id}");
            Run("pnputil", $"/remove-device \"{id}\"");
        }
        Run("pnputil", "/scan-devices");
        Thread.Sleep(3000);
        if (Ps($"({failed}).InstanceId").Trim().Length > 0)
            Say("Some Bluetooth enumerators are still in error: reboot the PC.");
    }

    static string Ps(string script)
    {
        var result = Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"");
        return result.Output;
    }

    static (int ExitCode, string Output) Run(string file, string arguments)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {file}.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    static void Check((int ExitCode, string Output) result, string what)
    {
        if (result.ExitCode is 0 or RebootNeeded) return;
        throw new InvalidOperationException($"{what} failed (code {result.ExitCode}): {result.Output.Trim()}");
    }

    static void Say(string message) => Console.WriteLine(message);
}
