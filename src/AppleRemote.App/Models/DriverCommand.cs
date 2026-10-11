// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AppleRemote.Windows;

namespace AppleRemote.App.Models;

/// <summary>
/// Installing/removing WinUSB needs administrator rights while the application runs "asInvoker":
/// it relaunches itself elevated with <c>--driver install|restore</c> (see <see cref="App.OnStartup"/>); the result comes back as the exit code.
/// </summary>
public static class DriverCommand
{
    public const int Ok = 0, Failed = 1, RebootNeeded = 3010;

    static string LogPath => Path.Combine(Path.GetTempPath(), "AppleRemoteDriver.log");

    /// <summary>Elevated-process side: runs the operation and returns the exit code.</summary>
    public static int Run(string verb)
    {
        try
        {
            File.Delete(LogPath);
            switch (verb)
            {
                case "install": return WinUsbDriver.Install() ? RebootNeeded : Ok;
                case "restore": WinUsbDriver.Restore(); return Ok;
                default: throw new ArgumentException(Loc.T("Driver.UnknownVerb", verb));
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(LogPath, e.Message);
            return Failed;
        }
    }

    const string InstallerName = "AppleRemote.DriverInstaller.exe";

    /// <summary>
    /// The filter installer: next to the exe, otherwise its latest build (Release, then Debug).
    /// It holds the install logic and requests elevation itself (requireAdministrator manifest).
    /// </summary>
    static string FindInstaller()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidates = new List<string> { Path.Combine(dir.FullName, InstallerName) };
            foreach (var config in new[] { "Release", "Debug" })
                candidates.Add(Path.Combine(dir.FullName, "src", "AppleRemote.DriverInstaller", "bin", config, "net10.0-windows10.0.19041.0", InstallerName));
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return found;
        }
        throw new FileNotFoundException(Loc.T("Driver.InstallerMissing", InstallerName));
    }

    /// <summary>Application side: relaunches elevated and waits. Returns (exit code, optional error message).</summary>
    public static async Task<(int Code, string? Error)> RunElevatedAsync(string verb)
    {
        try
        {
            // Bluetooth filter: DriverInstaller (install|uninstall); WinUSB: the application relaunches itself elevated.
            var info = verb.StartsWith("filter-")
                ? new ProcessStartInfo(FindInstaller(), $"{verb["filter-".Length..]} --quiet") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }
                : new ProcessStartInfo(Environment.ProcessPath!, $"--driver {verb}") { UseShellExecute = true, Verb = "runas" };
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            string? error = process.ExitCode == Failed && File.Exists(LogPath) ? File.ReadAllText(LogPath) : null;
            return (process.ExitCode, error);
        }
        catch (FileNotFoundException e)
        {
            return (Failed, e.Message);
        }
        catch (Win32Exception) // UAC declined
        {
            return (Failed, Loc.T("Driver.ElevationDenied"));
        }
    }
}
