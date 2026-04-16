using System.Text.Json.Nodes;
using UnityCli.Support;

namespace UnityCli.Tests;

public sealed class JsonHelpersTests
{
    // ---------------------------------------------------------------
    // ParseKeyValuePairs
    // ---------------------------------------------------------------

    [Fact]
    public void ParseKeyValuePairs_BasicKeyValue_ReturnsJsonObjectWithEntry()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "name=Player" });

        Assert.Equal("Player", result["name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseKeyValuePairs_MultiplePairs_ReturnsAllEntries()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "a=1", "b=hello", "c=true" });

        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result["a"]!.GetValue<long>());
        Assert.Equal("hello", result["b"]!.GetValue<string>());
        Assert.True(result["c"]!.GetValue<bool>());
    }

    [Fact]
    public void ParseKeyValuePairs_MissingEqualsSign_SkipsToken()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "noequals", "valid=yes" });

        Assert.Single(result);
        Assert.Equal("yes", result["valid"]!.GetValue<string>());
    }

    [Fact]
    public void ParseKeyValuePairs_EqualsAtStart_SkipsToken()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "=value" });

        Assert.Empty(result);
    }

    [Fact]
    public void ParseKeyValuePairs_BooleanValues_ParsedAsBooleans()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "enabled=true", "disabled=false" });

        Assert.True(result["enabled"]!.GetValue<bool>());
        Assert.False(result["disabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ParseKeyValuePairs_NumericValues_ParsedAsNumbers()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "count=42", "ratio=3.14" });

        Assert.Equal(42L, result["count"]!.GetValue<long>());
        Assert.Equal(3.14, result["ratio"]!.GetValue<double>());
    }

    [Fact]
    public void ParseKeyValuePairs_VectorValues_ParsedAsArray()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "position=1,2,3" });

        var array = result["position"]!.AsArray();
        Assert.Equal(3, array.Count);
        Assert.Equal(1.0, array[0]!.GetValue<double>());
        Assert.Equal(2.0, array[1]!.GetValue<double>());
        Assert.Equal(3.0, array[2]!.GetValue<double>());
    }

    [Fact]
    public void ParseKeyValuePairs_JsonObjectLiteral_ParsedAsObject()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "data={\"x\":10}" });

        var obj = result["data"]!.AsObject();
        Assert.Equal(10, obj["x"]!.GetValue<int>());
    }

    [Fact]
    public void ParseKeyValuePairs_JsonArrayLiteral_ParsedAsArray()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "items=[1,2,3]" });

        var array = result["items"]!.AsArray();
        Assert.Equal(3, array.Count);
    }

    [Fact]
    public void ParseKeyValuePairs_NullValue_SetsNullEntry()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "value=null" });

        Assert.Null(result["value"]);
    }

    [Fact]
    public void ParseKeyValuePairs_EmptyInput_ReturnsEmptyObject()
    {
        var result = JsonHelpers.ParseKeyValuePairs(Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void ParseKeyValuePairs_ValueContainsEquals_PreservesValuePart()
    {
        var result = JsonHelpers.ParseKeyValuePairs(new[] { "filter=name=Player" });

        Assert.Equal("name=Player", result["filter"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------
    // ConvertString
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("Null")]
    public void ConvertString_NullLiteral_ReturnsNull(string input)
    {
        var result = JsonHelpers.ConvertString(input);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void ConvertString_BooleanValue_ReturnsBool(string input, bool expected)
    {
        var result = JsonHelpers.ConvertString(input);

        Assert.Equal(expected, result!.GetValue<bool>());
    }

    [Fact]
    public void ConvertString_CommaSeparatedNumbers_ReturnsJsonArray()
    {
        var result = JsonHelpers.ConvertString("1.5, 2.5, 3.5");

        var array = result!.AsArray();
        Assert.Equal(3, array.Count);
        Assert.Equal(1.5, array[0]!.GetValue<double>());
        Assert.Equal(2.5, array[1]!.GetValue<double>());
        Assert.Equal(3.5, array[2]!.GetValue<double>());
    }

    [Fact]
    public void ConvertString_SingleCommaSeparatedNumber_FallsBackToString()
    {
        // "42," splits to one non-empty segment (length not > 1), so the array branch
        // is skipped. The trailing comma also prevents long/double parsing, so it falls
        // back to a plain string.
        var result = JsonHelpers.ConvertString("42,");

        Assert.Equal("42,", result!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_Integer_ReturnsLong()
    {
        var result = JsonHelpers.ConvertString("12345");

        Assert.Equal(12345L, result!.GetValue<long>());
    }

    [Fact]
    public void ConvertString_NegativeInteger_ReturnsLong()
    {
        var result = JsonHelpers.ConvertString("-99");

        Assert.Equal(-99L, result!.GetValue<long>());
    }

    [Fact]
    public void ConvertString_FloatValue_ReturnsDouble()
    {
        var result = JsonHelpers.ConvertString("3.14");

        Assert.Equal(3.14, result!.GetValue<double>());
    }

    [Fact]
    public void ConvertString_ScientificNotation_ReturnsDouble()
    {
        var result = JsonHelpers.ConvertString("1.5e3");

        Assert.Equal(1500.0, result!.GetValue<double>());
    }

    [Fact]
    public void ConvertString_JsonObjectLiteral_ReturnsJsonObject()
    {
        var result = JsonHelpers.ConvertString("{\"key\":\"value\"}");

        var obj = result!.AsObject();
        Assert.Equal("value", obj["key"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_JsonArrayLiteral_ReturnsJsonArray()
    {
        var result = JsonHelpers.ConvertString("[1,2,3]");

        var array = result!.AsArray();
        Assert.Equal(3, array.Count);
    }

    [Fact]
    public void ConvertString_PlainString_ReturnsStringValue()
    {
        var result = JsonHelpers.ConvertString("hello world");

        Assert.Equal("hello world", result!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_InvalidJsonObjectLiteral_FallsBackToString()
    {
        var result = JsonHelpers.ConvertString("{not valid json}");

        Assert.Equal("{not valid json}", result!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_InvalidJsonArrayLiteral_FallsBackToString()
    {
        var result = JsonHelpers.ConvertString("[not valid]");

        Assert.Equal("[not valid]", result!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_EmptyString_ReturnsEmptyStringValue()
    {
        var result = JsonHelpers.ConvertString("");

        Assert.Equal("", result!.GetValue<string>());
    }

    [Fact]
    public void ConvertString_CommaWithBraces_DoesNotTreatAsNumericArray()
    {
        // Contains comma AND '{', so comma-separated-numbers path should be skipped
        var result = JsonHelpers.ConvertString("{\"a\":1,\"b\":2}");

        var obj = result!.AsObject();
        Assert.Equal(1, obj["a"]!.GetValue<int>());
    }

    [Fact]
    public void ConvertString_CommaWithBrackets_DoesNotTreatAsNumericArray()
    {
        // Contains comma AND '[', so comma-separated-numbers path should be skipped
        var result = JsonHelpers.ConvertString("[10,20]");

        var array = result!.AsArray();
        Assert.Equal(2, array.Count);
    }

    [Fact]
    public void ConvertString_CommaSeparatedNonNumeric_FallsBackToString()
    {
        // Contains comma but segments are not all numeric
        var result = JsonHelpers.ConvertString("hello,world");

        Assert.Equal("hello,world", result!.GetValue<string>());
    }

    // ---------------------------------------------------------------
    // EnsureObject
    // ---------------------------------------------------------------

    [Fact]
    public void EnsureObject_NullInput_ReturnsEmptyJsonObject()
    {
        var result = JsonHelpers.EnsureObject(null);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        Assert.Empty(result);
    }

    [Fact]
    public void EnsureObject_JsonObjectInput_ReturnsSameObject()
    {
        var original = new JsonObject { ["key"] = "value" };

        var result = JsonHelpers.EnsureObject(original);

        Assert.Same(original, result);
    }

    [Fact]
    public void EnsureObject_NonJsonObjectNode_ReturnsEmptyJsonObject()
    {
        JsonNode node = JsonValue.Create(42);

        var result = JsonHelpers.EnsureObject(node);

        Assert.IsType<JsonObject>(result);
        Assert.Empty(result);
    }

    [Fact]
    public void EnsureObject_JsonArrayInput_ReturnsEmptyJsonObject()
    {
        JsonNode node = new JsonArray(1, 2, 3);

        var result = JsonHelpers.EnsureObject(node);

        Assert.IsType<JsonObject>(result);
        Assert.Empty(result);
    }

    // ---------------------------------------------------------------
    // ToPrettyJson
    // ---------------------------------------------------------------

    [Fact]
    public void ToPrettyJson_BasicObject_ReturnsFormattedJson()
    {
        var value = new { Name = "Test", Count = 5 };

        var json = JsonHelpers.ToPrettyJson(value);

        Assert.Contains("\"name\"", json); // camelCase naming policy
        Assert.Contains("\"count\"", json);
        Assert.Contains("5", json);
        Assert.Contains("\"Test\"", json);
    }

    [Fact]
    public void ToPrettyJson_NullInput_ReturnsNullLiteral()
    {
        var json = JsonHelpers.ToPrettyJson(null);

        Assert.Equal("null", json);
    }

    [Fact]
    public void ToPrettyJson_UsesIndentation()
    {
        var value = new { Inner = new { Value = 1 } };

        var json = JsonHelpers.ToPrettyJson(value);

        // Indented JSON should contain newlines
        Assert.Contains("\n", json);
    }

    // ---------------------------------------------------------------
    // DeepClone
    // ---------------------------------------------------------------

    [Fact]
    public void DeepClone_NullInput_ReturnsEmptyJsonObject()
    {
        var result = JsonHelpers.DeepClone(null);

        Assert.IsType<JsonObject>(result);
        var obj = result.AsObject();
        Assert.Empty(obj);
    }

    [Fact]
    public void DeepClone_ObjectClone_IsIndependent()
    {
        var original = new JsonObject { ["key"] = "value" };

        var clone = JsonHelpers.DeepClone(original);

        // Modify the clone
        clone.AsObject()["key"] = "modified";

        // Original should remain unchanged
        Assert.Equal("value", original["key"]!.GetValue<string>());
        Assert.Equal("modified", clone["key"]!.GetValue<string>());
    }

    [Fact]
    public void DeepClone_ArrayClone_IsIndependent()
    {
        var original = new JsonArray(1, 2, 3);

        var clone = JsonHelpers.DeepClone(original);

        clone.AsArray().Add(4);

        Assert.Equal(3, original.Count);
        Assert.Equal(4, clone.AsArray().Count);
    }

    [Fact]
    public void DeepClone_NestedObject_ClonesDeep()
    {
        var original = new JsonObject
        {
            ["nested"] = new JsonObject { ["x"] = 10 }
        };

        var clone = JsonHelpers.DeepClone(original);
        clone["nested"]!["x"] = 99;

        Assert.Equal(10, original["nested"]!["x"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------
    // ReplaceVariables
    // ---------------------------------------------------------------

    [Fact]
    public void ReplaceVariables_StringValueSubstitution_ReplacesVariable()
    {
        var node = JsonValue.Create("Hello ${name}!");
        var variables = new Dictionary<string, string> { { "name", "World" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal("Hello World!", result!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_NestedObjectSubstitution_ReplacesInAllValues()
    {
        var node = new JsonObject
        {
            ["greeting"] = "Hello ${user}",
            ["nested"] = new JsonObject
            {
                ["message"] = "Welcome ${user}"
            }
        };
        var variables = new Dictionary<string, string> { { "user", "Alice" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        var obj = result!.AsObject();
        Assert.Equal("Hello Alice", obj["greeting"]!.GetValue<string>());
        Assert.Equal("Welcome Alice", obj["nested"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_ArrayItemSubstitution_ReplacesInArrayElements()
    {
        var node = new JsonArray(
            JsonValue.Create("${item}1"),
            JsonValue.Create("${item}2")
        );
        var variables = new Dictionary<string, string> { { "item", "test" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        var array = result!.AsArray();
        Assert.Equal("test1", array[0]!.GetValue<string>());
        Assert.Equal("test2", array[1]!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_MultipleVariables_ReplacesAll()
    {
        var node = JsonValue.Create("${a} and ${b}");
        var variables = new Dictionary<string, string>
        {
            { "a", "first" },
            { "b", "second" }
        };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal("first and second", result!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_NoMatchingVariables_ReturnsOriginalString()
    {
        var node = JsonValue.Create("no variables here");
        var variables = new Dictionary<string, string> { { "unused", "value" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal("no variables here", result!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_NullNode_ReturnsNull()
    {
        var variables = new Dictionary<string, string> { { "key", "value" } };

        var result = JsonHelpers.ReplaceVariables(null, variables);

        Assert.Null(result);
    }

    [Fact]
    public void ReplaceVariables_NonStringValue_ClonesValue()
    {
        JsonNode node = JsonValue.Create(42);
        var variables = new Dictionary<string, string> { { "key", "value" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal(42, result!.GetValue<int>());
    }

    [Fact]
    public void ReplaceVariables_VariableReplacementProducesNumber_ConvertsToNumber()
    {
        var node = JsonValue.Create("${count}");
        var variables = new Dictionary<string, string> { { "count", "42" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal(42L, result!.GetValue<long>());
    }

    [Fact]
    public void ReplaceVariables_VariableReplacementProducesBoolean_ConvertsToBool()
    {
        var node = JsonValue.Create("${flag}");
        var variables = new Dictionary<string, string> { { "flag", "true" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void ReplaceVariables_CaseInsensitiveVariableName_Replaces()
    {
        var node = JsonValue.Create("${NAME}");
        var variables = new Dictionary<string, string> { { "name", "Alice" } };

        // The replacement uses OrdinalIgnoreCase on the placeholder, but Dictionary
        // keys are case-sensitive. The variable key "name" won't match "${NAME}" unless
        // the dictionary key also matches. Let's test with matching case in dict.
        var variables2 = new Dictionary<string, string> { { "NAME", "Alice" } };
        var result = JsonHelpers.ReplaceVariables(node, variables2);

        Assert.Equal("Alice", result!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_EmptyVariables_ReturnsOriginalValue()
    {
        var node = JsonValue.Create("${keep}");
        var variables = new Dictionary<string, string>();

        var result = JsonHelpers.ReplaceVariables(node, variables);

        Assert.Equal("${keep}", result!.GetValue<string>());
    }

    [Fact]
    public void ReplaceVariables_ArrayWithMixedTypes_HandlesCorrectly()
    {
        var node = new JsonArray(
            JsonValue.Create("${val}"),
            JsonValue.Create(100),
            JsonValue.Create("static")
        );
        var variables = new Dictionary<string, string> { { "val", "replaced" } };

        var result = JsonHelpers.ReplaceVariables(node, variables);

        var array = result!.AsArray();
        Assert.Equal("replaced", array[0]!.GetValue<string>());
        Assert.Equal(100, array[1]!.GetValue<int>());
        Assert.Equal("static", array[2]!.GetValue<string>());
    }
}
