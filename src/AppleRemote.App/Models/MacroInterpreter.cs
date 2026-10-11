using System.Globalization;
using System.Text.Json.Nodes;
using AppleRemote.App.Views;
using AppleRemote.Windows;

namespace AppleRemote.App.Models;

/// <summary>Une fenêtre de saisie a été fermée sans valider : la macro s'arrête.</summary>
sealed class MacroAborted : Exception;

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

/// <summary>Exécute une sérialisation Blockly : blocs Macro, boucles, conditions, logique, maths, texte et variables.
/// Valeurs : double, string ou bool. Un bloc inconnu est ignoré.</summary>
sealed class MacroInterpreter(KeyboardInjector injector, CancellationToken ct)
{
    readonly Dictionary<string, object?> _vars = new();

    public async Task RunAsync(JsonNode? workspace)
    {
        if (workspace?["blocks"]?["blocks"] is not JsonArray tops) return;
        try
        {
            // Plusieurs piles : de haut en bas (puis de gauche à droite).
            foreach (var top in tops.OrderBy(b => Num(b?["y"]?.ToString())).ThenBy(b => Num(b?["x"]?.ToString())))
                await Chain(top);
        }
        catch (MacroAborted) { }
        catch (OperationCanceledException) { }
    }

    static string? Field(JsonNode b, string name) => b["fields"]?[name]?.ToString();
    static JsonNode? Input(JsonNode b, string name) => b["inputs"]?[name]?["block"] ?? b["inputs"]?[name]?["shadow"];

    async Task Chain(JsonNode? b)
    {
        for (; b is not null; b = b["next"]?["block"]) await Exec(b);
    }

    async Task Exec(JsonNode b)
    {
        ct.ThrowIfCancellationRequested();
        switch ((string?)b["type"])
        {
            case "macro_combo":
                KeyCombos.Run(injector, KeyCombos.Prefix + (Field(b, "COMBO") ?? "").Replace(" ", ""));
                break;
            case "macro_app":
                if (Field(b, "PATH") is { Length: > 0 } path) SoftwareApps.Launch(path);
                break;
            case "macro_wait":
                await Task.Delay(Math.Max(0, (int)Num(Field(b, "MS"))), ct);
                break;
            case "macro_type":
                injector.TypeText(Str(await Eval(Input(b, "VALUE"))));
                break;
            case "controls_repeat_ext":
                for (int i = 0, n = (int)Num(await Eval(Input(b, "TIMES"))); i < n; i++)
                {
                    await Chain(Input(b, "DO"));
                    await Next();
                }
                break;
            case "controls_whileUntil":
                bool until = Field(b, "MODE") == "UNTIL";
                while (Truthy(await Eval(Input(b, "BOOL"))) != until)
                {
                    await Chain(Input(b, "DO"));
                    await Next();
                }
                break;
            case "controls_if":
                for (int n = 0; Input(b, "IF" + n) is { } cond; n++)
                    if (Truthy(await Eval(cond))) { await Chain(Input(b, "DO" + n)); return; }
                await Chain(Input(b, "ELSE"));
                break;
            case "variables_set":
                _vars[b["fields"]?["VAR"]?["id"]?.ToString() ?? ""] = await Eval(Input(b, "VALUE"));
                break;
        }
    }

    /// <summary>Entre deux tours de boucle : rend la main à l'interface (une boucle sans attente ne la bloque pas) et reste annulable.</summary>
    async Task Next()
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(1, ct);
    }

    async Task<object?> Eval(JsonNode? b)
    {
        if (b is null) return null;
        switch ((string?)b["type"])
        {
            case "math_number": return Num(Field(b, "NUM"));
            case "text": return Field(b, "TEXT") ?? "";
            case "logic_boolean": return Field(b, "BOOL") == "TRUE";
            case "logic_negate": return !Truthy(await Eval(Input(b, "BOOL")));
            case "logic_operation":
                bool left = Truthy(await Eval(Input(b, "A")));
                bool and = Field(b, "OP") == "AND";
                return and ? left && Truthy(await Eval(Input(b, "B"))) : left || Truthy(await Eval(Input(b, "B")));
            case "logic_compare":
            {
                object? x = await Eval(Input(b, "A")), y = await Eval(Input(b, "B"));
                int c = x is double dx && y is double dy ? dx.CompareTo(dy) : string.CompareOrdinal(Str(x), Str(y));
                return Field(b, "OP") switch
                {
                    "EQ" => c == 0, "NEQ" => c != 0, "LT" => c < 0, "LTE" => c <= 0, "GT" => c > 0, "GTE" => c >= 0, _ => false,
                };
            }
            case "math_arithmetic":
            {
                double x = Num(await Eval(Input(b, "A"))), y = Num(await Eval(Input(b, "B")));
                return Field(b, "OP") switch
                {
                    "ADD" => x + y, "MINUS" => x - y, "MULTIPLY" => x * y, "DIVIDE" => y == 0 ? 0 : x / y, "POWER" => Math.Pow(x, y), _ => 0,
                };
            }
            case "variables_get":
                return _vars.GetValueOrDefault(b["fields"]?["VAR"]?["id"]?.ToString() ?? "");
            case "macro_ask_text" or "macro_ask_file" or "macro_ask_drop":
                return await Ask((string)b["type"]!, Field(b, "PROMPT") ?? "");
        }
        return null;
    }

    /// <summary>Ouvre la fenêtre de saisie ; fermée sans valider, la macro s'arrête.</summary>
    async Task<string> Ask(string type, string prompt)
    {
        string title = Loc.T("Step.ask" + char.ToUpperInvariant(type[10]) + type[11..]);
        System.Windows.Window w = type switch
        {
            "macro_ask_text" => new InputTextWindow(title, prompt),
            "macro_ask_file" => new InputBrowseWindow(title, prompt),
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
        if (answer is null) throw new MacroAborted();
        await Task.Delay(300, ct); // le temps de rendre le focus à la fenêtre précédente
        return answer;
    }

    static bool Truthy(object? v) => v switch { bool b => b, double d => d != 0, string s => s.Length > 0, _ => false };

    static double Num(object? v) => v switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
        _ => 0,
    };

    static string Str(object? v) => v switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };
}
