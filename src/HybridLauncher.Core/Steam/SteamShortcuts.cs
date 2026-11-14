using System.Text;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core.Steam;

/// <summary>
/// "Non-Steam game" shortcuts in the Steam client (2.1, as Hydra makes them): an entry in the account's
/// userdata\&lt;account&gt;\config\shortcuts.vdf that starts the game through Hybrid Launcher (<c>--launch &lt;game id&gt;</c>), so
/// Steam's library, Big Picture and the Steam overlay list it while mods, Smart HDR and play time still work. The file is
/// Steam's binary key-value format; every entry already there is kept byte for byte. Steam reads the file when it starts, so a
/// running Steam shows the new entry after a restart.
/// </summary>
public static class SteamShortcuts
{
    /// <summary>The shortcuts file of the account signed in to Steam on this PC, or null when there is none.</summary>
    public static string? FileOfCurrentAccount()
    {
        var root = SteamScanner.SteamRoot();
        var account = SteamAchievements.CurrentAccountId();
        if (root is null || account is null) return null;
        var config = Path.Combine(root, "userdata", account, "config");
        return Directory.Exists(config) ? Path.Combine(config, "shortcuts.vdf") : null;
    }

    /// <summary>The id Steam gives a non-Steam shortcut: the CRC-32 of the exe (as written) and the name, top bit set.</summary>
    public static uint AppId(string exe, string name) => Crc32(Encoding.UTF8.GetBytes(exe + name)) | 0x80000000;

    /// <summary>
    /// Adds the game to <paramref name="file"/> (or updates the entry already starting it). Returns false when it was there
    /// already with the same name. A copy of the file as it was is kept next to it (shortcuts.vdf.hl-backup).
    /// </summary>
    public static bool Add(string file, string name, string exe, string launchOptions, string? icon)
    {
        var root = File.Exists(file) ? Read(File.ReadAllBytes(file)) : new KvMap();
        if (root.Get("shortcuts") is not KvMap list)
        {
            list = new KvMap();
            root.Set("shortcuts", list);
        }
        var quotedExe = $"\"{exe}\"";
        var startDir = $"\"{Path.GetDirectoryName(exe)}\"";
        // The entry already starting this game: by its launch options (they name the game), else by exe and name. Hybrid
        // Launcher moved or the game renamed updates that entry instead of adding a second one (2.1-rc.10 audit).
        var entries = list.Items.Select(i => i.Value).OfType<KvMap>().ToList();
        var entry = (launchOptions.Length > 0 ? entries.FirstOrDefault(e => e.Get("LaunchOptions") as string == launchOptions) : null)
            ?? entries.FirstOrDefault(e => e.Get("Exe") is string x && x.Equals(quotedExe, StringComparison.OrdinalIgnoreCase) && e.Get("AppName") as string == name);
        if (entry is not null && entry.Get("AppName") as string == name && (entry.Get("Exe") as string)?.Equals(quotedExe, StringComparison.OrdinalIgnoreCase) == true
            && entry.Get("LaunchOptions") as string == launchOptions) return false;
        if (entry is null)
        {
            entry = new KvMap();
            // The first free number: after entries deleted in Steam the keys can have gaps, and the count would reuse one.
            int next = 0;
            while (list.Get(next.ToString()) is not null) next++;
            list.Set(next.ToString(), entry);
        }
        // The id Steam knows the shortcut by (controller layouts, grid pictures) stays; a new entry gets Steam's own formula.
        if (entry.Get("appid") is not int id || id == 0) entry.Set("appid", unchecked((int)AppId(quotedExe, name)));
        entry.Set("AppName", name);
        entry.Set("Exe", quotedExe);
        entry.Set("StartDir", startDir);
        entry.Set("icon", icon ?? "");
        entry.Set("ShortcutPath", "");
        entry.Set("LaunchOptions", launchOptions);
        if (entry.Get("IsHidden") is null) entry.Set("IsHidden", 0);
        if (entry.Get("AllowDesktopConfig") is null) entry.Set("AllowDesktopConfig", 1);
        if (entry.Get("AllowOverlay") is null) entry.Set("AllowOverlay", 1);
        if (entry.Get("OpenVR") is null) entry.Set("OpenVR", 0);
        if (entry.Get("Devkit") is null) entry.Set("Devkit", 0);
        if (entry.Get("DevkitGameID") is null) entry.Set("DevkitGameID", "");
        if (entry.Get("DevkitOverrideAppID") is null) entry.Set("DevkitOverrideAppID", 0);
        if (entry.Get("LastPlayTime") is null) entry.Set("LastPlayTime", 0);
        if (entry.Get("FlatpakAppID") is null) entry.Set("FlatpakAppID", "");
        if (entry.Get("tags") is null) entry.Set("tags", new KvMap());

        if (File.Exists(file)) File.Copy(file, file + ".hl-backup", overwrite: true);
        var tmp = file + ".hl-tmp";
        File.WriteAllBytes(tmp, Write(root));
        File.Move(tmp, file, overwrite: true);
        return true;
    }

