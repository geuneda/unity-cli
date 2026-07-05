using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class SceneAuthoringIntegrationTests : IAsyncLifetime
{
    private readonly MockUnityBridgeServer _server = new();
    private int _port;
    private string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        await _server.StartAsync(port: _port);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    // ---------------------------------------------------------------
    // project.add-tag / add-layer / list-tags-layers
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProjectAddTag_ReturnsTagAndAdded()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "add-tag", "tag=Boss");

        Assert.Equal(0, exit);
        Assert.Contains("Boss", console.StdoutText);
        Assert.Contains("added", console.StdoutText);
    }

    [Fact]
    public async Task ProjectAddTag_FieldSelector_PrintsRawTag()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "add-tag", "tag=Boss", "--field=result.tag");

        Assert.Equal(0, exit);
        Assert.Equal("Boss", console.StdoutText.Trim());
    }

    [Fact]
    public async Task ProjectAddLayer_ReturnsLayerAndIndex()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "add-layer", "layer=Water");

        Assert.Equal(0, exit);
        Assert.Contains("Water", console.StdoutText);
        Assert.Contains("index", console.StdoutText);
    }

    [Fact]
    public async Task ProjectAddLayer_FieldSelector_PrintsIndex()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "add-layer", "layer=Water", "--field=result.index");

        Assert.Equal(0, exit);
        Assert.Equal("8", console.StdoutText.Trim());
    }

    [Fact]
    public async Task ProjectRemoveTag_ReturnsTagAndRemoved()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "remove-tag", "tag=Boss");

        Assert.Equal(0, exit);
        Assert.Contains("Boss", console.StdoutText);
        Assert.Contains("removed", console.StdoutText);
    }

    [Fact]
    public async Task ProjectRemoveLayer_ReturnsLayerAndRemoved()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "remove-layer", "layer=Water");

        Assert.Equal(0, exit);
        Assert.Contains("Water", console.StdoutText);
        Assert.Contains("removed", console.StdoutText);
    }

    [Fact]
    public async Task ProjectListTagsLayers_ReturnsTagsAndLayers()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "project", "list-tags-layers");

        Assert.Equal(0, exit);
        Assert.Contains("tags", console.StdoutText);
        Assert.Contains("layers", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // asset.set-addressable / remove-addressable
    // ---------------------------------------------------------------

    [Fact]
    public async Task AssetSetAddressable_ReturnsGuidAddressGroup()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "set-addressable", "path=Assets/Prefabs/Cube.prefab", "address=cube", "group=Main");

        Assert.Equal(0, exit);
        Assert.Contains("guid", console.StdoutText);
        Assert.Contains("cube", console.StdoutText);
        Assert.Contains("Main", console.StdoutText);
    }

    [Fact]
    public async Task AssetRemoveAddressable_ReturnsRemoved()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "remove-addressable", "path=Assets/Prefabs/Cube.prefab");

        Assert.Equal(0, exit);
        Assert.Contains("removed", console.StdoutText);
        Assert.Contains("Assets/Prefabs/Cube.prefab", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // scene.set-lighting / bake-navmesh
    // ---------------------------------------------------------------

    [Fact]
    public async Task SceneSetLighting_ReturnsAppliedKeys()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scene", "set-lighting", "fog=true", "fogColor=#123456", "ambientIntensity=1.0");

        Assert.Equal(0, exit);
        Assert.Contains("applied", console.StdoutText);
        Assert.Contains("fog", console.StdoutText);
    }

    [Fact]
    public async Task SceneBakeNavmesh_ReturnsBaked()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scene", "bake-navmesh");

        Assert.Equal(0, exit);
        Assert.Contains("baked", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // component.update values: array + nested struct + scene ref-spec
    // ---------------------------------------------------------------

    [Fact]
    public async Task ComponentUpdate_ArrayNestedAndSceneRef_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "update", "name=Hero", "type=Health",
            "values={\"_ints\":[1,2,3],\"_stat\":{\"hp\":10},\"_target\":{\"__ref\":\"name:Player\"}}");

        Assert.Equal(0, exit);
        Assert.Contains("applied", console.StdoutText);
        Assert.Contains("_ints", console.StdoutText);
        Assert.Contains("_stat", console.StdoutText);
        Assert.Contains("_target", console.StdoutText);
    }

    [Fact]
    public async Task ComponentUpdate_ArrayNestedAndSceneRef_ThenGet_RoundTrips()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var updateExit = await RunAsync(app, "component", "update", "name=Hero", "type=Health",
            "values={\"_ints\":[1,2,3],\"_stat\":{\"hp\":10},\"_target\":{\"__ref\":\"name:Player\"}}");
        Assert.Equal(0, updateExit);

        var getConsole = new RecordingConsole();
        var getExit = await RunAsync(new CliApplication(getConsole), "component", "get", "name=Hero", "type=Health");

        Assert.Equal(0, getExit);
        Assert.Contains("_ints", getConsole.StdoutText);
        Assert.Contains("hp", getConsole.StdoutText);
        Assert.Contains("10", getConsole.StdoutText);
        Assert.Contains("__ref", getConsole.StdoutText);
    }

    private Task<int> RunAsync(CliApplication app, params string[] args)
    {
        var fullArgs = new List<string> { $"--base-url={BaseUrl}" };
        fullArgs.AddRange(args);
        return app.RunAsync(fullArgs.ToArray(), CancellationToken.None);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
