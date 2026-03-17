#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly string[] ToolNames =
    {
        "scene.create", "scene.load", "scene.save", "scene.info", "scene.delete", "scene.unload",
        "gameobject.create", "gameobject.get", "gameobject.delete", "gameobject.duplicate", "gameobject.reparent", "gameobject.move", "gameobject.rotate", "gameobject.scale", "gameobject.set-transform", "gameobject.select",
        "sprite.create",
        "component.update",
        "material.create", "material.assign", "material.modify", "material.info",
        "asset.list", "asset.add-to-scene",
        "package.list", "package.add",
        "tests.list", "tests.run",
        "console.get", "console.clear", "console.send",
        "ui.canvas.create", "ui.button.create", "ui.text.create", "ui.image.create", "ui.click", "ui.drag",
        "input.tap", "input.drag",
        "menu.execute",
        "editor.play", "editor.stop", "editor.pause", "editor.refresh", "editor.compile"
    };

    static UnityCliBridgeServer()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
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
        try
        {
            Listener.Prefixes.Add($"http://{host}:{port}/");
            Listener.Start();
        }
        catch (HttpListenerException)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        Emit("bridge.started", $"Unity CLI bridge listening on http://{host}:{port}/", new JObject { ["port"] = port });
        RegisterInstance(host, port);
    }

    private static void Stop()
    {
        try
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (Listener.IsListening)
            {
                Listener.Stop();
            }

            Listener.Close();
        }
        catch
        {
        }
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
                    resources = new[] { "editor/state", "scene/active", "scene/hierarchy", "ui/hierarchy", "console/logs", "tests/catalog", "packages/list" },
                    events = new[] { "scene.changed", "hierarchy.changed", "selection.changed", "component.changed", "asset.changed", "package.changed", "tests.started", "tests.completed", "console.log", "ui.clicked", "ui.dragged", "input.tapped", "input.dragged", "editor.play_mode_changed", "editor.pause_changed", "editor.refreshed", "editor.compilation_started", "editor.compiled", "menu.executed" },
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
                    new { name = "ui/hierarchy", description = "UI hierarchy for active canvases." },
                    new { name = "console/logs", description = "Observed console logs." },
                    new { name = "tests/catalog", description = "Known tests." },
                    new { name = "packages/list", description = "Installed packages." },
                });
                return;
            }

            if (method == "GET" && path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
            {
                var resourceName = Uri.UnescapeDataString(path.Substring("resources/".Length));
                var resource = await BuildResourceAsync(resourceName);
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
            "sprite.create" => await OnMainThreadAsync(() =>
            {
                var gameObject = CreateSprite(arguments);
                Emit("hierarchy.changed", $"Sprite created: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name });
                return Success(GameObjectObject(gameObject), "Sprite created.");
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
                return Success(new JObject
                {
                    ["packages"] = GetInstalledPackages(),
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
                ["tests"] = await GetTestsCatalogAsync(arguments.Value<string>("mode")),
            }, "Tests listed."),
            "tests.run" => await StartTestsAsync(arguments),
            "console.get" => Success(new JObject { ["logs"] = new JArray(Events.Where(e => e.Type == "console.log").Select(e => new JObject { ["message"] = e.Message, ["timestamp"] = e.Timestamp })) }, "Logs fetched."),
            "console.clear" => await OnMainThreadAsync(() =>
            {
                ClearConsoleBuffer();
                return Success(new JObject { ["cleared"] = true }, "Console buffer cleared.");
            }),
            "console.send" => await OnMainThreadAsync(() =>
            {
                var message = arguments.Value<string>("message") ?? "unity-cli";
                Debug.Log(message);
                Emit("console.log", message, new JObject { ["level"] = arguments.Value<string>("level") ?? "info" });
                return Success(new JObject { ["message"] = message }, "Log emitted.");
            }),
            "ui.canvas.create" => await OnMainThreadAsync(() =>
            {
                var canvas = EnsureCanvas(arguments.Value<string>("name") ?? "Canvas");
                Emit("hierarchy.changed", $"Canvas ready: {canvas.name}", new JObject { ["id"] = canvas.GetInstanceID(), ["name"] = canvas.name });
                return Success(GameObjectObject(canvas), "Canvas created.");
            }),
            "ui.button.create" => await OnMainThreadAsync(() =>
            {
                var button = CreateButton(arguments);
                Emit("hierarchy.changed", $"Button created: {button.name}", new JObject { ["id"] = button.GetInstanceID(), ["name"] = button.name });
                return Success(GameObjectObject(button), "Button created.");
            }),
            "ui.text.create" => await OnMainThreadAsync(() =>
            {
                var text = CreateText(arguments);
                Emit("hierarchy.changed", $"Text created: {text.name}", new JObject { ["id"] = text.GetInstanceID(), ["name"] = text.name });
                return Success(GameObjectObject(text), "Text created.");
            }),
            "ui.image.create" => await OnMainThreadAsync(() =>
            {
                var image = CreateImage(arguments);
                Emit("hierarchy.changed", $"Image created: {image.name}", new JObject { ["id"] = image.GetInstanceID(), ["name"] = image.name });
                return Success(GameObjectObject(image), "Image created.");
            }),
            "ui.click" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchTap(arguments, "ui.clicked"), "UI click dispatched.");
            }),
            "ui.drag" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "ui.dragged"), "UI drag dispatched.");
            }),
            "input.tap" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchTap(arguments, "input.tapped"), "Input tap dispatched.");
            }),
            "input.drag" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "input.dragged"), "Input drag dispatched.");
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
            "editor.compile" => await OnMainThreadAsync(() => RequestScriptCompilation()),
            _ => Failure($"Unsupported tool '{toolName}'."),
        };
    }

    private static async Task<object> BuildResourceAsync(string resourceName)
    {
        switch (resourceName)
        {
            case "editor/state":
                return await OnMainThreadAsync(() => new { name = resourceName, data = EditorState() });
            case "scene/active":
                return await OnMainThreadAsync(() => new { name = resourceName, data = SceneObject(SceneManager.GetActiveScene(), SceneManager.GetActiveScene().path) });
            case "scene/hierarchy":
                return await OnMainThreadAsync(() => new { name = resourceName, data = new { scenePath = SceneManager.GetActiveScene().path, items = Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go.hideFlags == HideFlags.None).Select(GameObjectObject).ToArray() } });
            case "ui/hierarchy":
                return await OnMainThreadAsync(() => new { name = resourceName, data = new { scenePath = SceneManager.GetActiveScene().path, items = Resources.FindObjectsOfTypeAll<RectTransform>().Where(IsRuntimeObject).Select(rt => UiObject(rt.gameObject)).ToArray() } });
            case "console/logs":
                return new { name = resourceName, data = new { logs = Events.Where(e => e.Type == "console.log").ToArray() } };
            case "tests/catalog":
                return new { name = resourceName, data = new { tests = await GetTestsCatalogAsync("All") } };
            case "packages/list":
                return await OnMainThreadAsync(() => new { name = resourceName, data = new { packages = GetInstalledPackages() } });
            default:
                return new { name = resourceName, data = (object?)null };
        }
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
        var result = new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["activeSelf"] = gameObject.activeSelf,
            ["position"] = new JArray(gameObject.transform.position.x, gameObject.transform.position.y, gameObject.transform.position.z),
            ["rotation"] = new JArray(gameObject.transform.eulerAngles.x, gameObject.transform.eulerAngles.y, gameObject.transform.eulerAngles.z),
            ["scale"] = new JArray(gameObject.transform.localScale.x, gameObject.transform.localScale.y, gameObject.transform.localScale.z),
            ["parentId"] = gameObject.transform.parent != null ? gameObject.transform.parent.gameObject.GetInstanceID() : 0,
        };

        var rectTransform = gameObject.GetComponent<RectTransform>();
        if (rectTransform != null)
        {
            result["anchoredPosition"] = new JArray(rectTransform.anchoredPosition.x, rectTransform.anchoredPosition.y);
            result["sizeDelta"] = new JArray(rectTransform.sizeDelta.x, rectTransform.sizeDelta.y);
        }

        var spriteRenderer = gameObject.GetComponent<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            result["sprite"] = spriteRenderer.sprite != null ? spriteRenderer.sprite.name : string.Empty;
            result["color"] = "#" + ColorUtility.ToHtmlStringRGBA(spriteRenderer.color);
        }

        return result;
    }

    private static JObject UiObject(GameObject gameObject)
    {
        var result = GameObjectObject(gameObject);
        result["isCanvas"] = gameObject.GetComponent<Canvas>() != null;
        result["isSelectable"] = gameObject.GetComponent<Selectable>() != null;
        result["text"] = gameObject.GetComponent<Text>() != null ? gameObject.GetComponent<Text>().text : null;
        return result;
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

    private static JArray GetInstalledPackages()
    {
        var request = Client.List(true);
        while (!request.IsCompleted)
        {
            Thread.Sleep(25);
        }

        return new JArray(request.Result.Select(p => new JObject
        {
            ["name"] = p.name,
            ["version"] = p.version,
            ["source"] = p.source.ToString(),
        }));
    }

    private static async Task<JArray> GetTestsCatalogAsync(string? modeText)
    {
        var items = new JArray();
        foreach (var mode in ParseRequestedTestModes(modeText))
        {
            foreach (var test in await RetrieveTestsAsync(mode))
            {
                items.Add(TestDescriptor(test, mode));
            }
        }

        return items;
    }

    private static async Task<object> StartTestsAsync(JObject arguments)
    {
        var requestedMode = ParseRequestedTestMode(arguments.Value<string>("mode"));
        var requestedNames = ParseRequestedTestNames(arguments);
        var runId = Guid.NewGuid().ToString("N");

        if (requestedMode == TestMode.EditMode)
        {
            var availableTests = await RetrieveTestsAsync(requestedMode);
            if (requestedNames == null || requestedNames.Length == 0)
            {
                if (availableTests.Count == 0)
                {
                    return Failure("No tests were found for the requested mode.");
                }
            }
            else
            {
                requestedNames = availableTests
                    .Where(test => requestedNames.Contains(test.FullName, StringComparer.OrdinalIgnoreCase)
                        || requestedNames.Contains(test.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(test => test.FullName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (requestedNames.Length == 0)
                {
                    return Failure("No matching tests were found.");
                }
            }
        }

        var usesPersistentPlayModeTracking = requestedMode == TestMode.PlayMode;
        if (usesPersistentPlayModeTracking && TryGetActiveTestRun(out var activeRun))
        {
            return Failure($"A test run is already active (runId={activeRun.RunId}).");
        }

        if (usesPersistentPlayModeTracking)
        {
            StorePendingTestRun(runId, requestedMode, requestedNames);
        }

        await OnMainThreadAsync(() =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            if (usesPersistentPlayModeTracking)
            {
                TestRunnerApi.RegisterTestCallback(new UnityCliTestErrorCallbacks(runId, requestedMode));
            }
            else
            {
                var callbacks = ScriptableObject.CreateInstance<UnityCliTestRunCallbacks>();
                callbacks.Initialize(runId, requestedMode, requestedNames);
                TestRunnerApi.RegisterTestCallback(callbacks);
            }

            api.Execute(new ExecutionSettings(BuildTestFilter(requestedMode, requestedNames)));
            return true;
        });

        return Success(new JObject
        {
            ["runId"] = runId,
            ["mode"] = TestModeName(requestedMode),
            ["status"] = "started",
        }, "Tests started.");
    }

    private static async Task<IReadOnlyList<ITestAdaptor>> RetrieveTestsAsync(TestMode mode)
    {
        var completion = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnMainThreadAsync(() =>
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RetrieveTestList(mode, root =>
            {
                completion.TrySetResult(root);
                UnityEngine.Object.DestroyImmediate(api);
            });
            return true;
        });

        var root = await completion.Task;
        return EnumerateLeafTests(root)
            .Where(test => !test.IsSuite)
            .OrderBy(test => test.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<ITestAdaptor> EnumerateLeafTests(ITestAdaptor test)
    {
        if (!test.HasChildren)
        {
            yield return test;
            yield break;
        }

        foreach (var child in test.Children)
        {
            foreach (var descendant in EnumerateLeafTests(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<NUnit.Framework.Interfaces.ITestResult> EnumerateLeafResults(NUnit.Framework.Interfaces.ITestResult result)
    {
        if (!result.HasChildren)
        {
            yield return result;
            yield break;
        }

        foreach (var child in result.Children)
        {
            foreach (var descendant in EnumerateLeafResults(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<ITestResultAdaptor> EnumerateLeafAdaptorResults(ITestResultAdaptor result)
    {
        if (!result.HasChildren)
        {
            yield return result;
            yield break;
        }

        foreach (var child in result.Children)
        {
            foreach (var descendant in EnumerateLeafAdaptorResults(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountLeafTests(NUnit.Framework.Interfaces.ITest test)
    {
        if (!test.HasChildren)
        {
            return 1;
        }

        return test.Tests.Sum(CountLeafTests);
    }

    private static Filter BuildTestFilter(TestMode mode, string[]? testNames)
    {
        var filter = new Filter { testMode = mode };
        if (testNames != null && testNames.Length > 0)
        {
            filter.testNames = testNames;
        }

        return filter;
    }

    private static JObject TestDescriptor(ITestAdaptor test, TestMode mode)
    {
        return new JObject
        {
            ["name"] = test.FullName,
            ["displayName"] = test.Name,
            ["mode"] = TestModeName(mode),
            ["assembly"] = test.Parent != null && test.Parent.IsTestAssembly ? test.Parent.Name : string.Empty,
            ["categories"] = new JArray(test.Categories ?? Array.Empty<string>()),
        };
    }

    private static JObject TestResultDescriptor(NUnit.Framework.Interfaces.ITestResult result)
    {
        return new JObject
        {
            ["name"] = result.FullName,
            ["status"] = result.ResultState.Status.ToString(),
            ["durationSeconds"] = result.Duration,
            ["message"] = result.Message ?? string.Empty,
        };
    }

    private static JObject TestResultDescriptor(ITestResultAdaptor result)
    {
        return new JObject
        {
            ["name"] = result.FullName,
            ["status"] = result.TestStatus.ToString(),
            ["durationSeconds"] = result.Duration,
            ["message"] = result.Message ?? string.Empty,
        };
    }

    internal static JObject BuildTestRunSummary(NUnit.Framework.Interfaces.ITestResult result, TestMode mode)
    {
        var leafResults = EnumerateLeafResults(result).ToArray();
        return new JObject
        {
            ["mode"] = TestModeName(mode),
            ["passed"] = result.PassCount,
            ["failed"] = result.FailCount,
            ["skipped"] = result.SkipCount,
            ["inconclusive"] = result.InconclusiveCount,
            ["durationSeconds"] = result.Duration,
            ["tests"] = new JArray(leafResults.Select(TestResultDescriptor)),
        };
    }

    internal static JObject BuildTestRunSummary(ITestResultAdaptor result, TestMode mode)
    {
        var leafResults = EnumerateLeafAdaptorResults(result).ToArray();
        return new JObject
        {
            ["mode"] = TestModeName(mode),
            ["passed"] = result.PassCount,
            ["failed"] = result.FailCount,
            ["skipped"] = result.SkipCount,
            ["inconclusive"] = result.InconclusiveCount,
            ["durationSeconds"] = result.Duration,
            ["tests"] = new JArray(leafResults.Select(TestResultDescriptor)),
        };
    }

    internal static JObject BuildFailedTestRunSummary(TestMode mode, string message)
    {
        return new JObject
        {
            ["mode"] = TestModeName(mode),
            ["passed"] = 0,
            ["failed"] = 1,
            ["skipped"] = 0,
            ["inconclusive"] = 0,
            ["durationSeconds"] = 0,
            ["tests"] = new JArray
            {
                new JObject
                {
                    ["name"] = TestModeName(mode),
                    ["status"] = "Failed",
                    ["durationSeconds"] = 0,
                    ["message"] = message,
                }
            },
        };
    }

    internal static void EmitTestRunStarted(string runId, TestMode mode, string[] requestedNames, int count)
    {
        Emit("tests.started", $"Tests started: {TestModeName(mode)}", new JObject
        {
            ["runId"] = runId,
            ["mode"] = TestModeName(mode),
            ["count"] = count,
            ["names"] = new JArray(requestedNames ?? Array.Empty<string>()),
        });
    }

    internal static void EmitTestRunCompleted(string runId, TestMode mode, JObject summary)
    {
        Emit("tests.completed", $"Tests completed: {TestModeName(mode)}", new JObject
        {
            ["runId"] = runId,
            ["mode"] = TestModeName(mode),
            ["passed"] = summary.Value<int>("passed"),
            ["failed"] = summary.Value<int>("failed"),
            ["skipped"] = summary.Value<int>("skipped"),
            ["inconclusive"] = summary.Value<int>("inconclusive"),
            ["summary"] = summary,
        });
    }

    private static TestMode[] ParseRequestedTestModes(string? modeText)
    {
        if (string.IsNullOrWhiteSpace(modeText) || string.Equals(modeText, "EditMode", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { TestMode.EditMode };
        }

        if (string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { TestMode.PlayMode };
        }

        if (string.Equals(modeText, "All", StringComparison.OrdinalIgnoreCase) || string.Equals(modeText, "Both", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { TestMode.EditMode, TestMode.PlayMode };
        }

        throw new InvalidOperationException($"Unsupported test mode: {modeText}");
    }

    private static TestMode ParseRequestedTestMode(string? modeText)
    {
        if (string.IsNullOrWhiteSpace(modeText) || string.Equals(modeText, "EditMode", StringComparison.OrdinalIgnoreCase))
        {
            return TestMode.EditMode;
        }

        if (string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase))
        {
            return TestMode.PlayMode;
        }

        if (string.Equals(modeText, "All", StringComparison.OrdinalIgnoreCase) || string.Equals(modeText, "Both", StringComparison.OrdinalIgnoreCase))
        {
            return TestMode.EditMode | TestMode.PlayMode;
        }

        throw new InvalidOperationException($"Unsupported test mode: {modeText}");
    }

    internal static string TestModeName(TestMode mode)
    {
        if (mode == (TestMode.EditMode | TestMode.PlayMode))
        {
            return "All";
        }

        return mode == TestMode.PlayMode ? "PlayMode" : "EditMode";
    }

    private static TestMode ParseStoredTestMode(string? modeText)
    {
        return ParseRequestedTestMode(modeText);
    }

    private static string[]? ParseRequestedTestNames(JObject arguments)
    {
        var singleName = arguments.Value<string>("name");
        if (!string.IsNullOrWhiteSpace(singleName))
        {
            return new[] { singleName };
        }

        var names = arguments["names"] as JArray;
        if (names == null || names.Count == 0)
        {
            return null;
        }

        return names
            .Select(token => token?.Value<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static void ClearConsoleBuffer()
    {
        lock (Gate)
        {
            Events.RemoveAll(e => e.Type == "console.log");
        }

        var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
        var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        clearMethod?.Invoke(null, null);
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

    private static GameObject CreateSprite(JObject arguments)
    {
        var name = arguments.Value<string>("name") ?? "Sprite";
        var gameObject = new GameObject(name);
        var spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        spriteRenderer.color = ParseColor(arguments.Value<string>("color"), Color.white);
        ApplyTransform(gameObject.transform, arguments);

        if (arguments["collider"]?.Value<bool?>() ?? true)
        {
            gameObject.AddComponent<BoxCollider2D>();
        }

        return gameObject;
    }

    private static GameObject CreateButton(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var buttonObject = new GameObject(arguments.Value<string>("name") ?? "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(canvas.transform, false);
        ApplyRectTransform(buttonObject.GetComponent<RectTransform>(), arguments, new Vector2(160f, 48f));

        var image = buttonObject.GetComponent<Image>();
        image.color = ParseColor(arguments.Value<string>("color"), new Color(0.15f, 0.55f, 0.95f, 1f));

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var text = labelObject.GetComponent<Text>();
        text.text = arguments.Value<string>("text") ?? buttonObject.name;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = ParseColor(arguments.Value<string>("textColor"), Color.white);
        text.font = LoadBuiltinFont();

        EnsureEventSystem();
        return buttonObject;
    }

    private static GameObject CreateText(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var textObject = new GameObject(arguments.Value<string>("name") ?? "Text", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(canvas.transform, false);
        ApplyRectTransform(textObject.GetComponent<RectTransform>(), arguments, new Vector2(240f, 48f));

        var text = textObject.GetComponent<Text>();
        text.text = arguments.Value<string>("text") ?? textObject.name;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = ParseColor(arguments.Value<string>("color"), Color.white);
        text.font = LoadBuiltinFont();
        EnsureEventSystem();
        return textObject;
    }

    private static GameObject CreateImage(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var imageObject = new GameObject(arguments.Value<string>("name") ?? "Image", typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(canvas.transform, false);
        ApplyRectTransform(imageObject.GetComponent<RectTransform>(), arguments, new Vector2(128f, 128f));
        var image = imageObject.GetComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        image.color = ParseColor(arguments.Value<string>("color"), Color.white);
        EnsureEventSystem();
        return imageObject;
    }

    private static GameObject EnsureCanvas(string canvasName)
    {
        var existing = GameObject.Find(canvasName);
        if (existing != null && existing.GetComponent<Canvas>() != null)
        {
            EnsureEventSystem();
            return existing;
        }

        var canvasObject = new GameObject(canvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        EnsureEventSystem();
        return canvasObject;
    }

    private static EventSystem EnsureEventSystem()
    {
        var existing = UnityEngine.Object.FindObjectOfType<EventSystem>();
        if (existing != null)
        {
            if (existing.GetComponent<StandaloneInputModule>() == null)
            {
                existing.gameObject.AddComponent<StandaloneInputModule>();
            }

            return existing;
        }

        var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        return eventSystemObject.GetComponent<EventSystem>();
    }

    private static JObject DispatchTap(JObject arguments, string eventType)
    {
        var gameObject = FindGameObject(arguments);
        var eventSystem = EnsureEventSystem();
        var position = ParseVector2(arguments["position"] as JArray, Vector2.zero);
        var eventData = new PointerEventData(eventSystem)
        {
            button = PointerEventData.InputButton.Left,
            position = position,
            pressPosition = position,
            pointerId = -1,
        };

        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerClickHandler);

        Emit(eventType, $"Pointer tap: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["position"] = new JArray(position.x, position.y),
        };
    }

    private static JObject DispatchDrag(JObject arguments, string eventType)
    {
        var gameObject = FindGameObject(arguments);
        var eventSystem = EnsureEventSystem();
        var from = ParseVector2(arguments["from"] as JArray, Vector2.zero);
        var to = ParseVector2(arguments["to"] as JArray, new Vector2(128f, 128f));
        var eventData = new PointerEventData(eventSystem)
        {
            button = PointerEventData.InputButton.Left,
            pointerId = -1,
            pressPosition = from,
            position = from,
            delta = Vector2.zero,
            useDragThreshold = false,
            pointerDrag = gameObject,
        };

        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.initializePotentialDrag);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.beginDragHandler);

        eventData.delta = to - from;
        eventData.position = to;
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.dragHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.endDragHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);

        Emit(eventType, $"Pointer drag: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["from"] = new JArray(from.x, from.y),
            ["to"] = new JArray(to.x, to.y),
        };
    }

    private static void ApplyRectTransform(RectTransform rectTransform, JObject arguments, Vector2 defaultSize)
    {
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.anchoredPosition = ParseVector2(arguments["anchoredPosition"] as JArray, Vector2.zero);
        rectTransform.sizeDelta = ParseVector2(arguments["size"] as JArray, defaultSize);
    }

    private static Vector2 ParseVector2(JArray? values, Vector2 fallback)
    {
        if (values == null || values.Count < 2)
        {
            return fallback;
        }

        return new Vector2(values[0].Value<float>(), values[1].Value<float>());
    }

    private static Color ParseColor(string colorText, Color fallback)
    {
        Color color;
        return !string.IsNullOrEmpty(colorText) && ColorUtility.TryParseHtmlString(colorText, out color) ? color : fallback;
    }

    private static Font LoadBuiltinFont()
    {
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private static bool IsRuntimeObject(Component component)
    {
        return component.hideFlags == HideFlags.None
            && component.gameObject.scene.IsValid()
            && component.gameObject.scene == SceneManager.GetActiveScene();
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
        RecoverPendingCompilation();
        RecoverActivePlayModeTestRun();
        RecoverCompletedTestRuns();

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

    private static object RequestScriptCompilation()
    {
        var existingCompilation = LoadPendingCompilation();
        if (existingCompilation != null && !existingCompilation.Completed)
        {
            return Failure($"A script compilation is already active (compilationId={existingCompilation.CompilationId}).");
        }

        var compilationId = Guid.NewGuid().ToString("N");
        SavePendingCompilation(new PersistentCompilationState
        {
            CompilationId = compilationId,
            OriginSessionId = SessionId,
            RequestedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        Emit("editor.compilation_started", "Script compilation requested.", new JObject
        {
            ["compilationId"] = compilationId,
            ["isCompiling"] = EditorApplication.isCompiling,
        });

        EditorApplication.delayCall += () =>
        {
            try
            {
                AssetDatabase.Refresh();
                CompilationPipeline.RequestScriptCompilation();
            }
            catch (Exception exception)
            {
                CompletePendingCompilation(compilationId, false, $"Failed to request script compilation: {exception.Message}", emitImmediately: true);
            }
        };

        return Success(new JObject
        {
            ["compilationId"] = compilationId,
            ["status"] = "started",
        }, "Script compilation requested.");
    }

    private static void RecoverPendingCompilation()
    {
        var pendingCompilation = LoadPendingCompilation();
        if (pendingCompilation == null || pendingCompilation.Completed)
        {
            return;
        }

        if (EditorApplication.isCompiling)
        {
            return;
        }

        if (pendingCompilation.OriginSessionId == SessionId
            && DateTimeOffset.TryParse(pendingCompilation.RequestedAt, out var requestedAt)
            && DateTimeOffset.UtcNow - requestedAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        CompletePendingCompilation(pendingCompilation.CompilationId, !EditorUtility.scriptCompilationFailed, null, emitImmediately: true);
    }

    private static void RecoverCompletedTestRuns()
    {
        var pendingRuns = LoadPendingTestRuns();
        var dirty = false;

        foreach (var pendingRun in pendingRuns.Where(run => run.Completed && !run.CompletionEmitted).ToArray())
        {
            var mode = ParseStoredTestMode(pendingRun.Mode);
            EmitTestRunCompleted(pendingRun.RunId, mode, pendingRun.Summary ?? BuildFailedTestRunSummary(mode, pendingRun.ErrorMessage ?? "Unknown test run failure."));
            pendingRun.CompletionEmitted = true;
            dirty = true;
        }

        if (dirty)
        {
            SavePendingTestRuns(pendingRuns);
        }
    }

    private static void RecoverActivePlayModeTestRun()
    {
        if (!TryGetActiveTestRun(out var pendingRun) || pendingRun == null || !string.Equals(pendingRun.Mode, "PlayMode", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var controllerType = Type.GetType("UnityEngine.TestTools.TestRunner.PlaymodeTestsController, UnityEngine.TestRunner");
        var activeController = controllerType?.GetProperty("ActiveController", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (activeController == null)
        {
            return;
        }

        var runner = controllerType?.GetField("m_Runner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(activeController);
        if (runner == null)
        {
            return;
        }

        if (!pendingRun.Started)
        {
            var loadedTest = runner.GetType().GetProperty("LoadedTest", BindingFlags.Instance | BindingFlags.Public)?.GetValue(runner) as NUnit.Framework.Interfaces.ITest;
            if (loadedTest != null)
            {
                MarkPendingTestRunStarted(CountLeafTests(loadedTest));
            }
        }

        var raisedException = controllerType?.GetProperty("RaisedException", BindingFlags.Instance | BindingFlags.Public)?.GetValue(activeController) as Exception;
        if (raisedException != null)
        {
            FailPendingTestRun(pendingRun.RunId, TestMode.PlayMode, raisedException.Message);
            return;
        }

        var isTestComplete = runner.GetType().GetProperty("IsTestComplete", BindingFlags.Instance | BindingFlags.Public)?.GetValue(runner) as bool?;
        if (isTestComplete != true)
        {
            return;
        }

        var result = runner.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(runner) as NUnit.Framework.Interfaces.ITestResult;
        if (result != null)
        {
            CompletePendingTestRun(BuildTestRunSummary(result, TestMode.PlayMode));
        }
    }

    internal static bool TryGetActiveTestRun(out PersistentTestRunState? state)
    {
        state = LoadPendingTestRuns().LastOrDefault(run => !run.Completed);
        return state != null;
    }

    internal static void StorePendingTestRun(string runId, TestMode mode, string[]? requestedNames)
    {
        var pendingRuns = LoadPendingTestRuns();
        pendingRuns.RemoveAll(run => run.Completed && run.CompletionEmitted);
        pendingRuns.Add(new PersistentTestRunState
        {
            RunId = runId,
            Mode = TestModeName(mode),
            RequestedNames = requestedNames ?? Array.Empty<string>(),
            OriginSessionId = SessionId,
            RequestedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        SavePendingTestRuns(pendingRuns);
    }

    internal static void MarkPendingTestRunStarted(int discoveredCount)
    {
        var pendingRuns = LoadPendingTestRuns();
        var pendingRun = pendingRuns.LastOrDefault(run => !run.Completed);
        if (pendingRun == null || pendingRun.Started)
        {
            return;
        }

        pendingRun.Started = true;
        pendingRun.Count = discoveredCount;
        SavePendingTestRuns(pendingRuns);
        EmitTestRunStarted(pendingRun.RunId, ParseStoredTestMode(pendingRun.Mode), pendingRun.RequestedNames ?? Array.Empty<string>(), discoveredCount);
    }

    internal static void CompletePendingTestRun(JObject summary)
    {
        var pendingRuns = LoadPendingTestRuns();
        var pendingRun = pendingRuns.LastOrDefault(run => !run.Completed);
        if (pendingRun == null)
        {
            return;
        }

        pendingRun.Completed = true;
        pendingRun.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
        pendingRun.Summary = summary;
        pendingRun.ErrorMessage = null;
        SavePendingTestRuns(pendingRuns);
        RecoverCompletedTestRuns();
    }

    internal static void FailPendingTestRun(string runId, TestMode mode, string message)
    {
        var pendingRuns = LoadPendingTestRuns();
        var pendingRun = pendingRuns.LastOrDefault(run => !run.Completed && string.Equals(run.RunId, runId, StringComparison.OrdinalIgnoreCase))
            ?? pendingRuns.LastOrDefault(run => !run.Completed);
        if (pendingRun == null)
        {
            pendingRun = new PersistentTestRunState
            {
                RunId = runId,
                Mode = TestModeName(mode),
                OriginSessionId = SessionId,
                RequestedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            pendingRuns.Add(pendingRun);
        }

        pendingRun.Completed = true;
        pendingRun.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
        pendingRun.ErrorMessage = message;
        pendingRun.Summary = BuildFailedTestRunSummary(mode, message);
        SavePendingTestRuns(pendingRuns);
        RecoverCompletedTestRuns();
    }

    private static PersistentCompilationState? LoadPendingCompilation()
    {
        try
        {
            var path = GetPendingCompilationPath();
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<PersistentCompilationState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SavePendingCompilation(PersistentCompilationState state)
    {
        EnsureStateDirectory();
        File.WriteAllText(GetPendingCompilationPath(), JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    private static void CompletePendingCompilation(string compilationId, bool success, string? message, bool emitImmediately)
    {
        var state = LoadPendingCompilation();
        if (state == null || !string.Equals(state.CompilationId, compilationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        state.Completed = true;
        state.Success = success;
        state.Message = message;
        state.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
        SavePendingCompilation(state);

        if (emitImmediately)
        {
            EmitCompilationCompleted(compilationId, success, message);
        }
    }

    private static void EmitCompilationCompleted(string compilationId, bool success, string? message)
    {
        var state = LoadPendingCompilation();
        if (state == null || state.CompletionEmitted || !string.Equals(state.CompilationId, compilationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Emit("editor.compiled", success ? "Script compilation finished." : (message ?? "Script compilation finished with errors."), new JObject
        {
            ["compilationId"] = compilationId,
            ["success"] = success,
            ["message"] = message ?? string.Empty,
        });

        state.CompletionEmitted = true;
        SavePendingCompilation(state);
    }

    private static List<PersistentTestRunState> LoadPendingTestRuns()
    {
        try
        {
            var path = GetPendingTestRunsPath();
            if (!File.Exists(path))
            {
                return new List<PersistentTestRunState>();
            }

            return JsonConvert.DeserializeObject<List<PersistentTestRunState>>(File.ReadAllText(path))
                ?? new List<PersistentTestRunState>();
        }
        catch
        {
            return new List<PersistentTestRunState>();
        }
    }

    private static void SavePendingTestRuns(List<PersistentTestRunState> pendingRuns)
    {
        EnsureStateDirectory();
        File.WriteAllText(GetPendingTestRunsPath(), JsonConvert.SerializeObject(pendingRuns, Formatting.Indented));
    }

    private static void EnsureStateDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCliBridge"));
    }

    private static string GetPendingCompilationPath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCliBridge", "pending-compilation.json");
    }

    private static string GetPendingTestRunsPath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "Library", "UnityCliBridge", "pending-test-runs.json");
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

    internal sealed class PersistentCompilationState
    {
        public string CompilationId { get; set; } = string.Empty;
        public string OriginSessionId { get; set; } = string.Empty;
        public string RequestedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
        public bool Completed { get; set; }
        public bool CompletionEmitted { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    internal sealed class PersistentTestRunState
    {
        public string RunId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string[] RequestedNames { get; set; } = Array.Empty<string>();
        public string OriginSessionId { get; set; } = string.Empty;
        public string RequestedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
        public bool Started { get; set; }
        public int Count { get; set; }
        public bool Completed { get; set; }
        public bool CompletionEmitted { get; set; }
        public string? ErrorMessage { get; set; }
        public JObject? Summary { get; set; }
    }

}

[Serializable]
internal sealed class UnityCliTestRunCallbacks : ScriptableObject, IErrorCallbacks
{
    [SerializeField] private string _runId = string.Empty;
    [SerializeField] private string[] _requestedNames = Array.Empty<string>();
    [SerializeField] private TestMode _mode;
    [NonSerialized] private bool _completed;

    public void Initialize(string runId, TestMode mode, string[]? requestedNames)
    {
        hideFlags = HideFlags.HideAndDontSave;
        _runId = runId;
        _mode = mode;
        _requestedNames = requestedNames ?? Array.Empty<string>();
    }

    public void RunStarted(ITestAdaptor testsToRun)
    {
        UnityCliBridgeServer.EmitTestRunStarted(_runId, _mode, _requestedNames, testsToRun.TestCaseCount);
    }

    public void RunFinished(ITestResultAdaptor result)
    {
        Complete(UnityCliBridgeServer.BuildTestRunSummary(result, _mode));
    }

    public void OnError(string message)
    {
        Complete(UnityCliBridgeServer.BuildFailedTestRunSummary(_mode, message));
    }

    private void Complete(JObject summary)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        UnityCliBridgeServer.EmitTestRunCompleted(_runId, _mode, summary);
        TestRunnerApi.UnregisterTestCallback(this);
        DestroyImmediate(this);
    }

    public void TestStarted(ITestAdaptor test)
    {
    }

    public void TestFinished(ITestResultAdaptor result)
    {
    }
}

internal sealed class UnityCliTestErrorCallbacks : IErrorCallbacks
{
    private readonly string _runId;
    private readonly TestMode _mode;

    public UnityCliTestErrorCallbacks(string runId, TestMode mode)
    {
        _runId = runId;
        _mode = mode;
    }

    public void RunStarted(ITestAdaptor testsToRun)
    {
    }

    public void RunFinished(ITestResultAdaptor result)
    {
        TestRunnerApi.UnregisterTestCallback(this);
    }

    public void TestStarted(ITestAdaptor test)
    {
    }

    public void TestFinished(ITestResultAdaptor result)
    {
    }

    public void OnError(string message)
    {
        UnityCliBridgeServer.FailPendingTestRun(_runId, _mode, message);
        TestRunnerApi.UnregisterTestCallback(this);
    }
}
}
#endif
