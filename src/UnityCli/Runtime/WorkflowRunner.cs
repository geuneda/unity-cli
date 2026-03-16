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
        long cursor = 0;

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
                results.Add(new WorkflowStepResult(stepName, step.Call!, response.Success, response.Result, null, response.Message));
                if (!response.Success)
                {
                    throw new InvalidOperationException($"Workflow step failed: {stepName} - {response.Message}");
                }
            }

            if (step.WaitFor is not null)
            {
                var foundEvent = await WaitForEventAsync(step.WaitFor, cursor, cancellationToken);
                cursor = Math.Max(cursor, foundEvent.Cursor);
                results.Add(new WorkflowStepResult(stepName, "wait", true, null, foundEvent, foundEvent.Message));
            }
        }

        return results;
    }

    private async Task<BridgeEvent> WaitForEventAsync(WorkflowWaitCondition waitCondition, long after, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromMilliseconds(waitCondition.TimeoutMs))
        {
            var response = await _client.PollEventsAsync(after, 250, cancellationToken);
            after = response.Cursor;
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
