// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Globalization;
using System.Text.Json.Nodes;

namespace AppleRemote;

/// <summary>What a macro does to the outside world. Implemented by the app (SendInput, processes, dialogs).</summary>
public interface IMacroHost
{
    /// <summary>Presses a combination such as "Ctrl+C" (names joined by '+', no spaces).</summary>
    void Combo(string combo);
    void Launch(string path);
    void Type(string text);
    /// <summary>Runs a command (<paramref name="shell"/>: cmd or powershell) and waits for it to exit.
    /// Returns its text (stdout then stderr) and exit code (-1: could not start).</summary>
    Task<(string Output, int ExitCode)> ShellAsync(string shell, string command, CancellationToken ct);
    /// <summary>Asks the user (<paramref name="kind"/>: text, file or drop). Null = window closed: the macro stops.</summary>
    Task<string?> AskAsync(string kind, string prompt);
}

/// <summary>Runs a Blockly serialization: macro blocks, loops, conditions, logic, math, text and variables.
/// Values are double, string or bool. Unknown and disabled blocks are skipped.</summary>
public sealed class MacroEngine(IMacroHost host, CancellationToken ct)
{
    sealed class Aborted : Exception;

    readonly Dictionary<string, object?> _vars = new();

    /// <summary>Runs the stack under the start block; detached stacks are drafts and never run.
    /// Returns normally when cancelled or aborted.</summary>
    public async Task RunAsync(JsonNode? workspace)
    {
        if (workspace?["blocks"]?["blocks"] is not JsonArray tops) return;
        // Macros saved before the start block existed: the top stack is the entry point.
        var entry = tops.FirstOrDefault(b => (string?)b?["type"] == "macro_start")
            ?? tops.OrderBy(b => Num(b?["y"]?.ToString())).ThenBy(b => Num(b?["x"]?.ToString())).FirstOrDefault();
        try { await Chain(entry); }
        catch (Aborted) { }
        catch (OperationCanceledException) { }
    }

    static string? Field(JsonNode b, string name) => b["fields"]?[name]?.ToString();
    static string Var(JsonNode b) => b["fields"]?["VAR"]?["id"]?.ToString() ?? "";
    static JsonNode? Input(JsonNode b, string name) => b["inputs"]?[name]?["block"] ?? b["inputs"]?[name]?["shadow"];

    // Blockly 11+ writes "disabledReasons", older versions "enabled": false.
    static bool Disabled(JsonNode b) =>
        b["disabledReasons"] is JsonArray { Count: > 0 } || b["enabled"] is JsonValue v && v.TryGetValue(out bool on) && !on;

    async Task Chain(JsonNode? b)
    {
        for (; b is not null; b = b["next"]?["block"])
            if (!Disabled(b)) await Exec(b);
    }

    async Task Exec(JsonNode b)
    {
        ct.ThrowIfCancellationRequested();
        switch ((string?)b["type"])
        {
            case "macro_combo":
                host.Combo((Field(b, "COMBO") ?? "").Replace(" ", ""));
                break;
            case "macro_app":
                if (Field(b, "PATH") is { Length: > 0 } path) host.Launch(path);
                break;
            case "macro_wait":
                await Task.Delay(Math.Max(0, (int)Num(Field(b, "MS"))), ct);
                break;
            case "macro_type":
                host.Type(Str(await Eval(Input(b, "VALUE"))));
                break;
            case "macro_shell":
                if (Str(await Eval(Input(b, "COMMAND"))) is { Length: > 0 } command)
                {
                    var (output, code) = await host.ShellAsync(Field(b, "SHELL") == "powershell" ? "powershell" : "cmd", command, ct);
                    // Optional slots: a variable plugged there receives the result.
                    if (Input(b, "CODE") is { } c && (string?)c["type"] == "variables_get") _vars[Var(c)] = (double)code;
                    if (Input(b, "OUTPUT") is { } o && (string?)o["type"] == "variables_get") _vars[Var(o)] = output;
                }
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
                _vars[Var(b)] = await Eval(Input(b, "VALUE"));
                break;
            case "math_change":
                _vars[Var(b)] = Num(_vars.GetValueOrDefault(Var(b))) + Num(await Eval(Input(b, "DELTA")));
                break;
        }
    }

    /// <summary>Between two loop turns: yields (a loop without a wait does not freeze the UI) and stays cancellable.</summary>
    Task Next() => Task.Delay(1, ct);

    async Task<object?> Eval(JsonNode? b)
    {
        if (b is null || Disabled(b)) return null;
        ct.ThrowIfCancellationRequested();
        switch ((string?)b["type"])
        {
            case "math_number": return Num(Field(b, "NUM"));
            case "text": return Field(b, "TEXT") ?? "";
            case "logic_boolean": return Field(b, "BOOL") == "TRUE";
            case "logic_negate": return !Truthy(await Eval(Input(b, "BOOL")));
            case "logic_operation":
                bool left = Truthy(await Eval(Input(b, "A")));
                return Field(b, "OP") == "AND" ? left && Truthy(await Eval(Input(b, "B"))) : left || Truthy(await Eval(Input(b, "B")));
            case "logic_compare":
            {
                object? x = await Eval(Input(b, "A")), y = await Eval(Input(b, "B"));
                // Answers are strings: "10" > 9 must compare as numbers.
                int c = TryNum(x, out double dx) && TryNum(y, out double dy) ? dx.CompareTo(dy) : string.CompareOrdinal(Str(x), Str(y));
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
                return _vars.GetValueOrDefault(Var(b));
            case "macro_ask_text" or "macro_ask_file" or "macro_ask_drop":
                return await host.AskAsync(((string)b["type"]!)["macro_ask_".Length..], Field(b, "PROMPT") ?? "") ?? throw new Aborted();
        }
        return null;
    }

    static bool Truthy(object? v) => v switch { bool b => b, double d => d != 0, string s => s.Length > 0, _ => false };

    static bool TryNum(object? v, out double d)
    {
        switch (v)
        {
            case double x: d = x; return true;
            case bool b: d = b ? 1 : 0; return true;
            case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            default: d = 0; return false;
        }
    }

    static double Num(object? v) => TryNum(v, out double d) ? d : 0;

    static string Str(object? v) => v switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };
}
