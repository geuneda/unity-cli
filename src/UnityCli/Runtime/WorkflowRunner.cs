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

        var variables = workflow.Variables ?? new Dictionary<string, string>();
        var results = new List<WorkflowStepResult>();
        var observedEvents = new List<BridgeEvent>();
        long cursor = (await _client.GetStatusAsync(cancellationToken)).EventCursor;

        foreach (var step in workflow.Steps)
        {
            var stepName = step.Id ?? step.Call ?? step.WaitFor?.Type ?? "step";
            if (!string.IsNullOrWhiteSpace(step.Note))
            {
                results.Add(new WorkflowStepResult(stepName, "note", true, JsonValue.Create(step.Note), null, step.Note!));
            }

            if (!string.IsNullOrWhiteSpace(step.Call))
            {
                var arguments = JsonHelpers.EnsureObject(JsonHelpers.ReplaceVariables(step.Args, variables));
                var response = await _client.CallToolAsync(step.Call!, arguments, cancellationToken);
                if (response.Events is { Count: > 0 })
                {
                    observedEvents.AddRange(response.Events);
                }

                results.Add(new WorkflowStepResult(stepName, step.Call!, response.Success, response.Result, null, response.Message));
                if (!response.Success)
                {
                    throw new InvalidOperationException($"Workflow step failed: {stepName} - {response.Message}");
                }
            }

            if (step.WaitFor is not null)
            {
                var foundEvent = await WaitForEventAsync(step.WaitFor, cursor, observedEvents, cancellationToken);
                cursor = Math.Max(cursor, foundEvent.Cursor);
                results.Add(new WorkflowStepResult(stepName, "wait", true, null, foundEvent, foundEvent.Message));
            }
        }

        return results;
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
