using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

/// <summary>
/// scene.open-additive / scene.set-active / scene.list-loaded 멀티 씬 제어와 path 인자 기반 scene.unload 를
/// mock 브리지로 검증한다. not_found(종료 1) 와 missing_arg(종료 2) 오류 계약도 함께 확인한다.
/// </summary>
[Collection("MockBridge")]
public sealed class MultiSceneIntegrationTests : IAsyncLifetime
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
    public async Task OpenAdditive_ThenListLoaded_IncludesScene()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var openExit = await RunAsync(app, "scene", "open-additive", "path=Assets/Scenes/Extra.unity");
        Assert.Equal(0, openExit);
        Assert.Contains("Scene opened additively.", console.StdoutText);
        Assert.Contains("Assets/Scenes/Extra.unity", console.StdoutText);
        Assert.Contains("buildIndex", console.StdoutText);
        Assert.Contains("isActive", console.StdoutText);

        var listConsole = new RecordingConsole();
        var listExit = await RunAsync(new CliApplication(listConsole), "scene", "list-loaded");
        Assert.Equal(0, listExit);
        Assert.Contains("Loaded scenes listed.", listConsole.StdoutText);
        Assert.Contains("Assets/Scenes/Extra.unity", listConsole.StdoutText);
        Assert.Contains("count", listConsole.StdoutText);
    }

    [Fact]
    public async Task SetActive_OnLoadedScene_Succeeds()
    {
        var app = new CliApplication(new RecordingConsole());
        await RunAsync(app, "scene", "open-additive", "path=Assets/Scenes/Active.unity");

        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "scene", "set-active", "path=Assets/Scenes/Active.unity");

        Assert.Equal(0, exit);
        Assert.Contains("Active scene set.", console.StdoutText);
        Assert.Contains("\"isActive\": true", console.StdoutText);
    }

    [Fact]
    public async Task SetActive_OnUnloadedScene_NotFound_ExitCode1()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scene", "set-active", "path=Assets/Scenes/Never.unity", "--field=code");

        Assert.Equal(1, exit);
        Assert.Equal("not_found", console.StdoutText.Trim());
    }

    [Fact]
    public async Task OpenAdditive_MissingPath_MissingArg_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scene", "open-additive", "--field=code");

        Assert.Equal(2, exit);
        Assert.Equal("missing_arg", console.StdoutText.Trim());
    }

    [Fact]
    public async Task Unload_WithPath_Succeeds()
    {
        var app = new CliApplication(new RecordingConsole());
        await RunAsync(app, "scene", "open-additive", "path=Assets/Scenes/ToUnload.unity");

        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "scene", "unload", "path=Assets/Scenes/ToUnload.unity");

        Assert.Equal(0, exit);
        Assert.Contains("Scene unloaded.", console.StdoutText);
        Assert.Contains("Assets/Scenes/ToUnload.unity", console.StdoutText);
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
