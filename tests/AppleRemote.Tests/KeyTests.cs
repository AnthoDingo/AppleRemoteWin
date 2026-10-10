// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using Xunit;

namespace AppleRemote.Tests;

public class KeyTests
{
    sealed class FakeSink : IKeySink
    {
        public readonly List<(KeyCode Key, bool Down)> Log = new();
        public void Key(KeyCode key, bool down) => Log.Add((key, down));
    }

    static readonly KeyCode Up = Keys.Names["Up"], Enter = Keys.Names["Enter"];

    static ButtonsEvent Buttons(int mask, string[] pressed, string[] released) =>
        new(mask, pressed, released, Array.Empty<byte>());

    [Fact]
    public void Key_table_uses_windows_virtual_key_codes()
    {
        Assert.Equal(new KeyCode(0x26, true), Keys.Names["Up"]);
        Assert.Equal(new KeyCode(0x0D), Keys.Names["Enter"]);
        Assert.Equal(new KeyCode(0x41), Keys.Names["a"]);          // case-insensitive
        Assert.Equal(new KeyCode(0x30), Keys.Names["D0"]);
        Assert.Equal(new KeyCode(0x70), Keys.Names["F1"]);
        Assert.Equal(new KeyCode(0x7B), Keys.Names["F12"]);
        Assert.Equal(new KeyCode(0xB3), Keys.Names["MediaPlayPause"]);
    }

    [Fact]
    public void Default_map()
    {
        var m = KeyMap.Default();
        Assert.True(m.TryGet("up", out var up) && up == Up);
        Assert.True(m.TryGet("click", out var click) && click == Enter);
        Assert.True(m.TryGet("menu", out var menu) && menu == Keys.Names["Escape"]);
        Assert.True(m.TryGet("tv", out var tv) && tv == Keys.Names["BrowserHome"]);
        Assert.False(m.TryGet("siri", out _));                      // Siri: nothing by default
    }

    [Fact]
    public void ToJson_round_trips()
    {
        var m = KeyMap.Default();
        m.Set("siri", Keys.Names["C"]);
        m.Set("tv", null);
        var back = KeyMap.FromJson(m.ToJson());
        Assert.True(back.TryGet("siri", out var siri) && siri == Keys.Names["C"]);
        Assert.False(back.TryGet("tv", out _));
        Assert.True(back.TryGet("menu", out var menu) && menu == Keys.Names["Escape"]);
    }

    [Fact]
    public void Json_overrides_defaults()
    {
        var m = KeyMap.FromJson("""{"siri": "C", "tv": null, "click": 32, "Volup": "VolumeMute"}""");
        Assert.True(m.TryGet("siri", out var siri) && siri == Keys.Names["C"]);
        Assert.True(m.TryGet("click", out var click) && click == new KeyCode(32));
        Assert.True(m.TryGet("volup", out var volup) && volup == Keys.Names["VolumeMute"]);
        Assert.False(m.TryGet("tv", out _));                        // null: input disabled
        Assert.True(m.TryGet("up", out var up) && up == Up);        // the rest keeps its default
    }

    [Theory]
    [InlineData("""{"jump": "A"}""")]
    [InlineData("""{"click": "NOPE"}""")]
    [InlineData("""{"click": true}""")]
    [InlineData("""{"click": [1]}""")]
    [InlineData("""{"click": 0}""")]
    [InlineData("""{"click": 255}""")]
    [InlineData("""["click"]""")]
    public void Invalid_json_is_rejected(string json) =>
        Assert.Throws<ArgumentException>(() => KeyMap.FromJson(json));

    [Fact]
    public void Binder_translates_buttons_and_steps()
    {
        var sink = new FakeSink();
        var binder = new KeyBinder(KeyMap.Default(), sink);

        binder.OnEvent(Buttons(0x80, new[] { "click" }, Array.Empty<string>()));
        binder.OnEvent(Buttons(0x10, new[] { "siri" }, new[] { "click" }));   // siri: not mapped
        binder.OnGesture(new MoveGesture(Direction.Up, MoveSource.Slide));

        Assert.Equal(new[] { (Enter, true), (Enter, false), (Up, true), (Up, false) }, sink.Log);
    }

    [Fact]
    public void Binder_ignores_non_move_gestures_and_unmapped_directions()
    {
        var sink = new FakeSink();
        var map = KeyMap.FromJson("""{"left": null}""");
        var binder = new KeyBinder(map, sink);

        binder.OnGesture(new TouchGesture(true));
        binder.OnGesture(new TiltGesture(.5, .5));
        binder.OnGesture(new MoveGesture(Direction.Left, MoveSource.Flick));

        Assert.Empty(sink.Log);
    }

    [Fact]
    public void ReleaseAll_lifts_held_keys_once()
    {
        var sink = new FakeSink();
        var binder = new KeyBinder(KeyMap.Default(), sink);
        binder.OnEvent(Buttons(0x80, new[] { "click" }, Array.Empty<string>()));

        binder.ReleaseAll();
        binder.ReleaseAll();

        Assert.Equal(new[] { (Enter, true), (Enter, false) }, sink.Log);
    }

    [Fact]
    public void Changing_the_map_releases_held_keys()
    {
        var sink = new FakeSink();
        var binder = new KeyBinder(KeyMap.Default(), sink);
        binder.OnEvent(Buttons(0x80, new[] { "click" }, Array.Empty<string>()));

        binder.Map = KeyMap.FromJson("""{"click": "Space"}""");
        binder.OnEvent(Buttons(0, Array.Empty<string>(), new[] { "click" }));   // already released: no double release

        Assert.Equal(new[] { (Enter, true), (Enter, false) }, sink.Log);
    }
}
