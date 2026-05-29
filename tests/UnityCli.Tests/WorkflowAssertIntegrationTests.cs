using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class WorkflowAssertIntegrationTests : IAsyncLifetime
{
    private readonly MockUnityBridgeServer _server = new();
    private int _port;
    private readonly List<string> _tempFiles = [];
    private string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        await _server.StartAsync(port: _port);
    }

    public async Task DisposeAsync()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }

        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Capture_FeedsVariableSubstitution()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "make",
              "call": "gameobject.create",
              "args": { "name": "Hero" },
              "capture": { "heroName": "result.name" }
            },
            {
              "id": "fetch",
              "call": "gameobject.get",
              "args": { "name": "${heroName}" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Hero", console.StdoutText);
    }

    [Fact]
    public async Task AssertStep_Pass_ReturnsZero()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "make",
              "call": "gameobject.create",
              "args": { "name": "Hero" },
              "assert": { "path": "result.name", "op": "equals", "expected": "Hero" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AssertStep_Fail_ReturnsOne()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "make",
              "call": "gameobject.create",
              "args": { "name": "Hero" },
              "assert": { "path": "result.name", "op": "equals", "expected": "Ghost" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(1, exit);
        Assert.Contains("assert failed", console.StderrText);
    }

    [Fact]
    public async Task WaitForResource_PlayMode_Resolves()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "play",
              "call": "editor.play"
            },
            {
              "id": "await-play",
              "waitFor": {
                "resource": "editor/state",
                "path": "data.isPlaying",
                "op": "equals",
                "expected": "true",
                "pollMs": 200,
                "timeoutMs": 2000
              }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("matched", console.StdoutText);
    }

    private async Task<int> RunWorkflowAsync(RecordingConsole console, string workflowJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, workflowJson);
        _tempFiles.Add(path);

        var app = new CliApplication(console);
        return await app.RunAsync(new[] { $"--base-url={BaseUrl}", "workflow", "run", path }, CancellationToken.None);
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
