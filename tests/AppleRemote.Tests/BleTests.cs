// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

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
        Assert.Equal(new byte[] { 0x00, 0xCC, 0xBF, 0x26, 0x99, 0x9D, 0x14, 0x00 }, cmd[8..16]);   // public peer, public us

        CommandStatusOk(HciOpcode.LeCreateConnection);
        Assert.False(connecting.IsCompleted);   // waiting for the remote
        _hci.ReceiveEventBytes([0x3E, 0x13, 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, 0xCC, 0xBF, 0x26, 0x99, 0x9D, 0x14,
            0x18, 0x00, 0x00, 0x00, 0xC8, 0x00, 0x00]);
        Assert.Equal((ushort)0x0040, await connecting);
    }

    [Fact]
    public void Scan_reports_every_advertisement_with_address_and_rssi()
    {
        var seen = new List<(byte[] Address, sbyte Rssi, byte[] Data)>();
        _central.Advertisement += (a, r, d) => seen.Add((a, r, d));
        using var cts = new CancellationTokenSource();
        var scanning = _central.ScanAsync(_ => false, cts.Token);
        void CommandCompleteOk(ushort opcode) => _hci.ReceiveEventBytes([0x0E, 0x04, 0x01, (byte)opcode, (byte)(opcode >> 8), 0x00]);
        CommandCompleteOk(HciOpcode.LeSetScanParameters);
        CommandCompleteOk(HciOpcode.LeSetScanEnable);

        // Name "AB", Apple manufacturer data, 16-bit services 0x180F + 0x1812; random address; RSSI -60.
        byte[] ad = [0x03, 0x09, (byte)'A', (byte)'B', 0x04, 0xFF, 0x4C, 0x00, 0x01, 0x05, 0x03, 0x0F, 0x18, 0x12, 0x18];
        _hci.ReceiveEventBytes([0x3E, (byte)(12 + ad.Length), 0x02, 0x01, 0x00, 0x01, 1, 2, 3, 4, 5, 6, (byte)ad.Length, .. ad, 0xC4]);

        var (address, rssi, data) = seen.Single();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, address);
        Assert.Equal(-60, rssi);
        Assert.Equal([(0x09, "AB"u8.ToArray()), (0xFF, [0x4C, 0x00, 0x01]), (0x03, [0x0F, 0x18, 0x12, 0x18])],
            LeCentral.AdStructures(data).Select(s => (s.Type, s.Value)));

        Assert.False(scanning.IsCompleted);   // nothing matched: scans until cancelled
        cts.Cancel();
    }

    [Fact]
    public async Task Bluetooth_5_controller_scans_and_connects_with_the_extended_commands()
    {
        // Self-answering controller announcing LE Extended Advertising (feature bit 12).
        var sent = new List<byte[]>();
        HciHost? hci = null;
        hci = new HciHost(c =>
        {
            sent.Add(c);
            ushort op = (ushort)(c[0] | c[1] << 8);
            if (op == HciOpcode.LeExtendedCreateConnection) hci!.ReceiveEventBytes([0x0F, 0x04, 0x00, 0x01, c[0], c[1]]);
            else hci!.ReceiveEventBytes([0x0E, 12, 0x01, c[0], c[1], 0x00, .. (op == HciOpcode.LeReadLocalFeatures ? new byte[] { 0, 0x10, 0, 0, 0, 0, 0, 0 } : new byte[8])]);
        }, _ => { });
        var central = new LeCentral(hci);
        await central.InitializeAsync();
        Assert.True(central.Extended);
        Assert.Contains(sent, c => c[0] == 0x01 && c[1] == 0x20 && c[3] == 0x1F && c[4] == 0x10);   // LE event mask: + extended report

        var scanning = central.ScanAsync(SiriRemoteBle.IsSiriRemoteAdvertisement);
        Assert.Equal(new byte[] { 0x41, 0x20, 8, 0x00, 0x00, 0x01, 0x01, 0x60, 0x00, 0x30, 0x00 }, sent[^2]);
        // Extended report of a legacy ADV_IND (event type 0x13) from a public address, RSSI -40.
        byte[] ad = FakeRemote.PairingAdvertisement;
        hci.ReceiveEventBytes([0x3E, (byte)(26 + ad.Length), 0x0D, 0x01, 0x13, 0x00, 0x00, .. BdAddr.Parse(FakeRemote.Address),
            0x01, 0x00, 0xFF, 0x7F, 0xD8, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, (byte)ad.Length, .. ad]);
        Assert.Equal(FakeRemote.Address, await scanning);
        Assert.Equal(new byte[] { 0x42, 0x20, 6, 0, 0, 0, 0, 0, 0 }, sent[^1]);   // scan disabled

        var connecting = central.ConnectAsync(FakeRemote.Address);
        byte[] expected = [0x43, 0x20, 26, 0x00, 0x00, 0x00, .. BdAddr.Parse(FakeRemote.Address), 0x01, 0x60, 0x00, 0x30, 0x00,
            0x0C, 0x00, 0x18, 0x00, 0x00, 0x00, 0xC8, 0x00, 0, 0, 0, 0];
        Assert.Equal(expected, sent[^1]);
        hci.ReceiveEventBytes([0x3E, 0x13, 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, .. BdAddr.Parse(FakeRemote.Address),
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
