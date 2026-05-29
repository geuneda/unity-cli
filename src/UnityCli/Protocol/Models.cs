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
    IReadOnlyList<BridgeEvent>? Events,
    /// <summary>실패 시 안정적 오류 코드(not_found/missing_arg/unknown_tool 등), 성공 시 null.</summary>
    string? Code = null);

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
    string? Note,
    /// <summary>단계 호출 결과에 대한 검증 조건(실패 시 워크플로 중단).</summary>
    WorkflowAssertCondition? Assert = null,
    /// <summary>단계 결과에서 변수로 캡처할 매핑({변수명: jsonpath}). 이후 단계의 ${변수} 치환에 사용.</summary>
    JsonObject? Capture = null,
    /// <summary>단계 호출 재시도 정책(일시적 실패 흡수). null 이면 단일 시도.</summary>
    WorkflowRetry? Retry = null,
    /// <summary>단계 실행 전 조건. 평가가 false 면 단계를 건너뛴다(실패 아님).</summary>
    WorkflowCondition? When = null);

/// <summary>워크플로 단계 재시도 정책. 컴파일/플레이 전환 레이스 같은 일시적 실패를 흡수한다.</summary>
/// <param name="MaxAttempts">총 시도 횟수. 1 이면 재시도 없음(하위 호환 기본값).</param>
/// <param name="DelayMs">시도 사이 대기(ms). 기본 0.</param>
public sealed record WorkflowRetry(
    int MaxAttempts = 1,
    int DelayMs = 0);

/// <summary>단계 실행 전 조건 평가. <see cref="Op"/>/<see cref="Expected"/> 로 평가가 false 면 단계를 건너뛴다(실패가 아님).</summary>
/// <param name="Resource">평가 소스로 조회할 리소스 이름(예: editor/state). <see cref="FromVar"/> 와 상호 배타적.</param>
/// <param name="FromVar"><see cref="WorkflowFile.Variables"/> 또는 캡처 변수에서 가져올 변수 이름. <see cref="Resource"/> 와 상호 배타적.</param>
/// <param name="Path">소스에서 평가할 경로(예: data.isPlaying). 빈 문자열이면 소스 전체.</param>
/// <param name="Op">비교 연산자(equals|contains|exists|gt|lt|matches).</param>
/// <param name="Expected">기대 스칼라값(exists 는 무시).</param>
public sealed record WorkflowCondition(
    string? Resource,
    string? FromVar,
    string Path,
    string Op,
    string? Expected = null);

public sealed record WorkflowWaitCondition(
    /// <summary>대기할 이벤트 타입. 리소스 폴링 대기에서는 null.</summary>
    string? Type = null,
    string? Contains = null,
    int TimeoutMs = 2000,
    /// <summary>리소스 폴링 대기 시 조회할 리소스 이름(예: editor/state).</summary>
    string? Resource = null,
    /// <summary>리소스 응답에서 평가할 경로(예: data.isPlaying).</summary>
    string? Path = null,
    /// <summary>리소스 폴링 비교 연산자(equals|contains|exists|gt|lt|matches).</summary>
    string? Op = null,
    /// <summary>리소스 폴링 기대값(exists 는 무시).</summary>
    string? Expected = null,
    /// <summary>리소스 폴링 간격(ms). 기본 500.</summary>
    int PollMs = 500);

/// <summary>워크플로 단계 검증 조건. 단계 호출 결과(Result)에 대해 <see cref="Path"/>/<see cref="Op"/>/<see cref="Expected"/> 로 평가한다.</summary>
/// <param name="Path">검증할 경로(예: result.name).</param>
/// <param name="Op">비교 연산자(equals|contains|exists|gt|lt|matches).</param>
/// <param name="Expected">기대값(exists 는 무시).</param>
public sealed record WorkflowAssertCondition(
    string Path,
    string Op,
    string? Expected = null);

public sealed record WorkflowStepResult(
    string Step,
    string Action,
    bool Success,
    JsonNode? Result,
    BridgeEvent? Event,
    string Message);
