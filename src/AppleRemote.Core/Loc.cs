// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Globalization;
using System.Resources;

namespace AppleRemote;

/// <summary>
/// Translated texts (Strings.resx = French, Strings.en.resx = English). The language follows <see cref="CultureInfo.CurrentUICulture"/> :
/// the application sets it at startup (<see cref="SetLanguage"/>). An unknown key is returned as is.
/// </summary>
public static class Loc
{
    static readonly ResourceManager Resources = new("AppleRemote.Strings", typeof(Loc).Assembly);

    /// <summary>"fr", "en" or null/"auto": Windows language (English if it isn't French).</summary>
    public static void SetLanguage(string? language)
    {
        var culture = language is "fr" or "en"
            ? CultureInfo.GetCultureInfo(language)
            : CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "fr" ? CultureInfo.GetCultureInfo("fr") : CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = culture;
    }

    public static string T(string key) => Resources.GetString(key) ?? key;

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>Does the message start like the text of <paramref name="key"/> (up to the first parameter)? Used to classify states.</summary>
    public static bool StartsWith(string message, string key)
    {
        string format = T(key);
        int brace = format.IndexOf('{');
        return message.StartsWith(brace < 0 ? format : format[..brace], StringComparison.Ordinal);
    }
}
