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
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new ToolCallResponse(false, exception.Message, null, null));
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
            "component.update" => Success("Component updated.", UpdateComponent(args)),
            "material.create" => Success("Material created.", CreateMaterial(args)),
            "material.assign" => Success("Material assigned.", AssignMaterial(args)),
            "material.modify" => Success("Material modified.", ModifyMaterial(args)),
            "material.info" => Success("Material info fetched.", MaterialInfo(args)),
            "asset.list" => Success("Assets listed.", ListAssets(args)),
            "asset.add-to-scene" => Success("Asset added to scene.", AddAssetToScene(args)),
            "package.list" => Success("Packages listed.", ListPackages()),
            "package.add" => await AddPackageAsync(args, cancellationToken),
            "tests.list" => Success("Tests listed.", ListTests(args)),
            "tests.run" => await RunTestsAsync(args, cancellationToken),
            "console.get" => Success("Logs fetched.", GetLogs(args)),
            "console.clear" => Success("Logs cleared.", ClearLogs()),
            "console.send" => Success("Log emitted.", EmitConsoleLog(args)),
            "menu.execute" => Success("Menu command executed.", ExecuteMenu(args)),
            "sprite.create" => Success("Sprite created.", CreateSprite(args)),
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
            "editor.compile" => await CompileAsync(args, cancellationToken),
            "editor.play" => Success("Entered play mode.", SetPlayMode(true)),
            "editor.stop" => Success("Exited play mode.", SetPlayMode(false)),
            "editor.pause" => Success("Pause toggled.", TogglePause(args)),
            "editor.refresh" => Success("Editor refreshed.", RefreshEditor()),
            "editor.gameview.resize" => Success("GameView resized.", ResizeGameView(args)),
            _ => new ToolCallResponse(false, $"Unsupported tool '{request.Name}'.", null, null),
        };
    }

    private ToolCallResponse Success(string message, JsonNode? result, IReadOnlyList<BridgeEvent>? events = null)
    {
        return new ToolCallResponse(true, message, result, events);
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

    private JsonNode UpdateComponent(JsonObject args)
    {
        var state = ResolveGameObject(args);
        var type = GetString(args, "type", "Transform");
        var values = JsonHelpers.EnsureObject(args["values"]);

        lock (_gate)
        {
            state.Components[type] = JsonHelpers.EnsureObject(JsonHelpers.ReplaceVariables(values, new Dictionary<string, string>()));
        }

        Emit("component.changed", $"Component updated: {state.Name}/{type}", new JsonObject { ["id"] = state.Id, ["type"] = type });
        return GameObjectObject(state);
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
        var runId = Guid.NewGuid().ToString("N");
        var startedEvent = Emit("tests.started", $"Tests started: {mode}", new JsonObject { ["mode"] = mode, ["runId"] = runId });
        await Task.Delay(150, cancellationToken);
        var passed = _tests.Count(x => x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        var summary = new JsonObject { ["passed"] = passed, ["failed"] = 0, ["total"] = passed };
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
        var color = GetNullableString(args, "color") ?? "#FFFFFFFF";
        var goArgs = new JsonObject { ["name"] = name, ["primitive"] = "Sprite" };
        if (args["position"] is JsonArray pos)
        {
            goArgs["position"] = pos.DeepClone();
        }

        var result = CreateGameObject(goArgs);
        (result as JsonObject)!["color"] = color;
        return result;
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
            ["scene.changed", "scene.loaded", "scene.saved", "hierarchy.changed", "selection.changed", "component.changed", "asset.changed", "package.changed", "tests.started", "tests.completed", "console.log", "editor.compilation_started", "editor.compiled", "editor.play_mode_changed", "editor.pause_changed", "editor.refreshed", "menu.executed", "ui.created", "ui.focused", "ui.blurred", "ui.clicked", "ui.double_clicked", "ui.long_pressed", "ui.dragged", "ui.swiped", "ui.toggle_changed", "ui.slider_changed", "ui.inputfield_changed", "input.tapped", "input.double_tapped", "input.long_pressed", "input.dragged", "input.swiped"],
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
            new ToolDescriptor("scene.unload", "scene", "Unload a scene.", ["path"], []),
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
            new ToolDescriptor("sprite.create", "sprite", "Create a SpriteRenderer.", ["name"], ["position", "color"]),
            new ToolDescriptor("component.update", "component", "Patch a component.", ["type"], ["id", "name", "values"]),
            new ToolDescriptor("material.create", "material", "Create a material.", ["path"], ["name", "shader", "color"]),
            new ToolDescriptor("material.assign", "material", "Assign a material.", ["materialPath"], ["id", "name"]),
            new ToolDescriptor("material.modify", "material", "Modify a material.", ["path"], ["shader", "color"]),
            new ToolDescriptor("material.info", "material", "Fetch material info.", ["path"], []),
            new ToolDescriptor("asset.list", "asset", "List assets.", [], ["filter"]),
            new ToolDescriptor("asset.add-to-scene", "asset", "Instantiate an asset in the scene.", ["assetPath"], ["scenePath", "name"]),
            new ToolDescriptor("asset.import-texture", "asset", "Import a texture.", ["path"], []),
            new ToolDescriptor("package.list", "package", "List packages.", [], []),
            new ToolDescriptor("package.add", "package", "Install a package.", ["name"], ["version"]),
            new ToolDescriptor("tests.list", "tests", "List tests.", [], ["mode"]),
            new ToolDescriptor("tests.run", "tests", "Run tests.", [], ["mode", "assembly", "name"]),
            new ToolDescriptor("console.get", "console", "Fetch console logs.", [], ["level"]),
            new ToolDescriptor("console.clear", "console", "Clear console logs.", [], []),
            new ToolDescriptor("console.send", "console", "Emit a console log.", ["message"], ["level"]),
            new ToolDescriptor("menu.execute", "menu", "Execute a menu item.", ["path"], []),
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
            new ResourceDescriptor("packages/list", "Installed packages."),
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
            "packages/list" => new ResourceResponse(name, ListPackages()),
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
        };
    }

    private JsonObject GameObjectObject(GameObjectState state)
    {
        return new JsonObject
        {
            ["id"] = state.Id,
            ["name"] = state.Name,
            ["parentId"] = state.ParentId,
            ["scenePath"] = state.ScenePath,
            ["primitive"] = state.Primitive,
            ["materialPath"] = state.MaterialPath,
            ["position"] = new JsonArray(state.Position.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["rotation"] = new JsonArray(state.Rotation.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["scale"] = new JsonArray(state.Scale.Select(static x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["components"] = new JsonObject(state.Components.Select(x => new KeyValuePair<string, JsonNode?>(x.Key, JsonHelpers.DeepClone(x.Value))).ToArray()),
        };
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
            new TestCaseState("EditMode.PlayerCanSpawn", "EditMode"),
            new TestCaseState("EditMode.MaterialCanBeAssigned", "EditMode"),
            new TestCaseState("PlayMode.PlayerSurvivesReload", "PlayMode"),
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
                ?? throw new InvalidOperationException($"Scene '{path}' was not found.");
        }
    }

    private MaterialState RequireMaterial(string path)
    {
        lock (_gate)
        {
            return _materials.TryGetValue(path, out var material)
                ? material
                : throw new InvalidOperationException($"Material '{path}' was not found.");
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
            return byName ?? throw new InvalidOperationException($"GameObject '{id ?? name}' was not found.");
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

    private sealed record TestCaseState(string Name, string Mode);

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
