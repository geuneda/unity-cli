using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Protocol;
using UnityCli.Support;

namespace UnityCli.Tests;

public sealed class BridgeClientTests
{
    private const string BaseUrl = "http://localhost:9999";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    // -- GetStatusAsync --

    [Fact]
    public async Task GetStatusAsync_ReturnsStatus_WhenHealthResponseIncludesSessionId()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 42, new[] { "tools" }, "session-abc");
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("session-abc", result.SessionId);
        Assert.Equal("test-bridge", result.Name);
        Assert.Equal("ready", result.State);
    }

    [Fact]
    public async Task GetStatusAsync_RecoversSessionId_FromEventsWhenHealthResponseHasNoSessionId()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 42, new[] { "tools" }, null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        var eventData = new JsonObject { ["sessionId"] = "recovered-session" };
        var bridgeEvent = new BridgeEvent(10, "bridge.started", "started", DateTimeOffset.UtcNow, eventData);
        var eventPollResponse = new EventPollResponse(10, new[] { bridgeEvent });
        handler.Enqueue(JsonResponse(eventPollResponse));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("recovered-session", result.SessionId);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNullSessionId_WhenEventsHaveNoBridgeStarted()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 42, new[] { "tools" }, null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        var eventPollResponse = new EventPollResponse(0, Array.Empty<BridgeEvent>());
        handler.Enqueue(JsonResponse(eventPollResponse));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Null(result.SessionId);
    }

    [Fact]
    public async Task GetStatusAsync_RecoversLatestSessionId_WhenMultipleBridgeStartedEvents()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 42, new[] { "tools" }, null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        var oldEventData = new JsonObject { ["sessionId"] = "old-session" };
        var newEventData = new JsonObject { ["sessionId"] = "new-session" };
        var events = new[]
        {
            new BridgeEvent(5, "bridge.started", "started", DateTimeOffset.UtcNow.AddMinutes(-1), oldEventData),
            new BridgeEvent(15, "bridge.started", "started", DateTimeOffset.UtcNow, newEventData),
        };
        var eventPollResponse = new EventPollResponse(15, events);
        handler.Enqueue(JsonResponse(eventPollResponse));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("new-session", result.SessionId);
    }

    // -- CallToolAsync --

    [Fact]
    public async Task CallToolAsync_SendsCorrectRequest_AndReturnsResponse()
    {
        var expectedResponse = new ToolCallResponse(true, "done", JsonValue.Create("ok"), null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(expectedResponse));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var args = new JsonObject { ["key"] = "value" };
        var result = await client.CallToolAsync("test-tool", args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done", result.Message);

        var sentRequest = handler.SentRequests[0];
        Assert.Equal(HttpMethod.Post, sentRequest.Method);
        Assert.EndsWith("tools/call", sentRequest.RequestUri!.AbsoluteUri);

        var body = await sentRequest.Content!.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<JsonObject>(body, JsonHelpers.SerializerOptions);
        Assert.Equal("test-tool", parsed!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallToolAsync_ThrowsWithErrorMessage_WhenServerReturnsErrorWithToolCallResponseBody()
    {
        var errorResponse = new ToolCallResponse(false, "Tool not found", null, null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(errorResponse, HttpStatusCode.BadRequest));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var args = new JsonObject();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("missing-tool", args, CancellationToken.None));

        Assert.Equal("Tool not found", ex.Message);
    }

    [Fact]
    public async Task CallToolAsync_DoesNotRetry_OnHttpRequestException()
    {
        var handler = new MockHandler();
        handler.Enqueue(_ => throw new HttpRequestException("Connection refused"));

        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(1), handler);

        var args = new JsonObject();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CallToolAsync("some-tool", args, CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
    }

    // -- PollEventsAsync --

    [Fact]
    public async Task PollEventsAsync_FormatsUrlWithAfterAndWaitMsParameters()
    {
        var response = new EventPollResponse(100, Array.Empty<BridgeEvent>());
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(response));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.PollEventsAsync(42, 500, CancellationToken.None);

        Assert.Equal(100, result.Cursor);
        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.Contains("events?after=42&waitMs=500", sentUri);
    }

    // -- ListToolsAsync --

    [Fact]
    public async Task ListToolsAsync_ReturnsToolDescriptors()
    {
        var tools = new List<ToolDescriptor>
        {
            new("scene.create", "scene", "Creates a scene", new[] { "path" }, Array.Empty<string>()),
            new("gameobject.create", "gameobject", "Creates a gameobject", new[] { "name" }, new[] { "parent" }),
        };
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(tools));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("scene.create", result[0].Name);
        Assert.Equal("gameobject.create", result[1].Name);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.EndsWith("tools", sentUri);
    }

    // -- ListResourcesAsync --

    [Fact]
    public async Task ListResourcesAsync_ReturnsResourceDescriptors()
    {
        var resources = new List<ResourceDescriptor>
        {
            new("scene/hierarchy", "Scene hierarchy"),
            new("console/logs", "Console logs"),
        };
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(resources));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.ListResourcesAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("scene/hierarchy", result[0].Name);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.EndsWith("resources", sentUri);
    }

    // -- GetResourceAsync --

    [Fact]
    public async Task GetResourceAsync_EscapesResourceNameInUrl()
    {
        var response = new ResourceResponse("scene/hierarchy", JsonValue.Create("data"));
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(response));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetResourceAsync("scene/hierarchy", CancellationToken.None);

        Assert.Equal("scene/hierarchy", result.Name);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.Contains("resources/scene%2Fhierarchy", sentUri);
    }

    [Fact]
    public async Task GetResourceAsync_EscapesSpecialCharactersInName()
    {
        var response = new ResourceResponse("my&resource", null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(response));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        await client.GetResourceAsync("my&resource", CancellationToken.None);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        // Uri.EscapeDataString encodes '&' as '%26'
        Assert.Contains("resources/my%26resource", sentUri);
    }

    // -- GetCapabilitiesAsync --

    [Fact]
    public async Task GetCapabilitiesAsync_ReturnsCapabilities()
    {
        var capabilities = new CapabilityResponse(
            new[] { "scene.create" },
            new[] { "scene/hierarchy" },
            new[] { "bridge.started" },
            new Dictionary<string, string> { ["version"] = "1.0" });
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(capabilities));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var result = await client.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Single(result.Tools);
        Assert.Single(result.Resources);
        Assert.Equal("1.0", result.Metadata["version"]);
    }

    // -- Transient retry (ExecuteTransientGetAsync) --

    [Fact]
    public async Task TransientRetry_RetriesOnHttpRequestException_ThenSucceeds()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 0, Array.Empty<string>(), "s1");
        var handler = new MockHandler();
        handler.Enqueue(_ => throw new HttpRequestException("Connection refused"));
        handler.Enqueue(_ => throw new HttpRequestException("Connection refused"));
        handler.Enqueue(JsonResponse(status));

        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(10), handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("test-bridge", result.Name);
        Assert.True(handler.CallCount >= 3, $"Expected at least 3 calls, got {handler.CallCount}");
    }

    [Fact]
    public async Task TransientRetry_RetriesOnTaskCanceledException_WhenNotUserCancellation()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 0, Array.Empty<string>(), "s1");
        var handler = new MockHandler();
        handler.Enqueue(_ => throw new TaskCanceledException("Request timed out"));
        handler.Enqueue(JsonResponse(status));

        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(10), handler);

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("test-bridge", result.Name);
        Assert.True(handler.CallCount >= 2, $"Expected at least 2 calls, got {handler.CallCount}");
    }

    [Fact]
    public async Task TransientRetry_ThrowsInvalidOperationException_WhenTimeoutExceeded()
    {
        var handler = new MockHandler();
        handler.EnqueueForever(_ => throw new HttpRequestException("Connection refused"));

        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(1), handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetStatusAsync(CancellationToken.None));

        Assert.Contains("timed out", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task TransientRetry_ThrowsTaskCanceledException_WhenUserCancels()
    {
        var handler = new MockHandler();
        handler.EnqueueForever(_ => throw new HttpRequestException("Connection refused"));

        using var cts = new CancellationTokenSource();
        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(30), handler);

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStatusAsync(cts.Token));
    }

    [Fact]
    public async Task TransientRetry_SucceedsAfterTransientFailure()
    {
        var tools = new List<ToolDescriptor>
        {
            new("test.tool", "test", "A test tool", Array.Empty<string>(), Array.Empty<string>()),
        };
        var handler = new MockHandler();
        handler.Enqueue(_ => throw new HttpRequestException("Transient"));
        handler.Enqueue(JsonResponse(tools));

        using var client = new BridgeClient(BaseUrl, TimeSpan.FromSeconds(10), handler);

        var result = await client.ListToolsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("test.tool", result[0].Name);
    }

    // -- EnsureSuccessAsync --

    [Fact]
    public async Task EnsureSuccess_ThrowsWithMessage_WhenErrorBodyIsToolCallResponse()
    {
        var errorBody = new ToolCallResponse(false, "Specific error message", null, null);
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(errorBody, HttpStatusCode.InternalServerError));

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("failing-tool", new JsonObject(), CancellationToken.None));

        Assert.Equal("Specific error message", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccess_ThrowsWithRawText_WhenErrorBodyIsPlainText()
    {
        var handler = new MockHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.Content = new StringContent("Something went wrong on the server", Encoding.UTF8, "text/plain");
            return response;
        });

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("failing-tool", new JsonObject(), CancellationToken.None));

        Assert.Equal("Something went wrong on the server", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccess_ThrowsHttpRequestException_WhenErrorBodyIsEmpty()
    {
        var handler = new MockHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.Content = new StringContent("", Encoding.UTF8, "text/plain");
            return response;
        });

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CallToolAsync("failing-tool", new JsonObject(), CancellationToken.None));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccess_ThrowsWithRawText_WhenBodyIsInvalidJson()
    {
        var handler = new MockHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
            response.Content = new StringContent("{not valid json!!!", Encoding.UTF8, "application/json");
            return response;
        });

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("failing-tool", new JsonObject(), CancellationToken.None));

        Assert.Equal("{not valid json!!!", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccess_ThrowsWithRawText_WhenToolCallResponseHasNoMessage()
    {
        var handler = new MockHandler();
        var json = JsonSerializer.Serialize(
            new { success = false, message = "", result = (object?)null },
            JsonHelpers.SerializerOptions);
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return response;
        });

        using var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("failing-tool", new JsonObject(), CancellationToken.None));

        Assert.Contains("success", ex.Message);
    }

    // -- Dispose --

    [Fact]
    public void Dispose_DisposesOwnedHttpClient()
    {
        var handler = new MockHandler();
        var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        client.Dispose();

        var ex = Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await client.GetStatusAsync(CancellationToken.None);
        });

        Assert.NotNull(ex);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var handler = new MockHandler();
        var client = new BridgeClient(BaseUrl, DefaultTimeout, handler);

        client.Dispose();
        client.Dispose();
    }

    // -- BaseUrl normalization --

    [Fact]
    public async Task Constructor_NormalizesBaseUrl_WithTrailingSlash()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 0, Array.Empty<string>(), "s1");
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        using var client = new BridgeClient("http://localhost:9999/", DefaultTimeout, handler);

        await client.GetStatusAsync(CancellationToken.None);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.StartsWith("http://localhost:9999/", sentUri);
        Assert.DoesNotContain("//health", sentUri);
    }

    [Fact]
    public async Task Constructor_NormalizesBaseUrl_WithoutTrailingSlash()
    {
        var status = new BridgeStatus(
            "test-bridge", "1.0.0", "ready", "6000.3",
            "/project", 0, Array.Empty<string>(), "s1");
        var handler = new MockHandler();
        handler.Enqueue(JsonResponse(status));

        using var client = new BridgeClient("http://localhost:9999", DefaultTimeout, handler);

        await client.GetStatusAsync(CancellationToken.None);

        var sentUri = handler.SentRequests[0].RequestUri!.ToString();
        Assert.StartsWith("http://localhost:9999/", sentUri);
    }

    // -- Helpers --

    private static Func<HttpRequestMessage, HttpResponseMessage> JsonResponse<T>(T body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return _ =>
        {
            var json = JsonSerializer.Serialize(body, JsonHelpers.SerializerOptions);
            var response = new HttpResponseMessage(statusCode);
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return response;
        };
    }

    /// <summary>
    /// A test HTTP handler that returns pre-configured responses in order.
    /// </summary>
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        private Func<HttpRequestMessage, HttpResponseMessage>? _foreverResponse;
        private readonly List<HttpRequestMessage> _sentRequests = new();
        private int _callCount;

        public IReadOnlyList<HttpRequestMessage> SentRequests => _sentRequests;
        public int CallCount => _callCount;

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responses.Enqueue(factory);
        }

        public void EnqueueForever(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _foreverResponse = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _sentRequests.Add(request);

            if (_responses.Count > 0)
            {
                var factory = _responses.Dequeue();
                return Task.FromResult(factory(request));
            }

            if (_foreverResponse is not null)
            {
                return Task.FromResult(_foreverResponse(request));
            }

            throw new InvalidOperationException(
                $"MockHandler has no more queued responses. Unexpected request: {request.Method} {request.RequestUri}");
        }
    }
}
