using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

/// <summary>
/// asset.create-scriptableobject / scriptableobject.get / scriptableobject.list 도구를 mock 브리지로 검증한다.
/// 성공(종료 0), 필수 인자 누락 시 missing_arg(종료 2), --field 셀렉터 동작을 함께 확인한다.
/// </summary>
[Collection("MockBridge")]
public sealed class ScriptableObjectIntegrationTests : IAsyncLifetime
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
    public async Task CreateScriptableObject_WithValues_ReportsApplied()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(
            app,
            "asset",
            "create-scriptableobject",
            "type=MyConfig",
            "path=Assets/Configs/MyConfig.asset",
            "values={\"_amount\":10}");

        Assert.Equal(0, exit);
        Assert.Contains("ScriptableObject created.", console.StdoutText);
        Assert.Contains("Assets/Configs/MyConfig.asset", console.StdoutText);
        Assert.Contains("applied", console.StdoutText);
        Assert.Contains("_amount", console.StdoutText);
    }

    [Fact]
    public async Task CreateScriptableObject_MissingType_MissingArg_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "create-scriptableobject", "path=Assets/Configs/X.asset", "--field=code");

        Assert.Equal(2, exit);
        Assert.Equal("missing_arg", console.StdoutText.Trim());
    }

    [Fact]
    public async Task GetScriptableObject_ReturnsProperties()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scriptableobject", "get", "path=Assets/Configs/MyConfig.asset");

        Assert.Equal(0, exit);
        Assert.Contains("ScriptableObject fetched.", console.StdoutText);
        Assert.Contains("properties", console.StdoutText);
    }

    [Fact]
    public async Task GetScriptableObject_MissingPath_MissingArg_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scriptableobject", "get", "--field=code");

        Assert.Equal(2, exit);
        Assert.Equal("missing_arg", console.StdoutText.Trim());
    }

    [Fact]
    public async Task ListScriptableObjects_ReturnsAssetsArray()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "scriptableobject", "list", "filter=t:ScriptableObject");

        Assert.Equal(0, exit);
        Assert.Contains("ScriptableObjects listed.", console.StdoutText);
        Assert.Contains("assets", console.StdoutText);
        Assert.Contains("count", console.StdoutText);
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
