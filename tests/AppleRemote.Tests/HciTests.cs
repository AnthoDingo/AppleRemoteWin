using Xunit;

namespace AppleRemote.Tests;

public class HciTests
{
    readonly List<byte[]> _sent = new();
    readonly HciHost _host;

    public HciTests() => _host = new HciHost(_sent.Add, _sent.Add);

    [Fact]
    public async Task Command_is_encoded_and_its_complete_event_returns_the_return_parameters()
    {
        var pending = _host.CommandAsync(HciOpcode.ReadBdAddr);
        Assert.Equal(new byte[] { 0x09, 0x10, 0x00 }, _sent.Single());

        // Command Complete découpé n'importe où par les transferts USB : statut 0 + adresse (petit-boutiste).
        _host.ReceiveEventBytes(new byte[] { 0x0E, 0x0A, 0x01 });
        _host.ReceiveEventBytes(new byte[] { 0x09, 0x10, 0x00, 0x50, 0xA0, 0x3B, 0x4B, 0xC8, 0x8C });

        Assert.Equal(new byte[] { 0x00, 0x50, 0xA0, 0x3B, 0x4B, 0xC8, 0x8C }, await pending);
    }

    [Fact]
    public async Task Non_zero_status_throws_and_unrelated_completions_are_ignored()
    {
        var pending = _host.CommandAsync(HciOpcode.Reset);
        _host.ReceiveEventBytes(new byte[] { 0x0E, 0x03, 0x01, 0x00, 0x00 });          // opcode 0 : « prêt »
        _host.ReceiveEventBytes(new byte[] { 0x0F, 0x04, 0x12, 0x01, 0x03, 0x0C });    // Command Status, 0x12
        var e = await Assert.ThrowsAsync<HciException>(() => pending);
        Assert.Equal(0x12, e.Status);
    }

    [Fact]
    public void Other_events_and_acl_packets_are_reassembled()
    {
        var events = new List<(byte, byte[])>();
        var acl = new List<(ushort, int, byte[])>();
        _host.Event += (code, p) => events.Add((code, p));
        _host.AclData += (h, pb, d) => acl.Add((h, pb, d));

        // Deux événements dans un même transfert, le second coupé en deux.
        _host.ReceiveEventBytes(new byte[] { 0x05, 0x04, 0x00, 0x40, 0x00, 0x13, 0x08, 0x04 });
        _host.ReceiveEventBytes(new byte[] { 0x00, 0x40, 0x00, 0x01 });
        Assert.Equal(new byte[] { 0x05, 0x08 }, events.Select(e => e.Item1));
        Assert.Equal(new byte[] { 0x00, 0x40, 0x00, 0x13 }, events[0].Item2);

        // ACL : handle 0x040, PB = 2 (premier fragment), 3 octets.
        _host.ReceiveAclBytes(new byte[] { 0x40, 0x20, 0x03, 0x00, 0xAA });
        _host.ReceiveAclBytes(new byte[] { 0xBB, 0xCC });
        var (handle, pb, data) = acl.Single();
        Assert.Equal((ushort)0x040, handle);
        Assert.Equal(2, pb);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, data);
    }
}
