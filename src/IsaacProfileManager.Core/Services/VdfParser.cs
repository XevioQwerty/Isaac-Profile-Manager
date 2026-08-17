namespace IsaacProfileManager.Core.Services;

/// <summary>
/// A node in a Valve KeyValues (.acf / .vdf) document: either a leaf with a
/// string value, or a section with children.
/// </summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSection => Value is null;

    public VdfNode? this[string key] => Children.TryGetValue(key, out var child) ? child : null;

    /// <summary>Find the first section with this name anywhere in the tree.</summary>
    public VdfNode? Find(string key)
    {
        if (Children.TryGetValue(key, out var direct)) return direct;
        foreach (var child in Children.Values)
        {
            var hit = child.Find(key);
            if (hit is not null) return hit;
        }
        return null;
    }
}

/// <summary>
/// Minimal reader for Valve's KeyValues format, enough for
/// <c>appworkshop_&lt;appid&gt;.acf</c>.
///
/// Written rather than regexed because the file nests: the same workshop ids
/// appear under both <c>WorkshopItemsInstalled</c> and
/// <c>WorkshopItemDetails</c>, so pattern-matching ids across the whole file
/// counts every item twice.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var position = 0;
        ParseInto(text, ref position, root, depth: 0);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ParseInto(string text, ref int position, VdfNode parent, int depth)
    {
        if (depth > 64) throw new InvalidDataException("KeyValues nesting is implausibly deep; refusing to continue.");

        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length) return;

            if (text[position] == '}')
            {
                position++;
                return;
            }

            var key = ReadToken(text, ref position);
            if (key is null) return;

            SkipTrivia(text, ref position);
            if (position >= text.Length) return;

            if (text[position] == '{')
            {
                position++;
                var section = new VdfNode();
                ParseInto(text, ref position, section, depth + 1);
                parent.Children[key] = section;
            }
            else
            {
                var value = ReadToken(text, ref position) ?? string.Empty;
                parent.Children[key] = new VdfNode { Value = value };
            }
        }
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }

            // Valve uses // for comments in these files.
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] is not ('\n' or '\r')) position++;
                continue;
            }
            return;
        }
    }

    private static string? ReadToken(string text, ref int position)
    {
        if (position >= text.Length) return null;

        if (text[position] == '"')
        {
            position++;
            var builder = new System.Text.StringBuilder();
            while (position < text.Length && text[position] != '"')
            {
                if (text[position] == '\\' && position + 1 < text.Length)
                {
                    position++;
                    builder.Append(text[position] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        var other => other,
                    });
                }
                else
                {
                    builder.Append(text[position]);
                }
                position++;
            }
            position++; // closing quote
            return builder.ToString();
        }

        var start = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}'))
            position++;

        return position > start ? text[start..position] : null;
    }
}
