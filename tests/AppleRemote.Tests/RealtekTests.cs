// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using Xunit;

namespace AppleRemote.Tests;

public class RealtekTests
{
    /// <summary>Two-patch epatch file (chips 1 and 2) like rtl8761bu_fw.bin; patch 2 spans two fragments.</summary>
    static byte[] Firmware(out byte[] patch2)
    {
        byte[] patch1 = Enumerable.Repeat((byte)0x11, 20).ToArray();
        patch2 = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        int offset1 = 14 + 8 * 2, offset2 = offset1 + patch1.Length;
        return
        [
            .. "Realtech"u8, 0x22, 0xD9, 0xC6, 0xDF, 0x02, 0x00,
            0x01, 0x00, 0x02, 0x00,                                     // chip IDs
            (byte)patch1.Length, 0x00, (byte)patch2.Length, (byte)(patch2.Length >> 8),
            .. BitConverter.GetBytes(offset1), .. BitConverter.GetBytes(offset2),
            .. patch1, .. patch2, 0x51, 0x04, 0xFD, 0x77,
        ];
    }

    /// <summary>Fake controller: answers the commands, records the download fragments.</summary>
    static (HciHost Hci, List<byte[]> Downloads) Controller(ushort lmpSubversion)
    {
        var downloads = new List<byte[]>();
        HciHost? hci = null;
        hci = new HciHost(c =>
        {
            byte[] result = (ushort)(c[0] | c[1] << 8) switch
            {
                HciOpcode.ReadLocalVersion => [0x00, 0x0A, 0x0B, 0x00, 0x0A, 0x5D, 0x00, (byte)lmpSubversion, (byte)(lmpSubversion >> 8)],
                0xFC6D => [0x00, 0x01],                                // ROM version 1: chip 2
                0xFC20 => [0x00, c[3]],
                _ => [0x00],
            };
            if (c[0] == 0x20 && c[1] == 0xFC) downloads.Add(c[3..]);
            hci!.ReceiveEventBytes([0x0E, (byte)(3 + result.Length), 0x01, c[0], c[1], .. result]);
        }, _ => { });
        return (hci, downloads);
    }

    [Fact]
    public async Task Rom_controller_receives_the_matching_patch_then_the_config_in_indexed_fragments()
    {
        byte[] firmware = Firmware(out byte[] patch2);
        byte[] config = [0x55, 0xAB, 0x23, 0x87, 0x00, 0x00];
        var (hci, downloads) = Controller(0x8761);

        await RealtekFirmware.LoadIfNeededAsync(hci, name => name.EndsWith("_fw.bin") ? firmware : config);

        Assert.Equal(new byte[] { 0x00, 0x81 }, downloads.Select(d => d[0]));
        byte[] expected = [.. patch2[..^4], 0x22, 0xD9, 0xC6, 0xDF, .. config];
        Assert.Equal(expected, downloads.SelectMany(d => d[1..]));
        Assert.Equal(252, downloads[0].Length - 1);
    }

    [Fact]
    public async Task Patched_controller_and_missing_firmware_are_handled()
    {
        var (patched, downloads) = Controller(0xD922);
        await RealtekFirmware.LoadIfNeededAsync(patched, _ => throw new Exception("must not read"));
        Assert.Empty(downloads);

        var (rom, _) = Controller(0x8761);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => RealtekFirmware.LoadIfNeededAsync(rom, _ => null));
        Assert.Contains("rtl8761bu_fw.bin", e.Message);
    }

    [Fact]
    public void Unknown_chip_or_foreign_file_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RealtekFirmware.Patch(Firmware(out _), 3));
        Assert.Throws<InvalidDataException>(() => RealtekFirmware.Patch(new byte[40], 2));
    }
}
