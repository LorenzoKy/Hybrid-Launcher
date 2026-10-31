using System.Text;

namespace HybridLauncher.Core.Steam;

/// <summary>
/// Reads the names of the games in the Steam client's app cache (appcache\appinfo.vdf, binary). Formats 0x07564427 / 28 store
/// key names inline; 0x07564429 (2024+) keeps them in a string table at the end, referenced by index.
/// Only "appinfo/common/name" and "type" are taken; a damaged file throws.
/// </summary>
public static class SteamAppCache
{
    public static List<(string AppId, string Name)> ReadGames(string file)
    {
        var list = new List<(string, string)>();
        if (!File.Exists(file)) return list;
        var data = File.ReadAllBytes(file);
        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic is not (0x07564427 or 0x07564428 or 0x07564429)) throw new InvalidDataException($"appinfo.vdf version {magic:X8} is not known.");
        bool v29 = magic == 0x07564429;
        int pos = 8;
        string[]? strings = null;
        if (v29)
        {
            long tableOffset = BitConverter.ToInt64(data, 8);
            pos = 16;
            int count = BitConverter.ToInt32(data, (int)tableOffset);
            strings = new string[count];
            int p = (int)tableOffset + 4;
            for (int i = 0; i < count; i++)
            {
                int end = Array.IndexOf(data, (byte)0, p);
                strings[i] = Encoding.UTF8.GetString(data, p, end - p);
                p = end + 1;
            }
        }
        // Per app: id, size, state, last update, PICS token, SHA-1, change number, (28+: binary SHA-1), then the KeyValues.
        int header = magic == 0x07564427 ? 4 + 4 + 4 + 8 + 20 + 4 : 4 + 4 + 4 + 8 + 20 + 4 + 20;
        while (pos + 8 <= data.Length)
        {
            uint appId = BitConverter.ToUInt32(data, pos);
            if (appId == 0) break;
            int size = BitConverter.ToInt32(data, pos + 4);
            int next = pos + 8 + size;
            try
            {
                int kv = pos + 8 + header - 4; // header above counts the id; size starts after it
                var root = ReadBlock(data, ref kv, strings);
                var common = Find(Find(root, "appinfo") ?? root, "common");
                if (common is not null && Value(common, "type") is { } type && type.Equals("game", StringComparison.OrdinalIgnoreCase)
                    && Value(common, "name") is { Length: > 0 } name)
                    list.Add((appId.ToString(), name));
            }
            catch (Exception) { /* one bad entry does not stop the rest */ }
            if (next <= pos) break;
            pos = next;
        }
        return list;
    }

    sealed class Node
    {
        public required string Name;
        public string? Text;
        public List<Node> Children = [];
    }

    static Node? Find(Node? n, string name) => n?.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static string? Value(Node n, string name) => Find(n, name)?.Text;

    static Node ReadBlock(byte[] d, ref int pos, string[]? strings)
    {
        var node = new Node { Name = "" };
        while (true)
        {
            byte type = d[pos++];
            if (type is 8 or 11) return node;
            string key;
            if (strings is not null)
            {
                key = strings[BitConverter.ToInt32(d, pos)];
                pos += 4;
            }
            else key = CString(d, ref pos);
            switch (type)
            {
                case 0:
                    var child = ReadBlock(d, ref pos, strings);
                    child.Name = key;
                    node.Children.Add(child);
                    break;
                case 1:
                    node.Children.Add(new Node { Name = key, Text = CString(d, ref pos) });
                    break;
                case 2 or 3 or 4 or 6:
                    node.Children.Add(new Node { Name = key, Text = BitConverter.ToInt32(d, pos).ToString() });
                    pos += 4;
                    break;
                case 7 or 10:
                    node.Children.Add(new Node { Name = key, Text = BitConverter.ToInt64(d, pos).ToString() });
                    pos += 8;
                    break;
                default:
                    throw new InvalidDataException($"KeyValues type {type}");
            }
        }
    }

    static string CString(byte[] d, ref int pos)
    {
        int end = Array.IndexOf(d, (byte)0, pos);
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }
}
