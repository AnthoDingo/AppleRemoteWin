using Xunit;

namespace AppleRemote.Tests;

public class DecoderTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s);

    static byte[] Pad(byte[] r, int length = 21)
    {
        var p = new byte[length];
        r.CopyTo(p, 0);
        return p;
    }

    [Fact]
    public void Touch_report()
    {
        var ev = Assert.IsType<TouchEvent>(Assert.Single(new Decoder().Decode(Hex("fc010032e0ba019fcdcd5c81218b"))));
        Assert.Equal(0.0, ev.T);
        Assert.Equal((-609, -804, true), (ev.X, ev.Y, ev.Down));
        Assert.Equal(new[] { new Finger(-609, -804, 3) }, ev.Fingers);
    }

    [Fact]
    public void Two_fingers()
    {
        var ev = Assert.IsType<TouchEvent>(Assert.Single(new Decoder().Decode(Hex("fc020032e6d81944ffd51f280a8b5fcbc7313c0f83"))));
        Assert.Equal(new[] { new Finger(-188, -673, 3), new Finger(-1185, -900, 3) }, ev.Fingers);
    }

    /// <summary>Windows rembourre chaque report à InputReportByteLength (21) : le décodage doit rester identique.</summary>
    [Theory]
    [InlineData("fc010032e0ba019fcdcd5c81218b")]
    [InlineData("fc020032e6d81944ffd51f280a8b5fcbc7313c0f83")]
    public void Padded_touch_reports_decode_like_exact_ones(string hex)
    {
        var exact = Assert.IsType<TouchEvent>(Assert.Single(new Decoder().Decode(Hex(hex))));
        var padded = Assert.IsType<TouchEvent>(Assert.Single(new Decoder().Decode(Pad(Hex(hex)))));
        Assert.Equal(exact.Fingers, padded.Fingers);
        Assert.Equal((exact.X, exact.Y, exact.Down), (padded.X, padded.Y, padded.Down));
    }

    [Fact]
    public void Padded_buttons_only_and_unlock_reply()
    {
        var d = new Decoder();
        var unlock = Assert.IsType<ButtonsEvent>(Assert.Single(d.Decode(Pad(Hex("fce0226002")))));
        Assert.Equal(0x22, unlock.Mask);
        var release = Assert.IsType<ButtonsEvent>(Assert.Single(d.Decode(Pad(Hex("fc0000")))));
        Assert.Equal(new[] { "volup", "menu" }, release.Released);
    }

    [Fact]
    public void Buttons_come_from_every_report_kind()
    {
        var d = new Decoder();

        var unlock = Assert.IsType<ButtonsEvent>(d.Decode(Hex("fce0226002"))[0]);
        Assert.Equal(0x22, unlock.Mask);
        Assert.Equal(new[] { "volup", "menu" }, unlock.Pressed);
        Assert.Empty(unlock.Released);

        var drop = Assert.IsType<ButtonsEvent>(d.Decode(Hex("fc0020"))[0]);
        Assert.Equal(0x20, drop.Mask);
        Assert.Empty(drop.Pressed);
        Assert.Equal(new[] { "volup" }, drop.Released);

        // le clic voyage dans les reports tactiles
        var touchClick = d.Decode(Hex("fc018032e0ba019fcdcd5c81218c"));
        Assert.Collection(touchClick, e => Assert.IsType<ButtonsEvent>(e), e => Assert.IsType<TouchEvent>(e));
        var click = (ButtonsEvent)touchClick[0];
        Assert.Equal(0x80, click.Mask);
        Assert.Equal(new[] { "click" }, click.Pressed);
        Assert.Equal(new[] { "menu" }, click.Released);
    }

    [Fact]
    public void Link_drop_releases_everything()
    {
        var d = new Decoder();
        d.Decode(Hex("fc018032e0ba019fcdcd5c81218c"));
        var events = d.Decode(Array.Empty<byte>());
        var buttons = Assert.IsType<ButtonsEvent>(events[0]);
        var touch = Assert.IsType<TouchEvent>(events[1]);
        Assert.Equal(0, buttons.Mask);
        Assert.Equal(new[] { "click" }, buttons.Released);
        Assert.False(touch.Down);
        Assert.Empty(d.Decode(Array.Empty<byte>()));
    }

    [Fact]
    public void Voice_chunk()
    {
        // synthétique : mise en page de docs/PROTOCOL.md, octets audio à zéro
        var report = Hex("fc1010" + "0000" + "00" + "00" + "44" + "b8").Concat(new byte[12]).ToArray();
        var events = new Decoder().Decode(report);
        Assert.IsType<ButtonsEvent>(events[0]);
        Assert.Equal(new[] { "siri" }, ((ButtonsEvent)events[0]).Pressed);
        Assert.IsType<VoiceChunkEvent>(events[1]);
    }

    [Fact]
    public void Other_reports_are_ignored()
    {
        Assert.Empty(new Decoder().Decode(new byte[] { 1, 2, 3 }));
        Assert.Empty(new Decoder().Decode(null));
    }

    [Fact]
    public void Clock_wraps_without_going_backwards()
    {
        var d = new Decoder();
        // horloge 0xFFF0 puis 0x0010 : 0x20 ticks plus tard = 16 ms
        static byte[] At(int clock) => new byte[]
        {
            0xFC, 0x01, 0x00, 0x32, (byte)(clock & 0xFF), (byte)(clock >> 8), 0x01,   // en-tête + horloge + drapeaux
            0x9F, 0xCD, 0xCD, 0x5C, 0x81, 0x21, 0x8B,                                 // un doigt
        };
        var a = (TouchEvent)d.Decode(At(0xFFF0))[0];
        var b = (TouchEvent)d.Decode(At(0x0010))[0];
        Assert.Equal(0.016, b.T - a.T, 6);
    }
}
