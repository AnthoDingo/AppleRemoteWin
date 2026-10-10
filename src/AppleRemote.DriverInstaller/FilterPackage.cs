using System.Diagnostics;
using System.Text;
using AppleRemote.Windows;
using Microsoft.Win32;

namespace AppleRemote.DriverInstaller;

/// <summary>
/// Installe / retire le filtre noyau AppleRemoteDriver par son paquet (INF d'extension + catalogue), comme `pnputil` : le pilote
/// Qualcomm et son BtFilter restent en place, Windows gère le magasin de pilotes. Exige les droits administrateur et le mode test-signing.
/// </summary>
static class FilterPackage
{
    public const int Ok = 0, RebootNeeded = 3010;

    const string InfName = "AppleRemoteDriver.inf";
    const string CertName = "WDRLocalTestCert.cer";
    const string AdapterHardwareId = @"USB\VID_0CF3&PID_E007"; // QCA61x4A, le seul adaptateur ciblé par l'INF

    public static int Install(string? packageDir)
    {
        RequireTestSigning();
        string dir = FindPackage(packageDir);
        Say($"Paquet : {dir}");

        // Le catalogue est signé par un certificat de test : il doit être approuvé pour que pnputil accepte le paquet.
        string cer = Path.Combine(dir, CertName);
        if (!File.Exists(cer)) throw new FileNotFoundException($"{CertName} introuvable dans {dir}.");
        foreach (string store in new[] { "Root", "TrustedPublisher" })
            Check(Run("certutil", $"-addstore -f {store} \"{cer}\""), $"certutil -addstore {store}");

        // Mise à jour par-dessus l'ancien paquet, sans le désinstaller d'abord : retirer un filtre chargé laisse l'adaptateur
        // « en attente de redémarrage du PC », et Windows refuse ensuite de le redémarrer. Le paquet le plus récent l'emporte.
        string[] previous = PublishedPackages();
        int code = Run("pnputil", $"/add-driver \"{Path.Combine(dir, InfName)}\" /install").ExitCode;
        if (code is not (0 or RebootNeeded))
            throw new InvalidOperationException($"pnputil /add-driver a échoué (code {code}). Adaptateur {AdapterHardwareId} présent ?");

        if (!RestartAdapter())
            return RebootNeeded; // le paquet est en place : il sera chargé au prochain démarrage
        RepairEnumerators();

        // Les anciens paquets ne sont plus utilisés depuis le redémarrage de l'adaptateur : retirés du magasin (sans /uninstall,
        // donc sans toucher au périphérique). Un échec n'est pas grave : ils restent inutilisés.
        string[] current = PublishedPackages();
        foreach (string oem in previous.Intersect(current).Where(o => current.Except(previous).Any()))
        {
            var removed = Run("pnputil", $"/delete-driver {oem}");
            Say(removed.ExitCode == 0 ? $"Ancien paquet {oem} retiré." : $"Ancien paquet {oem} conservé (code {removed.ExitCode}).");
        }
        if (!FilterInstaller.IsInstalled())
            throw new InvalidOperationException("Paquet installé, mais le filtre n'est pas dans les LowerFilters de l'adaptateur.");
        // pnputil répond 3010 dès qu'un fichier du paquet était chargé ; le redémarrage du périphérique ci-dessus a rechargé le pilote.
        // Le redémarrage du PC n'est demandé que si le service du filtre ne tourne pas réellement.
        if (!WaitForService("Running", 5))
            return RebootNeeded;
        if (code == RebootNeeded) Say("pnputil demandait un redémarrage : inutile, le pilote est déjà chargé.");
        return Ok;
    }

    /// <summary>Retire le paquet (s'il est installé) et remet l'adaptateur sans filtre.</summary>
    public static int Uninstall()
    {
        string[] published = PublishedPackages();
        foreach (string oem in published)
        {
            Say($"Suppression du paquet {oem}");
            Check(Run("pnputil", $"/delete-driver {oem} /uninstall /force"), $"pnputil /delete-driver {oem}");
        }
        if (published.Length == 0) return Ok;
        if (!RestartAdapter()) return RebootNeeded;
        RepairEnumerators();
        if (FilterInstaller.IsInstalled())
            throw new InvalidOperationException("Le filtre est toujours dans les LowerFilters (installé par une ancienne méthode ?).");
        // Le filtre a quitté la pile : son service doit s'arrêter après le redémarrage du périphérique. Sinon seulement, redémarrage du PC.
        return WaitForService("Stopped", 8) ? Ok : RebootNeeded;
    }

