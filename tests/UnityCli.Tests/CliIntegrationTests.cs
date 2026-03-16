using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class CliIntegrationTests : IAsyncLifetime
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

    [Fact]
    public async Task StatusCommand_PrintsConnectedBridge()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task SceneAndGameObjectCommands_WorkThroughCli()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var createScene = await RunAsync(app, "scene", "create", "path=Assets/Scenes/Main.unity");
        var createObject = await RunAsync(app, "gameobject", "create", "name=Player", "scenePath=Assets/Scenes/Main.unity", "position=[1,2,3]");
        var selectObject = await RunAsync(app, "gameobject", "select", "name=Player");

        Assert.Equal(0, createScene);
        Assert.Equal(0, createObject);
        Assert.Equal(0, selectObject);
        Assert.Contains("\"name\": \"Player\"", console.StdoutText);
    }

    [Fact]
    public async Task WorkflowRunner_CanWaitForEmittedEvents()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "workflows", "smoke-test.json"));
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "workflow", "run", workflowPath);

        Assert.True(File.Exists(workflowPath), $"Workflow file missing: {workflowPath}");
        Assert.Equal(0, exitCode);
        Assert.Contains("tests.completed", console.StdoutText);
        Assert.Contains("Player ready", console.StdoutText);
    }

    [Fact]
    public async Task ResourceCommands_ReturnHierarchyAndLogs()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "console", "send", "message=resource-test", "level=info");
        var hierarchyExitCode = await RunAsync(app, "resource", "get", "scene/hierarchy");
        var logsExitCode = await RunAsync(app, "resource", "get", "console/logs");

        Assert.Equal(0, hierarchyExitCode);
        Assert.Equal(0, logsExitCode);
        Assert.Contains("resource-test", console.StdoutText);
        Assert.Contains("scenePath", console.StdoutText);
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
