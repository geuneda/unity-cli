using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

/// <summary>
/// 통합 오류 계약(BridgeException -> HTTP 상태 + 봉투 code -> CLI 종료 코드)을 mock 브리지로 검증한다.
/// </summary>
[Collection("MockBridge")]
public sealed class ErrorContractIntegrationTests : IAsyncLifetime
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
    public async Task NotFoundGameObject_ExitCode1_CodeNotFound()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "gameobject", "get", "name=Ghost");

        Assert.Equal(1, exitCode);
        Assert.Contains("\"code\": \"not_found\"", console.StdoutText);
        Assert.Contains("\"success\": false", console.StdoutText);
    }

    [Fact]
    public async Task NotFoundGameObject_FieldCode_PrintsNotFound()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "gameobject", "get", "name=Ghost", "--field=code");

        Assert.Equal(1, exitCode);
        Assert.Equal("not_found", console.StdoutText.Trim());
    }

    [Fact]
    public async Task ComponentUpdateMissingObject_NotFound_ExitCode1()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "component", "update", "name=Ghost", "type=Rigidbody2D", "--field=code");

        Assert.Equal(1, exitCode);
        Assert.Equal("not_found", console.StdoutText.Trim());
    }

    [Fact]
    public async Task SceneDeleteMissingPath_MissingArg_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "scene", "delete", "--field=code");

        Assert.Equal(2, exitCode);
        Assert.Equal("missing_arg", console.StdoutText.Trim());
    }

    [Fact]
    public async Task UnknownTool_NonStrict_ExitCode1()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "nonexistent.tool");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported tool", console.StdoutText);
        Assert.Contains("\"code\": \"unknown_tool\"", console.StdoutText);
    }

    [Fact]
    public async Task UnknownTool_Strict_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "--strict", "tool", "call", "nonexistent.tool");

        Assert.Equal(2, exitCode);
        Assert.Contains("\"code\": \"unknown_tool\"", console.StdoutText);
    }

    [Fact]
    public async Task FieldMiss_StillExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "gameobject", "create", "name=Hero", "--field=result.nope");

        Assert.Equal(2, exitCode);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

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
