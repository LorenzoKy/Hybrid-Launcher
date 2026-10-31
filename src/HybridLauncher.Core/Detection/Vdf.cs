using System.Text;

namespace HybridLauncher.Core.Detection;

/// <summary>Minimal parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;

    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        string? pendingKey = null;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                var node = new VdfNode();
                if (pendingKey is not null) stack.Peek().Children[pendingKey] = node;
                stack.Push(node);
                pendingKey = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                i++;
                continue;
            }

            string token = c == '"' ? ReadQuoted(text, ref i) : ReadBare(text, ref i);
            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Values[pendingKey] = token;
                pendingKey = null;
            }
        }
        return root;
    }

    static string ReadQuoted(string text, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
            }
            else
            {
                sb.Append(text[i]);
            }
            i++;
        }
        i++; // closing quote
        return sb.ToString();
    }

    static string ReadBare(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
        return text[start..i];
    }
}
