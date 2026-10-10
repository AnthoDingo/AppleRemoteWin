using System.Globalization;
using System.Resources;

namespace AppleRemote;

/// <summary>
/// Textes traduits (Strings.resx = français, Strings.en.resx = anglais). La langue suit <see cref="CultureInfo.CurrentUICulture"/> :
/// l'application la fixe au démarrage (<see cref="SetLanguage"/>). Une clé inconnue est rendue telle quelle.
/// </summary>
public static class Loc
{
    static readonly ResourceManager Resources = new("AppleRemote.Strings", typeof(Loc).Assembly);

    /// <summary>« fr », « en » ou null/« auto » : langue de Windows (anglais si ce n'est pas le français).</summary>
    public static void SetLanguage(string? language)
    {
        var culture = language is "fr" or "en"
            ? CultureInfo.GetCultureInfo(language)
            : CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "fr" ? CultureInfo.GetCultureInfo("fr") : CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = culture;
    }

    public static string T(string key) => Resources.GetString(key) ?? key;

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>Le message commence-t-il comme le texte <paramref name="key"/> (jusqu'au premier paramètre) ? Sert à classer les états.</summary>
    public static bool StartsWith(string message, string key)
    {
        string format = T(key);
        int brace = format.IndexOf('{');
        return message.StartsWith(brace < 0 ? format : format[..brace], StringComparison.Ordinal);
    }
}