    /// <summary>Paquets AppleRemoteDriver présents dans le magasin de pilotes (oemNN.inf).</summary>
    static string[] PublishedPackages() =>
        Ps("(Get-WindowsDriver -Online | Where-Object OriginalFileName -like '*" + InfName + "').Driver")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Attend que le service AppleRemoteDriver soit dans l'état voulu ("Running", "Stopped") ; un service absent compte comme arrêté.</summary>
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
            throw new InvalidOperationException("Le mode test-signing est désactivé : le pilote de test ne se chargerait pas. " +
                "Exécutez « bcdedit /set testsigning on » puis redémarrez.");
    }

    /// <summary>Le dossier contenant l'INF : celui demandé, celui de l'exe, sinon la dernière compilation de src\AppleRemoteDriver.</summary>
    static string FindPackage(string? requested)
    {
        var candidates = new List<string>();
        if (requested is not null) candidates.Add(requested);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "driver")); // publication : <dossier de l'application>\driver\
            candidates.Add(dir.FullName);
            foreach (string config in new[] { "release", "debug" })
                candidates.Add(Path.Combine(dir.FullName, "src", "AppleRemoteDriver", "target", config, "AppleRemoteDriver_package"));
        }
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, InfName)))
               ?? throw new FileNotFoundException($"{InfName} introuvable : compilez AppleRemoteDriver avec « cargo make --profile release ».");
    }

    /// <summary>
    /// Redémarre l'adaptateur pour (dé)charger le filtre. Faux si Windows le refuse (adaptateur en attente d'un redémarrage du PC,
    /// ou poignée ouverte par une application qui bloque l'arrêt de la pile) : il faut alors redémarrer le PC.
    /// </summary>
    static bool RestartAdapter()
    {
        string id = Ps($"(Get-PnpDevice -PresentOnly | Where-Object InstanceId -like '{AdapterHardwareId}*' | Select-Object -First 1).InstanceId").Trim();
        if (id.Length == 0) throw new InvalidOperationException($"Adaptateur {AdapterHardwareId} introuvable.");
        var result = Run("pnputil", $"/restart-device \"{id}\"");
        if (result.ExitCode is not (0 or RebootNeeded))
        {
            Say($"Redémarrage de l'adaptateur refusé (code {result.ExitCode}) : {result.Output.Trim()}");
            return false;
        }
        Thread.Sleep(3000);
        return true;
    }

    /// <summary>
    /// Un redémarrage du périphérique laisse parfois les énumérateurs Bluetooth (BTH\MS_BTHLE, MS_BTHBRB, MS_RFCOMM) en erreur
    /// (code 19, 0xC0000034) : le scanner BLE ne démarre plus. Les supprimer puis rescanner les recrée.
    /// </summary>
    static void RepairEnumerators()
    {
        const string failed = "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'BTH\\MS_*' -and $_.Status -eq 'Error' }";
        string ids = Ps($"({failed}).InstanceId").Trim();
        if (ids.Length == 0) return;
        foreach (string id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Say($"Énumérateur Bluetooth en erreur, recréation : {id}");
            Run("pnputil", $"/remove-device \"{id}\"");
        }
        Run("pnputil", "/scan-devices");
        Thread.Sleep(3000);
        if (Ps($"({failed}).InstanceId").Trim().Length > 0)
            Say("Des énumérateurs Bluetooth sont encore en erreur : redémarrez le PC.");
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
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Impossible de lancer {file}.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    static void Check((int ExitCode, string Output) result, string what)
    {
        if (result.ExitCode is 0 or RebootNeeded) return;
        throw new InvalidOperationException($"{what} a échoué (code {result.ExitCode}) : {result.Output.Trim()}");
    }

    static void Say(string message) => Console.WriteLine(message);
}
