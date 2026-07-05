using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Protocol;
using UnityCli.Support;

namespace UnityCli.Runtime;

public sealed class MockUnityBridgeServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HttpListener _listener = new();
    private readonly List<BridgeEvent> _events = [];
    private readonly List<SceneState> _scenes = [];
    private readonly Dictionary<string, GameObjectState> _gameObjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialState> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UiElementState> _uiElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PackageState> _packages = [];
    private readonly List<LogEntry> _logs = [];
    private readonly List<TestCaseState> _tests = [];
    private TaskCompletionSource<bool> _eventSignal = NewSignal();
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private long _cursor;
    private string? _activeScenePath;
    private string? _selectedObjectId;
    private string? _focusedUiElementName;
    private int _gameViewWidth = 1440;
    private int _gameViewHeight = 3040;
    private bool _playMode;
    private bool _pauseMode;
    private int _flakyCalls;
    private JsonObject? _lastTestRun;

    public MockUnityBridgeServer()
    {
        Seed();
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public async Task StartAsync(string host = "127.0.0.1", int port = 52737, CancellationToken cancellationToken = default)
    {
        if (_serverTask is not null)
        {
            return;
        }

        BaseUrl = $"http://{host}:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), cancellationToken);
        Emit("mock.started", $"Mock bridge listening on {BaseUrl}", new JsonObject { ["baseUrl"] = BaseUrl });
        await WaitUntilReadyAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch
            {
            }
        }

        _listener.Close();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;
            var method = context.Request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "health")
            {
                await WriteJsonAsync(context, BuildStatus());
                return;
            }

            if (method == "GET" && path == "capabilities")
            {
                await WriteJsonAsync(context, BuildCapabilities());
                return;
            }

            if (method == "GET" && path == "tools")
            {
                await WriteJsonAsync(context, ToolCatalog());
                return;
            }

            if (method == "GET" && path == "resources")
            {
                await WriteJsonAsync(context, ResourceCatalog());
                return;
            }

            if (method == "GET" && path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
            {
                var resourceName = Uri.UnescapeDataString(path["resources/".Length..]);
                await WriteJsonAsync(context, BuildResource(resourceName));
                return;
            }

            if (method == "GET" && path == "events")
            {
                var after = TryParseInt(context.Request.QueryString["after"]);
                var waitMs = TryParseInt(context.Request.QueryString["waitMs"], 0);
                var response = await PollEventsAsync(after, waitMs, cancellationToken);
                await WriteJsonAsync(context, response);
                return;
            }

            if (method == "POST" && path == "tools/call")
            {
                var request = await JsonSerializer.DeserializeAsync<ToolCallRequest>(context.Request.InputStream, JsonHelpers.SerializerOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Request body missing.");
                var response = await ExecuteToolAsync(request, cancellationToken);
                await WriteJsonAsync(context, response);
                return;
            }

            context.Response.StatusCode = 404;
            await WriteTextAsync(context, "not found");
        }
        catch (MockBridgeException exception)
        {
            context.Response.StatusCode = exception.Status;
            await WriteJsonAsync(context, new ToolCallResponse(false, exception.Message, null, null, exception.Code));
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new ToolCallResponse(false, exception.Message, null, null, "internal_error"));
        }
    }

    private async Task<EventPollResponse> PollEventsAsync(long after, int waitMs, CancellationToken cancellationToken)
    {
        var immediate = SnapshotEvents(after);
        if (immediate.Events.Count > 0 || waitMs <= 0)
        {
            return immediate;
        }

        Task waitTask;
        lock (_gate)
        {
            waitTask = _eventSignal.Task;
        }

        await Task.WhenAny(waitTask, Task.Delay(waitMs, cancellationToken));
        return SnapshotEvents(after);
    }

    private EventPollResponse SnapshotEvents(long after)
    {
        lock (_gate)
        {
            var matches = _events.Where(e => e.Cursor > after).ToList();
            return new EventPollResponse(_cursor, matches);
        }
    }

    private async Task<ToolCallResponse> ExecuteToolAsync(ToolCallRequest request, CancellationToken cancellationToken)
    {
        var args = request.Arguments;

        return request.Name switch
        {
            "scene.create" => Success("Scene created.", CreateScene(args)),
            "scene.load" => Success("Scene loaded.", LoadScene(args)),
            "scene.save" => Success("Scene saved.", SaveScene(args)),
            "scene.info" => Success("Scene info fetched.", SceneInfo(args)),
            "scene.delete" => Success("Scene deleted.", DeleteScene(args)),
            "scene.unload" => Success("Scene unloaded.", UnloadScene(args)),
            "scene.open-additive" => Success("Scene opened additively.", OpenSceneAdditive(args)),
            "scene.set-active" => Success("Active scene set.", SetActiveScene(args)),
            "scene.list-loaded" => Success("Loaded scenes listed.", ListLoadedScenes()),
            "scene.set-lighting" => Success("Scene lighting set.", SetSceneLighting(args)),
            "scene.bake-navmesh" => Success("NavMesh baked.", BakeNavMesh()),
            "gameobject.create" => Success("GameObject created.", CreateGameObject(args)),
            "gameobject.get" => Success("GameObject fetched.", GetGameObject(args)),
            "gameobject.delete" => Success("GameObject deleted.", DeleteGameObject(args)),
            "gameobject.duplicate" => Success("GameObject duplicated.", DuplicateGameObject(args)),
            "gameobject.reparent" => Success("GameObject reparented.", ReparentGameObject(args)),
            "gameobject.move" => Success("GameObject moved.", UpdateTransform(args, "move")),
            "gameobject.rotate" => Success("GameObject rotated.", UpdateTransform(args, "rotate")),
            "gameobject.scale" => Success("GameObject scaled.", UpdateTransform(args, "scale")),
            "gameobject.set-transform" => Success("GameObject transform updated.", UpdateTransform(args, "all")),
            "gameobject.select" => Success("GameObject selected.", SelectGameObject(args)),
            "gameobject.find" => Success("GameObjects found.", FindGameObjects(args)),
            "gameobject.set-properties" => Success("GameObject properties set.", SetGameObjectProperties(args)),
            "component.update" => UpdateComponentResponse(args),
            "component.list" => Success("Components listed.", ListComponents(args)),
            "component.get" => GetComponentResponse(args),
            "component.add" => AddComponentResponse(args),
            "component.remove" => RemoveComponentResponse(args),
            "material.create" => Success("Material created.", CreateMaterial(args)),
            "material.assign" => Success("Material assigned.", AssignMaterial(args)),
            "material.modify" => Success("Material modified.", ModifyMaterial(args)),
            "material.info" => Success("Material info fetched.", MaterialInfo(args)),
            "asset.list" => Success("Assets listed.", ListAssets(args)),
            "asset.add-to-scene" => Success("Asset added to scene.", AddAssetToScene(args)),
            "asset.set-addressable" => Success("Addressable set.", SetAddressable(args)),
            "asset.remove-addressable" => Success("Addressable removed.", RemoveAddressable(args)),
            "package.list" => Success("Packages listed.", ListPackages()),
            "package.add" => await AddPackageAsync(args, cancellationToken),
            "tests.list" => Success("Tests listed.", ListTests(args)),
            "tests.run" => await RunTestsAsync(args, cancellationToken),
            "console.get" => Success("Logs fetched.", GetLogs(args)),
            "console.clear" => Success("Logs cleared.", ClearLogs()),
            "console.send" => Success("Log emitted.", EmitConsoleLog(args)),
            "console.logs" => Success("Console logs queried.", QueryConsoleLogs(args)),
            "menu.execute" => Success("Menu command executed.", ExecuteMenu(args)),
            "project.add-tag" => Success("Tag added.", AddProjectTag(args)),
            "project.add-layer" => Success("Layer added.", AddProjectLayer(args)),
            "project.list-tags-layers" => Success("Tags and layers listed.", ListTagsAndLayers()),
            "sprite.create" => Success("Sprite created.", CreateSprite(args)),
            "sprite.set" => Success("SpriteRenderer updated.", SetSpriteRenderer(args)),
            "ui.canvas.create" => Success("Canvas created.", CreateUiElement(args, "Canvas")),
            "ui.button.create" => Success("Button created.", CreateUiElement(args, "Button")),
            "ui.toggle.create" => Success("Toggle created.", CreateUiElement(args, "Toggle")),
            "ui.slider.create" => Success("Slider created.", CreateUiElement(args, "Slider")),
            "ui.scrollrect.create" => Success("ScrollRect created.", CreateUiElement(args, "ScrollRect")),
            "ui.inputfield.create" => Success("InputField created.", CreateUiElement(args, "InputField")),
            "ui.text.create" => Success("Text created.", CreateUiElement(args, "Text")),
            "ui.image.create" => Success("Image created.", CreateUiElement(args, "Image")),
            "ui.panel.create" => Success("Panel created.", CreateUiElement(args, "Panel")),
            "ui.layout.add" => Success("Layout added.", AddLayout(args)),
            "ui.recttransform.modify" => Success("RectTransform modified.", ModifyRectTransform(args)),
            "ui.screenshot.capture" => Success("Screenshot captured.", CaptureScreenshot(args)),
            "ui.toggle.set" => Success("Toggle set.", SetToggle(args)),
            "ui.slider.set" => Success("Slider set.", SetSlider(args)),
            "ui.scrollrect.set" => Success("ScrollRect set.", SetScrollRect(args)),
            "ui.inputfield.set-text" => Success("InputField text set.", SetInputFieldText(args)),
            "ui.focus" => Success("Focused.", FocusUiElement(args)),
            "ui.blur" => Success("Blurred.", BlurUiElement()),
            "ui.click" => Success("Clicked.", ClickUiElement(args)),
            "ui.double-click" => Success("Double-clicked.", DoubleClickUi(args)),
            "ui.long-press" => Success("Long-pressed.", LongPressUi(args)),
            "ui.drag" => Success("Dragged.", DragUi(args)),
            "ui.swipe" => Success("Swiped.", SwipeUi(args)),
            "input.tap" => Success("Tapped.", InputTap(args)),
            "input.double-tap" => Success("Double-tapped.", InputDoubleTap(args)),
            "input.long-press" => Success("Long-pressed.", InputLongPress(args)),
            "input.drag" => Success("Dragged.", InputDrag(args)),
            "input.swipe" => Success("Swiped.", InputSwipe(args)),
            "asset.import-texture" => Success("Texture imported.", ImportTexture(args)),
            "asset.manage" => ManageAsset(args),
            "asset.create-scriptableobject" => Success("ScriptableObject created.", CreateScriptableObjectAsset(args)),
            "scriptableobject.get" => Success("ScriptableObject fetched.", GetScriptableObject(args)),
            "scriptableobject.list" => Success("ScriptableObjects listed.", ListScriptableObjects(args)),
            "editor.compile" => await CompileAsync(args, cancellationToken),
            "editor.play" => Success("Entered play mode.", SetPlayMode(true)),
            "editor.stop" => Success("Exited play mode.", SetPlayMode(false)),
            "editor.pause" => Success("Pause toggled.", TogglePause(args)),
            "editor.refresh" => Success("Editor refreshed.", RefreshEditor()),
            "editor.gameview.resize" => Success("GameView resized.", ResizeGameView(args)),
            "prefab.create" => Success("Prefab created.", CreatePrefabAsset(args)),
            "prefab.instantiate" => Success("Prefab instantiated.", InstantiatePrefabAsset(args)),
            "prefab.apply" => Success("Prefab overrides applied.", ApplyPrefabOverrides(args)),
            "prefab.unpack" => Success("Prefab unpacked.", UnpackPrefabInstance(args)),
            "mock.flaky" => FlakyResponse(args),
            _ => new ToolCallResponse(false, $"Unsupported tool '{request.Name}'.", null, null, "unknown_tool"),
        };
    }

    private ToolCallResponse Success(string message, JsonNode? result, IReadOnlyList<BridgeEvent>? events = null)
    {
        return new ToolCallResponse(true, message, result, events);
    }

    /// <summary>지정 횟수(failuresBeforeSuccess)만큼 실패한 뒤 성공하는 테스트 전용 도구. 워크플로 재시도 검증용이며 광고 목록(ToolCatalog/capabilities)에 노출하지 않는다.</summary>
    /// <param name="args">failuresBeforeSuccess(기본 1): 성공 전까지 실패할 횟수.</param>
    /// <returns>누적 호출 횟수가 임계치를 넘으면 성공, 아니면 실패 응답.</returns>
    private ToolCallResponse FlakyResponse(JsonObject args)
    {
        var failuresBeforeSuccess = (int)(args["failuresBeforeSuccess"]?.GetValue<long>() ?? 1);
        var attempt = System.Threading.Interlocked.Increment(ref _flakyCalls);
        return attempt > failuresBeforeSuccess
            ? Success($"Flaky succeeded on attempt {attempt}.", new JsonObject { ["attempt"] = attempt })
            : new ToolCallResponse(false, $"Flaky failure {attempt}.", null, null, "flaky");
    }

    /// <summary>실제 브리지의 BridgeException 을 모사하여 코드와 HTTP 상태를 가지는 계약 오류.</summary>
    private sealed class MockBridgeException : Exception
    {
        /// <summary>오류 코드, HTTP 상태, 메시지로 계약 오류를 생성한다.</summary>
        /// <param name="code">안정적 오류 코드.</param>
        /// <param name="status">매핑할 HTTP 상태 코드.</param>
        /// <param name="message">오류 메시지.</param>
        public MockBridgeException(string code, int status, string message) : base(message)
        {
            Code = code;
            Status = status;
        }

        /// <summary>안정적 오류 코드(not_found, missing_arg 등).</summary>
        public string Code { get; }

        /// <summary>이 오류에 매핑할 HTTP 상태 코드.</summary>
        public int Status { get; }
    }

    private JsonNode CreateScene(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Scenes/Untitled.unity");
        var name = GetString(args, "name", Path.GetFileNameWithoutExtension(path));

        lock (_gate)
        {
            var existing = _scenes.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SceneState(path, name, true, false);
                _scenes.Add(existing);
            }
            else
            {
                existing.IsLoaded = true;
            }

            _activeScenePath = path;
        }

        Emit("scene.changed", $"Scene created: {path}", new JsonObject { ["path"] = path, ["name"] = name, ["action"] = "create" });
        return SceneObject(path);
    }

    private JsonNode LoadScene(JsonObject args)
    {
        var path = GetString(args, "path", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        lock (_gate)
        {
            var existing = _scenes.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SceneState(path, Path.GetFileNameWithoutExtension(path), true, false);
                _scenes.Add(existing);
            }

            existing.IsLoaded = true;
            _activeScenePath = path;
        }

        Emit("scene.loaded", $"Scene loaded: {path}", new JsonObject { ["path"] = path });
        return SceneObject(path);
    }

    private JsonNode SaveScene(JsonObject args)
    {
        var path = GetString(args, "path", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        lock (_gate)
        {
            var scene = RequireScene(path);
            scene.IsDirty = false;
        }

        Emit("scene.saved", $"Scene saved: {path}", new JsonObject { ["path"] = path });
        return SceneObject(path);
    }

    private JsonNode SceneInfo(JsonObject args)
    {
        var path = GetString(args, "path", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        return SceneObject(path);
    }

    private JsonNode DeleteScene(JsonObject args)
    {
        if (args["path"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "path is required.");
        }

        var path = GetString(args, "path", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        lock (_gate)
        {
            var scene = RequireScene(path);
            _scenes.Remove(scene);
            if (string.Equals(_activeScenePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeScenePath = _scenes.FirstOrDefault(x => x.IsLoaded)?.Path;
            }
        }

        Emit("scene.changed", $"Scene deleted: {path}", new JsonObject { ["path"] = path, ["action"] = "delete" });
        return new JsonObject { ["deleted"] = path };
    }

    private JsonNode UnloadScene(JsonObject args)
    {
        var path = GetString(args, "path", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        lock (_gate)
        {
            var scene = RequireScene(path);
            scene.IsLoaded = false;
            if (string.Equals(_activeScenePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeScenePath = _scenes.FirstOrDefault(x => x.IsLoaded)?.Path;
            }
        }

        Emit("scene.unloaded", $"Scene unloaded: {path}", new JsonObject { ["path"] = path });
        return SceneObject(path);
    }

    /// <summary>scene.open-additive 를 모사한다. 미존재 씬은 추가하고 로드 상태로 만든다.</summary>
    /// <param name="args">씬 path 인자를 담은 JSON 오브젝트.</param>
    /// <returns>로드된 씬 요약(SceneObject), path 누락 시 missing_arg 예외.</returns>
    private JsonNode OpenSceneAdditive(JsonObject args)
    {
        if (args["path"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "path is required.");
        }

        var path = GetString(args, "path", "Assets/Scenes/Additive.unity");
        lock (_gate)
        {
            var existing = _scenes.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SceneState(path, Path.GetFileNameWithoutExtension(path), true, false);
                _scenes.Add(existing);
            }

            existing.IsLoaded = true;
            _activeScenePath ??= path;
        }

        Emit("scene.loaded", $"Scene opened additively: {path}", new JsonObject { ["path"] = path });
        return SceneObject(path);
    }

    /// <summary>scene.set-active 를 모사한다. 로드되지 않은 씬은 not_found 로 거부한다.</summary>
    /// <param name="args">씬 path 인자를 담은 JSON 오브젝트.</param>
    /// <returns>활성으로 설정된 씬 요약(SceneObject).</returns>
    private JsonNode SetActiveScene(JsonObject args)
    {
        if (args["path"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "path is required.");
        }

        var path = GetString(args, "path", string.Empty);
        lock (_gate)
        {
            var scene = RequireScene(path);
            if (!scene.IsLoaded)
            {
                throw new MockBridgeException("not_found", 404, $"Loaded scene not found: {path}");
            }

            _activeScenePath = path;
        }

        Emit("scene.changed", $"Active scene set: {path}", new JsonObject { ["path"] = path });
        return SceneObject(path);
    }

    /// <summary>scene.list-loaded 를 모사한다. 로드된 씬 목록과 활성 씬 경로를 반환한다.</summary>
    /// <returns>scenes 배열, count, activeScenePath 를 담은 JSON 오브젝트.</returns>
    private JsonNode ListLoadedScenes()
    {
        string[] loadedPaths;
        string? active;
        lock (_gate)
        {
            loadedPaths = _scenes.Where(x => x.IsLoaded).Select(x => x.Path).ToArray();
            active = _activeScenePath;
        }

        var scenes = new JsonArray(loadedPaths.Select(p => (JsonNode?)SceneObject(p)).ToArray());
        return new JsonObject
        {
            ["scenes"] = scenes,
            ["count"] = loadedPaths.Length,
            ["activeScenePath"] = active,
        };
    }

    private JsonNode CreateGameObject(JsonObject args)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = GetString(args, "name", "GameObject");
        var parentId = GetNullableString(args, "parentId");
        var scenePath = GetString(args, "scenePath", _activeScenePath ?? "Assets/Scenes/SampleScene.unity");
        var state = new GameObjectState(id, name, parentId, scenePath)
        {
            Primitive = GetNullableString(args, "primitive"),
        };

        if (args["position"] is JsonArray position)
        {
            state.Position = ToVector(position, state.Position);
        }

        if (args["scale"] is JsonArray scale)
        {
            state.Scale = ToVector(scale, state.Scale);
        }

        lock (_gate)
        {
            _gameObjects[id] = state;
            RequireScene(scenePath).IsDirty = true;
        }

        Emit("hierarchy.changed", $"GameObject created: {name}", new JsonObject { ["id"] = id, ["name"] = name, ["scenePath"] = scenePath });
        return GameObjectObject(state);
    }

    private JsonNode GetGameObject(JsonObject args)
    {
        return GameObjectObject(ResolveGameObject(args));
    }

    private JsonNode DeleteGameObject(JsonObject args)
    {
        var state = ResolveGameObject(args);
        lock (_gate)
        {
            _gameObjects.Remove(state.Id);
        }

        Emit("hierarchy.changed", $"GameObject deleted: {state.Name}", new JsonObject { ["id"] = state.Id, ["name"] = state.Name, ["action"] = "delete" });
        return new JsonObject { ["deleted"] = state.Id };
    }

    private JsonNode DuplicateGameObject(JsonObject args)
    {
        var source = ResolveGameObject(args);
        var duplicate = source.Clone();
        duplicate.Id = Guid.NewGuid().ToString("N");
        duplicate.Name = GetString(args, "name", source.Name + " Copy");

        lock (_gate)
        {
            _gameObjects[duplicate.Id] = duplicate;
        }

        Emit("hierarchy.changed", $"GameObject duplicated: {duplicate.Name}", new JsonObject { ["id"] = duplicate.Id, ["sourceId"] = source.Id });
        return GameObjectObject(duplicate);
    }

    private JsonNode ReparentGameObject(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var parentId = GetNullableString(args, "parentId");
        lock (_gate)
        {
            state.ParentId = parentId;
        }

        Emit("hierarchy.changed", $"GameObject reparented: {state.Name}", new JsonObject { ["id"] = state.Id, ["parentId"] = parentId });
        return GameObjectObject(state);
    }

    private JsonNode UpdateTransform(JsonObject args, string mode)
    {
        var state = ResolveGameObject(args);
        lock (_gate)
        {
            if (mode is "move" or "all")
            {
                state.Position = ToVector(args["position"] as JsonArray, state.Position);
            }

            if (mode is "rotate" or "all")
            {
                state.Rotation = ToVector(args["rotation"] as JsonArray, state.Rotation);
            }

            if (mode is "scale" or "all")
            {
                state.Scale = ToVector(args["scale"] as JsonArray, state.Scale);
            }
        }

        Emit("transform.changed", $"Transform changed: {state.Name}", new JsonObject { ["id"] = state.Id, ["mode"] = mode });
        return GameObjectObject(state);
    }

    private JsonNode SelectGameObject(JsonObject args)
    {
        var state = ResolveGameObject(args);
        lock (_gate)
        {
            _selectedObjectId = state.Id;
        }

        Emit("selection.changed", $"Selected: {state.Name}", new JsonObject { ["id"] = state.Id });
        return GameObjectObject(state);
    }

    /// <summary>component.update 목 핸들러. 컴포넌트가 없으면 생성하고 values 를 병합하며 applied/skipped 를 함께 반환한다.</summary>
    private ToolCallResponse UpdateComponentResponse(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var type = GetString(args, "type", "Transform");
        var values = JsonHelpers.EnsureObject(JsonHelpers.ReplaceVariables(args["values"], new Dictionary<string, string>()));
        lock (_gate)
        {
            if (!state.Components.TryGetValue(type, out var existing))
            {
                existing = new JsonObject();
                state.Components[type] = existing;
            }

            var applied = new JsonArray();
            foreach (var pair in values)
            {
                existing[pair.Key] = JsonHelpers.DeepClone(pair.Value);
                applied.Add(pair.Key);
            }

            Emit("component.changed", $"Component updated: {state.Name}/{type}", new JsonObject { ["id"] = state.Id, ["type"] = type });
            return Success("Component updated.", new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Name,
                ["type"] = type,
                ["applied"] = applied,
                ["skipped"] = new JsonArray(),
            });
        }
    }

    private JsonNode ListComponents(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var includeValues = args["includeValues"] is not null && bool.TryParse(args["includeValues"]!.ToString(), out var iv) && iv;
        var components = new JsonArray();
        lock (_gate)
        {
            foreach (var pair in state.Components)
            {
                var entry = new JsonObject
                {
                    ["type"] = pair.Key,
                    ["fullType"] = pair.Key,
                    ["enabled"] = true,
                };
                if (includeValues)
                {
                    entry["properties"] = JsonHelpers.DeepClone(pair.Value);
                }

                components.Add(entry);
            }

            return new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Name,
                ["count"] = state.Components.Count,
                ["components"] = components,
            };
        }
    }

    private ToolCallResponse GetComponentResponse(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var type = GetString(args, "type", string.Empty);
        lock (_gate)
        {
            if (string.IsNullOrEmpty(type) || !state.Components.TryGetValue(type, out var values))
            {
                return new ToolCallResponse(false, $"Component '{type}' not found on '{state.Name}'.", null, null, "not_found");
            }

            return Success("Component read.", new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Name,
                ["type"] = type,
                ["properties"] = JsonHelpers.DeepClone(values),
            });
        }
    }

    private ToolCallResponse AddComponentResponse(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var type = GetString(args, "type", string.Empty);
        if (string.IsNullOrEmpty(type))
        {
            return new ToolCallResponse(false, "type is required.", null, null, "missing_arg");
        }

        var allowDuplicate = args["allowDuplicate"] is not null && bool.TryParse(args["allowDuplicate"]!.ToString(), out var ad) && ad;
        lock (_gate)
        {
            if (!allowDuplicate && state.Components.ContainsKey(type))
            {
                return new ToolCallResponse(false, $"Component already exists: {type}. Pass allowDuplicate=true to add another.", null, null);
            }

            var values = JsonHelpers.EnsureObject(args["values"]);
            state.Components[type] = values;
            var applied = new JsonArray();
            foreach (var pair in values)
            {
                applied.Add(pair.Key);
            }

            Emit("component.changed", $"Component added: {state.Name}/{type}", new JsonObject { ["id"] = state.Id, ["type"] = type });
            return Success("Component added.", new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Name,
                ["type"] = type,
                ["applied"] = applied,
                ["skipped"] = new JsonArray(),
            });
        }
    }

    private ToolCallResponse RemoveComponentResponse(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var type = GetString(args, "type", string.Empty);
        if (string.Equals(type, "Transform", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolCallResponse(false, "Transform/RectTransform cannot be removed.", null, null);
        }

        lock (_gate)
        {
            if (!state.Components.Remove(type))
            {
                return new ToolCallResponse(false, $"Component '{type}' not found on '{state.Name}'.", null, null, "not_found");
            }

            Emit("component.changed", $"Component removed: {state.Name}/{type}", new JsonObject { ["id"] = state.Id, ["type"] = type });
            return Success("Component removed.", new JsonObject
            {
                ["id"] = state.Id,
                ["name"] = state.Name,
                ["removed"] = true,
                ["type"] = type,
                ["index"] = TryParseInt(GetNullableString(args, "index")),
            });
        }
    }

    private JsonNode FindGameObjects(JsonObject args)
    {
        var nameContains = GetNullableString(args, "nameContains");
        var tag = GetNullableString(args, "tag");
        var component = GetNullableString(args, "component");
        var activeOnly = args["activeOnly"] is not null && bool.TryParse(args["activeOnly"]!.ToString(), out var ao) && ao;
        var limit = TryParseInt(GetNullableString(args, "limit"), 200);

        var items = new JsonArray();
        var matched = 0;
        var truncated = false;
        lock (_gate)
        {
            foreach (var state in _gameObjects.Values)
            {
                if (activeOnly && !state.Active)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(nameContains) && state.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(tag) && !string.Equals(state.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(component) && !state.Components.ContainsKey(component))
                {
                    continue;
                }

                matched++;
                if (items.Count < limit)
                {
                    items.Add(GameObjectObject(state));
                }
                else
                {
                    truncated = true;
                }
            }
        }

        return new JsonObject { ["count"] = matched, ["truncated"] = truncated, ["items"] = items };
    }

    private JsonNode SetGameObjectProperties(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var applied = new JsonArray();
        lock (_gate)
        {
            if (args["active"] is not null && bool.TryParse(args["active"]!.ToString(), out var active))
            {
                state.Active = active;
                applied.Add("active");
            }

            var tag = GetNullableString(args, "tag");
            if (!string.IsNullOrEmpty(tag))
            {
                state.Tag = tag;
                applied.Add("tag");
            }

            if (args["layer"] is not null)
            {
                state.Layer = TryParseInt(args["layer"]!.ToString());
                applied.Add("layer");
            }

            var newName = GetNullableString(args, "newName");
            if (!string.IsNullOrEmpty(newName))
            {
                state.Name = newName;
                applied.Add("newName");
            }
        }

        Emit("hierarchy.changed", $"GameObject updated: {state.Name}", new JsonObject { ["id"] = state.Id });
        return new JsonObject { ["applied"] = applied, ["gameObject"] = GameObjectObject(state) };
    }

    private JsonNode BuildProjectInfo()
    {
        return new JsonObject
        {
            ["unityVersion"] = "mock",
            ["projectPath"] = _activeScenePath ?? string.Empty,
            ["productName"] = "MockProject",
            ["renderPipeline"] = "Mock",
            ["isPlaying"] = _playMode,
            ["scenesInBuild"] = new JsonArray(),
        };
    }

    private JsonNode BuildAddressablesList()
    {
        return new JsonObject
        {
            ["groups"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Default Local Group",
                    ["entries"] = new JsonArray(
                        new JsonObject
                        {
                            ["address"] = "Assets/Mock/Cube.prefab",
                            ["guid"] = "00000000000000000000000000000000",
                            ["assetPath"] = "Assets/Mock/Cube.prefab",
                            ["labels"] = new JsonArray("default"),
                        }),
                }),
        };
    }

    private JsonNode CreateMaterial(JsonObject args)
    {
        var path = GetString(args, "path", $"Assets/Materials/{GetString(args, "name", "Material")}.mat");
        var name = GetString(args, "name", Path.GetFileNameWithoutExtension(path));
        var shader = GetString(args, "shader", "Universal Render Pipeline/Lit");
        var material = new MaterialState(path, name, shader)
        {
            Color = GetNullableString(args, "color") ?? "#FFFFFFFF",
        };

        lock (_gate)
        {
            _materials[path] = material;
        }

        Emit("asset.changed", $"Material created: {path}", new JsonObject { ["path"] = path, ["type"] = "Material" });
        return MaterialObject(material);
    }

    private JsonNode AssignMaterial(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var materialPath = GetString(args, "materialPath", _materials.Keys.FirstOrDefault() ?? "Assets/Materials/Default.mat");
        lock (_gate)
        {
            state.MaterialPath = materialPath;
        }

        Emit("component.changed", $"Material assigned: {state.Name}", new JsonObject { ["id"] = state.Id, ["materialPath"] = materialPath });
        return GameObjectObject(state);
    }

    private JsonNode ModifyMaterial(JsonObject args)
    {
        var materialPath = GetString(args, "path", _materials.Keys.FirstOrDefault() ?? throw new InvalidOperationException("No materials created."));
        lock (_gate)
        {
            var material = RequireMaterial(materialPath);
            material.Shader = GetString(args, "shader", material.Shader);
            material.Color = GetString(args, "color", material.Color);
        }

        Emit("asset.changed", $"Material modified: {materialPath}", new JsonObject { ["path"] = materialPath, ["action"] = "modify" });
        return MaterialObject(RequireMaterial(materialPath));
    }

    private JsonNode MaterialInfo(JsonObject args)
    {
        var materialPath = GetString(args, "path", _materials.Keys.FirstOrDefault() ?? throw new InvalidOperationException("No materials created."));
        return MaterialObject(RequireMaterial(materialPath));
    }

    private JsonNode ListAssets(JsonObject args)
    {
        var filter = GetNullableString(args, "filter");
        var paths = _scenes.Select(x => x.Path)
            .Concat(_materials.Keys)
            .Concat(_packages.Select(x => $"Packages/{x.Name}"))
            .Where(x => string.IsNullOrWhiteSpace(filter) || x.Contains(filter!, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new JsonObject
        {
            ["assets"] = new JsonArray(paths.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };
    }

    private JsonNode AddAssetToScene(JsonObject args)
    {
        var assetPath = GetString(args, "assetPath", "Assets/Prefabs/Cube.prefab");
        var name = GetString(args, "name", Path.GetFileNameWithoutExtension(assetPath));
        var result = CreateGameObject(new JsonObject
        {
            ["name"] = name,
            ["scenePath"] = GetString(args, "scenePath", _activeScenePath ?? "Assets/Scenes/SampleScene.unity"),
            ["primitive"] = "Prefab",
        }) as JsonObject ?? new JsonObject();

        result["assetPath"] = assetPath;
        return result;
    }

    /// <summary>prefab.create 를 모사한다. 원본이 프리팹(primitive==Prefab)이면 Variant, 아니면 Regular 로 보고한다.</summary>
    /// <param name="args">대상 GameObject(id/name)와 path 인자.</param>
    /// <returns>prefabAssetPath/prefabAssetType/isPrefabInstance 가 추가된 GameObject 요약.</returns>
    private JsonNode CreatePrefabAsset(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Prefabs/New.prefab");
        var state = ResolveGameObject(args);
        var assetType = state.Primitive == "Prefab" ? "Variant" : "Regular";
        Emit("asset.changed", $"Prefab created: {path}", new JsonObject { ["path"] = path, ["type"] = "Prefab" });
        var result = GameObjectObject(state);
        result["prefabAssetPath"] = path;
        result["prefabAssetType"] = assetType;
        result["isPrefabInstance"] = true;
        return result;
    }

    /// <summary>prefab.instantiate 를 모사한다. CreateGameObject 로 씬에 인스턴스를 추가한다.</summary>
    /// <param name="args">프리팹 path, 선택적 name 과 position/scale 인자.</param>
    /// <returns>prefabAssetPath/prefabAssetType/isPrefabInstance 가 추가된 GameObject 요약.</returns>
    private JsonNode InstantiatePrefabAsset(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Prefabs/Cube.prefab");
        var name = GetString(args, "name", Path.GetFileNameWithoutExtension(path));
        var result = CreateGameObject(new JsonObject
        {
            ["name"] = name,
            ["scenePath"] = GetString(args, "scenePath", _activeScenePath ?? "Assets/Scenes/SampleScene.unity"),
            ["primitive"] = "Prefab",
            ["position"] = args["position"]?.DeepClone(),
            ["scale"] = args["scale"]?.DeepClone(),
        }) as JsonObject ?? new JsonObject();

        result["prefabAssetPath"] = path;
        result["prefabAssetType"] = "Regular";
        result["isPrefabInstance"] = true;
        return result;
    }

    /// <summary>prefab.apply 를 모사한다. 대상이 프리팹 인스턴스가 아니면 계약 오류를 던진다.</summary>
    /// <param name="args">대상 GameObject(id/name) 인자.</param>
    /// <returns>prefabAssetType/isPrefabInstance 가 추가된 GameObject 요약.</returns>
    private JsonNode ApplyPrefabOverrides(JsonObject args)
    {
        var state = ResolveGameObject(args);
        if (state.Primitive != "Prefab")
        {
            throw new MockBridgeException("internal_error", 500, $"GameObject '{state.Name}' is not a prefab instance.");
        }

        Emit("asset.changed", $"Prefab overrides applied: {state.Name}", new JsonObject { ["action"] = "apply" });
        var result = GameObjectObject(state);
        result["prefabAssetType"] = "Regular";
        result["isPrefabInstance"] = true;
        return result;
    }

    /// <summary>prefab.unpack 을 모사한다. 대상이 프리팹 인스턴스가 아니면 계약 오류를 던지고, 언팩 후에는 더 이상 인스턴스가 아니다.</summary>
    /// <param name="args">대상 GameObject(id/name)와 completely 인자.</param>
    /// <returns>언팩된 GameObject 요약.</returns>
    private JsonNode UnpackPrefabInstance(JsonObject args)
    {
        var state = ResolveGameObject(args);
        if (state.Primitive != "Prefab")
        {
            throw new MockBridgeException("internal_error", 500, $"GameObject '{state.Name}' is not a prefab instance.");
        }

        var completely = args["completely"]?.GetValue<bool?>() ?? false;
        lock (_gate)
        {
            state.Primitive = null;
        }

        Emit("hierarchy.changed", $"Prefab unpacked: {state.Name}", new JsonObject { ["completely"] = completely });
        return GameObjectObject(state);
    }

    private JsonNode ListPackages()
    {
        return new JsonObject
        {
            ["packages"] = new JsonArray(_packages.Select(x => new JsonObject
            {
                ["name"] = x.Name,
                ["version"] = x.Version,
            }).ToArray<JsonNode?>()),
        };
    }

    private async Task<ToolCallResponse> AddPackageAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var name = GetString(args, "name", "com.unity.textmeshpro");
        var version = GetString(args, "version", "1.0.0");

        await Task.Delay(100, cancellationToken);
        lock (_gate)
        {
            _packages.Add(new PackageState(name, version));
        }

        Emit("package.changed", $"Package added: {name}", new JsonObject { ["name"] = name, ["version"] = version });
        return Success("Package added.", new JsonObject { ["name"] = name, ["version"] = version });
    }

    private JsonNode ListTests(JsonObject args)
    {
        var mode = GetNullableString(args, "mode");
        var matches = _tests
            .Where(x => string.IsNullOrWhiteSpace(mode) || x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase))
            .Select(x => new JsonObject
            {
                ["name"] = x.Name,
                ["mode"] = x.Mode,
            })
            .ToArray<JsonNode?>();

        return new JsonObject { ["tests"] = new JsonArray(matches) };
    }

    private async Task<ToolCallResponse> RunTestsAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var mode = GetString(args, "mode", "EditMode");
        var category = GetNullableString(args, "category");
        var regex = GetNullableString(args, "regex");
        var runId = Guid.NewGuid().ToString("N");
        var startedEvent = Emit("tests.started", $"Tests started: {mode}", new JsonObject { ["mode"] = mode, ["runId"] = runId });
        await Task.Delay(150, cancellationToken);

        IEnumerable<TestCaseState> selected = _tests.Where(x => x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category))
        {
            selected = selected.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(regex))
        {
            try
            {
                selected = selected.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, regex));
            }
            catch (Exception ex)
            {
                return new ToolCallResponse(false, $"Invalid regex: {ex.Message}", null, null);
            }
        }

        var matched = selected.ToList();
        var passed = matched.Count;
        var summary = new JsonObject
        {
            ["passed"] = passed,
            ["failed"] = 0,
            ["skipped"] = 0,
            ["inconclusive"] = 0,
            ["total"] = passed,
            ["tests"] = new JsonArray(matched
                .Select(x => (JsonNode?)new JsonObject { ["name"] = x.Name, ["status"] = "Passed", ["message"] = "" })
                .ToArray()),
        };
        var finishedAt = DateTimeOffset.UtcNow.ToString("O");
        lock (_gate)
        {
            _lastTestRun = new JsonObject
            {
                ["runId"] = runId,
                ["mode"] = mode,
                ["passed"] = passed,
                ["failed"] = 0,
                ["skipped"] = 0,
                ["inconclusive"] = 0,
                ["finishedAt"] = finishedAt,
                ["failures"] = new JsonArray(),
            };
        }

        var completedEvent = Emit("tests.completed", $"Tests completed: {mode}",
            new JsonObject { ["mode"] = mode, ["runId"] = runId, ["summary"] = summary });
        return Success("Tests started.", new JsonObject { ["runId"] = runId, ["mode"] = mode }, [startedEvent, completedEvent]);
    }

    private JsonNode GetLogs(JsonObject args)
    {
        var level = GetNullableString(args, "level");
        var logs = _logs
            .Where(x => string.IsNullOrWhiteSpace(level) || x.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
            .Select(x => new JsonObject
            {
                ["timestamp"] = x.Timestamp,
                ["level"] = x.Level,
                ["message"] = x.Message,
            })
            .ToArray<JsonNode?>();

        return new JsonObject { ["logs"] = new JsonArray(logs) };
    }

    private JsonNode ClearLogs()
    {
        lock (_gate)
        {
            _logs.Clear();
        }

        Emit("console.cleared", "Console cleared.", null);
        return new JsonObject { ["cleared"] = true };
    }

    private JsonNode EmitConsoleLog(JsonObject args)
    {
        var message = GetString(args, "message", "CLI log");
        var level = GetString(args, "level", "info");
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, message);
        lock (_gate)
        {
            _logs.Add(entry);
        }

        Emit("console.log", message, new JsonObject { ["level"] = level });
        return new JsonObject { ["level"] = level, ["message"] = message };
    }

    /// <summary>console.log 이벤트 스트림을 커서/레벨/텍스트 필터로 조회한다(커넥터 동작과 동일한 봉투 반환).</summary>
    /// <param name="args">sinceCursor(long), level(string?), contains(string?) 인자.</param>
    /// <returns>logs 배열, 마지막 커서, 에러/경고 개수를 담은 JSON 객체.</returns>
    private JsonNode QueryConsoleLogs(JsonObject args)
    {
        var sinceCursor = args["sinceCursor"]?.GetValue<long>() ?? 0;
        var level = GetNullableString(args, "level");
        var contains = GetNullableString(args, "contains");
        var logs = new List<JsonNode?>();
        long maxCursor = sinceCursor;
        int errorCount = 0, warningCount = 0;
        lock (_gate)
        {
            foreach (var e in _events)
            {
                if (e.Type != "console.log" || e.Cursor <= sinceCursor) continue;
                var evLevel = e.Data?["level"]?.GetValue<string>() ?? "Log";
                if (!string.IsNullOrWhiteSpace(level) && !evLevel.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(contains) && !(e.Message?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                if (evLevel.Equals("Error", StringComparison.OrdinalIgnoreCase) || evLevel.Equals("Exception", StringComparison.OrdinalIgnoreCase) || evLevel.Equals("Assert", StringComparison.OrdinalIgnoreCase)) errorCount++;
                else if (evLevel.Equals("Warning", StringComparison.OrdinalIgnoreCase)) warningCount++;
                if (e.Cursor > maxCursor) maxCursor = e.Cursor;
                logs.Add(new JsonObject { ["cursor"] = e.Cursor, ["level"] = evLevel, ["message"] = e.Message, ["timestamp"] = e.Timestamp });
            }
        }

        return new JsonObject { ["logs"] = new JsonArray(logs.ToArray()), ["cursor"] = maxCursor, ["errorCount"] = errorCount, ["warningCount"] = warningCount };
    }

    private JsonNode ExecuteMenu(JsonObject args)
    {
        var path = GetString(args, "path", "File/Save");
        Emit("menu.executed", $"Menu executed: {path}", new JsonObject { ["path"] = path });
        return new JsonObject { ["path"] = path };
    }

    private JsonNode SetPlayMode(bool playMode)
    {
        lock (_gate)
        {
            _playMode = playMode;
            if (!playMode)
            {
                _pauseMode = false;
            }
        }

        Emit("editor.play_mode_changed", playMode ? "Play mode entered." : "Play mode exited.", new JsonObject { ["isPlaying"] = playMode });
        return BuildEditorState();
    }

    private JsonNode TogglePause(JsonObject args)
    {
        var pause = args["enabled"]?.GetValue<bool?>() ?? !_pauseMode;
        lock (_gate)
        {
            _pauseMode = pause;
        }

        Emit("editor.pause_changed", pause ? "Editor paused." : "Editor resumed.", new JsonObject { ["isPaused"] = pause });
        return BuildEditorState();
    }

    private JsonNode CreateSprite(JsonObject args)
    {
        var name = GetString(args, "name", "Sprite");
        var goArgs = new JsonObject { ["name"] = name, ["primitive"] = "Sprite" };
        if (args["position"] is JsonArray pos)
        {
            goArgs["position"] = pos.DeepClone();
        }

        var result = (JsonObject)CreateGameObject(goArgs);
        var id = result["id"]!.GetValue<string>();
        lock (_gate)
        {
            var state = _gameObjects[id];
            state.HasSpriteRenderer = true;
            state.Sprite = GetNullableString(args, "sprite");
            state.SpritePath = GetNullableString(args, "sprite");
            state.Color = GetNullableString(args, "color") ?? "#FFFFFFFF";
            ApplySpriteState(state, args);
            return GameObjectObject(state);
        }
    }

    private JsonNode SetSpriteRenderer(JsonObject args)
    {
        var state = ResolveGameObject(args);
        lock (_gate)
        {
            if (!state.HasSpriteRenderer)
            {
                throw new MockBridgeException("not_found", 404, $"SpriteRenderer not found on '{state.Name}'.");
            }

            var sprite = GetNullableString(args, "sprite");
            if (sprite != null)
            {
                state.Sprite = sprite;
                state.SpritePath = sprite;
            }

            ApplySpriteState(state, args);
            Emit("component.changed", $"SpriteRenderer set: {state.Name}", new JsonObject { ["id"] = state.Id });
            return GameObjectObject(state);
        }
    }

    private static void ApplySpriteState(GameObjectState state, JsonObject args)
    {
        var color = GetNullableString(args, "color");
        if (color != null)
        {
            state.Color = color;
        }

        var sortingLayer = GetNullableString(args, "sortingLayer");
        if (sortingLayer != null)
        {
            state.SortingLayerName = sortingLayer;
        }

        if (args["sortingOrder"] is not null)
        {
            state.SortingOrder = (int)(args["sortingOrder"]!.GetValue<long>());
        }

        if (args["flipX"] is not null)
        {
            state.FlipX = args["flipX"]!.GetValue<bool>();
        }

        if (args["flipY"] is not null)
        {
            state.FlipY = args["flipY"]!.GetValue<bool>();
        }
    }

    private JsonNode CreateUiElement(JsonObject args, string elementType)
    {
        var name = GetString(args, "name", elementType);
        var canvasName = GetNullableString(args, "canvasName");
        var element = new UiElementState(name, elementType)
        {
            CanvasName = canvasName,
            Text = GetNullableString(args, "text"),
            AnchoredPosition = GetNullableString(args, "anchoredPosition") ?? "0,0",
            Size = GetNullableString(args, "size") ?? "100,100",
            Color = GetNullableString(args, "color") ?? "#FFFFFFFF",
            Placeholder = GetNullableString(args, "placeholder"),
            ItemCount = (int)(args["itemCount"]?.GetValue<long>() ?? 0),
            MinValue = (float)(args["minValue"]?.GetValue<double>() ?? 0),
            MaxValue = (float)(args["maxValue"]?.GetValue<double>() ?? 1),
            Value = (float)(args["value"]?.GetValue<double>() ?? 0),
        };

        lock (_gate)
        {
            _uiElements[name] = element;
        }

        Emit("ui.created", $"UI element created: {name}", new JsonObject { ["name"] = name, ["type"] = elementType });
        return UiElementObject(element);
    }

    private JsonNode AddLayout(JsonObject args)
    {
        var name = GetString(args, "name", "Panel");
        var layoutType = GetString(args, "layoutType", "VerticalLayoutGroup");
        Emit("component.changed", $"Layout added: {name}/{layoutType}", new JsonObject { ["name"] = name, ["layoutType"] = layoutType });
        return new JsonObject { ["name"] = name, ["layoutType"] = layoutType };
    }

    private JsonNode ModifyRectTransform(JsonObject args)
    {
        var name = GetString(args, "name", "Panel");
        var element = RequireUiElement(name);
        if (args.ContainsKey("anchoredPosition"))
        {
            element.AnchoredPosition = args["anchoredPosition"]!.ToString();
        }

        if (args.ContainsKey("size"))
        {
            element.Size = args["size"]!.ToString();
        }

        return UiElementObject(element);
    }

    private JsonNode CaptureScreenshot(JsonObject args)
    {
        var path = GetString(args, "path", "screenshot.png");
        return new JsonObject { ["path"] = path, ["width"] = _gameViewWidth, ["height"] = _gameViewHeight };
    }

    private JsonNode SetToggle(JsonObject args)
    {
        var element = RequireUiElement(GetString(args, "name", "Toggle"));
        element.IsOn = args["isOn"]?.GetValue<bool>() ?? !element.IsOn;
        Emit("ui.toggle_changed", $"Toggle changed: {element.Name}", new JsonObject { ["name"] = element.Name, ["isOn"] = element.IsOn });
        return UiElementObject(element);
    }

    private JsonNode SetSlider(JsonObject args)
    {
        var element = RequireUiElement(GetString(args, "name", "Slider"));
        element.Value = (float)(args["value"]?.GetValue<double>() ?? element.Value);
        Emit("ui.slider_changed", $"Slider changed: {element.Name}", new JsonObject { ["name"] = element.Name, ["value"] = element.Value });
        return UiElementObject(element);
    }

    private JsonNode SetScrollRect(JsonObject args)
    {
        var element = RequireUiElement(GetString(args, "name", "ScrollRect"));
        if (args["normalizedPosition"] is JsonArray normPos && normPos.Count >= 2)
        {
            element.NormalizedPositionX = (float)(normPos[0]?.GetValue<double>() ?? 0);
            element.NormalizedPositionY = (float)(normPos[1]?.GetValue<double>() ?? 0);
        }

        return UiElementObject(element);
    }

    private JsonNode SetInputFieldText(JsonObject args)
    {
        var element = RequireUiElement(GetString(args, "name", "InputField"));
        element.Text = GetNullableString(args, "text") ?? "";
        Emit("ui.inputfield_changed", $"InputField changed: {element.Name}", new JsonObject { ["name"] = element.Name, ["text"] = element.Text });
        return UiElementObject(element);
    }

    private JsonNode FocusUiElement(JsonObject args)
    {
        var name = GetString(args, "name", "");
        var element = RequireUiElement(name);
        lock (_gate)
        {
            _focusedUiElementName = name;
        }

        Emit("ui.focused", $"Focused: {name}", new JsonObject { ["name"] = name });
        var result = UiElementObject(element);
        result["isSelected"] = true;
        return result;
    }

    private JsonNode BlurUiElement()
    {
        lock (_gate)
        {
            _focusedUiElementName = null;
        }

        Emit("ui.blurred", "Focus cleared.", new JsonObject { ["cleared"] = true });
        return new JsonObject { ["cleared"] = true };
    }

    private JsonNode ClickUiElement(JsonObject args)
    {
        var name = GetNullableString(args, "name") ?? "Unknown";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        Emit("ui.clicked", $"Clicked: {name}", new JsonObject { ["name"] = name, ["pointerId"] = pointerId });
        return new JsonObject { ["name"] = name, ["pointerId"] = pointerId, ["clicked"] = true };
    }

    private JsonNode DoubleClickUi(JsonObject args)
    {
        var name = GetNullableString(args, "name");
        var normalizedPosition = GetNullableString(args, "normalizedPosition") ?? "0.5,0.5";
        Emit("ui.double_clicked", $"Double-clicked: {name ?? normalizedPosition}", new JsonObject { ["name"] = name, ["clickCount"] = 2 });
        return new JsonObject { ["name"] = name, ["clickCount"] = 2, ["normalizedPosition"] = normalizedPosition };
    }

    private JsonNode LongPressUi(JsonObject args)
    {
        var name = GetNullableString(args, "name");
        var normalizedPosition = GetNullableString(args, "normalizedPosition") ?? "0.5,0.5";
        var durationMs = (int)(args["durationMs"]?.GetValue<long>() ?? 500);
        Emit("ui.long_pressed", $"Long-pressed: {name ?? normalizedPosition}", new JsonObject { ["name"] = name, ["durationMs"] = durationMs });
        return new JsonObject { ["name"] = name, ["durationMs"] = durationMs, ["normalizedPosition"] = normalizedPosition };
    }

    private JsonNode DragUi(JsonObject args)
    {
        var name = GetNullableString(args, "name") ?? "Unknown";
        var from = GetNullableString(args, "from") ?? "0,0";
        var to = GetNullableString(args, "to") ?? "0,0";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var element = _uiElements.GetValueOrDefault(name);
        if (element is { ElementType: "ScrollRect" })
        {
            element.NormalizedPositionY = Math.Clamp(element.NormalizedPositionY + 0.1f, 0f, 1f);
        }
        else if (element is { ElementType: "Slider" })
        {
            element.Value = Math.Clamp(element.Value + 0.3f, element.MinValue, element.MaxValue);
        }

        Emit("ui.dragged", $"Dragged: {name}", new JsonObject { ["name"] = name, ["from"] = from, ["to"] = to, ["pointerId"] = pointerId });
        return new JsonObject { ["name"] = name, ["from"] = from, ["to"] = to, ["pointerId"] = pointerId };
    }

    private JsonNode SwipeUi(JsonObject args)
    {
        var normalizedFrom = GetNullableString(args, "normalizedFrom") ?? "0.5,0.5";
        var normalizedTo = GetNullableString(args, "normalizedTo") ?? "0.5,0.5";
        var hitName = _uiElements.Keys.FirstOrDefault() ?? _gameObjects.Values.FirstOrDefault()?.Name ?? "Unknown";
        Emit("ui.swiped", $"Swiped over {hitName}", new JsonObject { ["hitName"] = hitName, ["normalizedFrom"] = normalizedFrom, ["normalizedTo"] = normalizedTo });
        return new JsonObject { ["hitName"] = hitName, ["normalizedFrom"] = normalizedFrom, ["normalizedTo"] = normalizedTo };
    }

    private JsonNode InputTap(JsonObject args)
    {
        var worldPosition = GetNullableString(args, "worldPosition") ?? "0,0,0";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var hitName = FindHitByWorld(worldPosition);
        Emit("input.tapped", $"Tapped: {hitName}", new JsonObject { ["hitName"] = hitName, ["worldPosition"] = worldPosition, ["pointerId"] = pointerId });
        return new JsonObject { ["hitName"] = hitName, ["worldPosition"] = worldPosition, ["pointerId"] = pointerId };
    }

    private JsonNode InputDoubleTap(JsonObject args)
    {
        var worldPosition = GetNullableString(args, "worldPosition") ?? "0,0,0";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var hitName = FindHitByWorld(worldPosition);
        Emit("input.double_tapped", $"Double-tapped: {hitName}", new JsonObject { ["hitName"] = hitName, ["clickCount"] = 2, ["pointerId"] = pointerId });
        return new JsonObject { ["hitName"] = hitName, ["clickCount"] = 2, ["worldPosition"] = worldPosition, ["pointerId"] = pointerId };
    }

    private JsonNode InputLongPress(JsonObject args)
    {
        var worldPosition = GetNullableString(args, "worldPosition") ?? "0,0,0";
        var durationMs = (int)(args["durationMs"]?.GetValue<long>() ?? 500);
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var hitName = FindHitByWorld(worldPosition);
        Emit("input.long_pressed", $"Long-pressed: {hitName}", new JsonObject { ["hitName"] = hitName, ["durationMs"] = durationMs, ["pointerId"] = pointerId });
        return new JsonObject { ["hitName"] = hitName, ["durationMs"] = durationMs, ["worldPosition"] = worldPosition, ["pointerId"] = pointerId };
    }

    private JsonNode InputDrag(JsonObject args)
    {
        var worldFrom = GetNullableString(args, "worldFrom") ?? "0,0,0";
        var worldTo = GetNullableString(args, "worldTo") ?? "0,0,0";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var hitName = FindHitByWorld(worldFrom);
        Emit("input.dragged", $"Dragged: {hitName}", new JsonObject { ["hitName"] = hitName, ["worldFrom"] = worldFrom, ["worldTo"] = worldTo, ["pointerId"] = pointerId });
        return new JsonObject { ["hitName"] = hitName, ["worldFrom"] = worldFrom, ["worldTo"] = worldTo, ["pointerId"] = pointerId };
    }

    private JsonNode InputSwipe(JsonObject args)
    {
        var worldFrom = GetNullableString(args, "worldFrom") ?? "0,0,0";
        var worldTo = GetNullableString(args, "worldTo") ?? "0,0,0";
        var pointerId = (int)(args["pointerId"]?.GetValue<long>() ?? -1);
        var hitName = FindHitByWorld(worldFrom);
        Emit("input.swiped", $"Swiped: {hitName}", new JsonObject { ["hitName"] = hitName, ["worldFrom"] = worldFrom, ["worldTo"] = worldTo, ["pointerId"] = pointerId });
        return new JsonObject { ["hitName"] = hitName, ["worldFrom"] = worldFrom, ["worldTo"] = worldTo, ["pointerId"] = pointerId };
    }

    private JsonNode ImportTexture(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Textures/imported.png");
        Emit("asset.changed", $"Texture imported: {path}", new JsonObject { ["path"] = path, ["type"] = "Texture2D" });
        return new JsonObject { ["path"] = path, ["imported"] = true };
    }

    /// <summary>asset.create-scriptableobject 를 모사한다. values 키를 그대로 applied 로 보고한다.</summary>
    /// <param name="args">type, path, 선택적 values 인자를 담은 JSON 오브젝트.</param>
    /// <returns>path/type/applied/skipped 를 담은 JSON 오브젝트.</returns>
    private JsonNode CreateScriptableObjectAsset(JsonObject args)
    {
        if (args["type"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "type is required.");
        }

        if (args["path"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "path is required.");
        }

        var type = GetString(args, "type", "ScriptableObject");
        var path = GetString(args, "path", "Assets/Configs/New.asset");
        var applied = new JsonArray();
        if (args["values"] is JsonObject values)
        {
            foreach (var pair in values)
            {
                applied.Add(JsonValue.Create(pair.Key));
            }
        }

        Emit("asset.changed", $"ScriptableObject created: {path}", new JsonObject { ["path"] = path, ["type"] = type });
        return new JsonObject
        {
            ["path"] = path,
            ["type"] = type,
            ["applied"] = applied,
            ["skipped"] = new JsonArray(),
        };
    }

    /// <summary>scriptableobject.get 을 모사한다. 결정론적 빈 properties 를 반환한다.</summary>
    /// <param name="args">path 인자를 담은 JSON 오브젝트.</param>
    /// <returns>path/type/properties 를 담은 JSON 오브젝트.</returns>
    private JsonNode GetScriptableObject(JsonObject args)
    {
        if (args["path"] is null)
        {
            throw new MockBridgeException("missing_arg", 400, "path is required.");
        }

        var path = GetString(args, "path", "Assets/Configs/New.asset");
        return new JsonObject
        {
            ["path"] = path,
            ["type"] = "ScriptableObject",
            ["properties"] = new JsonObject(),
        };
    }

    /// <summary>scriptableobject.list 를 모사한다. 빈 목록을 반환한다.</summary>
    /// <param name="args">선택적 filter 인자를 담은 JSON 오브젝트.</param>
    /// <returns>assets 배열과 count 를 담은 JSON 오브젝트.</returns>
    private JsonNode ListScriptableObjects(JsonObject args)
    {
        _ = GetString(args, "filter", "t:ScriptableObject");
        return new JsonObject
        {
            ["assets"] = new JsonArray(),
            ["count"] = 0,
        };
    }

    /// <summary>project.add-tag 를 모사한다. 태그 추가를 멱등한 성공으로 보고한다.</summary>
    /// <param name="args">tag 인자를 담은 JSON 오브젝트.</param>
    /// <returns>tag 와 added 플래그를 담은 JSON 오브젝트.</returns>
    private JsonNode AddProjectTag(JsonObject args)
    {
        var tag = GetString(args, "tag", "Untagged");
        Emit("asset.changed", $"Tag added: {tag}", new JsonObject { ["path"] = "ProjectSettings/TagManager.asset", ["tag"] = tag });
        return new JsonObject { ["tag"] = tag, ["added"] = true };
    }

    /// <summary>project.add-layer 를 모사한다. 사용자 레이어 슬롯 배정을 성공으로 보고한다.</summary>
    /// <param name="args">layer 및 선택적 index 인자를 담은 JSON 오브젝트.</param>
    /// <returns>layer/index/added 를 담은 JSON 오브젝트.</returns>
    private JsonNode AddProjectLayer(JsonObject args)
    {
        var layer = GetString(args, "layer", "UserLayer");
        var index = (int)(args["index"]?.GetValue<long>() ?? 8);
        Emit("asset.changed", $"Layer added: {layer}", new JsonObject { ["path"] = "ProjectSettings/TagManager.asset", ["layer"] = layer, ["index"] = index });
        return new JsonObject { ["layer"] = layer, ["index"] = index, ["added"] = true };
    }

    /// <summary>project.list-tags-layers 를 모사한다. 결정론적 태그/레이어 목록을 반환한다.</summary>
    /// <returns>tags 배열과 layers 배열을 담은 JSON 오브젝트.</returns>
    private JsonNode ListTagsAndLayers()
    {
        return new JsonObject
        {
            ["tags"] = new JsonArray(
                JsonValue.Create("Untagged"),
                JsonValue.Create("Player"),
                JsonValue.Create("MainCamera")),
            ["layers"] = new JsonArray(
                new JsonObject { ["index"] = 0, ["name"] = "Default" },
                new JsonObject { ["index"] = 5, ["name"] = "UI" }),
        };
    }

    /// <summary>asset.set-addressable 를 모사한다. 엔트리 생성/이동을 성공으로 보고한다.</summary>
    /// <param name="args">path 및 선택적 address/group 인자를 담은 JSON 오브젝트.</param>
    /// <returns>path/guid/address/group 을 담은 JSON 오브젝트.</returns>
    private JsonNode SetAddressable(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Mock/Asset.prefab");
        var address = GetNullableString(args, "address") ?? path;
        var group = GetNullableString(args, "group") ?? "Default Local Group";
        Emit("asset.changed", $"Addressable set: {path}", new JsonObject { ["path"] = path, ["address"] = address, ["group"] = group });
        return new JsonObject { ["path"] = path, ["guid"] = "mock", ["address"] = address, ["group"] = group };
    }

    /// <summary>asset.remove-addressable 을 모사한다. 엔트리 제거를 성공으로 보고한다.</summary>
    /// <param name="args">path 인자를 담은 JSON 오브젝트.</param>
    /// <returns>path 와 removed 플래그를 담은 JSON 오브젝트.</returns>
    private JsonNode RemoveAddressable(JsonObject args)
    {
        var path = GetString(args, "path", "Assets/Mock/Asset.prefab");
        Emit("asset.changed", $"Addressable removed: {path}", new JsonObject { ["path"] = path, ["action"] = "remove-addressable" });
        return new JsonObject { ["path"] = path, ["removed"] = true };
    }

    /// <summary>scene.set-lighting 을 모사한다. 제공된 조명 키만 applied 로 반환한다.</summary>
    /// <param name="args">조명/안개/스카이박스 관련 선택적 인자를 담은 JSON 오브젝트.</param>
    /// <returns>applied 배열을 담은 JSON 오브젝트.</returns>
    private JsonNode SetSceneLighting(JsonObject args)
    {
        string[] lightingKeys =
        [
            "ambientMode", "ambientColor", "ambientIntensity", "ambientSkyColor",
            "ambientEquatorColor", "ambientGroundColor", "fog", "fogColor", "fogMode",
            "fogDensity", "fogStartDistance", "fogEndDistance", "skyboxMaterial",
        ];
        var applied = new JsonArray();
        foreach (var key in lightingKeys)
        {
            if (args.ContainsKey(key))
            {
                applied.Add(JsonValue.Create(key));
            }
        }

        Emit("scene.changed", "Scene lighting set.", new JsonObject { ["path"] = _activeScenePath, ["action"] = "set-lighting" });
        return new JsonObject { ["applied"] = applied };
    }

    /// <summary>scene.bake-navmesh 를 모사한다. NavMesh 베이크 완료를 보고한다.</summary>
    /// <returns>baked 플래그를 담은 JSON 오브젝트.</returns>
    private JsonNode BakeNavMesh()
    {
        Emit("scene.changed", "NavMesh baked.", new JsonObject { ["path"] = _activeScenePath, ["action"] = "bake-navmesh" });
        return new JsonObject { ["baked"] = true };
    }

    /// <summary>asset.manage 도구의 테스트 더블로 op 값에 따라 결정론적 응답을 반환한다.</summary>
    /// <param name="args">op 및 작업별 인자를 담은 JSON 오브젝트.</param>
    /// <returns>커넥터와 동일한 성공/실패 메시지를 담은 <see cref="ToolCallResponse"/>.</returns>
    private ToolCallResponse ManageAsset(JsonObject args)
    {
        var op = GetNullableString(args, "op");
        if (string.IsNullOrEmpty(op))
            return new ToolCallResponse(false, "op is required (create-folder|move|delete|rename|duplicate).", null, null);

        switch (op)
        {
            case "create-folder":
            {
                var parent = GetString(args, "parent", "Assets");
                var folderName = GetNullableString(args, "folderName");
                if (string.IsNullOrEmpty(folderName))
                    return new ToolCallResponse(false, "folderName is required for create-folder.", null, null);

                var path = $"{parent}/{folderName}";
                Emit("asset.changed", $"Folder created: {path}", new JsonObject { ["path"] = path });
                return Success("Folder created.", new JsonObject { ["guid"] = "mock-guid", ["path"] = path });
            }
            case "move":
            {
                var from = GetNullableString(args, "from");
                var to = GetNullableString(args, "to");
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                    return new ToolCallResponse(false, "from/to required for move.", null, null);

                Emit("asset.changed", $"Asset moved: {from} -> {to}", new JsonObject { ["from"] = from, ["to"] = to });
                return Success("Asset moved.", new JsonObject { ["from"] = from, ["to"] = to });
            }
            case "delete":
            {
                if (args["paths"] is JsonArray paths)
                {
                    var deleted = new JsonArray();
                    foreach (var token in paths)
                    {
                        var value = token?.GetValue<string>();
                        if (!string.IsNullOrEmpty(value))
                            deleted.Add(value);
                    }

                    if (deleted.Count == 0)
                        return new ToolCallResponse(false, "paths must contain at least one asset path.", null, null);

                    Emit("asset.changed", $"Asset(s) deleted: {deleted.Count}", new JsonObject { ["count"] = deleted.Count });
                    return Success("Asset(s) deleted.", new JsonObject { ["deleted"] = deleted });
                }

                var path = GetNullableString(args, "path");
                if (string.IsNullOrEmpty(path))
                    return new ToolCallResponse(false, "path or paths is required for delete.", null, null);

                Emit("asset.changed", $"Asset deleted: {path}", new JsonObject { ["path"] = path });
                return Success("Asset(s) deleted.", new JsonObject { ["deleted"] = new JsonArray(JsonValue.Create(path)) });
            }
            case "rename":
            {
                var path = GetNullableString(args, "path");
                var newName = GetNullableString(args, "newName");
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(newName))
                    return new ToolCallResponse(false, "path/newName required for rename.", null, null);

                Emit("asset.changed", $"Asset renamed: {path} -> {newName}", new JsonObject { ["path"] = path, ["newName"] = newName });
                return Success("Asset renamed.", new JsonObject { ["path"] = path, ["newName"] = newName });
            }
            case "duplicate":
            {
                var path = GetNullableString(args, "path");
                if (string.IsNullOrEmpty(path))
                    return new ToolCallResponse(false, "path is required for duplicate.", null, null);

                var to = GetString(args, "to", $"{path} Copy");
                Emit("asset.changed", $"Asset duplicated: {path} -> {to}", new JsonObject { ["from"] = path, ["to"] = to });
                return Success("Asset duplicated.", new JsonObject { ["from"] = path, ["to"] = to });
            }
            default:
                return new ToolCallResponse(false, $"Unknown op: {op}. Expected create-folder|move|delete|rename|duplicate.", null, null);
        }
    }

    private async Task<ToolCallResponse> CompileAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var compilationId = Guid.NewGuid().ToString("N");
        var startedEvent = Emit("editor.compilation_started", "Compilation started.",
            new JsonObject { ["compilationId"] = compilationId });
        await Task.Delay(100, cancellationToken);
        var compiledEvent = Emit("editor.compiled", "Compilation completed.",
            new JsonObject { ["compilationId"] = compilationId, ["success"] = true, ["errors"] = 0, ["warnings"] = 0 });
        return Success("Compilation requested.", new JsonObject { ["compilationId"] = compilationId }, [startedEvent, compiledEvent]);
    }

    private JsonNode ResizeGameView(JsonObject args)
    {
        lock (_gate)
        {
            _gameViewWidth = (int)(args["width"]?.GetValue<long>() ?? _gameViewWidth);
            _gameViewHeight = (int)(args["height"]?.GetValue<long>() ?? _gameViewHeight);
        }

        return new JsonObject { ["width"] = _gameViewWidth, ["height"] = _gameViewHeight };
    }

    private JsonNode RefreshEditor()
    {
        Emit("editor.refreshed", "Editor refreshed.", null);
        return BuildEditorState();
    }

    private BridgeStatus BuildStatus()
    {
        lock (_gate)
        {
            return new BridgeStatus(
                "mock-unity-bridge",
                "0.1.0",
                "ready",
                "MockUnity 6000.0",
                "/Mock/Project",
                _cursor,
                BuildCapabilities().Tools);
        }
    }

    private CapabilityResponse BuildCapabilities()
    {
        return new CapabilityResponse(
            ToolCatalog().Select(x => x.Name).ToArray(),
            ResourceCatalog().Select(x => x.Name).ToArray(),
            ["bridge.started", "scene.changed", "scene.loaded", "scene.saved", "hierarchy.changed", "transform.changed", "selection.changed", "component.changed", "asset.changed", "package.changed", "tests.started", "tests.completed", "console.log", "editor.compilation_started", "editor.compiled", "editor.play_mode_changed", "editor.pause_changed", "editor.refreshed", "menu.executed", "ui.created", "ui.focused", "ui.blurred", "ui.clicked", "ui.double_clicked", "ui.long_pressed", "ui.dragged", "ui.swiped", "ui.toggle_changed", "ui.slider_changed", "ui.inputfield_changed", "input.tapped", "input.double_tapped", "input.long_pressed", "input.dragged", "input.swiped"],
            new Dictionary<string, string>
            {
                ["transport"] = "http",
                ["workflow"] = "event-polling",
            });
    }

    private IReadOnlyList<ToolDescriptor> ToolCatalog()
    {
        return
        [
            new ToolDescriptor("scene.create", "scene", "Create a scene.", ["path"], ["name"]),
            new ToolDescriptor("scene.load", "scene", "Load a scene.", ["path"], []),
            new ToolDescriptor("scene.save", "scene", "Save a scene.", [], ["path"]),
            new ToolDescriptor("scene.info", "scene", "Fetch scene info.", [], ["path"]),
            new ToolDescriptor("scene.delete", "scene", "Delete a scene.", ["path"], []),
            new ToolDescriptor("scene.unload", "scene", "Unload a scene.", [], ["path"]),
            new ToolDescriptor("scene.open-additive", "scene", "Open a scene additively.", ["path"], []),
            new ToolDescriptor("scene.set-active", "scene", "Set the active scene.", ["path"], []),
            new ToolDescriptor("scene.list-loaded", "scene", "List loaded scenes.", [], []),
            new ToolDescriptor("scene.set-lighting", "scene", "Configure scene lighting, fog and skybox.", [], ["ambientMode", "ambientColor", "ambientIntensity", "ambientSkyColor", "ambientEquatorColor", "ambientGroundColor", "fog", "fogColor", "fogMode", "fogDensity", "fogStartDistance", "fogEndDistance", "skyboxMaterial"]),
            new ToolDescriptor("scene.bake-navmesh", "scene", "Bake the NavMesh for the active scene.", [], []),
            new ToolDescriptor("gameobject.create", "gameobject", "Create a GameObject.", ["name"], ["scenePath", "parentId", "position", "scale", "primitive"]),
            new ToolDescriptor("gameobject.get", "gameobject", "Fetch a GameObject.", [], ["id", "name"]),
            new ToolDescriptor("gameobject.delete", "gameobject", "Delete a GameObject.", [], ["id", "name"]),
            new ToolDescriptor("gameobject.duplicate", "gameobject", "Duplicate a GameObject.", [], ["id", "name"]),
            new ToolDescriptor("gameobject.reparent", "gameobject", "Reparent a GameObject.", [], ["id", "name", "parentId"]),
            new ToolDescriptor("gameobject.move", "gameobject", "Move a GameObject.", [], ["id", "name", "position"]),
            new ToolDescriptor("gameobject.rotate", "gameobject", "Rotate a GameObject.", [], ["id", "name", "rotation"]),
            new ToolDescriptor("gameobject.scale", "gameobject", "Scale a GameObject.", [], ["id", "name", "scale"]),
            new ToolDescriptor("gameobject.set-transform", "gameobject", "Set a GameObject transform.", [], ["id", "name", "position", "rotation", "scale"]),
            new ToolDescriptor("gameobject.select", "gameobject", "Select a GameObject.", [], ["id", "name"]),
            new ToolDescriptor("gameobject.find", "gameobject", "Query GameObjects.", [], ["tag", "layer", "component", "nameContains", "path", "activeOnly", "includeInactive", "limit"]),
            new ToolDescriptor("gameobject.set-properties", "gameobject", "Set GameObject-level state.", [], ["id", "name", "active", "tag", "layer", "static", "newName", "recursiveLayer"]),
            new ToolDescriptor("sprite.create", "sprite", "Create a SpriteRenderer.", ["name"], ["sprite", "position", "color", "sortingLayer", "sortingOrder", "flipX", "flipY"]),
            new ToolDescriptor("sprite.set", "sprite", "Modify a SpriteRenderer.", [], ["id", "name", "sprite", "color", "sortingLayer", "sortingOrder", "flipX", "flipY"]),
            new ToolDescriptor("component.update", "component", "Patch a component.", ["type"], ["id", "name", "values"]),
            new ToolDescriptor("component.list", "component", "List components on a GameObject.", [], ["id", "name", "includeValues"]),
            new ToolDescriptor("component.get", "component", "Read a component's serialized properties.", ["type"], ["id", "name"]),
            new ToolDescriptor("component.add", "component", "Add a component.", ["type"], ["id", "name", "values", "allowDuplicate"]),
            new ToolDescriptor("component.remove", "component", "Remove a component.", ["type"], ["id", "name", "index"]),
            new ToolDescriptor("material.create", "material", "Create a material.", ["path"], ["name", "shader", "color"]),
            new ToolDescriptor("material.assign", "material", "Assign a material.", ["materialPath"], ["id", "name"]),
            new ToolDescriptor("material.modify", "material", "Modify a material.", ["path"], ["shader", "color"]),
            new ToolDescriptor("material.info", "material", "Fetch material info.", ["path"], []),
            new ToolDescriptor("asset.list", "asset", "List assets.", [], ["filter"]),
            new ToolDescriptor("asset.add-to-scene", "asset", "Instantiate an asset in the scene.", ["assetPath"], ["scenePath", "name"]),
            new ToolDescriptor("asset.set-addressable", "asset", "Mark an asset addressable.", ["path"], ["address", "group"]),
            new ToolDescriptor("asset.remove-addressable", "asset", "Remove an addressable entry.", ["path"], []),
            new ToolDescriptor("asset.import-texture", "asset", "Import a texture.", ["path"], []),
            new ToolDescriptor("asset.manage", "asset", "Manage assets (folder/move/delete/rename/duplicate).", ["op"], ["parent", "folderName", "from", "to", "path", "paths", "newName"]),
            new ToolDescriptor("asset.create-scriptableobject", "asset", "Create a ScriptableObject asset.", ["type", "path"], ["values"]),
            new ToolDescriptor("scriptableobject.get", "scriptableobject", "Read a ScriptableObject's serialized properties.", ["path"], []),
            new ToolDescriptor("scriptableobject.list", "scriptableobject", "List ScriptableObject assets.", [], ["filter"]),
            new ToolDescriptor("package.list", "package", "List packages.", [], []),
            new ToolDescriptor("package.add", "package", "Install a package.", ["name"], ["version"]),
            new ToolDescriptor("tests.list", "tests", "List tests.", [], ["mode"]),
            new ToolDescriptor("tests.run", "tests", "Run tests.", [], ["mode", "assembly", "name", "category", "regex"]),
            new ToolDescriptor("console.get", "console", "Fetch console logs.", [], ["level"]),
            new ToolDescriptor("console.clear", "console", "Clear console logs.", [], []),
            new ToolDescriptor("console.send", "console", "Emit a console log.", ["message"], ["level"]),
            new ToolDescriptor("console.logs", "console", "Query console logs from a cursor.", [], ["sinceCursor", "level", "contains"]),
            new ToolDescriptor("menu.execute", "menu", "Execute a menu item.", ["path"], []),
            new ToolDescriptor("project.add-tag", "project", "Add a tag to the project.", ["tag"], []),
            new ToolDescriptor("project.add-layer", "project", "Add a user layer.", ["layer"], ["index"]),
            new ToolDescriptor("project.list-tags-layers", "project", "List tags and user layers.", [], []),
            new ToolDescriptor("ui.canvas.create", "ui", "Create a Canvas.", ["name"], []),
            new ToolDescriptor("ui.button.create", "ui", "Create a Button.", ["canvasName", "name"], ["text", "anchoredPosition", "size"]),
            new ToolDescriptor("ui.toggle.create", "ui", "Create a Toggle.", ["canvasName", "name"], ["text", "anchoredPosition", "size"]),
            new ToolDescriptor("ui.slider.create", "ui", "Create a Slider.", ["canvasName", "name"], ["anchoredPosition", "size", "minValue", "maxValue", "value"]),
            new ToolDescriptor("ui.scrollrect.create", "ui", "Create a ScrollRect.", ["canvasName", "name"], ["anchoredPosition", "size", "itemCount"]),
            new ToolDescriptor("ui.inputfield.create", "ui", "Create an InputField.", ["canvasName", "name"], ["anchoredPosition", "size", "placeholder"]),
            new ToolDescriptor("ui.text.create", "ui", "Create a Text element.", ["canvasName", "name"], ["text", "anchoredPosition", "size"]),
            new ToolDescriptor("ui.image.create", "ui", "Create an Image.", ["canvasName", "name"], ["anchoredPosition", "size", "color"]),
            new ToolDescriptor("ui.panel.create", "ui", "Create a Panel.", ["canvasName", "name"], ["anchoredPosition", "size"]),
            new ToolDescriptor("ui.layout.add", "ui", "Add a layout component.", ["name", "layoutType"], []),
            new ToolDescriptor("ui.recttransform.modify", "ui", "Modify RectTransform.", ["name"], ["anchoredPosition", "size"]),
            new ToolDescriptor("ui.screenshot.capture", "ui", "Capture a screenshot.", [], ["path"]),
            new ToolDescriptor("ui.toggle.set", "ui", "Set toggle value.", ["name"], ["isOn"]),
            new ToolDescriptor("ui.slider.set", "ui", "Set slider value.", ["name", "value"], []),
            new ToolDescriptor("ui.scrollrect.set", "ui", "Set scroll position.", ["name", "normalizedPosition"], []),
            new ToolDescriptor("ui.inputfield.set-text", "ui", "Set input text.", ["name", "text"], []),
            new ToolDescriptor("ui.focus", "ui", "Focus a UI element.", ["name"], []),
            new ToolDescriptor("ui.blur", "ui", "Clear UI focus.", [], []),
            new ToolDescriptor("ui.click", "ui", "Click a UI element.", [], ["name", "pointerId"]),
            new ToolDescriptor("ui.double-click", "ui", "Double-click.", [], ["name", "normalizedPosition"]),
            new ToolDescriptor("ui.long-press", "ui", "Long-press.", [], ["name", "normalizedPosition", "durationMs"]),
            new ToolDescriptor("ui.drag", "ui", "Drag a UI element.", ["name"], ["from", "to", "pointerId"]),
            new ToolDescriptor("ui.swipe", "ui", "Swipe gesture.", [], ["normalizedFrom", "normalizedTo"]),
            new ToolDescriptor("input.tap", "input", "Tap at world position.", ["worldPosition"], ["pointerId"]),
            new ToolDescriptor("input.double-tap", "input", "Double-tap.", ["worldPosition"], ["pointerId"]),
            new ToolDescriptor("input.long-press", "input", "Long-press.", ["worldPosition"], ["durationMs", "pointerId"]),
            new ToolDescriptor("input.drag", "input", "Drag gesture.", ["worldFrom", "worldTo"], ["pointerId"]),
            new ToolDescriptor("input.swipe", "input", "Swipe gesture.", ["worldFrom", "worldTo"], ["pointerId"]),
            new ToolDescriptor("editor.compile", "editor", "Request script compilation.", [], []),
            new ToolDescriptor("editor.play", "editor", "Enter play mode.", [], []),
            new ToolDescriptor("editor.stop", "editor", "Exit play mode.", [], []),
            new ToolDescriptor("editor.pause", "editor", "Pause or resume.", [], ["enabled"]),
            new ToolDescriptor("editor.refresh", "editor", "Refresh editor.", [], []),
            new ToolDescriptor("editor.gameview.resize", "editor", "Resize Game view.", ["width", "height"], []),
            new ToolDescriptor("prefab.create", "prefab", "Save a GameObject as a prefab.", ["path"], ["id", "name"]),
            new ToolDescriptor("prefab.instantiate", "prefab", "Instantiate a prefab asset.", ["path"], ["name", "position", "rotation", "scale"]),
            new ToolDescriptor("prefab.apply", "prefab", "Apply prefab overrides.", [], ["id", "name"]),
            new ToolDescriptor("prefab.unpack", "prefab", "Unpack a prefab instance.", [], ["id", "name", "completely"]),
        ];
    }

    private IReadOnlyList<ResourceDescriptor> ResourceCatalog()
    {
        return
        [
            new ResourceDescriptor("editor/state", "Editor play/pause/selection state."),
            new ResourceDescriptor("scene/active", "Active scene summary."),
            new ResourceDescriptor("scene/hierarchy", "Hierarchy for the active scene."),
            new ResourceDescriptor("ui/hierarchy", "UI element hierarchy."),
            new ResourceDescriptor("console/logs", "Console logs."),
            new ResourceDescriptor("tests/catalog", "Known tests."),
            new ResourceDescriptor("tests/last-run", "Last completed test run summary."),
            new ResourceDescriptor("packages/list", "Installed packages."),
            new ResourceDescriptor("project/info", "Project, build target, render pipeline and scenes-in-build info."),
            new ResourceDescriptor("addressables/list", "Addressables groups and entries (reflection; available:false when package absent)."),
        ];
    }

    private ResourceResponse BuildResource(string name)
    {
        return name switch
        {
            "editor/state" => new ResourceResponse(name, BuildEditorState()),
            "scene/active" => new ResourceResponse(name, string.IsNullOrWhiteSpace(_activeScenePath) ? null : SceneObject(_activeScenePath)),
            "scene/hierarchy" => new ResourceResponse(name, BuildHierarchy()),
            "ui/hierarchy" => new ResourceResponse(name, BuildUiHierarchy()),
            "console/logs" => new ResourceResponse(name, GetLogs(new JsonObject())),
            "tests/catalog" => new ResourceResponse(name, ListTests(new JsonObject())),
            "tests/last-run" => new ResourceResponse(name, _lastTestRun is null ? null : JsonHelpers.DeepClone(_lastTestRun)),
            "packages/list" => new ResourceResponse(name, ListPackages()),
            "project/info" => new ResourceResponse(name, BuildProjectInfo()),
            "addressables/list" => new ResourceResponse(name, BuildAddressablesList()),
            _ => new ResourceResponse(name, null),
        };
    }

    private JsonNode BuildHierarchy()
    {
        var activeScene = _activeScenePath;
        var items = _gameObjects.Values
            .Where(x => string.IsNullOrWhiteSpace(activeScene) || x.ScenePath.Equals(activeScene, StringComparison.OrdinalIgnoreCase))
            .Select(GameObjectObject)
            .ToArray<JsonNode?>();

        return new JsonObject
        {
            ["scenePath"] = activeScene,
            ["items"] = new JsonArray(items),
        };
    }

    private JsonNode BuildUiHierarchy()
    {
        lock (_gate)
        {
            var items = _uiElements.Values.Select(UiElementObject).ToArray<JsonNode?>();
            return new JsonObject { ["items"] = new JsonArray(items) };
        }
    }

    private JsonNode BuildEditorState()
    {
        return new JsonObject
        {
            ["isPlaying"] = _playMode,
            ["isPlayingOrWillChangePlaymode"] = _playMode,
            ["isPaused"] = _pauseMode,
            ["selectedObjectId"] = _selectedObjectId,
            ["activeScenePath"] = _activeScenePath,
            ["eventSystemSelectedObjectName"] = _focusedUiElementName,
            ["eventSystemSelectedObjectId"] = _focusedUiElementName is null ? 0 : 1,
            ["gameViewWidth"] = _gameViewWidth,
            ["gameViewHeight"] = _gameViewHeight,
        };
    }

    private JsonObject SceneObject(string path)
    {
        var scene = RequireScene(path);
        return new JsonObject
        {
            ["path"] = scene.Path,
            ["name"] = scene.Name,
            ["isLoaded"] = scene.IsLoaded,
            ["isDirty"] = scene.IsDirty,
            ["buildIndex"] = scene.BuildIndex,
            ["isActive"] = string.Equals(_activeScenePath, scene.Path, StringComparison.OrdinalIgnoreCase),
        };
    }

    private JsonObject GameObjectObject(GameObjectState state)
    {
        var obj = new JsonObject
        {
            ["id"] = state.Id,
            ["name"] = state.Name,
            ["activeSelf"] = state.Active,
            ["activeInHierarchy"] = state.Active,
            ["tag"] = state.Tag,
            ["layer"] = state.Layer,
            ["parentId"] = state.ParentId,
            ["scenePath"] = state.ScenePath,
            ["primitive"] = state.Primitive,
            ["materialPath"] = state.MaterialPath,
            ["position"] = new JsonArray(state.Position.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["rotation"] = new JsonArray(state.Rotation.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["scale"] = new JsonArray(state.Scale.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["components"] = new JsonObject(state.Components.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, JsonHelpers.DeepClone(x.Value))).ToArray()),
        };

        if (state.HasSpriteRenderer)
        {
            obj["sprite"] = state.Sprite ?? string.Empty;
            obj["spritePath"] = state.SpritePath ?? string.Empty;
            obj["color"] = state.Color;
            obj["sortingLayerName"] = state.SortingLayerName;
            obj["sortingOrder"] = state.SortingOrder;
            obj["flipX"] = state.FlipX;
            obj["flipY"] = state.FlipY;
            obj["rendererEnabled"] = true;
        }

        return obj;
    }

    private JsonObject MaterialObject(MaterialState material)
    {
        return new JsonObject
        {
            ["path"] = material.Path,
            ["name"] = material.Name,
            ["shader"] = material.Shader,
            ["color"] = material.Color,
        };
    }

    private JsonObject UiElementObject(UiElementState element)
    {
        var obj = new JsonObject
        {
            ["name"] = element.Name,
            ["type"] = element.ElementType,
            ["canvasName"] = element.CanvasName,
            ["text"] = element.Text,
            ["anchoredPosition"] = element.AnchoredPosition,
            ["size"] = element.Size,
            ["color"] = element.Color,
        };

        if (element.ElementType == "Toggle")
        {
            obj["toggle"] = new JsonObject { ["isOn"] = element.IsOn };
            obj["isOn"] = element.IsOn;
        }

        if (element.ElementType == "Slider")
        {
            obj["slider"] = new JsonObject { ["value"] = element.Value, ["minValue"] = element.MinValue, ["maxValue"] = element.MaxValue };
            obj["value"] = element.Value;
        }

        if (element.ElementType == "ScrollRect")
        {
            obj["scrollRect"] = new JsonObject
            {
                ["normalizedPosition"] = new JsonArray(
                    JsonValue.Create(element.NormalizedPositionX),
                    JsonValue.Create(element.NormalizedPositionY)),
            };
            obj["normalizedPosition"] = new JsonArray(
                JsonValue.Create(element.NormalizedPositionX),
                JsonValue.Create(element.NormalizedPositionY));
        }

        if (element.ElementType == "InputField")
        {
            obj["placeholder"] = element.Placeholder;
        }

        return obj;
    }

    private UiElementState RequireUiElement(string name)
    {
        lock (_gate)
        {
            return _uiElements.TryGetValue(name, out var element)
                ? element
                : throw new InvalidOperationException($"UI element '{name}' was not found.");
        }
    }

    private string FindHitByWorld(string worldPosition)
    {
        return _gameObjects.Values.FirstOrDefault()?.Name ?? "World";
    }

    private BridgeEvent Emit(string type, string message, JsonNode? data)
    {
        TaskCompletionSource<bool> signalToRelease;
        BridgeEvent bridgeEvent;

        lock (_gate)
        {
            _cursor++;
            bridgeEvent = new BridgeEvent(_cursor, type, message, DateTimeOffset.UtcNow, JsonHelpers.DeepClone(data));
            _events.Add(bridgeEvent);
            signalToRelease = _eventSignal;
            _eventSignal = NewSignal();
        }

        signalToRelease.TrySetResult(true);
        return bridgeEvent;
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(250),
        };

        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(2))
        {
            try
            {
                using var response = await client.GetAsync(new Uri(new Uri(BaseUrl), "health"), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException($"Mock bridge did not become ready: {BaseUrl}");
    }

    private void Seed()
    {
        _scenes.Add(new SceneState("Assets/Scenes/SampleScene.unity", "SampleScene", true, false));
        _activeScenePath = "Assets/Scenes/SampleScene.unity";
        _packages.Add(new PackageState("com.unity.textmeshpro", "3.0.8"));
        _tests.AddRange(
        [
            new TestCaseState("EditMode.PlayerCanSpawn", "EditMode", "Smoke"),
            new TestCaseState("EditMode.MaterialCanBeAssigned", "EditMode", "Rendering"),
            new TestCaseState("PlayMode.PlayerSurvivesReload", "PlayMode", "Smoke"),
        ]);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonHelpers.SerializerOptions);
        await WriteTextAsync(context, json);
    }

    private static async Task WriteTextAsync(HttpListenerContext context, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private SceneState RequireScene(string path)
    {
        lock (_gate)
        {
            return _scenes.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? throw new MockBridgeException("not_found", 404, $"Scene '{path}' was not found.");
        }
    }

    private MaterialState RequireMaterial(string path)
    {
        lock (_gate)
        {
            return _materials.TryGetValue(path, out var material)
                ? material
                : throw new MockBridgeException("not_found", 404, $"Material '{path}' was not found.");
        }
    }

    private GameObjectState ResolveGameObject(JsonObject args)
    {
        var id = GetNullableString(args, "id");
        var name = GetNullableString(args, "name");

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(id) && _gameObjects.TryGetValue(id, out var byId))
            {
                return byId;
            }

            var byName = _gameObjects.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return byName ?? throw new MockBridgeException("not_found", 404, $"GameObject '{id ?? name}' was not found.");
        }
    }

    private static string GetString(JsonObject obj, string propertyName, string defaultValue)
    {
        return obj[propertyName]?.GetValue<string>() ?? defaultValue;
    }

    private static string? GetNullableString(JsonObject obj, string propertyName)
    {
        return obj[propertyName]?.GetValue<string>();
    }

    private static int TryParseInt(string? value, int defaultValue = 0)
    {
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static float[] ToVector(JsonArray? array, IReadOnlyList<float> fallback)
    {
        if (array is null || array.Count < 3)
        {
            return fallback.ToArray();
        }

        return
        [
            array[0]?.GetValue<float>() ?? fallback[0],
            array[1]?.GetValue<float>() ?? fallback[1],
            array[2]?.GetValue<float>() ?? fallback[2],
        ];
    }

    private sealed class SceneState(string path, string name, bool isLoaded, bool isDirty)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public bool IsLoaded { get; set; } = isLoaded;

        public bool IsDirty { get; set; } = isDirty;

        public int BuildIndex { get; set; } = -1;
    }

    private sealed class GameObjectState(string id, string name, string? parentId, string scenePath)
    {
        public string Id { get; set; } = id;

        public string Name { get; set; } = name;

        public string? ParentId { get; set; } = parentId;

        public string ScenePath { get; set; } = scenePath;

        public string? Primitive { get; set; }

        public string? MaterialPath { get; set; }

        public float[] Position { get; set; } = [0, 0, 0];

        public float[] Rotation { get; set; } = [0, 0, 0];

        public float[] Scale { get; set; } = [1, 1, 1];

        public bool Active { get; set; } = true;

        public string Tag { get; set; } = "Untagged";

        public int Layer { get; set; }

        public bool HasSpriteRenderer { get; set; }

        public string? Sprite { get; set; }

        public string? SpritePath { get; set; }

        public string Color { get; set; } = "#FFFFFFFF";

        public string SortingLayerName { get; set; } = "Default";

        public int SortingOrder { get; set; }

        public bool FlipX { get; set; }

        public bool FlipY { get; set; }

        public Dictionary<string, JsonObject> Components { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Transform"] = new JsonObject(),
        };

        public GameObjectState Clone()
        {
            var clone = new GameObjectState(Id, Name, ParentId, ScenePath)
            {
                Primitive = Primitive,
                MaterialPath = MaterialPath,
                Position = Position.ToArray(),
                Rotation = Rotation.ToArray(),
                Scale = Scale.ToArray(),
                Active = Active,
                Tag = Tag,
                Layer = Layer,
                HasSpriteRenderer = HasSpriteRenderer,
                Sprite = Sprite,
                SpritePath = SpritePath,
                Color = Color,
                SortingLayerName = SortingLayerName,
                SortingOrder = SortingOrder,
                FlipX = FlipX,
                FlipY = FlipY,
            };

            foreach (var pair in Components)
            {
                clone.Components[pair.Key] = JsonHelpers.EnsureObject(pair.Value.DeepClone());
            }

            return clone;
        }
    }

    private sealed class MaterialState(string path, string name, string shader)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public string Shader { get; set; } = shader;

        public string Color { get; set; } = "#FFFFFFFF";
    }

    private sealed record PackageState(string Name, string Version);

    private sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);

    private sealed record TestCaseState(string Name, string Mode, string Category = "");

    private sealed class UiElementState(string name, string elementType)
    {
        public string Name { get; } = name;

        public string ElementType { get; } = elementType;

        public string? CanvasName { get; set; }

        public string? Text { get; set; }

        public string AnchoredPosition { get; set; } = "0,0";

        public string Size { get; set; } = "100,100";

        public string Color { get; set; } = "#FFFFFFFF";

        public string? Placeholder { get; set; }

        public int ItemCount { get; set; }

        public bool IsOn { get; set; }

        public float Value { get; set; }

        public float MinValue { get; set; }

        public float MaxValue { get; set; } = 1;

        public float NormalizedPositionX { get; set; }

        public float NormalizedPositionY { get; set; } = 1;
    }
}
