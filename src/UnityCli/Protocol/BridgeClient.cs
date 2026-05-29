using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Support;

namespace UnityCli.Protocol;

public sealed class BridgeClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public BridgeClient(string baseUrl, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _ownsHttpClient = true;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = timeout;
    }

    public async Task<BridgeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await GetJsonAsync<BridgeStatus>("health", cancellationToken);
        if (!string.IsNullOrWhiteSpace(status.SessionId))
        {
            return status;
        }

        var recoveredSessionId = await TryRecoverSessionIdAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(recoveredSessionId)
            ? status
            : status with { SessionId = recoveredSessionId };
    }

    public async Task<CapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        return await GetJsonAsync<CapabilityResponse>("capabilities", cancellationToken);
    }

    public async Task<IReadOnlyList<ToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        return await GetJsonAsync<List<ToolDescriptor>>("tools", cancellationToken);
    }

    public async Task<IReadOnlyList<ResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken)
    {
        return await GetJsonAsync<List<ResourceDescriptor>>("resources", cancellationToken);
    }

    public async Task<ResourceResponse> GetResourceAsync(string name, CancellationToken cancellationToken)
    {
        return await GetJsonAsync<ResourceResponse>($"resources/{Uri.EscapeDataString(name)}", cancellationToken);
    }

    /// <summary>도구를 호출하고, 구조화된 응답 봉투가 있으면 HTTP 상태와 무관하게 파싱해 반환한다.</summary>
    /// <remarks>
    /// 비-2xx 응답이라도 <see cref="ToolCallResponse"/> 봉투(예: not_found 404)를 담고 있으면
    /// 예외 없이 그대로 반환하여, 종료 코드 정책은 CLI 계층이 <see cref="ToolCallResponse.Code"/> 로 결정한다.
    /// 봉투가 아닌 본문은 원문으로 예외를 던지고, 본문이 비어 있으면 전송 오류로 예외를 던진다.
    /// </remarks>
    /// <param name="toolName">호출할 도구 이름.</param>
    /// <param name="arguments">도구 인자.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>파싱한 <see cref="ToolCallResponse"/>.</returns>
    public async Task<ToolCallResponse> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new ToolCallRequest(toolName, arguments);
        using var response = await _httpClient.PostAsJsonAsync("tools/call", request, JsonHelpers.SerializerOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ToolCallResponse>(body, JsonHelpers.SerializerOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException(body);
        }

        response.EnsureSuccessStatusCode();
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
    }

    public async Task<EventPollResponse> PollEventsAsync(long after, int waitMs, CancellationToken cancellationToken)
    {
        return await GetJsonAsync<EventPollResponse>($"events?after={after}&waitMs={waitMs}", cancellationToken);
    }

    private async Task<string?> TryRecoverSessionIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var events = await PollEventsAsync(0, 0, cancellationToken);
            return events.Events
                .Where(@event => string.Equals(@event.Type, "bridge.started", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(@event => @event.Cursor)
                .Select(@event => @event.Data?["sessionId"]?.GetValue<string>())
                .FirstOrDefault(sessionId => !string.IsNullOrWhiteSpace(sessionId));
        }
        catch
        {
            return null;
        }
    }

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        return await ExecuteTransientGetAsync(async ct =>
        {
            using var response = await _httpClient.GetAsync(relativePath, ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(JsonHelpers.SerializerOptions, ct))!;
        }, cancellationToken);
    }

    private async Task<T> ExecuteTransientGetAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _httpClient.Timeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action(cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = exception;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("Unity bridge was temporarily unavailable and did not recover before the request timed out.", lastException);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var error = JsonSerializer.Deserialize<ToolCallResponse>(payload, JsonHelpers.SerializerOptions);
                if (error is not null && !string.IsNullOrWhiteSpace(error.Message))
                {
                    throw new InvalidOperationException(error.Message);
                }
            }
            catch (JsonException)
            {
            }

            throw new InvalidOperationException(payload);
        }

        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
