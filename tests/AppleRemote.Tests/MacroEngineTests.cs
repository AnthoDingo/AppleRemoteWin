// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text.Json.Nodes;
using Xunit;

namespace AppleRemote.Tests;

public class MacroEngineTests
{
    sealed class FakeHost(params string?[] answers) : IMacroHost
    {
        readonly Queue<string?> _answers = new(answers);
        public List<string> Log { get; } = [];
        public Action? OnType;

        public void Combo(string combo) => Log.Add("combo " + combo);
        public void Launch(string path) => Log.Add("app " + path);
        public void Type(string text) { Log.Add("type " + text); OnType?.Invoke(); }
        public Task<(string, int)> ShellAsync(string shell, string command, CancellationToken ct)
        {
            Log.Add($"{shell} {command}");
            return Task.FromResult(("out of " + command, command.Length));
        }
        public Task<string?> AskAsync(string kind, string prompt)
        {
            Log.Add($"ask {kind} {prompt}");
            return Task.FromResult(_answers.Dequeue());
        }
    }

    // Blockly serialization builders: a block, its fields, its inputs ({"block": …}) and its next block.
    static JsonObject B(string type, object? fields = null, (string, JsonNode)[]? inputs = null, JsonObject? next = null)
    {
        var b = new JsonObject { ["type"] = type };
        if (fields is not null) b["fields"] = System.Text.Json.JsonSerializer.SerializeToNode(fields);
        if (inputs is not null) b["inputs"] = new JsonObject(inputs.Select(i => KeyValuePair.Create(i.Item1, (JsonNode?)new JsonObject { ["block"] = i.Item2 })));
        if (next is not null) b["next"] = new JsonObject { ["block"] = next };
        return b;
    }

    static JsonObject Num(double n) => B("math_number", new { NUM = n });
    static JsonObject Text(string s) => B("text", new { TEXT = s });
    static JsonObject TypeOf(JsonNode value, JsonObject? next = null) => B("macro_type", inputs: [("VALUE", value)], next: next);
    static readonly object I = new { VAR = new { id = "i" } };
    static JsonObject GetI() => B("variables_get", I);

    static async Task<List<string>> Run(JsonObject top, FakeHost? host = null, CancellationToken ct = default) =>
        await RunAll([top], host, ct);

    static async Task<List<string>> RunAll(JsonObject[] tops, FakeHost? host = null, CancellationToken ct = default)
    {
        host ??= new FakeHost();
        await new MacroEngine(host, ct).RunAsync(new JsonObject { ["blocks"] = new JsonObject { ["blocks"] = new JsonArray(tops) } });
        return host.Log;
    }

    static JsonObject At(JsonObject b, int y) { b["x"] = 0; b["y"] = y; return b; }

    [Fact]
    public async Task Only_the_start_block_stack_runs()
    {
        var log = await RunAll([
            At(TypeOf(Text("draft above")), 0),
            At(B("macro_start", next: TypeOf(Text("main"))), 100),
            At(TypeOf(Text("draft below")), 200)]);
        Assert.Equal(["type main"], log);

        // Saved before the start block existed: only the top stack runs.
        Assert.Equal(["type top"], await RunAll([At(TypeOf(Text("bottom")), 200), At(TypeOf(Text("top")), 0)]));
    }

    [Fact]
    public async Task Macro_blocks_run_in_order_and_disabled_ones_are_skipped()
    {
        var disabled = B("macro_combo", new { COMBO = "Alt + F4" }, next: B("macro_combo", new { COMBO = "Ctrl + A" }, next: TypeOf(Text("hi"))));
        disabled["disabledReasons"] = new JsonArray("MANUALLY_DISABLED");
        var log = await Run(B("macro_app", new { PATH = "notepad.exe" }, next: B("macro_wait", new { MS = 1 }, next: disabled)));
        Assert.Equal(["app notepad.exe", "combo Ctrl+A", "type hi"], log);
    }

    [Fact]
    public async Task Shell_commands_take_a_computed_value_and_skip_empty_ones()
    {
        var log = await Run(B("macro_shell", new { SHELL = "powershell" }, [("COMMAND", Text("Get-Date"))],
            next: B("macro_shell", new { SHELL = "cmd" }, [("COMMAND", B("macro_ask_text", new { PROMPT = "c" }))],
            next: B("macro_shell", new { SHELL = "cmd" }, [("COMMAND", Text(""))]))), new FakeHost("dir"));
        Assert.Equal(["powershell Get-Date", "ask text c", "cmd dir"], log);
    }

    [Fact]
    public async Task Exit_code_and_output_go_to_the_plugged_variables()
    {
        static JsonObject Get(string id) => B("variables_get", new { VAR = new { id } });
        var log = await Run(B("macro_shell", new { SHELL = "cmd" }, [("COMMAND", Text("ver")), ("CODE", Get("c")), ("OUTPUT", Get("o"))],
            next: TypeOf(Get("o"), next: TypeOf(Get("c"),
            // Without variables the result is dropped; a non-variable block in a slot is ignored.
            next: B("macro_shell", new { SHELL = "cmd" }, [("COMMAND", Text("vol")), ("CODE", Text("x"))],
            next: TypeOf(Get("c")))))));
        Assert.Equal(["cmd ver", "type out of ver", "type 3", "cmd vol", "type 3"], log);
    }

    [Fact]
    public async Task Repeat_variables_and_if_else()
    {
        // i = 0; repeat 3 { i += 1; if i > 2 type "big" else type i }
        var body = B("math_change", I, [("DELTA", Num(1))], next: B("controls_if", inputs: [
            ("IF0", B("logic_compare", new { OP = "GT" }, [("A", GetI()), ("B", Num(2))])),
            ("DO0", TypeOf(Text("big"))),
            ("ELSE", TypeOf(GetI()))]));
        var log = await Run(B("variables_set", I, [("VALUE", Num(0))], next: B("controls_repeat_ext", inputs: [("TIMES", Num(3)), ("DO", body)])));
        Assert.Equal(["type 1", "type 2", "type big"], log);
    }

    [Fact]
    public async Task Answers_compare_as_numbers_and_a_closed_window_stops_the_macro()
    {
        // while ask("n") < 10 { type "small" }; type "end"
        var loop = B("controls_whileUntil", new { MODE = "WHILE" }, [
            ("BOOL", B("logic_compare", new { OP = "LT" }, [("A", B("macro_ask_text", new { PROMPT = "n" })), ("B", Num(10))])),
            ("DO", TypeOf(Text("small")))], next: TypeOf(Text("end")));
        Assert.Equal(["ask text n", "type small", "ask text n", "type end"], await Run(loop, new FakeHost("9", "10")));

        // Window closed: nothing after it runs, RunAsync returns normally.
        var closed = TypeOf(B("macro_ask_file", new { PROMPT = "f" }), next: TypeOf(Text("after")));
        Assert.Equal(["ask file f"], await Run(closed, new FakeHost([null])));
    }

    [Fact]
    public async Task Cancellation_stops_an_endless_loop()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHost();
        host.OnType = () => { if (host.Log.Count == 3) cts.Cancel(); };
        var forever = B("controls_whileUntil", new { MODE = "WHILE" }, [("BOOL", B("logic_boolean", new { BOOL = "TRUE" })), ("DO", TypeOf(Text("x")))]);
        Assert.Equal(3, (await Run(forever, host, cts.Token)).Count);
    }
}
