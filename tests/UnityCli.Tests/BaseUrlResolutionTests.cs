using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using UnityCli.Cli;
using UnityCli.Runtime;
using UnityCli.Support;

namespace UnityCli.Tests;

/// <summary>
/// base-url 해석 우선순위(--base-url &gt; --project/--instance &gt; UNITY_CLI_BASE_URL &gt; instances.json &gt; 기본값) 검증.
/// 프로세스 전역 환경변수와 <see cref="InstanceRegistry.FilePathOverride"/> 를 조작하므로 병렬 비활성 컬렉션에 둔다.
/// 우승 소스는 살아있는 mock(빈 포트)으로, 하위 소스는 바인딩되지 않은 죽은 포트로 구분한다.
/// </summary>
[Collection("MockBridge")]
public sealed class BaseUrlResolutionTests : IAsyncLifetime
{
    private readonly MockUnityBridgeServer _server = new();
    private int _livePort;
    private int _deadPort;
    private string _tempDir = string.Empty;
    private string _filePath = string.Empty;
    private string? _envSnapshot;
    private string LiveUrl => $"http://127.0.0.1:{_livePort}";
    private string DeadUrl => $"http://127.0.0.1:{_deadPort}";

    public async Task InitializeAsync()
    {
        _livePort = GetFreePort();
        _deadPort = GetFreePort();
        await _server.StartAsync(port: _livePort);
        _tempDir = Path.Combine(Path.GetTempPath(), "unity-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "instances.json");
        InstanceRegistry.FilePathOverride = _filePath;
        _envSnapshot = Environment.GetEnvironmentVariable("UNITY_CLI_BASE_URL");
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", null);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", _envSnapshot);
        InstanceRegistry.FilePathOverride = null;
        await _server.DisposeAsync();
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

    private static JsonObject Entry(string baseUrl, string projectPath, int port, bool alive = true) => new()
    {
        ["baseUrl"] = baseUrl,
        ["projectPath"] = projectPath,
        ["port"] = port,
        ["alive"] = alive,
        ["updatedAt"] = "2026-05-29T00:00:00.000Z",
    };

    [Fact]
    public async Task FlagBeatsEnv()
    {
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", DeadUrl);
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { $"--base-url={LiveUrl}", "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task EnvBeatsInstancesFile()
    {
        WriteRoot(new JsonObject { ["default"] = Entry(DeadUrl, "/x/Game", _deadPort) });
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", LiveUrl);
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task InstancesFileUsedWhenNoFlagNoEnv()
    {
        WriteRoot(new JsonObject { ["default"] = Entry(LiveUrl, "/x/Game", _livePort) });
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task EnvUsedWhenNoFlagNoFileNoSelector()
    {
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", LiveUrl);
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task SelectorBeatsEnv()
    {
        WriteRoot(new JsonObject
        {
            ["default"] = Entry(DeadUrl, "/x/Game", _deadPort),
            [$"MockProj:{_livePort}"] = Entry(LiveUrl, "/tmp/MockProj", _livePort),
        });
        Environment.SetEnvironmentVariable("UNITY_CLI_BASE_URL", DeadUrl);
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "--project=MockProj", "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
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
