using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Abstractions;
using UnityCli.Protocol;
using UnityCli.Runtime;
using UnityCli.Support;

namespace UnityCli.Cli;

public sealed class CliApplication
{
    private readonly IConsole _console;

    public CliApplication(IConsole console)
    {
        _console = console;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        if (args[0] == "mock" && args.ElementAtOrDefault(1) == "serve")
        {
            return await RunMockServerAsync(args.Skip(2).ToArray(), cancellationToken);
        }

        var options = ParseGlobalOptions(args);
        var command = options.RemainingArgs;

        if (command.Count == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            using var client = new BridgeClient(options.BaseUrl, TimeSpan.FromMilliseconds(options.TimeoutMs));

            switch (command[0])
            {
                case "status":
                    return await PrintStatusAsync(client, options.Json, cancellationToken);
                case "capabilities":
                    return await PrintCapabilitiesAsync(client, options.Json, cancellationToken);
                case "doctor":
                    return await RunDoctorAsync(client, options, cancellationToken);
                case "events":
                    return await RunEventsAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "workflow":
                    return await RunWorkflowAsync(client, command.Skip(1).ToArray(), cancellationToken);
                case "batch":
                    return await RunBatchAsync(client, command.Skip(1).ToArray(), cancellationToken);
                case "tool":
                    return await RunToolAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "resource":
                    return await RunResourceAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                default:
                    return await RunMappedToolCommandAsync(client, options, command, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            _console.ErrorLine(exception.Message);
            return 1;
        }
    }

    private async Task<int> RunMockServerAsync(string[] args, CancellationToken cancellationToken)
    {
        var host = "127.0.0.1";
        var port = 52737;

        foreach (var arg in args)
        {
            if (arg.StartsWith("host=", StringComparison.OrdinalIgnoreCase))
            {
                host = arg["host=".Length..];
            }
            else if (arg.StartsWith("port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg["port=".Length..], out var parsedPort))
            {
                port = parsedPort;
            }
        }

        await using var server = new MockUnityBridgeServer();
        await server.StartAsync(host, port, cancellationToken);
        _console.WriteLine($"Mock Unity bridge running at {server.BaseUrl}");
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    private async Task<int> PrintStatusAsync(BridgeClient client, bool json, CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken);
        if (json)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(status));
            return 0;
        }

        _console.WriteLine($"name: {status.Name}");
        _console.WriteLine($"version: {status.Version}");
        _console.WriteLine($"state: {status.State}");
        _console.WriteLine($"editor: {status.EditorVersion}");
        _console.WriteLine($"project: {status.ProjectPath}");
        _console.WriteLine($"eventCursor: {status.EventCursor}");
        if (!string.IsNullOrWhiteSpace(status.SessionId))
        {
            _console.WriteLine($"sessionId: {status.SessionId}");
        }
        return 0;
    }

    private async Task<int> PrintCapabilitiesAsync(BridgeClient client, bool json, CancellationToken cancellationToken)
    {
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken);
        if (json)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(capabilities));
            return 0;
        }

        _console.WriteLine("tools:");
        foreach (var tool in capabilities.Tools)
        {
            _console.WriteLine($"  {tool}");
        }

        _console.WriteLine("resources:");
        foreach (var resource in capabilities.Resources)
        {
            _console.WriteLine($"  {resource}");
        }

        _console.WriteLine("events:");
        foreach (var @event in capabilities.Events)
        {
            _console.WriteLine($"  {@event}");
        }

