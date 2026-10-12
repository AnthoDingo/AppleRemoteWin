// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>
/// Patch download for Realtek controllers (port of Bumble's <c>drivers/rtk.py</c> / Linux <c>btrtl.c</c>).
/// They boot on a ROM firmware that answers HCI but can't hold an LE connection; Windows' Realtek driver
/// sends the patch on every plug, under WinUSB nobody does. The patch lives in RAM only: lost on unplug,
/// kept across HCI Reset. Firmware files come from linux-firmware (<c>rtl_bt/</c>).
/// ponytail: only the RTL8761B (TP-Link UB500, validated on hardware); other chips are rows of Bumble's table.
/// </summary>
public static class RealtekFirmware
{
    const ushort ReadRomVersion = 0xFC6D, Download = 0xFC20, Realtek = 0x005D;
    const int FragmentLength = 252;
    const string FirmwareName = "rtl8761bu_fw.bin", ConfigName = "rtl8761bu_config.bin";
    static readonly byte[] Signature = "Realtech"u8.ToArray(), ExtensionSignature = [0x51, 0x04, 0xFD, 0x77];

    /// <summary>Where the firmware files are looked for: the user's data, then next to the executable.</summary>
    public static readonly string[] Directories =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleRemote", "firmware"),
        Path.Combine(AppContext.BaseDirectory, "firmware"),
    ];

    static byte[]? ReadFromDirectories(string name) =>
        Directories.Select(d => Path.Combine(d, name)).Where(File.Exists).Select(File.ReadAllBytes).FirstOrDefault();

    /// <summary>
    /// Sends the patch if the controller is an RTL8761B still on its ROM firmware; does nothing otherwise
    /// (other chip, or patch already loaded). <paramref name="readFile"/>: file name -> content, or null if missing.
    /// </summary>
    public static async Task LoadIfNeededAsync(HciHost hci, Func<string, byte[]?>? readFile = null)
    {
        await hci.CommandAsync(HciOpcode.Reset);
        // status, HCI version, HCI subversion (2), LMP version, manufacturer (2), LMP subversion (2)
        byte[] v = await hci.CommandAsync(HciOpcode.ReadLocalVersion);
        if (v.Length < 9 || U16(v, 5) != Realtek || U16(v, 7) != 0x8761 || v[1] != 0x0A || U16(v, 2) != 0x000B) return;

        readFile ??= ReadFromDirectories;
        byte[] firmware = readFile(FirmwareName) ?? throw new InvalidOperationException(
            $"Realtek adapter without its firmware: copy {FirmwareName} and {ConfigName} (linux-firmware, rtl_bt) to {Directories[0]}.");
        int rom = (await hci.CommandAsync(ReadRomVersion))[1];
        byte[] payload = [.. Patch(firmware, rom + 1), .. readFile(ConfigName) ?? []];

        int count = (payload.Length + FragmentLength - 1) / FragmentLength;
        for (int i = 0; i < count; i++)
        {
            // Index as in btrtl.c: wraps past 0x7F skipping 0, bit 7 marks the last fragment.
            byte index = (byte)(i > 0x7F ? (i & 0x7F) + 1 : i);
            if (i == count - 1) index |= 0x80;
            await hci.CommandAsync(Download, [index, .. payload.AsSpan(i * FragmentLength, Math.Min(FragmentLength, payload.Length - i * FragmentLength))]);
        }
        await hci.CommandAsync(HciOpcode.Reset);
    }

    /// <summary>
    /// Patch of an "epatch" file for this chip ID (ROM version + 1): its bytes, the last 4 replaced by the firmware version.
    /// Layout: "Realtech", version (4), count N (2), N chip IDs (2), N lengths (2), N offsets (4).
    /// </summary>
    public static byte[] Patch(byte[] firmware, int chipId)
    {
        if (firmware.Length < 14 || !firmware.AsSpan(0, 8).SequenceEqual(Signature) || !firmware.AsSpan(^4).SequenceEqual(ExtensionSignature))
            throw new InvalidDataException("Not a Realtek epatch firmware.");
        byte[] version = firmware[8..12];
        int count = U16(firmware, 12);
        if (14 + 8 * count > firmware.Length) throw new InvalidDataException("Truncated Realtek firmware.");
        for (int i = 0; i < count; i++)
        {
            if (U16(firmware, 14 + 2 * i) != chipId) continue;
            int length = U16(firmware, 14 + 2 * count + 2 * i);
            int offset = BitConverter.ToInt32(firmware, 14 + 4 * count + 4 * i);
            if (length < 4 || offset < 0 || offset + length > firmware.Length) throw new InvalidDataException("Truncated Realtek firmware.");
            return [.. firmware.AsSpan(offset, length - 4), .. version];
        }
        throw new InvalidDataException($"No patch for Realtek chip {chipId} in this firmware.");
    }

    static int U16(byte[] b, int i) => b[i] | b[i + 1] << 8;
}
