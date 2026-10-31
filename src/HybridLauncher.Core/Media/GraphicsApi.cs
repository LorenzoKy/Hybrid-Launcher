using System.Text.Json;
using System.Text.RegularExpressions;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Media;

/// <summary>The rendering APIs a game uses, e.g. ["DX12"] or ["DX11", "Vulkan"], and where that came from.</summary>
public sealed record GraphicsApiInfo(IReadOnlyList<string> Apis, string Source)
{
    public static readonly GraphicsApiInfo Unknown = new([], "");
    public bool IsKnown => Apis.Count > 0;
    public string Display => string.Join(" / ", Apis);
}

/// <summary>
/// Which graphics API a game renders with. PCGamingWiki's API table is the reference (it knows about DX12-only games
/// whose exe still links DX11); when the wiki has nothing, the game exe's import table is read instead.
/// </summary>
public static partial class GraphicsApi
{
    static readonly string[] Order = ["DX9", "DX10", "DX11", "DX12", "Vulkan", "OpenGL"];

    public static async Task<GraphicsApiInfo> GetAsync(Game game, string? exe, string? steamAppId, CancellationToken ct = default)
    {
        try
        {
            var wiki = await FromPcgwAsync(game.Name, steamAppId, ct);
            if (wiki.IsKnown) return wiki;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"PCGamingWiki API lookup for {game.Name} failed: {ex.Message}");
        }
        return exe is null ? GraphicsApiInfo.Unknown : await Task.Run(() => FromExe(exe), ct);
    }

    // ---------- PCGamingWiki ----------

    static async Task<GraphicsApiInfo> FromPcgwAsync(string name, string? steamAppId, CancellationToken ct)
    {
        var key = steamAppId is not null ? $"app-{steamAppId}" : $"name-{Hash(name)}";
        var cache = Path.Combine(AppInfo.CacheDir, "pcgw", $"api-{key}.txt");
        if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(30))
        {
            var cached = (await File.ReadAllTextAsync(cache, ct)).Split(',', StringSplitOptions.RemoveEmptyEntries);
            return new GraphicsApiInfo(cached, "PCGamingWiki");
        }

        // The page is shared with the overview and the PCGamingWiki tab (one download for all three).
        var page = await Pcgw.GetPageAsync(name, steamAppId, ct);
        if (page is null) return GraphicsApiInfo.Unknown;
        var apis = ParseWikitext(page.Value.Wikitext);

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache, string.Join(",", apis), ct);
        return new GraphicsApiInfo(apis, "PCGamingWiki");
    }

    /// <summary>Reads "direct3d versions = 11, 12", "vulkan versions = 1.3" and "opengl versions" from the {{API}} template.</summary>
    public static List<string> ParseWikitext(string wikitext)
    {
        var found = new HashSet<string>();
        var api = wikitext.IndexOf("{{API", StringComparison.OrdinalIgnoreCase);
        var block = api < 0 ? wikitext : wikitext[api..Math.Min(wikitext.Length, api + 4000)];
        foreach (Match m in ApiField().Matches(block))
        {
            var field = m.Groups[1].Value.ToLowerInvariant();
            var value = StripMarkup(m.Groups[2].Value);
            if (value.Length == 0 || value.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
            if (field == "direct3d")
            {
                foreach (Match v in Regex.Matches(value, @"\b(9|10|11|12)(\.\d+)?\b"))
                    found.Add("DX" + v.Groups[1].Value);
            }
            else if (field == "vulkan") found.Add("Vulkan");
            else if (field == "opengl") found.Add("OpenGL");
        }
        return Order.Where(found.Contains).ToList();
    }

    static string StripMarkup(string s)
    {
        s = Regex.Replace(s, @"<ref[^>]*/>|<ref[^>]*>.*?</ref>|<!--.*?-->|\{\{[^}]*\}\}", "", RegexOptions.Singleline);
        return s.Trim();
    }

    [GeneratedRegex(@"^\|\s*(direct3d|vulkan|opengl)\s+versions\s*=([^\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ApiField();

    static string Hash(string s) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..12];

    // ---------- the exe's imports ----------

    static readonly (string Dll, string Api)[] ApiDlls =
    [
        ("d3d9.dll", "DX9"), ("d3d10.dll", "DX10"), ("d3d10_1.dll", "DX10"), ("d3d11.dll", "DX11"),
        ("d3d12.dll", "DX12"), ("vulkan-1.dll", "Vulkan"), ("opengl32.dll", "OpenGL"),
    ];

    /// <summary>The APIs the exe links (normal and delay-loaded imports), plus DX12 when the Agility SDK ships next to it.</summary>
    public static GraphicsApiInfo FromExe(string exe)
    {
        try
        {
            var found = new HashSet<string>();
            foreach (var dll in PeImports.Read(exe))
                foreach (var (name, api) in ApiDlls)
                    if (dll.Equals(name, StringComparison.OrdinalIgnoreCase)) found.Add(api);
            var dir = Path.GetDirectoryName(exe)!;
            if (File.Exists(Path.Combine(dir, "D3D12", "D3D12Core.dll"))) found.Add("DX12");
            // DX11 is linked by nearly every DX12 game for tools and fallbacks; it only counts when nothing newer is there.
            if (found.Contains("DX12") && found.Contains("DX11")) found.Remove("DX11");
            return new GraphicsApiInfo(Order.Where(found.Contains).ToList(), "exe imports");
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading imports of {exe} failed: {ex.Message}");
            return GraphicsApiInfo.Unknown;
        }
    }
}

/// <summary>Minimal PE reader: the names of the dlls an exe imports, including delay-loaded ones.</summary>
public static class PeImports
{
    public static List<string> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new BinaryReader(fs);
        var names = new List<string>();
        if (fs.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return names;
        fs.Position = 0x3C;
        var peOffset = r.ReadInt32();
        if (peOffset <= 0 || peOffset > fs.Length - 24) return names;
        fs.Position = peOffset;
        if (r.ReadUInt32() != 0x00004550) return names;

        fs.Position = peOffset + 6;
        int sectionCount = r.ReadUInt16();
        fs.Position = peOffset + 20;
        int optionalSize = r.ReadUInt16();
        long optional = peOffset + 24;
        fs.Position = optional;
        bool pe32Plus = r.ReadUInt16() == 0x20B;
        long dataDirs = optional + (pe32Plus ? 112 : 96);

        var sections = new List<(uint Va, uint Size, uint Raw)>();
        fs.Position = optional + optionalSize;
        for (int i = 0; i < sectionCount && i < 96; i++)
        {
            fs.Position = optional + optionalSize + i * 40 + 8;
            uint vsize = r.ReadUInt32(), va = r.ReadUInt32(), rawSize = r.ReadUInt32(), raw = r.ReadUInt32();
            sections.Add((va, Math.Max(vsize, rawSize), raw));
        }

        long Offset(uint rva)
        {
            foreach (var s in sections)
                if (rva >= s.Va && rva < s.Va + s.Size) return rva - s.Va + s.Raw;
            return -1;
        }

        string? NameAt(uint rva)
        {
            var off = Offset(rva);
            if (off < 0 || off >= fs.Length) return null;
            fs.Position = off;
            var bytes = new List<byte>(32);
            for (int b; bytes.Count < 256 && (b = fs.ReadByte()) > 0;) bytes.Add((byte)b);
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        // Directory 1 = imports (20-byte descriptors, name at +12); 13 = delay imports (32-byte descriptors, name at +4).
        foreach (var (index, size, nameAt) in new[] { (1, 20, 12), (13, 32, 4) })
        {
            fs.Position = dataDirs + index * 8;
            uint rva = r.ReadUInt32();
            if (rva == 0) continue;
            var start = Offset(rva);
            if (start < 0) continue;
            for (int i = 0; i < 4096; i++)
            {
                fs.Position = start + i * size + nameAt;
                if (fs.Position + 4 > fs.Length) break;
                uint nameRva = r.ReadUInt32();
                if (nameRva == 0) break;
                var pos = fs.Position;
                if (NameAt(nameRva) is { Length: > 0 } n) names.Add(n);
                fs.Position = pos;
            }
        }
        return names;
    }
}
