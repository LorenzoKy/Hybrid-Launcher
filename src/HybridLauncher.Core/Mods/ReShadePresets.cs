namespace HybridLauncher.Core.Mods;

/// <summary>A ReShade preset in a game folder: its file, the effects it turns on, and the ones whose shader file is missing.</summary>
public sealed record ReShadePreset(string Path, string Name, List<string> Techniques, List<string> MissingShaders, bool Active, long Size, DateTime Modified)
{
    /// <summary>Imported by Hybrid Launcher (kept in the reshade-presets folder) rather than found next to the exe.</summary>
    public bool Imported => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) is ReShadePresets.Folder;
}

/// <summary>
/// ReShade presets of one game: the .ini files with a "Techniques=" line next to the exe and in the reshade-presets folder
/// Hybrid Launcher imports into. The active one is ReShade.ini's [GENERAL] PresetPath, which is what ReShade loads at start (and what
/// its own menu changes). Turning one on writes that line; turning it off goes back to ReShade's default preset file.
/// </summary>
public static class ReShadePresets
{
    public const string Folder = "reshade-presets";
    const string DefaultPreset = @".\ReShadePreset.ini";

    static readonly HashSet<string> NotPresets = new(StringComparer.OrdinalIgnoreCase) { "ReShade.ini", "OptiScaler.ini", "ReShade.log" };

    public static List<ReShadePreset> List(string dir)
    {
        var list = new List<ReShadePreset>();
        var active = ActivePath(dir);
        var shaderFiles = ShaderFiles(dir);
        var files = new List<string>();
        try { files.AddRange(Directory.GetFiles(dir, "*.ini")); } catch { }
        var imported = Path.Combine(dir, Folder);
        try { if (Directory.Exists(imported)) files.AddRange(Directory.GetFiles(imported, "*.ini")); } catch { }
        foreach (var file in files)
        {
            if (NotPresets.Contains(Path.GetFileName(file))) continue;
            var techniques = Techniques(file);
            if (techniques is null) continue;
            var missing = techniques.Select(t => t.Contains('@') ? t[(t.IndexOf('@') + 1)..] : "").Where(f => f.Length > 0 && !shaderFiles.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var info = new FileInfo(file);
            list.Add(new ReShadePreset(file, Path.GetFileNameWithoutExtension(file), techniques.Select(t => t.Split('@')[0]).ToList(), missing,
                active is not null && Path.GetFullPath(file).Equals(active, StringComparison.OrdinalIgnoreCase), info.Length, info.LastWriteTime));
        }
        return list.OrderByDescending(p => p.Active).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The "Techniques=" entries of a preset ("SMAA@SMAA.fx"), or null when the file is not a ReShade preset.</summary>
    static List<string>? Techniques(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var t = line.Trim();
                if (!t.StartsWith("Techniques", StringComparison.OrdinalIgnoreCase)) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                return t[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Every .fx file of the game's reshade-shaders folder (by name), to spot what a preset needs and does not have.</summary>
    static HashSet<string> ShaderFiles(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaders = Path.Combine(dir, "reshade-shaders");
        try
        {
            if (Directory.Exists(shaders))
                foreach (var f in Directory.EnumerateFiles(shaders, "*.fx", SearchOption.AllDirectories)) set.Add(Path.GetFileName(f));
        }
        catch { }
        return set;
    }

    /// <summary>The full path of the preset ReShade.ini points at, or null (no ReShade.ini, or no PresetPath).</summary>
    public static string? ActivePath(string dir)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) return null;
        try
        {
            var doc = Ini.IniDocument.Load(ini);
            var value = doc.Get("GENERAL", "PresetPath");
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(dir, value));
        }
        catch { return null; }
    }

    /// <summary>Copies picked preset files into the game's reshade-presets folder (a name already there is replaced). Returns the new paths.</summary>
    public static List<string> Import(string dir, IEnumerable<string> files)
    {
        var target = Directory.CreateDirectory(Path.Combine(dir, Folder)).FullName;
        var added = new List<string>();
        foreach (var file in files)
        {
            if (Techniques(file) is null) throw new InvalidDataException(L.F("{0} is not a ReShade preset (it has no Techniques line).", Path.GetFileName(file)));
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (!Path.GetFullPath(file).Equals(dest, StringComparison.OrdinalIgnoreCase)) File.Copy(file, dest, overwrite: true);
            added.Add(dest);
        }
        return added;
    }

    /// <summary>Makes ReShade load this preset (on), or its default preset file (off).</summary>
    public static void SetActive(string dir, string? preset)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) throw new FileNotFoundException(L.T("ReShade.ini is not in the game folder: install ReShade first (or start the game once with it)."), ini);
        string value = preset is null ? DefaultPreset : ".\\" + Path.GetRelativePath(dir, preset);
        var lines = File.ReadAllLines(ini).ToList();
        int general = lines.FindIndex(l => l.Trim().Equals("[GENERAL]", StringComparison.OrdinalIgnoreCase));
        if (general < 0)
        {
            lines.Insert(0, "[GENERAL]");
            lines.Insert(1, "PresetPath=" + value);
            lines.Insert(2, "");
        }
        else
        {
            int end = lines.FindIndex(general + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            int at = lines.FindIndex(general + 1, end - general - 1, l => l.TrimStart().StartsWith("PresetPath", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = "PresetPath=" + value;
            else lines.Insert(general + 1, "PresetPath=" + value);
        }
        File.WriteAllLines(ini, lines);
    }

    /// <summary>Deletes a preset file; when it was the active one, ReShade goes back to its default preset.</summary>
    public static void Delete(string dir, ReShadePreset preset)
    {
        if (preset.Active && File.Exists(Path.Combine(dir, "ReShade.ini"))) SetActive(dir, null);
        File.Delete(preset.Path);
    }
}
