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

    public async Task<ToolCallResponse> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new ToolCallRequest(toolName, arguments);
        using var response = await _httpClient.PostAsJsonAsync("tools/call", request, JsonHelpers.SerializerOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ToolCallResponse>(JsonHelpers.SerializerOptions, cancellationToken))!;
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
