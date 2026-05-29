using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using UnityCli.Cli;
using UnityCli.Runtime;
using UnityCli.Support;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class CliInstancesIntegrationTests : IAsyncLifetime
{
    private readonly MockUnityBridgeServer _server = new();
    private int _port;
    private string _tempDir = string.Empty;
    private string _filePath = string.Empty;
    private string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        await _server.StartAsync(port: _port);
        _tempDir = Path.Combine(Path.GetTempPath(), "unity-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "instances.json");
        InstanceRegistry.FilePathOverride = _filePath;
    }

    public async Task DisposeAsync()
    {
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

    private static JsonObject Entry(string baseUrl, string projectPath, int port, string sessionId, bool alive = true) => new()
    {
        ["baseUrl"] = baseUrl,
        ["projectPath"] = projectPath,
        ["port"] = port,
        ["sessionId"] = sessionId,
        ["alive"] = alive,
        ["updatedAt"] = "2026-05-29T00:00:00.0000000+00:00",
    };

    [Fact]
    public async Task InstancesList_PrintsRegisteredInstances_Exit0()
    {
        WriteRoot(new JsonObject
        {
            ["A:100"] = Entry("http://127.0.0.1:100", "/x/A", 100, "session-a"),
            ["B:200"] = Entry("http://127.0.0.1:200", "/x/B", 200, "session-b"),
        });
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "instances", "list", "--json" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("http://127.0.0.1:100", console.StdoutText);
        Assert.Contains("http://127.0.0.1:200", console.StdoutText);
        Assert.Contains("session-a", console.StdoutText);
        Assert.Contains("session-b", console.StdoutText);
    }

    [Fact]
    public async Task InstancesList_FieldSelector_PrintsScalar()
    {
        WriteRoot(new JsonObject
        {
            ["A:100"] = Entry("http://127.0.0.1:100", "/x/A", 100, "session-a"),
        });
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "instances", "list", "--field=[0].port" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("100", console.StdoutText);
    }

    [Fact]
    public async Task InstancesList_BadSubcommand_Exit1()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "instances", "bogus" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: unity-cli instances list", console.StderrText);
    }

    [Fact]
    public async Task Selector_Instance_RoutesToBridge_Exit0()
    {
        WriteRoot(new JsonObject
        {
            [$"Mock:{_port}"] = Entry(BaseUrl, "/tmp/MockProj", _port, "session-mock"),
        });
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { $"--instance=Mock:{_port}", "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task Selector_Project_RoutesToBridge_Exit0()
    {
        WriteRoot(new JsonObject
        {
            [$"MockProj:{_port}"] = Entry(BaseUrl, "/tmp/MockProj", _port, "session-mock"),
        });
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "--project=MockProj", "status" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task Selector_NoMatch_Exit1()
    {
        WriteRoot(new JsonObject());
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "--instance=ghost:1", "status" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("No registered Unity instance", console.StderrText);
    }

    [Fact]
    public async Task BaseUrlExplicit_OverridesSelector()
    {
        WriteRoot(new JsonObject());
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "--instance=ghost:1", $"--base-url={BaseUrl}", "status" }, CancellationToken.None);

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
