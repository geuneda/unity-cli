using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class CliAssertIntegrationTests : IAsyncLifetime
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
    public async Task Assert_Resource_Equals_Pass()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "resource", "editor/state", "path=data.isPlaying", "equals=false");

        Assert.Equal(0, exit);
        Assert.Contains("[PASS]", console.StdoutText);
    }

    [Fact]
    public async Task Assert_Resource_Gt_OnGameViewWidth()
    {
        var passConsole = new RecordingConsole();
        var passExit = await RunAsync(new CliApplication(passConsole), "assert", "resource", "editor/state", "path=data.gameViewWidth", "gt=1000");
        Assert.Equal(0, passExit);
        Assert.Contains("[PASS]", passConsole.StdoutText);

        var failConsole = new RecordingConsole();
        var failExit = await RunAsync(new CliApplication(failConsole), "assert", "resource", "editor/state", "path=data.gameViewWidth", "gt=2000");
        Assert.Equal(1, failExit);
        Assert.Contains("[FAIL]", failConsole.StdoutText);
    }

    [Fact]
    public async Task Assert_Resource_Exists()
    {
        var passConsole = new RecordingConsole();
        var passExit = await RunAsync(new CliApplication(passConsole), "assert", "resource", "editor/state", "path=data.activeScenePath", "exists=true");
        Assert.Equal(0, passExit);

        var failConsole = new RecordingConsole();
        var failExit = await RunAsync(new CliApplication(failConsole), "assert", "resource", "editor/state", "path=data.nope", "exists=true");
        Assert.Equal(1, failExit);
    }

    [Fact]
    public async Task Assert_Tool_ResultField_Equals()
    {
        await RunAsync(new CliApplication(new RecordingConsole()), "gameobject", "create", "name=Hero");

        var passConsole = new RecordingConsole();
        var passExit = await RunAsync(new CliApplication(passConsole), "assert", "tool", "gameobject.get", "name=Hero", "path=result.name", "equals=Hero");
        Assert.Equal(0, passExit);
        Assert.Contains("[PASS]", passConsole.StdoutText);

        var failConsole = new RecordingConsole();
        var failExit = await RunAsync(new CliApplication(failConsole), "assert", "tool", "gameobject.get", "name=Hero", "path=result.name", "equals=Ghost");
        Assert.Equal(1, failExit);
    }

    [Fact]
    public async Task Assert_Tool_UnderlyingFailure_ReturnsOne()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "tool", "nonexistent.tool", "path=result.x", "exists=true");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Assert_Event_AfterConsoleSend()
    {
        await RunAsync(new CliApplication(new RecordingConsole()), "console", "send", "message=hi", "level=warn");

        // The mock buffers a startup event at cursor 1, so assert against the events array via contains
        // rather than a fixed index to stay deterministic across buffered events.
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "event", "waitMs=1000", "path=events", "contains=console.log");

        Assert.Equal(0, exit);
        Assert.Contains("[PASS]", console.StdoutText);
    }

    [Fact]
    public async Task Assert_BadSource_ReturnsTwo()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "bogus", "path=x", "exists=true");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown assert source", console.StderrText);
    }

    [Fact]
    public async Task Assert_MissingPath_ReturnsTwo()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "resource", "editor/state", "equals=true");

        Assert.Equal(2, exit);
        Assert.Contains("Missing path", console.StderrText);
    }

    [Fact]
    public async Task Assert_UnknownOp_ReturnsTwo()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "resource", "editor/state", "path=data.isPlaying", "frobnicate=true");

        Assert.Equal(2, exit);
        Assert.Contains("operator", console.StderrText);
    }

    [Fact]
    public async Task Assert_QuietPass_NoStdout()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "resource", "editor/state", "path=data.isPlaying", "equals=false", "--quiet");

        Assert.Equal(0, exit);
        Assert.True(string.IsNullOrEmpty(console.StdoutText));
    }

    [Fact]
    public async Task Assert_Json_EmitsStructuredResult()
    {
        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "assert", "resource", "editor/state", "path=data.isPlaying", "equals=false", "--json");

        Assert.Equal(0, exit);
        Assert.Contains("\"passed\": true", console.StdoutText);
        Assert.Contains("\"op\": \"equals\"", console.StdoutText);
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
