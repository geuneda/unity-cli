using System.Text.Json.Nodes;
using UnityCli.Support;

namespace UnityCli.Tests;

public sealed class AssertEvaluatorTests
{
    private static JsonNode Root() => JsonNode.Parse(
        "{\"result\":{\"name\":\"Hero\",\"count\":42,\"ratio\":1.5},\"data\":{\"isPlaying\":false}}")!;

    [Fact]
    public void Equals_String_PassAndFail()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Equals, "Hero").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Equals, "Ghost").Passed);
    }

    [Fact]
    public void Equals_Numeric_StringMatch()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Equals, "42").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Equals, "43").Passed);
    }

    [Fact]
    public void Contains_PassAndFail()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Contains, "er").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Contains, "zzz").Passed);
    }

    [Fact]
    public void Exists_TrueOnPresent_FalseOnMissing_IgnoresExpected()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Exists, null).Passed);
        Assert.True(AssertEvaluator.Evaluate(root, "data.isPlaying", AssertEvaluator.AssertOp.Exists, "whatever").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.nope", AssertEvaluator.AssertOp.Exists, "whatever").Passed);
    }

    [Fact]
    public void Gt_Lt_Numeric_PassAndFail()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Gt, "10").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Gt, "100").Passed);
        Assert.True(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Lt, "100").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.count", AssertEvaluator.AssertOp.Lt, "10").Passed);
    }

    [Fact]
    public void Gt_NonNumericActual_FailsWithDetail()
    {
        var root = Root();

        var result = AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Gt, "10");

        Assert.False(result.Passed);
        Assert.Equal("non-numeric", result.Detail);
    }

    [Fact]
    public void Matches_Valid_PassAndFail()
    {
        var root = Root();

        Assert.True(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Matches, "^He.o$").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Matches, "^Zz").Passed);
    }

    [Fact]
    public void Matches_InvalidRegex_FailsWithoutThrow()
    {
        var root = Root();

        var result = AssertEvaluator.Evaluate(root, "result.name", AssertEvaluator.AssertOp.Matches, "[");

        Assert.False(result.Passed);
        Assert.Equal("invalid regex", result.Detail);
    }

    [Theory]
    [InlineData("equals", AssertEvaluator.AssertOp.Equals)]
    [InlineData("CONTAINS", AssertEvaluator.AssertOp.Contains)]
    [InlineData("Exists", AssertEvaluator.AssertOp.Exists)]
    [InlineData("gt", AssertEvaluator.AssertOp.Gt)]
    [InlineData("LT", AssertEvaluator.AssertOp.Lt)]
    [InlineData("matches", AssertEvaluator.AssertOp.Matches)]
    public void TryParseOp_AcceptsKnownTokensCaseInsensitively(string token, AssertEvaluator.AssertOp expected)
    {
        Assert.True(AssertEvaluator.TryParseOp(token, out var op));
        Assert.Equal(expected, op);
    }

    [Fact]
    public void TryParseOp_RejectsUnknown()
    {
        Assert.False(AssertEvaluator.TryParseOp("frobnicate", out _));
    }

    [Fact]
    public void MissingPath_FailsEqualsAndExists()
    {
        var root = Root();

        Assert.False(AssertEvaluator.Evaluate(root, "no.such.path", AssertEvaluator.AssertOp.Equals, "x").Passed);
        Assert.False(AssertEvaluator.Evaluate(root, "no.such.path", AssertEvaluator.AssertOp.Exists, null).Passed);
    }
}
