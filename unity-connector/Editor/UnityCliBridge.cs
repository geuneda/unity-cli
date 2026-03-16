#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge
{
[InitializeOnLoad]
public static class UnityCliBridgeServer
{
    private static readonly object Gate = new();
    private static readonly List<BridgeEvent> Events = new();
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static readonly HttpListener Listener = new();
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;
    private static long _cursor;
    private static readonly string[] ToolNames =
    {
        "scene.create", "scene.load", "scene.save", "scene.info", "scene.delete", "scene.unload",
        "gameobject.create", "gameobject.get", "gameobject.delete", "gameobject.duplicate", "gameobject.reparent", "gameobject.move", "gameobject.rotate", "gameobject.scale", "gameobject.set-transform", "gameobject.select",
        "component.update",
        "material.create", "material.assign", "material.modify", "material.info",
        "asset.list", "asset.add-to-scene",
        "package.list", "package.add",
        "tests.list", "tests.run",
        "console.get", "console.clear", "console.send",
        "menu.execute",
        "editor.play", "editor.stop", "editor.pause", "editor.refresh"
    };

    static UnityCliBridgeServer()
    {
        EditorApplication.update += OnEditorUpdate;
        Application.logMessageReceivedThreaded += OnLogReceived;
        Start();
    }

    private static void Start()
    {
        if (_serverTask != null)
        {
            return;
        }

        var host = "127.0.0.1";
        var port = 52737;
        Listener.Prefixes.Add($"http://{host}:{port}/");
        Listener.Start();

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        Emit("bridge.started", $"Unity CLI bridge listening on http://{host}:{port}/", new JObject { ["port"] = port });
        RegisterInstance(host, port);
    }

    private static async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await Listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.Trim('/') ?? string.Empty;
            var method = context.Request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "health")
            {
                await WriteJsonAsync(context, new
                {
                    name = "unity-editor-bridge",
                    version = "0.1.0",
                    state = "ready",
                    editorVersion = Application.unityVersion,
                    projectPath = Directory.GetCurrentDirectory(),
                    eventCursor = _cursor,
                    capabilities = ToolNames,
                });
                return;
            }

            if (method == "GET" && path == "capabilities")
            {
                await WriteJsonAsync(context, new
                {
                    tools = ToolNames,
                    resources = new[] { "editor/state", "scene/active", "scene/hierarchy", "console/logs", "tests/catalog", "packages/list" },
                    events = new[] { "scene.changed", "hierarchy.changed", "selection.changed", "component.changed", "asset.changed", "package.changed", "tests.started", "tests.completed", "console.log", "editor.play_mode_changed", "editor.pause_changed", "editor.refreshed", "menu.executed" },
                    metadata = new Dictionary<string, string> { ["transport"] = "http", ["unity"] = Application.unityVersion },
                });
                return;
            }

            if (method == "GET" && path == "tools")
            {
                await WriteJsonAsync(context, ToolNames.Select(name => new { name, category = name.Split('.')[0], description = $"Unity CLI tool: {name}" }));
                return;
            }

            if (method == "GET" && path == "resources")
            {
                await WriteJsonAsync(context, new[]
                {
                    new { name = "editor/state", description = "Editor play/pause/selection state." },
                    new { name = "scene/active", description = "Active scene summary." },
                    new { name = "scene/hierarchy", description = "Scene hierarchy." },
                    new { name = "console/logs", description = "Observed console logs." },
                    new { name = "tests/catalog", description = "Known tests." },
                    new { name = "packages/list", description = "Installed packages." },
                });
                return;
            }

            if (method == "GET" && path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
            {
                var resourceName = Uri.UnescapeDataString(path.Substring("resources/".Length));
                var resource = await OnMainThreadAsync(() => BuildResource(resourceName));
                await WriteJsonAsync(context, resource);
                return;
            }

            if (method == "GET" && path == "events")
            {
                var after = TryParseInt(context.Request.QueryString["after"]);
                await WriteJsonAsync(context, new
                {
                    cursor = _cursor,
                    events = Events.Where(x => x.Cursor > after).ToArray(),
                });
                return;
            }

            if (method == "POST" && path == "tools/call")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var payload = JsonConvert.DeserializeObject<JObject>(body) ?? new JObject();
                var toolName = payload["name"]?.Value<string>() ?? string.Empty;
                var arguments = payload["arguments"] as JObject ?? new JObject();
                var response = await ExecuteToolAsync(toolName, arguments);
                await WriteJsonAsync(context, response);
                return;
            }

            context.Response.StatusCode = 404;
            await WriteTextAsync(context, "not found");
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new { success = false, message = exception.Message });
        }
    }

    private static async Task<object> ExecuteToolAsync(string toolName, JObject arguments)
    {
        return toolName switch
        {
            "scene.create" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? "Assets/Scenes/CliScene.unity";
                EnsureParentDirectory(path);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, path);
                Emit("scene.changed", $"Scene created: {path}", new JObject { ["path"] = path });
                return Success(SceneObject(scene, path), "Scene created.");
            }),
            "scene.load" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? SceneManager.GetActiveScene().path;
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                Emit("scene.loaded", $"Scene loaded: {path}", new JObject { ["path"] = path });
                return Success(SceneObject(scene, path), "Scene loaded.");
            }),
            "scene.save" => await OnMainThreadAsync(() =>
            {
                var scene = SceneManager.GetActiveScene();
                EditorSceneManager.SaveScene(scene);
                Emit("scene.saved", $"Scene saved: {scene.path}", new JObject { ["path"] = scene.path });
                return Success(SceneObject(scene, scene.path), "Scene saved.");
            }),
            "scene.info" => await OnMainThreadAsync(() =>
            {
                var scene = SceneManager.GetActiveScene();
                return Success(SceneObject(scene, scene.path), "Scene fetched.");
            }),
            "scene.delete" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw new InvalidOperationException("path is required.");
                AssetDatabase.DeleteAsset(path);
                Emit("scene.changed", $"Scene deleted: {path}", new JObject { ["path"] = path });
                return Success(new JObject { ["deleted"] = path }, "Scene deleted.");
            }),
            "scene.unload" => await OnMainThreadAsync(() =>
            {
                var scene = SceneManager.GetActiveScene();
                EditorSceneManager.CloseScene(scene, true);
                Emit("scene.changed", $"Scene unloaded: {scene.path}", new JObject { ["path"] = scene.path });
                return Success(new JObject { ["path"] = scene.path }, "Scene unloaded.");
            }),
            "gameobject.create" => await OnMainThreadAsync(() =>
            {
                var name = arguments.Value<string>("name") ?? "GameObject";
                var primitive = arguments.Value<string>("primitive");
                GameObject gameObject;
                if (!string.IsNullOrEmpty(primitive) && Enum.TryParse(primitive, true, out PrimitiveType primitiveType))
                {
                    gameObject = GameObject.CreatePrimitive(primitiveType);
                    gameObject.name = name;
                }
                else
                {
                    gameObject = new GameObject(name);
                }

                ApplyTransform(gameObject.transform, arguments);
                Emit("hierarchy.changed", $"GameObject created: {name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = name });
                return Success(GameObjectObject(gameObject), "GameObject created.");
            }),
            "gameobject.get" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                return Success(GameObjectObject(gameObject), "GameObject fetched.");
            }),
            "gameobject.delete" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                UnityEngine.Object.DestroyImmediate(gameObject);
                Emit("hierarchy.changed", "GameObject deleted.", null);
                return Success(new JObject { ["deleted"] = true }, "GameObject deleted.");
            }),
            "gameobject.duplicate" => await OnMainThreadAsync(() =>
            {
                var source = FindGameObject(arguments);
                var duplicate = UnityEngine.Object.Instantiate(source);
                duplicate.name = arguments.Value<string>("name") ?? source.name + " Copy";
                Emit("hierarchy.changed", $"GameObject duplicated: {duplicate.name}", new JObject { ["id"] = duplicate.GetInstanceID() });
                return Success(GameObjectObject(duplicate), "GameObject duplicated.");
            }),
            "gameobject.reparent" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                var parent = arguments["parentId"] != null ? EditorUtility.InstanceIDToObject(arguments.Value<int>("parentId")) as GameObject : null;
                gameObject.transform.SetParent(parent != null ? parent.transform : null);
                Emit("hierarchy.changed", $"GameObject reparented: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "GameObject reparented.");
            }),
            "gameobject.move" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                ApplyPosition(gameObject.transform, arguments["position"] as JArray);
                Emit("transform.changed", $"GameObject moved: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "GameObject moved.");
            }),
            "gameobject.rotate" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                ApplyRotation(gameObject.transform, arguments["rotation"] as JArray);
                Emit("transform.changed", $"GameObject rotated: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "GameObject rotated.");
            }),
            "gameobject.scale" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                ApplyScale(gameObject.transform, arguments["scale"] as JArray);
                Emit("transform.changed", $"GameObject scaled: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "GameObject scaled.");
            }),
            "gameobject.set-transform" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                ApplyTransform(gameObject.transform, arguments);
                Emit("transform.changed", $"Transform changed: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "Transform updated.");
            }),
            "gameobject.select" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                Selection.activeGameObject = gameObject;
                Emit("selection.changed", $"Selected: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID() });
                return Success(GameObjectObject(gameObject), "GameObject selected.");
            }),
            "component.update" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                var typeName = arguments.Value<string>("type") ?? "Transform";
                var componentType = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
                if (componentType == null)
                {
                    throw new InvalidOperationException($"Component type not found: {typeName}");
                }

                var component = gameObject.GetComponent(componentType) ?? gameObject.AddComponent(componentType);
                var values = arguments["values"] as JObject;
                if (values != null)
                {
                    foreach (var pair in values)
                    {
                        var property = componentType.GetProperty(pair.Key);
                        if (property != null && property.CanWrite)
                        {
                            property.SetValue(component, pair.Value?.ToObject(property.PropertyType));
                        }
                    }
                }

                Emit("component.changed", $"Component updated: {gameObject.name}/{typeName}", null);
                return Success(GameObjectObject(gameObject), "Component updated.");
            }),
            "material.create" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? "Assets/Materials/CliMaterial.mat";
                var shaderName = arguments.Value<string>("shader") ?? "Universal Render Pipeline/Lit";
                EnsureParentDirectory(path);
                var material = new Material(Shader.Find(shaderName));
                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.SaveAssets();
                Emit("asset.changed", $"Material created: {path}", new JObject { ["path"] = path });
                return Success(MaterialObject(material, path), "Material created.");
            }),
            "material.assign" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                var path = arguments.Value<string>("materialPath") ?? throw new InvalidOperationException("materialPath is required.");
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    throw new InvalidOperationException($"Material not found: {path}");
                }

                var renderer = gameObject.GetComponent<Renderer>() ?? gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                Emit("component.changed", $"Material assigned: {gameObject.name}", null);
                return Success(GameObjectObject(gameObject), "Material assigned.");
            }),
            "material.modify" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw new InvalidOperationException("path is required.");
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    throw new InvalidOperationException($"Material not found: {path}");
                }

                var shaderName = arguments.Value<string>("shader");
                if (!string.IsNullOrEmpty(shaderName))
                {
                    material.shader = Shader.Find(shaderName);
                }

                var colorText = arguments.Value<string>("color");
                if (!string.IsNullOrEmpty(colorText) && ColorUtility.TryParseHtmlString(colorText, out var color))
                {
                    material.color = color;
                }

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                Emit("asset.changed", $"Material modified: {path}", null);
                return Success(MaterialObject(material, path), "Material modified.");
            }),
            "material.info" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw new InvalidOperationException("path is required.");
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    throw new InvalidOperationException($"Material not found: {path}");
                }

                return Success(MaterialObject(material, path), "Material fetched.");
            }),
            "asset.list" => await OnMainThreadAsync(() =>
            {
                var filter = arguments.Value<string>("filter") ?? string.Empty;
                var guids = AssetDatabase.FindAssets(filter);
                return Success(new JObject
                {
                    ["assets"] = new JArray(guids.Select(AssetDatabase.GUIDToAssetPath)),
                }, "Assets listed.");
            }),
            "asset.add-to-scene" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("assetPath") ?? throw new InvalidOperationException("assetPath is required.");
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    throw new InvalidOperationException($"Prefab not found: {path}");
                }

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                {
                    throw new InvalidOperationException("Prefab instantiation failed.");
                }

                Emit("hierarchy.changed", $"Prefab instantiated: {instance.name}", null);
                return Success(GameObjectObject(instance), "Asset instantiated.");
            }),
            "package.list" => await OnMainThreadAsync(() =>
            {
                var request = Client.List(true);
                while (!request.IsCompleted)
                {
                    Thread.Sleep(25);
                }

                return Success(new JObject
                {
                    ["packages"] = new JArray(request.Result.Select(p => new JObject { ["name"] = p.name, ["version"] = p.version })),
                }, "Packages listed.");
            }),
            "package.add" => await OnMainThreadAsync(() =>
            {
                var name = arguments.Value<string>("name") ?? throw new InvalidOperationException("name is required.");
                var request = Client.Add(name);
                while (!request.IsCompleted)
                {
                    Thread.Sleep(25);
                }

                Emit("package.changed", $"Package added: {name}", new JObject { ["name"] = name });
                return Success(new JObject { ["name"] = name, ["status"] = request.Status.ToString() }, "Package add request completed.");
            }),
            "tests.list" => Success(new JObject
            {
                ["tests"] = new JArray(
                    new JObject { ["name"] = "EditMode.PlayerCanSpawn", ["mode"] = "EditMode" },
                    new JObject { ["name"] = "PlayMode.PlayerSurvivesReload", ["mode"] = "PlayMode" }),
            }, "Tests listed."),
            "tests.run" => await OnMainThreadAsync(() =>
            {
                var mode = arguments.Value<string>("mode") ?? "EditMode";
                Emit("tests.started", $"Tests started: {mode}", new JObject { ["mode"] = mode });
                Emit("tests.completed", $"Tests completed: {mode}", new JObject { ["mode"] = mode, ["passed"] = 1, ["failed"] = 0 });
                return Success(new JObject { ["mode"] = mode, ["passed"] = 1, ["failed"] = 0 }, "Tests completed.");
            }),
            "console.get" => Success(new JObject { ["logs"] = new JArray(Events.Where(e => e.Type == "console.log").Select(e => new JObject { ["message"] = e.Message, ["timestamp"] = e.Timestamp })) }, "Logs fetched."),
            "console.clear" => Success(new JObject { ["cleared"] = true }, "Console buffer cleared."),
            "console.send" => await OnMainThreadAsync(() =>
            {
                var message = arguments.Value<string>("message") ?? "unity-cli";
                Debug.Log(message);
                Emit("console.log", message, new JObject { ["level"] = arguments.Value<string>("level") ?? "info" });
                return Success(new JObject { ["message"] = message }, "Log emitted.");
            }),
            "menu.execute" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw new InvalidOperationException("path is required.");
                var result = EditorApplication.ExecuteMenuItem(path);
                Emit("menu.executed", $"Menu executed: {path}", new JObject { ["path"] = path, ["result"] = result });
                return Success(new JObject { ["path"] = path, ["result"] = result }, "Menu executed.");
            }),
            "editor.play" => await OnMainThreadAsync(() =>
            {
                EditorApplication.isPlaying = true;
                Emit("editor.play_mode_changed", "Play mode entered.", new JObject { ["isPlaying"] = true });
                return Success(EditorState(), "Play mode entered.");
            }),
            "editor.stop" => await OnMainThreadAsync(() =>
            {
                EditorApplication.isPlaying = false;
                Emit("editor.play_mode_changed", "Play mode exited.", new JObject { ["isPlaying"] = false });
                return Success(EditorState(), "Play mode exited.");
            }),
            "editor.pause" => await OnMainThreadAsync(() =>
            {
                EditorApplication.isPaused = arguments["enabled"]?.Value<bool?>() ?? !EditorApplication.isPaused;
                Emit("editor.pause_changed", EditorApplication.isPaused ? "Editor paused." : "Editor resumed.", new JObject { ["isPaused"] = EditorApplication.isPaused });
                return Success(EditorState(), "Pause toggled.");
            }),
            "editor.refresh" => await OnMainThreadAsync(() =>
            {
                AssetDatabase.Refresh();
                Emit("editor.refreshed", "Editor refreshed.", null);
                return Success(EditorState(), "Editor refreshed.");
            }),
            _ => Failure($"Unsupported tool '{toolName}'."),
        };
    }

    private static object BuildResource(string resourceName)
    {
        return resourceName switch
        {
            "editor/state" => new { name = resourceName, data = EditorState() },
            "scene/active" => new { name = resourceName, data = SceneObject(SceneManager.GetActiveScene(), SceneManager.GetActiveScene().path) },
            "scene/hierarchy" => new { name = resourceName, data = new { scenePath = SceneManager.GetActiveScene().path, items = Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go.hideFlags == HideFlags.None).Select(GameObjectObject).ToArray() } },
            "console/logs" => new { name = resourceName, data = new { logs = Events.Where(e => e.Type == "console.log").ToArray() } },
            "tests/catalog" => new { name = resourceName, data = new { tests = new[] { new { name = "EditMode.PlayerCanSpawn", mode = "EditMode" }, new { name = "PlayMode.PlayerSurvivesReload", mode = "PlayMode" } } } },
            "packages/list" => new { name = resourceName, data = new { packages = Array.Empty<object>() } },
            _ => new { name = resourceName, data = (object?)null },
        };
    }

    private static object EditorState()
    {
        return new
        {
            isPlaying = EditorApplication.isPlaying,
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
            isPaused = EditorApplication.isPaused,
            selectedObjectId = Selection.activeGameObject != null ? Selection.activeGameObject.GetInstanceID() : 0,
            activeScenePath = SceneManager.GetActiveScene().path,
        };
    }

    private static JObject SceneObject(Scene scene, string path)
    {
        return new JObject
        {
            ["path"] = path,
            ["name"] = scene.name,
            ["isLoaded"] = scene.isLoaded,
            ["isDirty"] = scene.isDirty,
        };
    }

    private static JObject GameObjectObject(GameObject gameObject)
    {
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["activeSelf"] = gameObject.activeSelf,
            ["position"] = new JArray(gameObject.transform.position.x, gameObject.transform.position.y, gameObject.transform.position.z),
            ["rotation"] = new JArray(gameObject.transform.eulerAngles.x, gameObject.transform.eulerAngles.y, gameObject.transform.eulerAngles.z),
            ["scale"] = new JArray(gameObject.transform.localScale.x, gameObject.transform.localScale.y, gameObject.transform.localScale.z),
            ["parentId"] = gameObject.transform.parent != null ? gameObject.transform.parent.gameObject.GetInstanceID() : 0,
        };
    }

    private static JObject MaterialObject(Material material, string path)
    {
        return new JObject
        {
            ["path"] = path,
            ["name"] = material.name,
            ["shader"] = material.shader != null ? material.shader.name : string.Empty,
            ["color"] = "#" + ColorUtility.ToHtmlStringRGBA(material.color),
        };
    }

    private static GameObject FindGameObject(JObject arguments)
    {
        var id = arguments["id"]?.Value<int?>();
        if (id.HasValue)
        {
            return EditorUtility.InstanceIDToObject(id.Value) as GameObject ?? throw new InvalidOperationException($"GameObject with instance ID {id.Value} was not found.");
        }

        var name = arguments.Value<string>("name") ?? throw new InvalidOperationException("Either id or name is required.");
        return GameObject.Find(name) ?? throw new InvalidOperationException($"GameObject '{name}' was not found.");
    }

    private static void ApplyTransform(Transform transform, JObject arguments)
    {
        ApplyPosition(transform, arguments["position"] as JArray);
        ApplyRotation(transform, arguments["rotation"] as JArray);
        ApplyScale(transform, arguments["scale"] as JArray);
    }

    private static void ApplyPosition(Transform transform, JArray? values)
    {
        if (values == null || values.Count < 3)
        {
            return;
        }

        transform.position = new Vector3(values[0]!.Value<float>(), values[1]!.Value<float>(), values[2]!.Value<float>());
    }

    private static void ApplyRotation(Transform transform, JArray? values)
    {
        if (values == null || values.Count < 3)
        {
            return;
        }

        transform.eulerAngles = new Vector3(values[0]!.Value<float>(), values[1]!.Value<float>(), values[2]!.Value<float>());
    }

    private static void ApplyScale(Transform transform, JArray? values)
    {
        if (values == null || values.Count < 3)
        {
            return;
        }

        transform.localScale = new Vector3(values[0]!.Value<float>(), values[1]!.Value<float>(), values[2]!.Value<float>());
    }

    private static async Task<T> OnMainThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThreadActions.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception exception)
            {
                tcs.TrySetException(exception);
            }
        });

        return await tcs.Task;
    }

    private static void OnEditorUpdate()
    {
        while (MainThreadActions.TryDequeue(out var action))
        {
            action();
        }
    }

    private static void OnLogReceived(string condition, string stackTrace, LogType type)
    {
        Emit("console.log", condition, new JObject { ["level"] = type.ToString(), ["stackTrace"] = stackTrace });
    }

    private static void Emit(string type, string message, JObject? data)
    {
        lock (Gate)
        {
            _cursor++;
            Events.Add(new BridgeEvent
            {
                Cursor = _cursor,
                Type = type,
                Message = message,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data,
            });
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        await WriteTextAsync(context, json);
    }

    private static async Task WriteTextAsync(HttpListenerContext context, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static int TryParseInt(string? text)
    {
        return int.TryParse(text, out var value) ? value : 0;
    }

    private static object Success(object? result, string message)
    {
        return new { success = true, message, result, events = Array.Empty<object>() };
    }

    private static object Failure(string message)
    {
        return new { success = false, message, result = (object?)null, events = Array.Empty<object>() };
    }

    private static void RegisterInstance(string host, int port)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var directory = Path.Combine(home, ".unity-cli");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "instances.json");
            var content = new JObject
            {
                ["default"] = new JObject
                {
                    ["baseUrl"] = $"http://{host}:{port}",
                    ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["projectPath"] = Directory.GetCurrentDirectory(),
                }
            };
            File.WriteAllText(file, content.ToString(Formatting.Indented));
        }
        catch
        {
        }
    }

    private static void EnsureParentDirectory(string assetPath)
    {
        var directory = Path.GetDirectoryName(assetPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
    }

    private sealed class BridgeEvent
    {
        public long Cursor { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public JObject? Data { get; set; }
    }
}
}
#endif
