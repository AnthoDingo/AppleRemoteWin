// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text;
using Xunit;

namespace AppleRemote.Tests;

public class FirmwareImageTests
{
    /// <summary>Signed ESP32-S3 image shape: header, esp_app_desc_t at 32, 4 KB signature sector at the end.</summary>
    static byte[] Signed(int sectors = 3, string version = "1.2.0", string project = FirmwareImage.Project)
    {
        var image = new byte[sectors * 4096];
        image[0] = 0xE9;
        image[12] = 0x09;
        BitConverter.GetBytes(0xABCD5432u).CopyTo(image, 32);
        Encoding.ASCII.GetBytes(version).CopyTo(image, 48);
        Encoding.ASCII.GetBytes(project).CopyTo(image, 80);
        image[^4096] = 0xE7;
        image[^4095] = 0x02;
        return image;
    }

    [Fact]
    public void Accepts_a_signed_image_and_reads_its_version()
    {
        Assert.Null(FirmwareImage.Problem(Signed()));
        Assert.Equal("1.2.0", FirmwareImage.Version(Signed()));
    }

    [Fact]
    public void Rejects_the_wrong_files()
    {
        Assert.Equal("Firmware.BadSize", FirmwareImage.Problem(new byte[100]));
        Assert.Equal("Firmware.BadSize", FirmwareImage.Problem(Signed(FirmwareImage.MaxSize / 4096 + 1)));

        var image = Signed();
        image[0] = 0x7F;   // e.g. an ELF file
        Assert.Equal("Firmware.NotImage", FirmwareImage.Problem(image));

        image = Signed();
        image[12] = 0x00;   // plain ESP32
        Assert.Equal("Firmware.WrongChip", FirmwareImage.Problem(image));

        Assert.Equal("Firmware.WrongProject", FirmwareImage.Problem(Signed(project: "hello_world")));

        image = Signed();
        image[^4096] = 0xFF;   // unsigned build
        Assert.Equal("Firmware.Unsigned", FirmwareImage.Problem(image));
        Assert.Equal("Firmware.Unsigned", FirmwareImage.Problem([.. Signed(), 0]));   // not on a 4 KB boundary
    }

    [Theory]
    [InlineData("1.2.1", "1.2.0", true)]
    [InlineData("1.10.0", "1.9.3", true)]    // numeric, not alphabetical
    [InlineData("1.2.0", "1.2.0", false)]    // same: nothing to offer
    [InlineData("1.1.0", "1.2.0", false)]    // the dongle would refuse a downgrade anyway
    [InlineData("1.2.0", "dev", true)]       // unreadable running version counts as older (ota.c)
    [InlineData("1.2.0", null, false)]       // dongle that cannot report its version cannot be updated
    public void Offers_only_newer_versions(string candidate, string? running, bool expected) =>
        Assert.Equal(expected, FirmwareImage.IsNewer(candidate, running));
}
