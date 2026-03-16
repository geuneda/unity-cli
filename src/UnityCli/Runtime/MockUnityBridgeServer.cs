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
    private readonly List<PackageState> _packages = [];
    private readonly List<LogEntry> _logs = [];
    private readonly List<TestCaseState> _tests = [];
    private TaskCompletionSource<bool> _eventSignal = NewSignal();
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private long _cursor;
    private string? _activeScenePath;
    private string? _selectedObjectId;
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
            "editor.play" => Success("Entered play mode.", SetPlayMode(true)),
            "editor.stop" => Success("Exited play mode.", SetPlayMode(false)),
            "editor.pause" => Success("Pause toggled.", TogglePause(args)),
            "editor.refresh" => Success("Editor refreshed.", RefreshEditor()),
            _ => new ToolCallResponse(false, $"Unsupported tool '{request.Name}'.", null, null),
        };
    }

    private ToolCallResponse Success(string message, JsonNode? result)
    {
        return new ToolCallResponse(true, message, result, null);
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
        Emit("tests.started", $"Tests started: {mode}", new JsonObject { ["mode"] = mode });
        await Task.Delay(150, cancellationToken);
        var passed = _tests.Count(x => x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        Emit("tests.completed", $"Tests completed: {mode}", new JsonObject { ["mode"] = mode, ["passed"] = passed, ["failed"] = 0 });
        return Success("Tests completed.", new JsonObject { ["mode"] = mode, ["passed"] = passed, ["failed"] = 0 });
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
            ["scene.changed", "scene.loaded", "scene.saved", "hierarchy.changed", "selection.changed", "component.changed", "asset.changed", "package.changed", "tests.started", "tests.completed", "console.log", "editor.play_mode_changed", "editor.pause_changed", "editor.refreshed", "menu.executed"],
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
            new ToolDescriptor("component.update", "component", "Patch a component.", ["type"], ["id", "name", "values"]),
            new ToolDescriptor("material.create", "material", "Create a material.", ["path"], ["name", "shader", "color"]),
            new ToolDescriptor("material.assign", "material", "Assign a material.", ["materialPath"], ["id", "name"]),
            new ToolDescriptor("material.modify", "material", "Modify a material.", ["path"], ["shader", "color"]),
            new ToolDescriptor("material.info", "material", "Fetch material info.", ["path"], []),
            new ToolDescriptor("asset.list", "asset", "List assets.", [], ["filter"]),
            new ToolDescriptor("asset.add-to-scene", "asset", "Instantiate an asset in the scene.", ["assetPath"], ["scenePath", "name"]),
            new ToolDescriptor("package.list", "package", "List packages.", [], []),
            new ToolDescriptor("package.add", "package", "Install a package.", ["name"], ["version"]),
            new ToolDescriptor("tests.list", "tests", "List tests.", [], ["mode"]),
            new ToolDescriptor("tests.run", "tests", "Run tests.", [], ["mode"]),
            new ToolDescriptor("console.get", "console", "Fetch console logs.", [], ["level"]),
            new ToolDescriptor("console.clear", "console", "Clear console logs.", [], []),
            new ToolDescriptor("console.send", "console", "Emit a console log.", ["message"], ["level"]),
            new ToolDescriptor("menu.execute", "menu", "Execute a menu item.", ["path"], []),
            new ToolDescriptor("editor.play", "editor", "Enter play mode.", [], []),
            new ToolDescriptor("editor.stop", "editor", "Exit play mode.", [], []),
            new ToolDescriptor("editor.pause", "editor", "Pause or resume.", [], ["enabled"]),
            new ToolDescriptor("editor.refresh", "editor", "Refresh editor.", [], []),
        ];
    }

    private IReadOnlyList<ResourceDescriptor> ResourceCatalog()
    {
        return
        [
            new ResourceDescriptor("editor/state", "Editor play/pause/selection state."),
            new ResourceDescriptor("scene/active", "Active scene summary."),
            new ResourceDescriptor("scene/hierarchy", "Hierarchy for the active scene."),
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

    private JsonNode BuildEditorState()
    {
        return new JsonObject
        {
            ["isPlaying"] = _playMode,
            ["isPaused"] = _pauseMode,
            ["selectedObjectId"] = _selectedObjectId,
            ["activeScenePath"] = _activeScenePath,
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

    private void Emit(string type, string message, JsonNode? data)
    {
        TaskCompletionSource<bool> signalToRelease;

        lock (_gate)
        {
            _cursor++;
            _events.Add(new BridgeEvent(_cursor, type, message, DateTimeOffset.UtcNow, JsonHelpers.DeepClone(data)));
            signalToRelease = _eventSignal;
            _eventSignal = NewSignal();
        }

        signalToRelease.TrySetResult(true);
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
}
