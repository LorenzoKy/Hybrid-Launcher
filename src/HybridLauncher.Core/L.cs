using System.Globalization;

namespace HybridLauncher.Core;

/// <summary>
/// The UI language. The English text in the code is the key: <see cref="T"/> returns its translation for the chosen language,
/// or the English itself when there is none. Picked once at start (Settings → Appearance → Language, "auto" = Windows' language).
/// </summary>
public static class L
{
    static IReadOnlyDictionary<string, string>? _table;

    /// <summary>"en" or "it": the language the UI is shown in this session.</summary>
    public static string Language { get; private set; } = "en";

    public static bool Italian => Language == "it";

    /// <summary>The languages offered in Settings: (setting value, name in its own language).</summary>
    public static readonly (string Key, string Name)[] Choices = [("auto", "Automatic (Windows)"), ("en", "English"), ("it", "Italiano")];

    /// <summary>Windows' own culture, as it was before <see cref="Init"/> (the console's code page follows it, not the UI).</summary>
    public static CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>Windows' display language, as it was before <see cref="Init"/>.</summary>
    public static CultureInfo SystemUICulture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>The language "auto" stands for: Italian on an Italian Windows, English otherwise.</summary>
    public static string AutoLanguage => SystemUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";

    public static void Init(string? setting)
    {
        SystemCulture = CultureInfo.CurrentCulture;
        SystemUICulture = CultureInfo.CurrentUICulture;
        Language = setting is "en" or "it" ? setting : AutoLanguage;
        _table = Language == "it" ? Localization.Italian.Table : null;
        // Dates and numbers follow the UI language: English on an Italian Windows showed "18 lug 2026" and "15,9 GB". A Windows
        // already in that language keeps its own region (en-US stays en-US); otherwise British English or Italian.
        if (SystemCulture.TwoLetterISOLanguageName != Language)
        {
            var culture = CultureInfo.GetCultureInfo(Language == "it" ? "it-IT" : "en-GB");
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary>The text in the UI language.</summary>
    public static string T(string english) => _table is not null && _table.TryGetValue(english, out var s) ? s : english;

    /// <summary>A format string in the UI language, filled with <paramref name="args"/>.</summary>
    public static string F(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>Singular or plural ("{0} game" / "{0} games"), filled with the count.</summary>
    public static string N(long count, string one, string many) => F(count == 1 ? one : many, count);
}