        return 0;
    }

    private const string ExpectedBridgeVersion = "0.1.0";

    private sealed record DoctorCheck(string Check, string Status, string Detail);

    private async Task<int> RunDoctorAsync(BridgeClient client, GlobalOptions options, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();

        BridgeStatus status;
        try
        {
            status = await client.GetStatusAsync(cancellationToken);
            checks.Add(new DoctorCheck("bridge.reachable", "PASS", $"{status.Name} v{status.Version}, editor {status.EditorVersion}, project {status.ProjectPath}"));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("bridge.reachable", "FAIL", exception.Message));
            return PrintDoctor(checks, options);
        }

        CapabilityResponse capabilities;
        try
        {
            capabilities = await client.GetCapabilitiesAsync(cancellationToken);
            checks.Add(new DoctorCheck("capabilities", "PASS", $"{capabilities.Tools.Count} tools, {capabilities.Resources.Count} resources, {capabilities.Events.Count} events"));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("capabilities", "FAIL", exception.Message));
            return PrintDoctor(checks, options);
        }

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken);
            var toolNames = tools.Select(tool => tool.Name).OrderBy(name => name).ToArray();
            var capabilityTools = capabilities.Tools.OrderBy(name => name).ToArray();
            var onlyInCapabilities = capabilityTools.Except(toolNames).ToArray();
            var onlyInTools = toolNames.Except(capabilityTools).ToArray();
            checks.Add(onlyInCapabilities.Length == 0 && onlyInTools.Length == 0
                ? new DoctorCheck("tools.parity", "PASS", $"{toolNames.Length} tools consistent between /tools and /capabilities")
                : new DoctorCheck("tools.parity", "FAIL", $"capabilities-only: [{string.Join(", ", onlyInCapabilities)}]; tools-only: [{string.Join(", ", onlyInTools)}]"));
        }
        catch (Exception exception)
        {
            checks.Add(new DoctorCheck("tools.parity", "WARN", exception.Message));
        }

        var requiredEvents = new[] { "bridge.started", "console.log", "hierarchy.changed", "scene.loaded", "scene.saved", "transform.changed", "tests.completed", "editor.compiled" };
        var missingEvents = requiredEvents.Where(name => !capabilities.Events.Contains(name)).ToArray();
        checks.Add(missingEvents.Length == 0
            ? new DoctorCheck("events.contract", "PASS", $"all {requiredEvents.Length} CLI-required events advertised")
            : new DoctorCheck("events.contract", "FAIL", $"missing from capabilities: [{string.Join(", ", missingEvents)}]"));

        checks.Add(status.Version == ExpectedBridgeVersion
            ? new DoctorCheck("version", "PASS", $"bridge {status.Version} matches CLI-expected {ExpectedBridgeVersion}")
            : new DoctorCheck("version", "WARN", $"bridge {status.Version} != CLI-expected {ExpectedBridgeVersion}"));

        return PrintDoctor(checks, options);
    }

    private int PrintDoctor(List<DoctorCheck> checks, GlobalOptions options)
    {
        if (options.Json)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(checks));
        }
        else if (!options.Quiet)
        {
            foreach (var check in checks)
            {
                _console.WriteLine($"[{check.Status}] {check.Check}: {check.Detail}");
            }
        }

        return checks.Any(check => check.Status == "FAIL") ? 1 : 0;
    }

    private async Task<int> RunEventsAsync(BridgeClient client, GlobalOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "tail")
        {
            _console.ErrorLine("Usage: unity-cli events tail [after=0] [waitMs=1000]");
            return 1;
        }

        var kv = JsonHelpers.ParseKeyValuePairs(args.Skip(1));
        var after = kv["after"]?.GetValue<long>() ?? 0;
        var waitMs = (int)(kv["waitMs"]?.GetValue<long>() ?? options.TimeoutMs);
        var response = await client.PollEventsAsync(after, waitMs, cancellationToken);
        _console.WriteLine(JsonHelpers.ToPrettyJson(response));
        return 0;
    }

    private async Task<int> RunWorkflowAsync(BridgeClient client, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || args[0] != "run")
        {
            _console.ErrorLine("Usage: unity-cli workflow run <file>");
            return 1;
        }

        var runner = new WorkflowRunner(client);
        var results = await runner.RunAsync(args[1], cancellationToken);
        _console.WriteLine(JsonHelpers.ToPrettyJson(results));
        return 0;
    }

    private async Task<int> RunBatchAsync(BridgeClient client, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2 || args[0] != "run")
        {
            _console.ErrorLine("Usage: unity-cli batch run <file>");
            return 1;
        }

        var batch = JsonSerializer.Deserialize<BatchFile>(await File.ReadAllTextAsync(args[1], cancellationToken), JsonHelpers.SerializerOptions)
            ?? throw new InvalidOperationException($"Batch parse failed: {args[1]}");
        var results = new List<ToolCallResponse>();
        foreach (var call in batch.Calls)
        {
            results.Add(await client.CallToolAsync(call.Name, call.Arguments, cancellationToken));
        }

        _console.WriteLine(JsonHelpers.ToPrettyJson(results));
        return 0;
    }

    private async Task<int> RunToolAsync(BridgeClient client, GlobalOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.ErrorLine("Usage: unity-cli tool list | tool call <name> [key=value...]");
            return 1;
        }

        if (args[0] == "list")
        {
            var tools = await client.ListToolsAsync(cancellationToken);
            if (options.Json)
            {
                _console.WriteLine(JsonHelpers.ToPrettyJson(tools));
            }
            else
            {
                foreach (var tool in tools)
                {
                    var required = tool.RequiredArguments.Count > 0
                        ? $"  (required: {string.Join(", ", tool.RequiredArguments)})"
                        : string.Empty;
                    _console.WriteLine($"{tool.Name} :: {tool.Description}{required}");
                }
            }

            return 0;
        }

        if (args.Length >= 2 && args[0] == "call")
        {
            var toolName = args[1];
            var arguments = JsonHelpers.ParseKeyValuePairs(args.Skip(2));
            if (toolName == "tests.run")
            {
                return await RunTestsCommandAsync(client, options, arguments, cancellationToken);
            }

            if (toolName == "editor.compile")
            {
                return await RunCompileCommandAsync(client, options, arguments, cancellationToken);
            }

            if (toolName is "editor.play" or "editor.stop")
            {
                return await RunPlayModeCommandAsync(client, options, toolName, arguments, cancellationToken);
            }

            var response = await client.CallToolAsync(toolName, arguments, cancellationToken);
            return EmitResponse(response, response.Success, options);
        }

        _console.ErrorLine("Usage: unity-cli tool list | tool call <name> [key=value...]");
        return 1;
    }

    private async Task<int> RunResourceAsync(BridgeClient client, GlobalOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.ErrorLine("Usage: unity-cli resource list | resource get <name>");
            return 1;
        }

        if (args[0] == "list")
        {
            var resources = await client.ListResourcesAsync(cancellationToken);
            return EmitResponse(resources, true, options);
        }

        if (args.Length >= 2 && args[0] == "get")
        {
            var response = await client.GetResourceAsync(args[1], cancellationToken);
            return EmitResponse(response, true, options);
        }

        _console.ErrorLine("Usage: unity-cli resource list | resource get <name>");
        return 1;
    }

    private int EmitResponse(object payload, bool success, GlobalOptions options)
    {
        if (!string.IsNullOrEmpty(options.Field))
        {
            var node = JsonSerializer.SerializeToNode(payload, JsonHelpers.SerializerOptions);
            var scalar = JsonPathResolver.ResolveToScalar(node, options.Field!);
            if (scalar is null)
            {
                _console.ErrorLine($"Field not found: {options.Field}");
                return 2;
            }

            _console.WriteLine(scalar);
            return success ? 0 : 1;
        }

        if (!options.Quiet)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(payload));
        }

        return success ? 0 : 1;
    }

    private async Task<int> RunMappedToolCommandAsync(BridgeClient client, GlobalOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            _console.ErrorLine("Expected a command group and action. Example: unity-cli scene create path=Assets/Scenes/Main.unity");
            return 1;
        }

        var toolName = $"{args[0]}.{args[1]}";
        var parameters = JsonHelpers.ParseKeyValuePairs(args.Skip(2));
        if (toolName == "tests.run")
        {
            return await RunTestsCommandAsync(client, options, parameters, cancellationToken);
        }

        if (toolName == "editor.compile")
        {
            return await RunCompileCommandAsync(client, options, parameters, cancellationToken);
        }

        if (toolName is "editor.play" or "editor.stop")
        {
            return await RunPlayModeCommandAsync(client, options, toolName, parameters, cancellationToken);
        }

        var response = await client.CallToolAsync(toolName, parameters, cancellationToken);
        return EmitResponse(response, response.Success, options);
    }

    private async Task<int> RunTestsCommandAsync(BridgeClient client, GlobalOptions options, JsonObject arguments, CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken);
        var cursor = status.EventCursor;

        var startResponse = await client.CallToolAsync("tests.run", arguments, cancellationToken);
        if (!startResponse.Success)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(startResponse));
            return 1;
        }

        var runId = startResponse.Result?["runId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(runId))
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(startResponse));
            return 0;
        }

        if (startResponse.Events is { Count: > 0 })
        {
            var inlineCompleted = startResponse.Events
                .LastOrDefault(@event => @event.Type == "tests.completed"
                    && string.Equals(@event.Data?["runId"]?.GetValue<string>(), runId, StringComparison.OrdinalIgnoreCase));
            if (inlineCompleted != null)
            {
                return EmitTestResult(inlineCompleted);
            }
        }

        var timeoutMs = Math.Max(options.TimeoutMs, 60000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.PollEventsAsync(cursor, 1000, cancellationToken);
                cursor = response.Cursor;

                var completed = response.Events
                    .LastOrDefault(@event => @event.Type == "tests.completed"
                        && string.Equals(@event.Data?["runId"]?.GetValue<string>(), runId, StringComparison.OrdinalIgnoreCase));

                if (completed != null)
                {
                    return EmitTestResult(completed);
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for test completion for run '{runId}'.");
    }

    private int EmitTestResult(BridgeEvent completed)
    {
        var summary = completed.Data?["summary"];
        var failed = summary?["failed"]?.GetValue<int>() ?? 0;
        var finalResponse = new ToolCallResponse(
            failed == 0,
            completed.Message,
            summary,
            new[] { completed });
        _console.WriteLine(JsonHelpers.ToPrettyJson(finalResponse));
        return failed == 0 ? 0 : 1;
    }

    private async Task<int> RunCompileCommandAsync(BridgeClient client, GlobalOptions options, JsonObject arguments, CancellationToken cancellationToken)
    {
        var status = await client.GetStatusAsync(cancellationToken);
        var cursor = status.EventCursor;

        var startResponse = await client.CallToolAsync("editor.compile", arguments, cancellationToken);
        if (!startResponse.Success)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(startResponse));
            return 1;
        }

        var compilationId = startResponse.Result?["compilationId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(compilationId))
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(startResponse));
            return 0;
        }

        if (startResponse.Events is { Count: > 0 })
        {
            var inlineCompleted = startResponse.Events
                .LastOrDefault(@event => @event.Type == "editor.compiled"
                    && string.Equals(@event.Data?["compilationId"]?.GetValue<string>(), compilationId, StringComparison.OrdinalIgnoreCase));
            if (inlineCompleted != null)
            {
                return EmitCompileResult(inlineCompleted);
            }
        }

        var timeoutMs = Math.Max(options.TimeoutMs, 120000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await client.PollEventsAsync(cursor, 1000, cancellationToken);
                cursor = response.Cursor;

                var completed = response.Events
                    .LastOrDefault(@event => @event.Type == "editor.compiled"
                        && string.Equals(@event.Data?["compilationId"]?.GetValue<string>(), compilationId, StringComparison.OrdinalIgnoreCase));

                if (completed != null)
                {
                    return EmitCompileResult(completed);
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for script compilation completion for '{compilationId}'.");
    }

    private int EmitCompileResult(BridgeEvent completed)
    {
        var success = completed.Data?["success"]?.GetValue<bool>() ?? false;
        var finalResponse = new ToolCallResponse(
            success,
            completed.Message,
            completed.Data,
            new[] { completed });
        _console.WriteLine(JsonHelpers.ToPrettyJson(finalResponse));
        return success ? 0 : 1;
    }

    private async Task<int> RunPlayModeCommandAsync(BridgeClient client, GlobalOptions options, string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var startResponse = await client.CallToolAsync(toolName, arguments, cancellationToken);
        if (!startResponse.Success)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(startResponse));
            return 1;
        }

        var targetState = toolName == "editor.play";
        var timeoutMs = Math.Max(options.TimeoutMs, 30000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var state = await client.GetResourceAsync("editor/state", cancellationToken);
                var isPlaying = state.Data?["isPlaying"]?.GetValue<bool>() ?? false;
                var changing = state.Data?["isPlayingOrWillChangePlaymode"]?.GetValue<bool>() ?? false;
                var settled = targetState
                    ? isPlaying
                    : !isPlaying && !changing;

                if (settled)
                {
                    var finalResponse = new ToolCallResponse(
                        true,
                        targetState ? "Play mode entered." : "Play mode exited.",
                        state.Data,
                        startResponse.Events);
                    _console.WriteLine(JsonHelpers.ToPrettyJson(finalResponse));
                    return 0;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {(targetState ? "play mode entry" : "play mode exit")}.");
    }

    private static bool IsHelp(string command)
    {
        return command is "help" or "--help" or "-h";
    }

    private void PrintHelp()
    {
        _console.WriteLine("unity-cli");
        _console.WriteLine("  status");
        _console.WriteLine("  capabilities");
        _console.WriteLine("  doctor");
        _console.WriteLine("  tool list");
        _console.WriteLine("  tool call <tool> [key=value...]");
        _console.WriteLine("  resource list");
        _console.WriteLine("  resource get <name>");
        _console.WriteLine("  events tail [after=0] [waitMs=1000]");
        _console.WriteLine("  workflow run <file>");
        _console.WriteLine("  batch run <file>");
        _console.WriteLine("  mock serve [host=127.0.0.1] [port=52737]");
        _console.WriteLine("  scene|gameobject|component|material|asset|package|tests|console|menu|editor <action> [key=value...]");
        _console.WriteLine("global options:");
        _console.WriteLine("  --base-url=<url>  --json  --quiet  --field=<jsonpath>  --timeout-ms=<milliseconds>");
    }

    private static GlobalOptions ParseGlobalOptions(string[] args)
    {
        var options = new GlobalOptions
        {
            BaseUrl = InstanceRegistry.ResolveDefaultBaseUrl() ?? "http://127.0.0.1:52737",
        };
        var remaining = new List<string>();

        foreach (var arg in args)
        {
            if (arg.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
            {
                options.BaseUrl = arg["--base-url=".Length..];
            }
            else if (arg.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg["--timeout-ms=".Length..], out var timeout))
            {
                options.TimeoutMs = timeout;
            }
            else if (arg == "--json")
            {
                options.Json = true;
            }
            else if (arg == "--quiet")
            {
                options.Quiet = true;
            }
            else if (arg.StartsWith("--field=", StringComparison.OrdinalIgnoreCase))
            {
                options.Field = arg["--field=".Length..];
            }
            else
            {
                remaining.Add(arg);
            }
        }

        options.RemainingArgs = remaining;
        return options;
    }

    private sealed class GlobalOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:52737";

        public int TimeoutMs { get; set; } = 10000;

        public bool Json { get; set; }

        public string? Field { get; set; }

        public bool Quiet { get; set; }

        public List<string> RemainingArgs { get; set; } = [];
    }
}
