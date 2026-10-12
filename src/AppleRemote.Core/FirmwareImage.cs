// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text;

namespace AppleRemote;

/// <summary>
/// The ESP32-S3 dongle firmware shipped with the app. Not a security boundary: the dongle verifies the signature and the
/// version itself (src/AppleRemoteEsp32/main/ota.c). This only rejects a damaged file early and reads its version.
/// </summary>
public static class FirmwareImage
{
    /// <summary>OTA slot of the "two OTA" partition table (1 MB).</summary>
    public const int MaxSize = 0x100000;

    public const string Project = "AppleRemoteEsp32";

    const byte ImageMagic = 0xE9, SignatureMagic = 0xE7, SignatureVersion = 0x02;
    const ushort ChipEsp32S3 = 0x0009;
    const uint AppDescMagic = 0xABCD5432;
    const int Sector = 4096;
    // esp_app_desc_t right after the image header (24 bytes) and the first segment header (8 bytes)
    const int AppDesc = 32, AppDescVersion = AppDesc + 16, AppDescProject = AppDesc + 48;

    /// <summary>Resource key of the problem (<c>Firmware.*</c>), null if the image looks sendable.</summary>
    public static string? Problem(byte[] image)
    {
        if (image.Length < 2 * Sector || image.Length > MaxSize) return "Firmware.BadSize";
        if (image[0] != ImageMagic || BitConverter.ToUInt32(image, AppDesc) != AppDescMagic) return "Firmware.NotImage";
        if ((image[12] | image[13] << 8) != ChipEsp32S3) return "Firmware.WrongChip";   // esp_image_header_t.chip_id
        if (Text(image, AppDescProject) != Project) return "Firmware.WrongProject";
        // Secure Boot V2 format: image padded to 4 KB, then a 4 KB signature sector starting with a signature block.
        if (image.Length % Sector != 0 || image[^Sector] != SignatureMagic || image[^(Sector - 1)] != SignatureVersion)
            return "Firmware.Unsigned";
        return null;
    }

    /// <summary>Version of an image <see cref="Problem"/> accepted (PROJECT_VER of the firmware).</summary>
    public static string Version(byte[] image) => Text(image, AppDescVersion);

    /// <summary>
    /// Should <paramref name="candidate"/> be offered to a dongle running <paramref name="running"/>? Only strictly newer
    /// "major.minor.patch" versions; an unreadable running version counts as older, like in ota.c.
    /// </summary>
    public static bool IsNewer(string candidate, string? running)
    {
        if (running is null || !System.Version.TryParse(candidate, out var next)) return false;
        return !System.Version.TryParse(running, out var current) || next > current;
    }

    static string Text(byte[] image, int offset) => Encoding.ASCII.GetString(image, offset, 32).TrimEnd('\0');
}
