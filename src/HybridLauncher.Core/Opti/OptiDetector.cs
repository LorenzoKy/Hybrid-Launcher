using System.Diagnostics;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Opti;

public sealed record DetectedOpti(string DllPath, string Version)
{
    public string FileName => Path.GetFileName(DllPath);
}

public static class OptiDetector
{
    static readonly string[] ProxyNames = [.. OptiSetupOptions.DllNames, "OptiScaler.dll", "nvngx.dll"];

    /// <summary>Finds an OptiScaler dll in <paramref name="dir"/> by its version resource, whoever installed it.</summary>
    public static DetectedOpti? Detect(string dir)
    {
        foreach (var name in ProxyNames)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && IsOptiScaler(path)) return new DetectedOpti(path, Version(path));
        }
        return null;
    }

    public static bool IsOptiScaler(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(v.OriginalFilename, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.ProductName, "OptiScaler", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string Version(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.ProductVersion ?? v.FileVersion ?? "?").Trim();
        }
        catch { return "?"; }
    }

    /// <summary>Describes a file that would be replaced by the proxy dll, or null if the name is free or already OptiScaler.</summary>
    public static string? DescribeConflict(string dir, string dllName)
    {
        var path = Path.Combine(dir, dllName);
        if (!File.Exists(path) || IsOptiScaler(path)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var what = string.Join(" ", new[] { v.ProductName, v.ProductVersion, string.IsNullOrWhiteSpace(v.CompanyName) ? null : $"by {v.CompanyName}" }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(what) ? $"{dllName} (unknown mod or game file)" : $"{dllName}: {what}";
        }
        catch { return dllName; }
    }
}

/// <summary>Mirrors the bat's OptiPatcher check: the game is supported if one of its exes is listed in OptiPatcher's dllmain.cpp.</summary>
public static partial class OptiPatcherSupport
{
    const string Source = "https://raw.githubusercontent.com/optiscaler/OptiPatcher/main/OptiPatcher/dllmain.cpp";
    public const string ReleasesPage = "https://github.com/optiscaler/OptiPatcher";

    static HashSet<string>? _supported;

    [GeneratedRegex(@"CHECK_UE\s*\(\s*([a-zA-Z0-9_]+)\s*\)")]
    private static partial Regex CheckUe();

    [GeneratedRegex(@"exeName\s*==\s*[""']([^""']+)[""']")]
    private static partial Regex DirectExe();

    public static async Task<bool?> IsSupportedAsync(string exeDir, CancellationToken ct = default)
    {
        if (_supported is null)
        {
            var code = await Http.GetCachedAsync(Source, "optipatcher-dllmain.cpp", TimeSpan.FromHours(12), ct: ct);
            if (code is null) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in CheckUe().Matches(code))
            {
                set.Add(m.Groups[1].Value + "-win64-shipping.exe");
                set.Add(m.Groups[1].Value + "-wingdk-shipping.exe");
            }
            foreach (Match m in DirectExe().Matches(code)) set.Add(m.Groups[1].Value);
            _supported = set;
        }
        try
        {
            return Directory.EnumerateFiles(exeDir, "*.exe").Any(f => _supported.Contains(Path.GetFileName(f)));
        }
        catch { return null; }
    }
}

/// <summary>One row of a game's OptiScaler wiki page ("Known Issues" → its lines).</summary>
public sealed record OptiWikiField(string Name, List<Media.RichLine> Lines)
{
    public string Text => string.Join(" ", Lines.Select(l => l.Text)).Trim();
    public bool IsEmpty => Text.Length == 0 || Text == "-";
}

