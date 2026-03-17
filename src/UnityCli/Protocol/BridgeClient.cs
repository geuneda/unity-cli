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
        return await GetJsonAsync<BridgeStatus>("health", cancellationToken);
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

    private async Task<T> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativePath, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<T>(JsonHelpers.SerializerOptions, cancellationToken))!;
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
