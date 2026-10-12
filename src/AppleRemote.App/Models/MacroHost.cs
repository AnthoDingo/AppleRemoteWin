using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json.Nodes;
using AppleRemote.App.Views;
using AppleRemote.Windows;

namespace AppleRemote.App.Models;

/// <summary>Convertit l'ancien format (liste d'étapes) en blocs Blockly.</summary>
static class MacroBlocks
{
    static JsonObject Block(string type, (string, JsonNode)[]? fields = null, (string, JsonObject)[]? inputs = null)
    {
        var b = new JsonObject { ["type"] = type };
        if (fields is not null) b["fields"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Item1, f.Item2)));
        if (inputs is not null) b["inputs"] = new JsonObject(inputs.Select(i => new KeyValuePair<string, JsonNode?>(i.Item1, new JsonObject { ["block"] = i.Item2 })));
        return b;
    }

    static JsonObject StepBlock(MacroStep s) => s.Type switch
    {
        "combo" => Block("macro_combo", [("COMBO", s.Value)]),
        "app" => Block("macro_app", [("PATH", s.Value)]),
        "wait" => Block("macro_wait", [("MS", int.TryParse(s.Value, out int ms) ? ms : 1000)]),
        "text" => Block("macro_type", inputs: [("VALUE", Block("text", [("TEXT", s.Value)]))]),
        _ => Block("macro_type", inputs: [("VALUE", Block("macro_ask_" + s.Type[3..].ToLowerInvariant(),
                [("PROMPT", s.Value.Length > 0 ? s.Value : Loc.T("Prompt." + s.Type))]))]),
    };

    public static JsonNode FromSteps(IEnumerable<MacroStep> steps)
    {
        JsonObject? first = null, prev = null;
        foreach (var s in steps)
        {
            var b = StepBlock(s);
            if (prev is null) { first = b; b["x"] = 20; b["y"] = 20; }
            else prev["next"] = new JsonObject { ["block"] = b };
            prev = b;
        }
        var blocks = first is null ? new JsonArray() : new JsonArray(first);
        return new JsonObject { ["blocks"] = new JsonObject { ["languageVersion"] = 0, ["blocks"] = blocks } };
    }
}

/// <summary>Effets d'une macro sur le PC : touches (SendInput), logiciels, fenêtres de saisie (thread de l'interface).</summary>
sealed class MacroHost(KeyboardInjector injector) : IMacroHost
{
    public void Combo(string combo) => KeyCombos.Run(injector, KeyCombos.Prefix + combo);

    public void Launch(string path) => SoftwareApps.Launch(path);

    public void Type(string text) => injector.TypeText(text);

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    static MacroHost() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // pages OEM (850…) pour cmd

    /// <summary>Commande sans fenêtre ; la macro attend sa fin et récupère sa sortie (stdout puis stderr) et son code.
    /// Une macro arrêtée n'attend plus mais ne tue pas le processus.</summary>
    public async Task<(string Output, int ExitCode)> ShellAsync(string shell, string command, CancellationToken ct)
    {
        bool ps = shell == "powershell";
        var psi = ps
            // -EncodedCommand (UTF-16LE en base 64) : aucun souci de guillemets. Préambule sur sa propre ligne :
            // il n'apparaît pas dans la position des erreurs.
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(
                    "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'\n" + command)))
            // /s : cmd retire seulement les guillemets extérieurs, la commande passe telle quelle.
            : new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = ps ? Encoding.UTF8 : Encoding.GetEncoding((int)GetOEMCP());
        Process? p;
        try { p = Process.Start(psi); }
        catch { return ("", -1); } // shell introuvable : on continue, comme pour un logiciel absent
        if (p is null) return ("", -1);
        using (p)
        {
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var error = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            string err = ps ? FromClixml(await error) : await error;
            return ((await output + err).TrimEnd(), p.ExitCode);
        }
    }

    /// <summary>Avec -EncodedCommand, PowerShell écrit ses erreurs en CLIXML : on n'en garde que le texte.</summary>
    static string FromClixml(string s) => !s.StartsWith("#< CLIXML") ? s
        : string.Concat(Regex.Matches(s, @"<S S=""Error"">(.*?)</S>", RegexOptions.Singleline)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Replace("_x000D__x000A_", "\n")));

    /// <summary>Ouvre la fenêtre de saisie ; null si elle est fermée sans valider.</summary>
    public async Task<string?> AskAsync(string kind, string prompt)
    {
        string title = Loc.T("Step.ask" + char.ToUpperInvariant(kind[0]) + kind[1..]);
        System.Windows.Window w = kind switch
        {
            "text" => new InputTextWindow(title, prompt),
            "file" => new InputBrowseWindow(title, prompt),
            _ => new InputDropWindow(title, prompt),
        };
        w.Topmost = true;
        w.ShowDialog();
        string? answer = w switch
        {
            InputTextWindow t => t.Result,
            InputBrowseWindow f => f.Result,
            InputDropWindow d => d.Result,
            _ => null,
        };
        if (answer is not null) await Task.Delay(300); // le temps de rendre le focus à la fenêtre précédente
        return answer;
    }
}