/// <summary>A game's page on the OptiScaler wiki: the table of tested versions, inputs, settings, known issues and notes.</summary>
public sealed class OptiWikiEntry
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public List<OptiWikiField> Fields { get; init; } = [];

    public OptiWikiField? Get(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Finds a game's page on the OptiScaler wiki by matching its name against the Compatibility List, and reads it.</summary>
public static partial class CompatWiki
{
    public const string ListUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
    const string WikiBase = "https://github.com/optiscaler/OptiScaler/wiki/";

    static Dictionary<string, string>? _pages;
    static readonly SemaphoreSlim ListGate = new(1, 1);

    // Table rows link with absolute URLs, the sidebar with relative ones (its title sits in a nested div, so group 2 may be empty).
    [GeneratedRegex(@"href=""(?:https://github\.com)?/optiscaler/OptiScaler/wiki/([^""#?/]+)""[^>]*>([^<]*)<", RegexOptions.IgnoreCase)]
    private static partial Regex WikiLink();

    /// <summary>The game's page, or the Compatibility List when it has none.</summary>
    public static async Task<string> PageForAsync(string gameName, CancellationToken ct = default) =>
        await SlugForAsync(gameName, ct) is { } slug ? WikiBase + slug : ListUrl;

    /// <summary>The wiki page name (still percent-encoded) for the game, or null when the Compatibility List has none.</summary>
    public static async Task<string?> SlugForAsync(string gameName, CancellationToken ct = default)
    {
        if (_pages is null)
        {
            await ListGate.WaitAsync(ct);
            try
            {
                if (_pages is null)
                {
                    var html = await Http.GetCachedAsync(ListUrl, "optiscaler-compat.html", TimeSpan.FromHours(24), ct: ct);
                    if (html is null) return null; // offline: try again next time
                    var pages = new Dictionary<string, string>();
                    foreach (Match m in WikiLink().Matches(html))
                    {
                        var slug = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                        // Slugs are percent-encoded ("Marvels-Spider%E2%80%90Man-2"): decode before matching, keep encoded for the URL.
                        var slugKey = Detection.ExeLocator.Normalize(Uri.UnescapeDataString(slug));
                        var titleKey = Detection.ExeLocator.Normalize(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));
                        pages.TryAdd(slugKey, slug);
                        // Skip generic link texts ("compatibility entry", "this entry") that don't name the page.
                        if (titleKey.Length >= 3 && slugKey.StartsWith(titleKey[..3])) pages.TryAdd(titleKey, slug);
                    }
                    // Wiki housekeeping pages are not games.
                    foreach (var k in pages.Keys.Where(k => k is "home" or "compatibilitylist" or "fsr4compatibilitylist").ToList()) pages.Remove(k);
                    _pages = pages;
                }
            }
            finally
            {
                ListGate.Release();
            }
        }

        var key = Detection.ExeLocator.Normalize(gameName);
        if (key.Length == 0) return null;
        if (_pages.TryGetValue(key, out var exact)) return exact;
        if (_pages.TryGetValue(Media.GameNames.Key(gameName), out var plain)) return plain;
        // "Cyberpunk 2077: Phantom Liberty" vs "Cyberpunk-2077": the page may name less than the game, never more
        // ("Control" must not open "Control-Resonant").
        var best = _pages.Where(p => p.Key.Length >= 5 && key.StartsWith(p.Key))
                         .OrderByDescending(p => p.Key.Length).FirstOrDefault();
        return best.Value;
    }

    /// <summary>The game's wiki page read into fields, or null when it has none. The page's content is cached for three days.</summary>
    public static async Task<OptiWikiEntry?> GetEntryAsync(string gameName, CancellationToken ct = default)
    {
        try
        {
            var slug = await SlugForAsync(gameName, ct);
            if (slug is null) return null;
            var dir = Path.Combine(AppInfo.CacheDir, "optiwiki");
            var file = Path.Combine(dir, Media.Pcgw.Hash(slug) + ".html");
            string? body = null;
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(3))
                body = await File.ReadAllTextAsync(file, ct);
            else
            {
                // The page is ~800 KB of GitHub chrome around a few KB of wiki text: only the text is kept.
                var tmpName = Path.Combine("optiwiki", $"page-{Guid.NewGuid():N}.tmp");
                var html = await Http.GetCachedAsync(WikiBase + slug, tmpName, TimeSpan.Zero, ct: ct);
                try { File.Delete(Path.Combine(AppInfo.CacheDir, tmpName)); } catch { }
                if (html is not null && Content(html) is { Length: > 0 } content)
                {
                    body = content;
                    Directory.CreateDirectory(dir);
                    await File.WriteAllTextAsync(file, body, ct);
                }
                else if (File.Exists(file)) body = await File.ReadAllTextAsync(file, ct);
            }
            if (body is null) return null;
            var title = Uri.UnescapeDataString(slug).Replace('-', ' ').Replace('‐', '-');
            return new OptiWikiEntry { Url = WikiBase + slug, Title = title, Fields = await Task.Run(() => ParseFields(body), ct) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"OptiScaler wiki for {gameName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The page's markdown-body div (the wiki text), without GitHub's navigation.</summary>
    static string Content(string html)
    {
        var start = html.IndexOf("<div class=\"markdown-body\">", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = html.IndexOf("Layout-sidebar", start, StringComparison.Ordinal);
        if (end > start) end = html.LastIndexOf('<', end);
        if (end < 0) end = Math.Min(html.Length, start + 200_000);
        return html[start..end];
    }

    /// <summary>Two-column table rows become fields; text outside the table becomes one more field.</summary>
    public static List<OptiWikiField> ParseFields(string body)
    {
        var fields = new List<OptiWikiField>();
        foreach (Match row in Regex.Matches(body, @"<tr>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = Regex.Matches(row.Groups[1].Value, @"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (cells.Count < 2) continue;
            var name = Media.HtmlText.Plain(cells[0].Groups[1].Value);
            if (name.Length == 0) continue;
            fields.Add(new OptiWikiField(name, Media.HtmlText.ToLines(cells[1].Groups[1].Value)));
        }
        var outside = Regex.Replace(body, @"<table.*?</table>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var extra = Media.HtmlText.ToLines(outside);
        if (extra.Count > 0) fields.Add(new OptiWikiField(fields.Count == 0 ? "Notes" : "More on the page", extra));
        return fields;
    }
}
