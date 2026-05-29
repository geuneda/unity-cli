using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Protocol;
using UnityCli.Support;

namespace UnityCli.Runtime;

public sealed class WorkflowRunner
{
    private readonly BridgeClient _client;

    public WorkflowRunner(BridgeClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<WorkflowStepResult>> RunAsync(string filePath, CancellationToken cancellationToken)
    {
        var workflow = JsonSerializer.Deserialize<WorkflowFile>(await File.ReadAllTextAsync(filePath, cancellationToken), JsonHelpers.SerializerOptions)
            ?? throw new InvalidOperationException($"Workflow parse failed: {filePath}");

        var variables = new Dictionary<string, string>(workflow.Variables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var results = new List<WorkflowStepResult>();
        var observedEvents = new List<BridgeEvent>();
        long cursor = (await _client.GetStatusAsync(cancellationToken)).EventCursor;

        foreach (var step in workflow.Steps)
        {
            var stepName = step.Id ?? step.Call ?? step.WaitFor?.Type ?? step.WaitFor?.Resource ?? "step";
            if (step.When is not null && !await ShouldRunStepAsync(step.When, variables, cancellationToken))
            {
                results.Add(new WorkflowStepResult(stepName, "skip", true, JsonValue.Create("condition false"), null, "Skipped: condition not met."));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(step.Note))
            {
                results.Add(new WorkflowStepResult(stepName, "note", true, JsonValue.Create(step.Note), null, step.Note!));
            }

            if (!string.IsNullOrWhiteSpace(step.Call))
            {
                var arguments = JsonHelpers.EnsureObject(JsonHelpers.ReplaceVariables(step.Args, variables));
                var response = await CallWithRetryAsync(step.Call!, arguments, step.Retry, cancellationToken);
                if (response.Events is { Count: > 0 })
                {
                    observedEvents.AddRange(response.Events);
                }

                results.Add(new WorkflowStepResult(stepName, step.Call!, response.Success, response.Result, null, response.Message));
                if (!response.Success)
                {
                    throw new InvalidOperationException($"Workflow step failed: {stepName} - {response.Message}");
                }

                // Capture/assert paths are evaluated against the full response envelope
                // (e.g. result.name), matching CLI `assert tool` and the --field selector.
                var responseRoot = JsonSerializer.SerializeToNode(response, JsonHelpers.SerializerOptions);
                CaptureVariables(step, stepName, responseRoot, variables);
                EvaluateAssert(step, stepName, responseRoot, results);
            }

            if (step.WaitFor is not null)
            {
                if (!string.IsNullOrWhiteSpace(step.WaitFor.Resource))
                {
                    var resourceResult = await WaitForResourceAsync(step.WaitFor, cancellationToken);
                    results.Add(new WorkflowStepResult(stepName, "wait-resource", true, resourceResult, null, $"Resource '{step.WaitFor.Resource}' matched '{step.WaitFor.Path}'."));
                }
                else
                {
                    var foundEvent = await WaitForEventAsync(step.WaitFor, cursor, observedEvents, cancellationToken);
                    cursor = Math.Max(cursor, foundEvent.Cursor);
                    results.Add(new WorkflowStepResult(stepName, "wait", true, null, foundEvent, foundEvent.Message));
                }
            }
        }

        return results;
    }

    /// <summary>단계의 <see cref="WorkflowStep.When"/> 조건을 평가하여 단계를 실행할지(true) 건너뛸지(false) 결정한다. 리소스 또는 변수에서 소스를 얻어 <see cref="AssertEvaluator"/> 로 평가한다.</summary>
    /// <param name="when">평가할 조건(Resource 또는 FromVar 중 하나, Path/Op/Expected).</param>
    /// <param name="variables">FromVar 해석에 사용할 변수 집합.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>조건이 만족되어 단계를 실행해야 하면 true, 건너뛰어야 하면 false.</returns>
    private async Task<bool> ShouldRunStepAsync(WorkflowCondition when, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        if (!AssertEvaluator.TryParseOp(when.Op, out var op))
        {
            throw new InvalidOperationException($"Workflow when has unknown op '{when.Op}'.");
        }

        JsonNode? source;
        if (!string.IsNullOrWhiteSpace(when.Resource))
        {
            var resource = await _client.GetResourceAsync(when.Resource!, cancellationToken);
            source = JsonSerializer.SerializeToNode(resource, JsonHelpers.SerializerOptions);
        }
        else if (!string.IsNullOrWhiteSpace(when.FromVar) && variables.TryGetValue(when.FromVar!, out var raw))
        {
            source = JsonHelpers.ConvertString(raw);
        }
        else
        {
            source = null;
        }

        return AssertEvaluator.Evaluate(source, when.Path, op, when.Expected).Passed;
    }

    /// <summary>단계 호출을 재시도 정책에 따라 실행한다. 성공하면 즉시 반환하고, 실패 응답이면 정책 횟수만큼 재시도한 뒤 마지막 응답을 반환한다(호출자가 실패를 던진다).</summary>
    /// <param name="call">호출할 도구 이름.</param>
    /// <param name="arguments">치환이 끝난 도구 인자.</param>
    /// <param name="retry">재시도 정책(null 이면 단일 시도).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>마지막 호출 응답. 모든 시도가 전송 오류면 예외를 던진다.</returns>
    private async Task<ToolCallResponse> CallWithRetryAsync(string call, JsonObject arguments, WorkflowRetry? retry, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, retry?.MaxAttempts ?? 1);
        var delayMs = Math.Max(0, retry?.DelayMs ?? 0);
        ToolCallResponse? last = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                last = await _client.CallToolAsync(call, arguments, cancellationToken);
                if (last.Success)
                {
                    return last;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            if (attempt < maxAttempts && delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        if (last is not null)
        {
            return last;
        }

        throw new InvalidOperationException($"Workflow step call failed after {maxAttempts} attempt(s): {call} - {lastError?.Message}");
    }

    /// <summary>단계의 <see cref="WorkflowStep.Capture"/> 매핑({변수명: jsonpath})을 응답 봉투에서 해석해 변수 집합에 추가한다. 경로 해석 실패 시 예외로 빠르게 실패한다.</summary>
    /// <param name="step">현재 단계.</param>
    /// <param name="stepName">단계 식별 이름(오류 메시지용).</param>
    /// <param name="responseRoot">직렬화된 응답 봉투(result.* 경로 기준).</param>
    /// <param name="variables">이후 단계 치환에 사용할 변수 집합(가변).</param>
    private static void CaptureVariables(WorkflowStep step, string stepName, JsonNode? responseRoot, Dictionary<string, string> variables)
    {
        if (step.Capture is null)
        {
            return;
        }

        foreach (var pair in step.Capture)
        {
            var jsonPath = pair.Value?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(jsonPath))
            {
                throw new InvalidOperationException($"Workflow capture for '{pair.Key}' is missing a jsonpath at step '{stepName}'.");
            }

            var captured = JsonPathResolver.ResolveToScalar(responseRoot, jsonPath);
            if (captured is null)
            {
                throw new InvalidOperationException($"Workflow capture '{pair.Key}' resolved no value at path '{jsonPath}' (step '{stepName}').");
            }

            variables[pair.Key] = captured;
        }
    }

    /// <summary>단계의 <see cref="WorkflowStep.Assert"/> 조건을 응답 봉투에 대해 평가한다. 실패 시 예외로 워크플로를 중단한다.</summary>
    /// <param name="step">현재 단계.</param>
    /// <param name="stepName">단계 식별 이름(오류 메시지용).</param>
    /// <param name="responseRoot">직렬화된 응답 봉투(result.* 경로 기준).</param>
    /// <param name="results">단계 결과 누적 목록(통과 시 기록).</param>
    private static void EvaluateAssert(WorkflowStep step, string stepName, JsonNode? responseRoot, List<WorkflowStepResult> results)
    {
        if (step.Assert is null)
        {
            return;
        }

        if (!AssertEvaluator.TryParseOp(step.Assert.Op, out var op))
        {
            throw new InvalidOperationException($"Workflow assert at step '{stepName}' has unknown op '{step.Assert.Op}'.");
        }

        var result = AssertEvaluator.Evaluate(responseRoot, step.Assert.Path, op, step.Assert.Expected);
        if (!result.Passed)
        {
            var detail = string.IsNullOrEmpty(result.Detail) ? string.Empty : $" ({result.Detail})";
            throw new InvalidOperationException($"Workflow assert failed at step '{stepName}': {step.Assert.Path} {step.Assert.Op} {step.Assert.Expected} (actual: {result.Actual ?? "<null>"}){detail}");
        }

        results.Add(new WorkflowStepResult(stepName, "assert", true, JsonValue.Create(result.Actual), null, $"{step.Assert.Path} {step.Assert.Op} {step.Assert.Expected}"));
    }

    /// <summary>리소스를 주기적으로 조회하여 경로/연산자/기대값 조건이 만족될 때까지 대기한다(에디터 상태 폴링의 워크플로 일반화).</summary>
    /// <param name="waitCondition">리소스 폴링 대기 조건(Resource/Path/Op/Expected/PollMs/TimeoutMs).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>조건을 만족시킨 시점의 리소스 데이터.</returns>
    private async Task<JsonNode?> WaitForResourceAsync(WorkflowWaitCondition waitCondition, CancellationToken cancellationToken)
    {
        if (!AssertEvaluator.TryParseOp(waitCondition.Op ?? "exists", out var op))
        {
            throw new InvalidOperationException($"Workflow waitFor.resource has unknown op '{waitCondition.Op}'.");
        }

        var path = waitCondition.Path ?? string.Empty;
        var pollMs = Math.Max(waitCondition.PollMs, 50);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(waitCondition.TimeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var resource = await _client.GetResourceAsync(waitCondition.Resource!, cancellationToken);
                var root = JsonSerializer.SerializeToNode(resource, JsonHelpers.SerializerOptions);
                var result = AssertEvaluator.Evaluate(root, path, op, waitCondition.Expected);
                if (result.Passed)
                {
                    return resource.Data;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(pollMs, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for resource '{waitCondition.Resource}' to satisfy '{path} {waitCondition.Op} {waitCondition.Expected}'.");
    }

    private async Task<BridgeEvent> WaitForEventAsync(WorkflowWaitCondition waitCondition, long after, List<BridgeEvent> observedEvents, CancellationToken cancellationToken)
    {
        var bufferedMatch = observedEvents.FirstOrDefault(e =>
            e.Cursor > after
            && string.Equals(e.Type, waitCondition.Type, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(waitCondition.Contains) || e.Message.Contains(waitCondition.Contains, StringComparison.OrdinalIgnoreCase)));
        if (bufferedMatch is not null)
        {
            return bufferedMatch;
        }

        if (observedEvents.Count > 0)
        {
            after = Math.Max(after, observedEvents[^1].Cursor);
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromMilliseconds(waitCondition.TimeoutMs))
        {
            var response = await _client.PollEventsAsync(after, 250, cancellationToken);
            if (response.Cursor < after)
            {
                after = 0;
                response = await _client.PollEventsAsync(after, 250, cancellationToken);
            }

            after = response.Cursor;
            if (response.Events.Count > 0)
            {
                observedEvents.AddRange(response.Events);
            }

            var match = response.Events.FirstOrDefault(e =>
                string.Equals(e.Type, waitCondition.Type, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(waitCondition.Contains) || e.Message.Contains(waitCondition.Contains, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException($"Timed out waiting for event '{waitCondition.Type}'.");
    }
}
