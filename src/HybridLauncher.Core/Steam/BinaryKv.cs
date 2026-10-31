using System.Text;

namespace HybridLauncher.Core.Steam;

/// <summary>A node of Valve's binary KeyValues format (appcache\stats\*.bin): a value, or named children.</summary>
public sealed class KvNode
{
    public string Name { get; }
    public object? Value { get; init; }
    public List<KvNode> Children { get; } = [];

    public KvNode(string name) => Name = name;

    public KvNode? this[string key] => Children.FirstOrDefault(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string? Str(string key) => this[key]?.Value switch
    {
        string s => s,
        null => null,
        var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    public long? Int(string key) => this[key]?.Value switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => (long)ul,
        string s when long.TryParse(s, out var n) => n,
        _ => null,
    };
}

/// <summary>
/// Reader for binary KeyValues: a type byte, a NUL-terminated name, then the value; 0x08 closes a nested block.
/// Malformed or truncated files throw <see cref="InvalidDataException"/>.
/// </summary>
public static class BinaryKv
{
    const byte None = 0, String = 1, Int32 = 2, Float32 = 3, Pointer = 4, WideString = 5, Color = 6, UInt64 = 7, End = 8, Int64 = 10, AltEnd = 11;

    public static KvNode Parse(byte[] data)
    {
        var root = new KvNode("");
        int pos = 0;
        ReadChildren(data, ref pos, root, 0);
        return root;
    }

    static void ReadChildren(byte[] d, ref int pos, KvNode parent, int depth)
    {
        if (depth > 64) throw new InvalidDataException("KeyValues nested too deep");
        while (pos < d.Length)
        {
            byte type = d[pos++];
            if (type is End or AltEnd) return;
            var name = ReadString(d, ref pos);
            switch (type)
            {
                case None:
                    var child = new KvNode(name);
                    ReadChildren(d, ref pos, child, depth + 1);
                    parent.Children.Add(child);
                    break;
                case String:
                    parent.Children.Add(new KvNode(name) { Value = ReadString(d, ref pos) });
                    break;
                case Int32 or Pointer or Color:
                    Need(d, pos, 4);
                    parent.Children.Add(new KvNode(name) { Value = BitConverter.ToInt32(d, pos) });
                    pos += 4;
                    break;
                case Float32:
                    Need(d, pos, 4);
                    parent.Children.Add(new KvNode(name) { Value = BitConverter.ToSingle(d, pos) });
                    pos += 4;
                    break;
                case UInt64:
                    Need(d, pos, 8);
                    parent.Children.Add(new KvNode(name) { Value = BitConverter.ToUInt64(d, pos) });
                    pos += 8;
                    break;
                case Int64:
                    Need(d, pos, 8);
                    parent.Children.Add(new KvNode(name) { Value = BitConverter.ToInt64(d, pos) });
                    pos += 8;
                    break;
                case WideString:
                    int start = pos;
                    while (pos + 1 < d.Length && (d[pos] != 0 || d[pos + 1] != 0)) pos += 2;
                    parent.Children.Add(new KvNode(name) { Value = Encoding.Unicode.GetString(d, start, pos - start) });
                    pos += 2;
                    break;
                default:
                    throw new InvalidDataException($"Unknown KeyValues type {type} at {pos}");
            }
        }
    }

    static string ReadString(byte[] d, ref int pos)
    {
        int end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) throw new InvalidDataException("Unterminated KeyValues string");
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }

    static void Need(byte[] d, int pos, int count)
    {
        if (pos + count > d.Length) throw new InvalidDataException("Truncated KeyValues value");
    }
}
