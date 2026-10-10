// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using Xunit;

namespace AppleRemote.Tests;

public class SmpTests
{
    /// <summary>Spec value (written most significant first) -> little-endian bytes, as on the wire.</summary>
    static byte[] Le(string hex) => Convert.FromHexString(hex).Reverse().ToArray();

    [Fact]
    public void C1_matches_the_specification_example()
    {
        // Core, vol. 3, partie H, 2.2.3.
        byte[] c1 = SmpCrypto.C1(new byte[16], Le("5783D52156AD6F0E6388274EC6702EE0"),
            preq: Le("07071000000101"), pres: Le("05000800000302"),
            iat: 1, ia: Le("A1A2A3A4A5A6"), rat: 0, ra: Le("B1B2B3B4B5B6"));
        Assert.Equal(Le("1E1E3FEF878988EAD2A74DC5BEF13B86"), c1);
    }

    [Fact]
    public void S1_matches_the_specification_example()
    {
        // Core, vol. 3, partie H, 2.2.4.
        byte[] s1 = SmpCrypto.S1(new byte[16], Le("000F0E0D0C0B0A091122334455667788"), Le("010203040506070899AABBCCDDEEFF00"));
        Assert.Equal(Le("9A1FE1F0E8B0F49B5B4216AE796DA062"), s1);
    }
}
