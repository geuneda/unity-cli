using System.Text.Json.Nodes;

namespace UnityCli.Support;

/// <summary>
/// Resolves a dotted/indexed path (e.g. <c>result.id</c>, <c>data.logs[0].data.level</c>)
/// against a <see cref="JsonNode"/>. Shared by the <c>--field</c> selector and assertions.
/// </summary>
public static class JsonPathResolver
{
    public static JsonNode? Resolve(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        var current = root;
        foreach (var token in Tokenize(path))
        {
            if (current is null)
            {
                return null;
            }

            if (token.IsIndex)
            {
                if (current is JsonArray array && token.Index >= 0 && token.Index < array.Count)
                {
                    current = array[token.Index];
                }
                else
                {
                    return null;
                }
            }
            else if (current is JsonObject obj && obj.TryGetPropertyValue(token.Name!, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Resolve to a raw scalar string: strings are returned unquoted, numbers/bools as-is,
    /// objects/arrays as compact JSON. Returns null when the path does not resolve.
    /// </summary>
    public static string? ResolveToScalar(JsonNode? root, string path)
    {
        var node = Resolve(root, path);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        }

        return node.ToJsonString();
    }

    private readonly struct Token
    {
        public Token(string? name, int index, bool isIndex)
        {
            Name = name;
            Index = index;
            IsIndex = isIndex;
        }

        public string? Name { get; }

        public int Index { get; }

        public bool IsIndex { get; }
    }

    private static IEnumerable<Token> Tokenize(string path)
    {
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
            {
                yield return new Token(segment, -1, false);
                continue;
            }

            var property = segment[..bracket];
            if (!string.IsNullOrEmpty(property))
            {
                yield return new Token(property, -1, false);
            }

            var rest = segment[bracket..];
            while (rest.StartsWith('['))
            {
                var close = rest.IndexOf(']');
                if (close < 0)
                {
                    break;
                }

                if (int.TryParse(rest[1..close], out var index))
                {
                    yield return new Token(null, index, true);
                }

                rest = rest[(close + 1)..];
            }
        }
    }
}
