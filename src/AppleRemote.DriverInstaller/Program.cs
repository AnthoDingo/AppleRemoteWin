// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using AppleRemote.DriverInstaller;

// Usage: AppleRemote.DriverInstaller.exe install|uninstall [--package <folder>] [--quiet]
// Exit code: 0 = done, 3010 = reboot needed, 1 = failure (message in %TEMP%\AppleRemoteDriver.log, read by the application).
bool quiet = args.Contains("--quiet");
string? package = args.SkipWhile(a => a != "--package").Skip(1).FirstOrDefault();
string logPath = Path.Combine(Path.GetTempPath(), "AppleRemoteDriver.log");

Console.WriteLine("=== AppleRemote Driver Installer ===");
if (args.Length == 0 || args[0] is not ("install" or "uninstall"))
{
    Console.WriteLine("Usage: AppleRemote.DriverInstaller.exe install|uninstall [--package <folder>] [--quiet]");
    return 1;
}

int exitCode;
try
{
    File.Delete(logPath);
    exitCode = args[0] == "install" ? FilterPackage.Install(package) : FilterPackage.Uninstall();
    Console.WriteLine(exitCode == FilterPackage.RebootNeeded ? "Done: reboot the PC." : "Done.");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    File.WriteAllText(logPath, ex.Message);
    exitCode = 1;
}

if (!quiet)
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}
return exitCode;
