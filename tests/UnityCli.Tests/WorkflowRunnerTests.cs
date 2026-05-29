using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

/// <summary>워크플로 단계의 재시도(retry)와 조건부 건너뛰기(when) 동작을 모의 브리지로 검증한다.</summary>
[Collection("MockBridge")]
public sealed class WorkflowRunnerTests : IAsyncLifetime
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
    public async Task Retry_FailingCall_Exhausts_ReturnsOne()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "ghost",
              "call": "gameobject.get",
              "args": { "name": "Missing" },
              "retry": { "maxAttempts": 3, "delayMs": 0 }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(1, exit);
        Assert.Contains("Workflow step failed", console.StderrText);
    }

    [Fact]
    public async Task Retry_SucceedsAfterFailures_ReturnsZero()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "flaky",
              "call": "mock.flaky",
              "args": { "failuresBeforeSuccess": 2 },
              "retry": { "maxAttempts": 3, "delayMs": 0 }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Flaky succeeded on attempt 3", console.StdoutText);
    }

    [Fact]
    public async Task Retry_InsufficientAttempts_OnFlaky_ReturnsOne()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "flaky",
              "call": "mock.flaky",
              "args": { "failuresBeforeSuccess": 5 },
              "retry": { "maxAttempts": 2, "delayMs": 0 }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(1, exit);
        Assert.Contains("Workflow step failed", console.StderrText);
        Assert.Contains("Flaky failure 2", console.StderrText);
    }

    [Fact]
    public async Task When_ConditionTrue_RunsStep_ReturnsZero()
    {
        var workflow = """
        {
          "steps": [
            {
              "id": "make",
              "call": "gameobject.create",
              "args": { "name": "Hero" },
              "when": { "resource": "editor/state", "path": "data.isPlaying", "op": "equals", "expected": "false" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Hero", console.StdoutText);
        Assert.DoesNotContain("Skipped", console.StdoutText);
    }

    [Fact]
    public async Task When_ConditionFalse_SkipsStep_ReturnsZero()
    {
        // editor/state seeds isPlaying=false, so expecting "true" is unmet -> step is skipped.
        // The gated call would 404 on a missing GameObject if it ran, so a clean exit proves the skip.
        var workflow = """
        {
          "steps": [
            {
              "id": "guarded",
              "call": "gameobject.get",
              "args": { "name": "Missing" },
              "when": { "resource": "editor/state", "path": "data.isPlaying", "op": "equals", "expected": "true" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Skipped", console.StdoutText);
    }

    [Fact]
    public async Task When_FromVar_ConditionFalse_SkipsStep_ReturnsZero()
    {
        var workflow = """
        {
          "variables": { "mode": "prod" },
          "steps": [
            {
              "id": "guarded",
              "call": "gameobject.get",
              "args": { "name": "Missing" },
              "when": { "fromVar": "mode", "path": "", "op": "equals", "expected": "staging" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Skipped", console.StdoutText);
    }

    [Fact]
    public async Task When_FromVar_ConditionTrue_RunsStep_ReturnsZero()
    {
        var workflow = """
        {
          "variables": { "mode": "prod" },
          "steps": [
            {
              "id": "make",
              "call": "gameobject.create",
              "args": { "name": "Hero" },
              "when": { "fromVar": "mode", "path": "", "op": "equals", "expected": "prod" }
            }
          ]
        }
        """;

        var console = new RecordingConsole();
        var exit = await RunWorkflowAsync(console, workflow);

        Assert.Equal(0, exit);
        Assert.Contains("Hero", console.StdoutText);
        Assert.DoesNotContain("Skipped", console.StdoutText);
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
