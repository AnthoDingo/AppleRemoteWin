using AppleRemote.DriverInstaller;

// Usage : AppleRemote.DriverInstaller.exe install|uninstall [--package <dossier>] [--quiet]
// Code de sortie : 0 = fait, 3010 = redémarrage nécessaire, 1 = échec (message dans %TEMP%\AppleRemoteDriver.log, lu par l'application).
bool quiet = args.Contains("--quiet");
string? package = args.SkipWhile(a => a != "--package").Skip(1).FirstOrDefault();
string logPath = Path.Combine(Path.GetTempPath(), "AppleRemoteDriver.log");

Console.WriteLine("=== AppleRemote Driver Installer ===");
if (args.Length == 0 || args[0] is not ("install" or "uninstall"))
{
    Console.WriteLine("Usage : AppleRemote.DriverInstaller.exe install|uninstall [--package <dossier>] [--quiet]");
    return 1;
}

int exitCode;
try
{
    File.Delete(logPath);
    exitCode = args[0] == "install" ? FilterPackage.Install(package) : FilterPackage.Uninstall();
    Console.WriteLine(exitCode == FilterPackage.RebootNeeded ? "Terminé : redémarrez le PC." : "Terminé.");
}
catch (Exception ex)
{
    Console.WriteLine($"ERREUR : {ex.Message}");
    File.WriteAllText(logPath, ex.Message);
    exitCode = 1;
}

if (!quiet)
{
    Console.WriteLine("Appuyez sur une touche pour quitter...");
    Console.ReadKey();
}
return exitCode;
