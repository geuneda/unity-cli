using System.Text.Json.Nodes;
using UnityCli.Support;

namespace UnityCli.Tests;

public sealed class JsonPathResolverTests
{
    [Fact]
    public void ResolvesNestedObjectPath()
    {
        var root = JsonNode.Parse("{\"result\":{\"id\":42,\"name\":\"Hero\"}}");

        Assert.Equal("42", JsonPathResolver.ResolveToScalar(root, "result.id"));
        Assert.Equal("Hero", JsonPathResolver.ResolveToScalar(root, "result.name"));
    }

    [Fact]
    public void ResolvesArrayIndex()
    {
        var root = JsonNode.Parse("{\"items\":[{\"x\":1},{\"x\":2}]}");

        Assert.Equal("2", JsonPathResolver.ResolveToScalar(root, "items[1].x"));
    }

    [Fact]
    public void StringScalarIsReturnedUnquoted()
    {
        var root = JsonNode.Parse("{\"message\":\"done\"}");

        Assert.Equal("done", JsonPathResolver.ResolveToScalar(root, "message"));
    }

    [Fact]
    public void ObjectPathReturnsCompactJson()
    {
        var root = JsonNode.Parse("{\"result\":{\"id\":1}}");

        var scalar = JsonPathResolver.ResolveToScalar(root, "result");

        Assert.Contains("\"id\":1", scalar);
    }

    [Fact]
    public void MissingPathReturnsNull()
    {
        var root = JsonNode.Parse("{\"result\":{\"id\":1}}");

        Assert.Null(JsonPathResolver.ResolveToScalar(root, "result.missing"));
        Assert.Null(JsonPathResolver.ResolveToScalar(root, "nope.deep"));
    }

    [Fact]
    public void OutOfRangeIndexReturnsNull()
    {
        var root = JsonNode.Parse("{\"items\":[1,2]}");

        Assert.Null(JsonPathResolver.ResolveToScalar(root, "items[5]"));
    }

    [Fact]
    public void EmptyPathReturnsRoot()
    {
        var root = JsonNode.Parse("{\"a\":1}");

        Assert.Same(root, JsonPathResolver.Resolve(root, string.Empty));
    }
}
