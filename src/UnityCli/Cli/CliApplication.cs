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

        GlobalOptions options;
        IReadOnlyList<string> command;
        try
        {
            options = ParseGlobalOptions(args);
            command = options.RemainingArgs;

            if (command.Count == 0)
            {
                PrintHelp();
                return 1;
            }

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
                case "logs":
                    return await RunLogsCommandAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "workflow":
                    return await RunWorkflowAsync(client, command.Skip(1).ToArray(), cancellationToken);
                case "batch":
                    return await RunBatchAsync(client, command.Skip(1).ToArray(), cancellationToken);
                case "tool":
                    return await RunToolAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "resource":
                    return await RunResourceAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "assert":
                    return await RunAssertAsync(client, options, command.Skip(1).ToArray(), cancellationToken);
                case "instances":
                    return await RunInstancesAsync(options, command.Skip(1).ToArray());
                default:
                    return await RunMappedToolCommandAsync(client, options, command, cancellationToken);
            }
        }
        catch (HttpRequestException exception)
        {
            _console.ErrorLine(exception.Message);
            return 3;
        }
        catch (InvalidOperationException exception) when (exception.InnerException is HttpRequestException or TaskCanceledException)
        {
            _console.ErrorLine(exception.Message);
            return 3;
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

    /// <summary>
    /// 콘솔 로그를 폴링하여 지정한 레벨/텍스트의 로그 출현을 기다린다.
    /// expectNone=true 이면 매칭 로그가 나타나면 실패(exit 1)로 간주한다.
    /// </summary>
    /// <param name="client">브리지 클라이언트.</param>
    /// <param name="options">전역 옵션(--quiet/--timeout-ms 반영).</param>
    /// <param name="args">하위 명령 인자(wait + level/contains/timeoutMs/expectNone).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>종료 코드. 기대대로면 0, 예상치 못한 결과면 1.</returns>
    private async Task<int> RunLogsCommandAsync(BridgeClient client, GlobalOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "wait")
        {
            _console.ErrorLine("Usage: unity-cli logs wait [level=Error] [contains=<text>] [timeoutMs=<ms>] [expectNone=false]");
            return 1;
        }

        var kv = JsonHelpers.ParseKeyValuePairs(args.Skip(1));
        var level = kv["level"]?.GetValue<string>() ?? "Error";
        var contains = kv["contains"]?.GetValue<string>();
        var timeoutMs = (int)(kv["timeoutMs"]?.GetValue<long>() ?? Math.Max(options.TimeoutMs, 5000));
        var expectNone = kv["expectNone"]?.GetValue<bool>() ?? false;

        // Seed at cursor 0 so an already-buffered matching log in this scenario is caught,
        // then tail from the returned cursor. This makes expectNone deterministic.
        long cursor = 0;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            EventPollResponse response;
            try
            {
                response = await client.PollEventsAsync(cursor, 1000, cancellationToken);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            cursor = response.Cursor;
            var match = response.Events.FirstOrDefault(@event =>
                @event.Type == "console.log"
                && string.Equals(@event.Data?["level"]?.GetValue<string>(), level, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(contains)
                    || (@event.Message?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (@event.Data?["stackTrace"]?.GetValue<string>()?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false)));
            if (match != null)
            {
                var payload = new ToolCallResponse(!expectNone, expectNone ? $"Unexpected {level} log appeared." : $"{level} log observed.", match.Data, new[] { match });
                if (!options.Quiet)
                {
                    _console.WriteLine(JsonHelpers.ToPrettyJson(payload));
                }

                return expectNone ? 1 : 0;
            }

            await Task.Delay(250, cancellationToken);
        }

        var timeoutPayload = new ToolCallResponse(expectNone, expectNone ? $"No {level} log appeared." : $"Timed out waiting for a {level} log.", null, Array.Empty<BridgeEvent>());
        if (!options.Quiet)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(timeoutPayload));
        }

        return expectNone ? 0 : 1;
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
            return EmitToolResponse(response, options);
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

    private const string AssertUsage = "Usage: unity-cli assert <resource|tool|event> [selector...] path=<jsonpath> <equals|contains|exists|gt|lt|matches>=<value>";

    /// <summary>
    /// 리소스/도구/이벤트 응답의 경로 값을 연산자와 기대값으로 검증한다.
    /// 통과 시 exit 0, 불일치(또는 대상 도구 호출 실패) 시 exit 1, 인자 오류 시 exit 2 를 반환한다.
    /// </summary>
    /// <param name="client">브리지 클라이언트.</param>
    /// <param name="options">전역 옵션(--json/--quiet/--timeout-ms 반영).</param>
    /// <param name="args">assert 하위 인자(source, selector, path=, op=value).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>종료 코드. 통과 0, 실패 1, 인자 오류 2.</returns>
    private async Task<int> RunAssertAsync(BridgeClient client, GlobalOptions options, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _console.ErrorLine(AssertUsage);
            return 2;
        }

        var source = args[0];
        if (source is not ("resource" or "tool" or "event"))
        {
            _console.ErrorLine($"Unknown assert source '{source}'. {AssertUsage}");
            return 2;
        }

        string? pathValue = null;
        string? expectedValue = null;
        var hasOp = false;
        var op = AssertEvaluator.AssertOp.Exists;
        var selectors = new List<string>();
        var extraArgs = new List<string>();

        foreach (var token in args.Skip(1))
        {
            var splitIndex = token.IndexOf('=');
            if (splitIndex > 0)
            {
                var key = token[..splitIndex];
                var value = token[(splitIndex + 1)..];
                if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                {
                    pathValue = value;
                    continue;
                }

                if (!hasOp && AssertEvaluator.TryParseOp(key, out var parsedOp))
                {
                    op = parsedOp;
                    expectedValue = value;
                    hasOp = true;
                    continue;
                }

                extraArgs.Add(token);
                continue;
            }

            if (!hasOp && AssertEvaluator.TryParseOp(token, out var bareOp))
            {
                op = bareOp;
                hasOp = true;
                continue;
            }

            selectors.Add(token);
        }

        if (string.IsNullOrEmpty(pathValue))
        {
            _console.ErrorLine($"Missing path= token. {AssertUsage}");
            return 2;
        }

        if (!hasOp)
        {
            _console.ErrorLine($"Missing or unknown operator token. {AssertUsage}");
            return 2;
        }

        JsonNode? root;
        switch (source)
        {
            case "resource":
            {
                if (selectors.Count == 0)
                {
                    _console.ErrorLine($"Missing resource name. {AssertUsage}");
                    return 2;
                }

                var resource = await client.GetResourceAsync(selectors[0], cancellationToken);
                root = JsonSerializer.SerializeToNode(resource, JsonHelpers.SerializerOptions);
                break;
            }

            case "tool":
            {
                var toolName = ResolveAssertToolName(selectors);
                if (toolName is null)
                {
                    _console.ErrorLine($"Missing tool name. {AssertUsage}");
                    return 2;
                }

                var arguments = JsonHelpers.ParseKeyValuePairs(extraArgs);
                var response = await client.CallToolAsync(toolName, arguments, cancellationToken);
                if (!response.Success)
                {
                    if (!options.Quiet)
                    {
                        _console.WriteLine(response.Message);
                    }

                    return 1;
                }

                root = JsonSerializer.SerializeToNode(response, JsonHelpers.SerializerOptions);
                break;
            }

            default:
            {
                var kv = JsonHelpers.ParseKeyValuePairs(selectors.Concat(extraArgs));
                var after = kv["after"]?.GetValue<long>() ?? 0;
                var waitMs = (int)(kv["waitMs"]?.GetValue<long>() ?? Math.Min(options.TimeoutMs, 1000));
                var response = await client.PollEventsAsync(after, waitMs, cancellationToken);
                root = JsonSerializer.SerializeToNode(response, JsonHelpers.SerializerOptions);
                break;
            }
        }

        var result = AssertEvaluator.Evaluate(root, pathValue, op, expectedValue);
        EmitAssertResult(result, pathValue, op, expectedValue, options);
        return result.Passed ? 0 : 1;
    }

    /// <summary>assert tool 의 셀렉터 토큰에서 도구 이름을 해석한다(단일 점 표기 또는 그룹.액션 두 토큰).</summary>
    /// <param name="selectors">셀렉터 토큰 목록.</param>
    /// <returns>도구 이름. 해석 실패 시 null.</returns>
    private static string? ResolveAssertToolName(IReadOnlyList<string> selectors)
    {
        if (selectors.Count == 1 && selectors[0].Contains('.'))
        {
            return selectors[0];
        }

        if (selectors.Count >= 2)
        {
            return $"{selectors[0]}.{selectors[1]}";
        }

        return null;
    }

    /// <summary>assert 평가 결과를 --json 또는 한 줄 [PASS]/[FAIL] 형식으로 출력한다(--quiet 시 출력 생략).</summary>
    /// <param name="result">평가 결과.</param>
    /// <param name="path">평가 경로.</param>
    /// <param name="op">비교 연산자.</param>
    /// <param name="expected">기대값.</param>
    /// <param name="options">전역 옵션.</param>
    private void EmitAssertResult(AssertEvaluator.AssertResult result, string path, AssertEvaluator.AssertOp op, string? expected, GlobalOptions options)
    {
        var opToken = op.ToString().ToLowerInvariant();
        if (options.Json)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(new
            {
                passed = result.Passed,
                path,
                op = opToken,
                expected,
                actual = result.Actual,
            }));
            return;
        }

        if (options.Quiet)
        {
            return;
        }

        var status = result.Passed ? "[PASS]" : "[FAIL]";
        _console.WriteLine($"{status} {path} {opToken} {expected} (actual: {result.Actual ?? "<null>"})");
    }

    /// <summary>instances list: 등록된 Unity 인스턴스 목록을 출력한다. exit 0.</summary>
    /// <param name="options">전역 옵션(--json/--quiet/--field 반영).</param>
    /// <param name="args">하위 명령 인자(list 만 지원).</param>
    /// <returns>종료 코드. 성공 0, 잘못된 하위 명령 1.</returns>
    private Task<int> RunInstancesAsync(GlobalOptions options, string[] args)
    {
        if (args.Length == 0 || args[0] != "list")
        {
            _console.ErrorLine("Usage: unity-cli instances list");
            return Task.FromResult(1);
        }

        var instances = InstanceRegistry.ListInstances();
        return Task.FromResult(EmitResponse(instances, true, options));
    }

    /// <summary>도구 응답을 출력하고, <see cref="ToolCallResponse.Code"/> 에 따라 실패 종료 코드(1/2)를 결정한다.</summary>
    /// <param name="response">도구 호출 응답.</param>
    /// <param name="options">전역 옵션(--strict 등).</param>
    /// <returns>종료 코드. 성공 0, 인자/경로 오류 2, 그 외 도구 실패 1.</returns>
    private int EmitToolResponse(ToolCallResponse response, GlobalOptions options)
    {
        var failureExitCode = (response.Code is "missing_arg" or "bad_arg") ? 2
            : (options.Strict && response.Code == "unknown_tool") ? 2
            : 1;
        return EmitResponse(response, response.Success, options, failureExitCode);
    }

    private int EmitResponse(object payload, bool success, GlobalOptions options, int failureExitCode = 1)
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
            return success ? 0 : failureExitCode;
        }

        if (!options.Quiet)
        {
            _console.WriteLine(JsonHelpers.ToPrettyJson(payload));
        }

        return success ? 0 : failureExitCode;
    }

    private async Task<int> RunMappedToolCommandAsync(BridgeClient client, GlobalOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            _console.ErrorLine("Expected a command group and action. Example: unity-cli scene create path=Assets/Scenes/Main.unity");
            return 2;
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
        return EmitToolResponse(response, options);
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
        _console.WriteLine("  assert resource <name> path=<p> <equals|contains|exists|gt|lt|matches>=<value>");
        _console.WriteLine("  assert tool <tool> [key=value...] path=<p> <op>=<value>");
        _console.WriteLine("  assert event [after=0] [waitMs=1000] path=<p> <op>=<value>");
        _console.WriteLine("  instances list");
        _console.WriteLine("  events tail [after=0] [waitMs=1000]");
        _console.WriteLine("  logs wait [level=Error] [contains=] [timeoutMs=] [expectNone=false]");
        _console.WriteLine("  workflow run <file>");
        _console.WriteLine("  batch run <file>");
        _console.WriteLine("  mock serve [host=127.0.0.1] [port=52737]");
        _console.WriteLine("  scene|gameobject|component|material|asset|package|project|tests|console|menu|editor <action> [key=value...]");
        _console.WriteLine("  values=<json> supports arrays, nested structs, and scene refs, e.g. values={\"_target\":{\"__ref\":\"name:Player\"}}");
        _console.WriteLine("global options:");
        _console.WriteLine("  --base-url=<url>  --project=<name>  --instance=<project:port>  --json  --quiet  --strict  --field=<jsonpath>  --timeout-ms=<milliseconds>");
    }

    private static GlobalOptions ParseGlobalOptions(string[] args)
    {
        var options = new GlobalOptions
        {
            BaseUrl = ResolveFallbackBaseUrl(),
        };
        var remaining = new List<string>();
        var baseUrlExplicit = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--base-url=", StringComparison.OrdinalIgnoreCase))
            {
                options.BaseUrl = arg["--base-url=".Length..];
                baseUrlExplicit = true;
            }
            else if (arg.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
            {
                options.Project = arg["--project=".Length..];
            }
            else if (arg.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
            {
                options.Instance = arg["--instance=".Length..];
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
            else if (arg == "--strict")
            {
                options.Strict = true;
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

        if (!baseUrlExplicit && (options.Instance != null || options.Project != null))
        {
            options.BaseUrl = InstanceRegistry.ResolveBaseUrl(options.Instance, options.Project)
                ?? throw new InvalidOperationException($"No registered Unity instance matches selector (instance='{options.Instance}', project='{options.Project}').");
        }

        options.RemainingArgs = remaining;
        return options;
    }

    /// <summary>
    /// --base-url 플래그와 --project/--instance 셀렉터가 없을 때 사용할 base-url 을 우선순위에 따라 결정한다.
    /// UNITY_CLI_BASE_URL 환경변수 > instances.json 의 default 별칭 > 기본값 http://127.0.0.1:52737.
    /// </summary>
    /// <returns>해석된 base-url.</returns>
    private static string ResolveFallbackBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("UNITY_CLI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env!;
        }

        return InstanceRegistry.ResolveDefaultBaseUrl() ?? "http://127.0.0.1:52737";
    }

    private sealed class GlobalOptions
    {
        public string BaseUrl { get; set; } = "http://127.0.0.1:52737";

        public int TimeoutMs { get; set; } = 10000;

        public bool Json { get; set; }

        public string? Field { get; set; }

        public bool Quiet { get; set; }

        public bool Strict { get; set; }

        public string? Project { get; set; }

        public string? Instance { get; set; }

        public List<string> RemainingArgs { get; set; } = [];
    }
}
