using System.Text.Json.Nodes;

namespace UnityCli.Protocol;

public sealed record BridgeStatus(
    string Name,
    string Version,
    string State,
    string EditorVersion,
    string? ProjectPath,
    long EventCursor,
    IReadOnlyList<string> Capabilities,
    string? SessionId = null);

public sealed record CapabilityResponse(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Events,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ToolDescriptor(
    string Name,
    string Category,
    string Description,
    IReadOnlyList<string> RequiredArguments,
    IReadOnlyList<string> OptionalArguments);

public sealed record ToolCallRequest(
    string Name,
    JsonObject Arguments,
    string? CorrelationId = null);

public sealed record ToolCallResponse(
    bool Success,
    string Message,
    JsonNode? Result,
    IReadOnlyList<BridgeEvent>? Events);

public sealed record BridgeEvent(
    long Cursor,
    string Type,
    string Message,
    DateTimeOffset Timestamp,
    JsonNode? Data);

public sealed record EventPollResponse(
    long Cursor,
    IReadOnlyList<BridgeEvent> Events);

public sealed record ResourceDescriptor(
    string Name,
    string Description);

public sealed record ResourceResponse(
    string Name,
    JsonNode? Data);

public sealed record BatchFile(
    IReadOnlyList<ToolCallRequest> Calls);

public sealed record WorkflowFile(
    IReadOnlyDictionary<string, string>? Variables,
    IReadOnlyList<WorkflowStep> Steps);

public sealed record WorkflowStep(
    string? Id,
    string? Call,
    JsonObject? Args,
    WorkflowWaitCondition? WaitFor,
    string? Note);

public sealed record WorkflowWaitCondition(
    string Type,
    string? Contains,
    int TimeoutMs = 2000);

public sealed record WorkflowStepResult(
    string Step,
    string Action,
    bool Success,
    JsonNode? Result,
    BridgeEvent? Event,
    string Message);
