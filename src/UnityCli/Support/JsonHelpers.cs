using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace UnityCli.Support;

public static class JsonHelpers
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static JsonObject ParseKeyValuePairs(IEnumerable<string> tokens)
    {
        var result = new JsonObject();
        foreach (var token in tokens)
        {
            var splitIndex = token.IndexOf('=');
            if (splitIndex <= 0)
            {
                continue;
            }

            var key = token[..splitIndex];
            var value = token[(splitIndex + 1)..];
            result[key] = ConvertString(value);
        }

        return result;
    }

    public static JsonNode? ConvertString(string value)
    {
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        if (value.Contains(',') && !value.Contains('{') && !value.Contains('['))
        {
            var segments = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 1 && segments.All(static x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var array = new JsonArray();
                foreach (var segment in segments)
                {
                    array.Add(JsonValue.Create(double.Parse(segment, NumberStyles.Float, CultureInfo.InvariantCulture)));
                }

                return array;
            }
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        if ((value.StartsWith('{') && value.EndsWith('}')) || (value.StartsWith('[') && value.EndsWith(']')))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch
            {
            }
        }

        return JsonValue.Create(value);
    }

    public static JsonObject EnsureObject(JsonNode? node)
    {
        return node as JsonObject ?? new JsonObject();
    }

    public static string ToPrettyJson(object? value)
    {
        return JsonSerializer.Serialize(value, SerializerOptions);
    }

    public static JsonNode DeepClone(JsonNode? node)
    {
        return node?.DeepClone() ?? new JsonObject();
    }

    public static JsonNode? ReplaceVariables(JsonNode? node, IReadOnlyDictionary<string, string> variables)
    {
        return node switch
        {
            null => null,
            JsonValue value => ReplaceValue(value, variables),
            JsonObject obj => ReplaceObject(obj, variables),
            JsonArray array => ReplaceArray(array, variables),
            _ => node.DeepClone(),
        };
    }

    private static JsonNode? ReplaceValue(JsonValue value, IReadOnlyDictionary<string, string> variables)
    {
        if (!value.TryGetValue<string>(out var stringValue))
        {
            return value.DeepClone();
        }

        foreach (var (key, replacement) in variables)
        {
            stringValue = stringValue.Replace("${" + key + "}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        return ConvertString(stringValue);
    }

    private static JsonObject ReplaceObject(JsonObject obj, IReadOnlyDictionary<string, string> variables)
    {
        var result = new JsonObject();
        foreach (var pair in obj)
        {
            result[pair.Key] = ReplaceVariables(pair.Value, variables);
        }

        return result;
    }

    private static JsonArray ReplaceArray(JsonArray array, IReadOnlyDictionary<string, string> variables)
    {
        var result = new JsonArray();
        foreach (var item in array)
        {
            result.Add(ReplaceVariables(item, variables));
        }

        return result;
    }
}
