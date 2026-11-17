using System.Globalization;
using System.Runtime.InteropServices;

namespace HybridLauncher.Core.Achievements;

/// <summary>
/// The language achievements are shown in (2.3): Windows' own display language, as Steam and the other launchers do, even
/// when Hybrid Launcher itself has no translation for it (a German Windows shows German achievement names in an English
/// Hybrid Launcher). Falls back to English where a store has no text in that language.
/// </summary>
public static class AchievementLanguage
{
    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    static CultureInfo? _culture;

    /// <summary>Windows' display language (not the app's own, which <see cref="CultureInfo.CurrentUICulture"/> follows).</summary>
    public static CultureInfo Culture => _culture ??= Read();

    static CultureInfo Read()
    {
        // Tests: HL_ACH_LANG=de-DE.
        if (Environment.GetEnvironmentVariable("HL_ACH_LANG") is { Length: > 0 } forced)
            try { return CultureInfo.GetCultureInfo(forced); } catch (CultureNotFoundException) { }
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (Exception) { return CultureInfo.InstalledUICulture; }
    }

    /// <summary>A BCP-47 name such as "it-IT" (Epic, Xbox).</summary>
    public static string Locale => Culture.Name is { Length: > 0 } n ? n : "en-US";

    /// <summary>Steam's name for the language ("english", "italian", "schinese", ...).</summary>
    public static string Steam => SteamName(Culture);

    public static bool IsEnglish => Steam == "english";

    public static string SteamName(CultureInfo c)
    {
        var name = c.Name.ToLowerInvariant();
        return c.TwoLetterISOLanguageName switch
        {
            "it" => "italian",
            "de" => "german",
            "fr" => "french",
            "es" => name == "es-es" ? "spanish" : "latam",
            "pt" => name == "pt-br" ? "brazilian" : "portuguese",
            "ru" => "russian",
            "pl" => "polish",
            "ja" => "japanese",
            "ko" => "koreana",
            "zh" => name.Contains("hant") || name is "zh-tw" or "zh-hk" or "zh-mo" ? "tchinese" : "schinese",
            "tr" => "turkish",
            "uk" => "ukrainian",
            "nl" => "dutch",
            "sv" => "swedish",
            "da" => "danish",
            "nb" or "nn" or "no" => "norwegian",
            "fi" => "finnish",
            "cs" => "czech",
            "hu" => "hungarian",
            "ro" => "romanian",
            "th" => "thai",
            "vi" => "vietnamese",
            "el" => "greek",
            "bg" => "bulgarian",
            "ar" => "arabic",
            "id" => "indonesian",
            _ => "english",
        };
    }
}