    /// <summary>Whether the file has an entry with these launch options.</summary>
    public static bool Has(string file, string launchOptions)
    {
        try
        {
            return File.Exists(file) && Read(File.ReadAllBytes(file)).Get("shortcuts") is KvMap list
                && list.Items.Select(i => i.Value).OfType<KvMap>().Any(e => e.Get("LaunchOptions") as string == launchOptions);
        }
        catch { return false; }
    }

    // ---------- Steam's binary key-value format ----------

    /// <summary>A map of the binary format, in file order. Values: string, int, float, ulong, long, wide string (char[]) or a map.</summary>
    public sealed class KvMap
    {
        public List<(string Key, object Value)> Items { get; } = [];

        public object? Get(string key) => Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        public void Set(string key, object value)
        {
            int at = Items.FindIndex(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) Items[at] = (Items[at].Key, value);
            else Items.Add((key, value));
        }
    }

    // Types of the format: 0 map, 1 string, 2 int32, 3 float, 4 pointer, 5 wide string, 6 colour, 7 uint64, 8 end, 10 int64.
    sealed record Raw4(byte Type, int Value);

    public static KvMap Read(byte[] data)
    {
        int pos = 0;
        var map = ReadMap(data, ref pos);
        return map;
    }

    static KvMap ReadMap(byte[] d, ref int pos)
    {
        var map = new KvMap();
        while (pos < d.Length)
        {
            byte type = Byte(d, ref pos);
            if (type == 8) return map;
            var key = CString(d, ref pos);
            object value = type switch
            {
                0 => ReadMap(d, ref pos),
                1 => CString(d, ref pos),
                2 => Int(d, ref pos),
                3 => BitConverter.ToSingle(d, Advance(d, ref pos, 4)),
                4 or 6 => new Raw4(type, Int(d, ref pos)),
                5 => WString(d, ref pos),
                7 => BitConverter.ToUInt64(d, Advance(d, ref pos, 8)),
                10 => BitConverter.ToInt64(d, Advance(d, ref pos, 8)),
                _ => throw new InvalidDataException($"shortcuts.vdf: unknown value type {type} at {pos - 1}"),
            };
            map.Items.Add((key, value));
        }
        // Every map ends with its end mark: a file cut short is refused, never written back with entries missing.
        throw new InvalidDataException("shortcuts.vdf: the file is cut short");
    }

    static int Advance(byte[] d, ref int pos, int n)
    {
        if (pos + n > d.Length) throw new InvalidDataException("shortcuts.vdf: the file is cut short");
        int at = pos;
        pos += n;
        return at;
    }

    static byte Byte(byte[] d, ref int pos) => d[Advance(d, ref pos, 1)];
    static int Int(byte[] d, ref int pos) => BitConverter.ToInt32(d, Advance(d, ref pos, 4));

    static string CString(byte[] d, ref int pos)
    {
        int end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) throw new InvalidDataException("shortcuts.vdf: a string runs past the end");
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }

    static char[] WString(byte[] d, ref int pos)
    {
        int start = pos;
        while (pos + 2 <= d.Length && (d[pos] != 0 || d[pos + 1] != 0)) pos += 2;
        if (pos + 2 > d.Length) throw new InvalidDataException("shortcuts.vdf: a wide string runs past the end");
        var s = Encoding.Unicode.GetString(d, start, pos - start).ToCharArray();
        pos += 2;
        return s;
    }

    public static byte[] Write(KvMap root)
    {
        using var ms = new MemoryStream();
        WriteMap(ms, root);
        return ms.ToArray();
    }

    static void WriteMap(MemoryStream ms, KvMap map)
    {
        foreach (var (key, value) in map.Items)
        {
            switch (value)
            {
                case KvMap m: ms.WriteByte(0); Name(ms, key); WriteMap(ms, m); break;
                case string s: ms.WriteByte(1); Name(ms, key); Name(ms, s); break;
                case int i: ms.WriteByte(2); Name(ms, key); ms.Write(BitConverter.GetBytes(i)); break;
                case float f: ms.WriteByte(3); Name(ms, key); ms.Write(BitConverter.GetBytes(f)); break;
                case Raw4 r: ms.WriteByte(r.Type); Name(ms, key); ms.Write(BitConverter.GetBytes(r.Value)); break;
                case char[] w: ms.WriteByte(5); Name(ms, key); ms.Write(Encoding.Unicode.GetBytes(w)); ms.WriteByte(0); ms.WriteByte(0); break;
                case ulong u: ms.WriteByte(7); Name(ms, key); ms.Write(BitConverter.GetBytes(u)); break;
                case long l: ms.WriteByte(10); Name(ms, key); ms.Write(BitConverter.GetBytes(l)); break;
                default: throw new InvalidDataException($"shortcuts.vdf: cannot write {value?.GetType().Name}");
            }
        }
        ms.WriteByte(8);
    }

    static void Name(MemoryStream ms, string s)
    {
        ms.Write(Encoding.UTF8.GetBytes(s));
        ms.WriteByte(0);
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
