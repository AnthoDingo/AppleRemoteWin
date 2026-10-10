// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Xunit;

namespace AppleRemote.Tests;

public class LocTests
{
    static readonly ResourceManager Rm = new("AppleRemote.Strings", typeof(Loc).Assembly);

    static Dictionary<string, string> Load(string culture) =>
        Rm.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!
          .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [Fact]
    public void English_has_every_french_key_with_the_same_placeholders()
    {
        var fr = Load("fr");
        var en = Load("en");
        Assert.Equal(fr.Keys.Order(), en.Keys.Order());
        foreach (var (key, text) in fr)
            Assert.Equal(Placeholders(text), Placeholders(en[key]));
    }

    static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\d+[^}]*\}").Select(m => m.Value).Order());

    [Fact]
    public void SetLanguage_switches_the_texts_and_status_prefixes_still_match()
    {
        var previous = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Loc.SetLanguage("en");
            Assert.Equal("Stopped.", Loc.T("Status.Stopped"));
            Assert.True(Loc.StartsWith(Loc.T("Status.ConnectedHid", "Siri Remote"), "Status.ConnectedHid"));
            Loc.SetLanguage("fr");
            Assert.Equal("Arrêté.", Loc.T("Status.Stopped"));
            Assert.Equal("Inconnue", Loc.T("Inconnue"));   // unknown key returned as is
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = previous ?? CultureInfo.InvariantCulture;
        }
    }
}
