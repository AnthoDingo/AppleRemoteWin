using Xunit;

namespace AppleRemote.Tests;

public class BleTests
{
    readonly List<byte[]> _sent = new();
    readonly HciHost _hci;
    readonly LeCentral _central;

    public BleTests()
    {
        _hci = new HciHost(_sent.Add, _sent.Add);
        _central = new LeCentral(_hci);
    }

    void CommandStatusOk(ushort opcode) => _hci.ReceiveEventBytes([0x0F, 0x04, 0x00, 0x01, (byte)opcode, (byte)(opcode >> 8)]);

    [Fact]
    public void Addresses_are_little_endian_on_the_wire()
    {
        Assert.Equal(new byte[] { 0xCC, 0xBF, 0x26, 0x99, 0x9D, 0x14 }, BdAddr.Parse("14:9D:99:26:BF:CC"));
        Assert.Equal("14:9D:99:26:BF:CC", BdAddr.Format(BdAddr.Parse("14:9d:99:26:bf:cc")));
    }

    [Fact]
    public async Task Connect_targets_the_public_address_and_returns_the_handle()
    {
        var connecting = _central.ConnectAsync("14:9D:99:26:BF:CC");
        byte[] cmd = _sent.Single();
        Assert.Equal(new byte[] { 0x0D, 0x20, 25 }, cmd[..3]);
        Assert.Equal(new byte[] { 0x00, 0xCC, 0xBF, 0x26, 0x99, 0x9D, 0x14, 0x00 }, cmd[8..16]);   // pair publique, nous publics

        CommandStatusOk(HciOpcode.LeCreateConnection);
        Assert.False(connecting.IsCompleted);   // en attente de la télécommande
        _hci.ReceiveEventBytes([0x3E, 0x13, 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, 0xCC, 0xBF, 0x26, 0x99, 0x9D, 0x14,
            0x18, 0x00, 0x00, 0x00, 0xC8, 0x00, 0x00]);
        Assert.Equal((ushort)0x0040, await connecting);
    }

    [Fact]
    public async Task Encrypt_sends_the_bond_keys_and_waits_for_the_encryption_change()
    {
        var keys = new BondKeys(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(), 0x4D3E, [9, 8, 7, 6, 5, 4, 3, 2]);
        var encrypting = _central.EncryptAsync(0x0040, keys);
        Assert.Equal(new byte[] { 0x19, 0x20, 28, 0x40, 0x00, 9, 8, 7, 6, 5, 4, 3, 2, 0x3E, 0x4D, 1, 2, 3, 4, 5, 6, 7, 8,
            9, 10, 11, 12, 13, 14, 15, 16 }, _sent.Single());

        CommandStatusOk(HciOpcode.LeEnableEncryption);
        Assert.False(encrypting.IsCompleted);
        _hci.ReceiveEventBytes([0x08, 0x04, 0x00, 0x40, 0x00, 0x01]);
        await encrypting;
    }

    [Fact]
    public async Task Disconnection_fails_a_pending_encryption_and_is_reported()
    {
        var disconnected = new List<(ushort, byte)>();
        _central.Disconnected += (h, reason) => disconnected.Add((h, reason));

        var encrypting = _central.EncryptAsync(0x0040, new BondKeys(new byte[16], 0, new byte[8]));
        CommandStatusOk(HciOpcode.LeEnableEncryption);
        _hci.ReceiveEventBytes([0x05, 0x04, 0x00, 0x40, 0x00, 0x3E]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => encrypting);
        Assert.Equal((0x0040, 0x3E), disconnected.Single());
    }
}
