using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class NewToolsIntegrationTests : IAsyncLifetime
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
    // component.list / get / add / remove
    // ---------------------------------------------------------------

    [Fact]
    public async Task ComponentList_ReturnsTransform()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "list", "name=Hero");

        Assert.Equal(0, exit);
        Assert.Contains("Components listed.", console.StdoutText);
        Assert.Contains("Transform", console.StdoutText);
    }

    [Fact]
    public async Task ComponentAddThenGet_RoundTripsValues()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var addExit = await RunAsync(app, "component", "add", "name=Hero", "type=Health", "values={\"hp\":100}");
        Assert.Equal(0, addExit);
        Assert.Contains("Component added.", console.StdoutText);

        var getExit = await RunAsync(app, "component", "get", "name=Hero", "type=Health");
        Assert.Equal(0, getExit);
        Assert.Contains("hp", console.StdoutText);
        Assert.Contains("100", console.StdoutText);
    }

    [Fact]
    public async Task ComponentAdd_Duplicate_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        await RunAsync(app, "component", "add", "name=Hero", "type=Health");
        var second = await RunAsync(app, "component", "add", "name=Hero", "type=Health");

        Assert.Equal(1, second);
        Assert.Contains("already exists", console.StdoutText);
    }

    [Fact]
    public async Task ComponentRemove_RemovesComponent()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        await RunAsync(app, "component", "add", "name=Hero", "type=Health");
        var removeExit = await RunAsync(app, "component", "remove", "name=Hero", "type=Health");
        Assert.Equal(0, removeExit);
        Assert.Contains("Component removed.", console.StdoutText);

        var getExit = await RunAsync(app, "component", "get", "name=Hero", "type=Health");
        Assert.Equal(1, getExit);
    }

    [Fact]
    public async Task ComponentRemove_Transform_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "remove", "name=Hero", "type=Transform");

        Assert.Equal(1, exit);
        Assert.Contains("cannot be removed", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // gameobject.find / set-properties
    // ---------------------------------------------------------------

    [Fact]
    public async Task GameObjectFind_ByNameContains_ReturnsMatches()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero1");
        await RunAsync(app, "gameobject", "create", "name=Hero2");
        await RunAsync(app, "gameobject", "create", "name=Villain");

        var findConsole = new RecordingConsole();
        var findApp = new CliApplication(findConsole);
        var exit = await RunAsync(findApp, "gameobject", "find", "nameContains=Hero");

        Assert.Equal(0, exit);
        Assert.Contains("\"count\": 2", findConsole.StdoutText);
    }

    [Fact]
    public async Task GameObjectSetProperties_RenamesAndTags()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var setExit = await RunAsync(app, "gameobject", "set-properties", "name=Hero", "newName=Boss", "tag=Enemy", "active=false");
        Assert.Equal(0, setExit);
        Assert.Contains("newName", console.StdoutText);
        Assert.Contains("tag", console.StdoutText);

        var getExit = await RunAsync(app, "gameobject", "get", "name=Boss");
        Assert.Equal(0, getExit);
    }

    // ---------------------------------------------------------------
    // project/info resource
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProjectInfoResource_ReturnsRenderPipeline()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "resource", "get", "project/info");

        Assert.Equal(0, exit);
        Assert.Contains("renderPipeline", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // --field / --quiet / doctor
    // ---------------------------------------------------------------

    [Fact]
    public async Task FieldSelector_PrintsRawScalar()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--field=result.name");

        Assert.Equal(0, exit);
        Assert.Equal("Hero", console.StdoutText.Trim());
        Assert.DoesNotContain("{", console.StdoutText);
    }

    [Fact]
    public async Task FieldSelector_Miss_ReturnsExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--field=result.doesNotExist");

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Quiet_SuppressesOutput_KeepsExitCode()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--quiet");

        Assert.Equal(0, exit);
        Assert.True(string.IsNullOrEmpty(console.StdoutText));
    }

    [Fact]
    public async Task Doctor_ReportsReachableAndParity()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "doctor");

        Assert.Equal(0, exit);
        Assert.Contains("[PASS] bridge.reachable", console.StdoutText);
        Assert.Contains("tools.parity", console.StdoutText);
        Assert.Contains("events.contract", console.StdoutText);
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
