using System.Text.Json.Nodes;
using UnityCli.Support;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class InstanceRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public InstanceRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unity-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "instances.json");
        InstanceRegistry.FilePathOverride = _filePath;
    }

    public void Dispose()
    {
        InstanceRegistry.FilePathOverride = null;
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    private void WriteRoot(JsonObject root) => File.WriteAllText(_filePath, root.ToJsonString());

    private static JsonObject Entry(string baseUrl, string projectPath, int port, bool alive, string updatedAt) => new()
    {
        ["baseUrl"] = baseUrl,
        ["projectPath"] = projectPath,
        ["port"] = port,
        ["alive"] = alive,
        ["updatedAt"] = updatedAt,
    };

    [Fact]
    public void ResolveBaseUrl_ByInstanceKey_ReturnsBaseUrl()
    {
        WriteRoot(new JsonObject
        {
            ["A:52737"] = Entry("http://127.0.0.1:52737", "/x/A", 52737, true, "2026-05-29T00:00:00.0000000+00:00"),
            ["B:52738"] = Entry("http://127.0.0.1:52738", "/x/B", 52738, true, "2026-05-29T00:00:00.0000000+00:00"),
        });

        Assert.Equal("http://127.0.0.1:52738", InstanceRegistry.ResolveBaseUrl("B:52738", null));
    }

    [Fact]
    public void ResolveBaseUrl_ByProject_PrefersAliveThenNewest()
    {
        WriteRoot(new JsonObject
        {
            ["Game:1"] = Entry("http://127.0.0.1:1", "/x/Game", 1, false, "2026-05-29T10:00:00.0000000+00:00"),
            ["Game:2"] = Entry("http://127.0.0.1:2", "/x/Game", 2, true, "2026-05-29T09:00:00.0000000+00:00"),
        });

        Assert.Equal("http://127.0.0.1:2", InstanceRegistry.ResolveBaseUrl(null, "Game"));
    }

    [Fact]
    public void ResolveBaseUrl_ByProjectFolderName_Matches()
    {
        WriteRoot(new JsonObject
        {
            ["MyGame:5"] = Entry("http://127.0.0.1:5", "/x/y/MyGame", 5, true, "2026-05-29T00:00:00.0000000+00:00"),
        });

        Assert.Equal("http://127.0.0.1:5", InstanceRegistry.ResolveBaseUrl(null, "MyGame"));
    }

    [Fact]
    public void ResolveBaseUrl_NoMatch_ReturnsNull()
    {
        WriteRoot(new JsonObject
        {
            ["Game:1"] = Entry("http://127.0.0.1:1", "/x/Game", 1, true, "2026-05-29T00:00:00.0000000+00:00"),
        });

        Assert.Null(InstanceRegistry.ResolveBaseUrl("nope:1", null));
        Assert.Null(InstanceRegistry.ResolveBaseUrl(null, "nope"));
    }

    [Fact]
    public void ResolveDefaultBaseUrl_ReadsDefaultAlias()
    {
        WriteRoot(new JsonObject
        {
            ["default"] = Entry("http://127.0.0.1:9", "/x/Game", 9, true, "2026-05-29T00:00:00.0000000+00:00"),
        });

        Assert.Equal("http://127.0.0.1:9", InstanceRegistry.ResolveDefaultBaseUrl());
    }

    [Fact]
    public void ResolveDefaultBaseUrl_MissingFile_ReturnsNull()
    {
        InstanceRegistry.FilePathOverride = Path.Combine(_tempDir, "does-not-exist.json");

        Assert.Null(InstanceRegistry.ResolveDefaultBaseUrl());
    }

    [Fact]
    public void ListInstances_ExcludesDefaultAlias()
    {
        WriteRoot(new JsonObject
        {
            ["default"] = Entry("http://127.0.0.1:999", "/x/Game", 999, true, "2026-05-29T00:00:00.0000000+00:00"),
            ["Game:1"] = Entry("http://127.0.0.1:1", "/x/Game", 1, true, "2026-05-29T00:00:00.0000000+00:00"),
            ["Game:2"] = Entry("http://127.0.0.1:2", "/x/Game", 2, true, "2026-05-29T00:00:00.0000000+00:00"),
        });

        var instances = InstanceRegistry.ListInstances();
        var ports = instances.Select(node => node?["port"]?.GetValue<int>()).ToArray();

        Assert.Equal(2, instances.Count);
        Assert.Contains(1, ports);
        Assert.Contains(2, ports);
        Assert.DoesNotContain(999, ports);
    }
}
