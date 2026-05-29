#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS0618, CS8600, CS8602, CS8604, CS8619, CS8625
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
using TMPro;

namespace UnityCliBridge
{
[InitializeOnLoad]
public static partial class UnityCliBridgeServer
{
    private static readonly object Gate = new();

    /// <summary>
    /// 모든 JSON 직렬화에 사용하는 공유 설정. 들여쓰기 유지, 문자열 이스케이프는 기본값으로 고정(부호 보존),
    /// 타임스탬프는 UTC 밀리초 + 'Z' 접미사(ISO 8601)로 결정론적으로 출력한다.
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,
        StringEscapeHandling = StringEscapeHandling.Default,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
    };

    private static readonly List<BridgeEvent> Events = new();

    /// <summary>
    /// 마지막으로 완료된 테스트 실행 결과 스냅샷. tests/last-run 리소스가 노출한다.
    /// 메모리에만 보관하므로 도메인 리로드 시 사라지며, 복구된 PlayMode 실행이 완료될 때 다시 채워진다.
    /// </summary>
    private static JObject? _lastTestRun;

    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static readonly HttpListener Listener = new();
    private static CancellationTokenSource? _cts;
    private static Task? _serverTask;
    private static long _cursor;
    private static double _nextStartAttemptAt = -1d;
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    /// <summary>현재 에디터 인스턴스가 instances.json 에 등록한 키(project:port). Stop 시 alive=false 표시에 사용.</summary>
    private static string? _registeredInstanceKey;

    /// <summary>등록 파일 경로 헬퍼와 alive 토글이 공유하는 락 오브젝트.</summary>
    private static readonly object InstancesFileGate = new object();

    // ToolNames, ToolCatalog, ResourceCatalog and EventTypes are the single source of truth.
    // They live in UnityCliBridge.Catalog.cs. Adding a tool there + a switch arm in ExecuteToolAsync
    // is enforced for parity by the BridgeCatalogConsistency tests.

    static UnityCliBridgeServer()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update += OnEditorUpdate;
        Application.logMessageReceivedThreaded += OnLogReceived;
        Start();
    }

    /// <summary>
    /// 브릿지 리슨 포트를 결정한다. 우선순위: UNITY_CLI_PORT 환경변수 > EditorPrefs("UnityCliBridge.Port") > 기본값 52737.
    /// 유효 범위(1-65535)를 벗어나거나 파싱 불가하면 다음 소스로 폴백한다.
    /// </summary>
    /// <returns>해석된 리슨 포트.</returns>
    private static int ResolvePort()
    {
        var envPort = Environment.GetEnvironmentVariable("UNITY_CLI_PORT");
        if (int.TryParse(envPort, out var fromEnv) && fromEnv > 0 && fromEnv <= 65535)
        {
            return fromEnv;
        }

        var prefsPort = EditorPrefs.GetInt("UnityCliBridge.Port", 0);
        if (prefsPort > 0 && prefsPort <= 65535)
        {
            return prefsPort;
        }

        return 52737;
    }

    private static void Start()
    {
        if (_serverTask != null || Listener.IsListening)
        {
            return;
        }

        var host = "127.0.0.1";
        var port = ResolvePort();
        var prefix = $"http://{host}:{port}/";
        try
        {
            if (!Listener.Prefixes.Contains(prefix))
            {
                Listener.Prefixes.Add(prefix);
            }

            Listener.Start();
        }
        catch (HttpListenerException)
        {
            _nextStartAttemptAt = EditorApplication.timeSinceStartup + 1d;
            return;
        }
        catch (System.Net.Sockets.SocketException)
        {
            _nextStartAttemptAt = EditorApplication.timeSinceStartup + 1d;
            return;
        }

        _nextStartAttemptAt = -1d;
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        Emit("bridge.started", $"Unity CLI bridge listening on {prefix}", new JObject { ["port"] = port, ["sessionId"] = SessionId });
        RegisterInstance(host, port);
    }

    private static void Stop()
    {
        MarkRegisteredInstanceDead();
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
            _serverTask = null;
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
                    sessionId = SessionId,
                });
                return;
            }

            if (method == "GET" && path == "capabilities")
            {
                await WriteJsonAsync(context, new
                {
                    tools = ToolNames,
                    resources = ResourceCatalog.Select(resource => resource.Name).ToArray(),
                    events = EventNames,
                    metadata = new Dictionary<string, string> { ["transport"] = "http", ["unity"] = Application.unityVersion },
                });
                return;
            }

            if (method == "GET" && path == "tools")
            {
                await WriteJsonAsync(context, ToolCatalog.Select(tool => new
                {
                    name = tool.Name,
                    category = tool.Category,
                    description = tool.Summary,
                    requiredArguments = tool.Args.Where(arg => arg.Required).Select(arg => arg.Name).ToArray(),
                    optionalArguments = tool.Args.Where(arg => !arg.Required).Select(arg => arg.Name).ToArray(),
                    arguments = tool.Args.Select(arg => new { name = arg.Name, type = arg.Type, required = arg.Required, description = arg.Description }).ToArray(),
                }));
                return;
            }

            if (method == "GET" && path == "resources")
            {
                await WriteJsonAsync(context, ResourceCatalog.Select(resource => new { name = resource.Name, description = resource.Description }).ToArray());
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
                object eventsPayload;
                lock (Gate)
                {
                    eventsPayload = new
                    {
                        cursor = _cursor,
                        floor = Events.Count > 0 ? Events[0].Cursor : _cursor,
                        events = Events.Where(x => x.Cursor > after).ToArray(),
                    };
                }

                await WriteJsonAsync(context, eventsPayload);
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
        catch (BridgeException bridgeException)
        {
            context.Response.StatusCode = bridgeException.Status;
            await WriteJsonAsync(context, new { success = false, message = bridgeException.Message, code = bridgeException.Code, result = (object?)null, events = Array.Empty<object>() });
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new { success = false, message = exception.Message, code = ErrorCodes.Internal, result = (object?)null, events = Array.Empty<object>() });
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
                var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    throw NotFound($"Scene asset not found: {path}");
                }

                AssetDatabase.DeleteAsset(path);
                Emit("scene.changed", $"Scene deleted: {path}", new JObject { ["path"] = path });
                return Success(new JObject { ["deleted"] = path }, "Scene deleted.");
            }),
            "scene.unload" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path");
                var scene = string.IsNullOrEmpty(path) ? SceneManager.GetActiveScene() : SceneManager.GetSceneByPath(path);
                if (!string.IsNullOrEmpty(path) && !scene.IsValid())
                {
                    throw NotFound($"Loaded scene not found: {path}");
                }

                var closedPath = scene.path;
                EditorSceneManager.CloseScene(scene, true);
                Emit("scene.changed", $"Scene unloaded: {closedPath}", new JObject { ["path"] = closedPath });
                return Success(new JObject { ["path"] = closedPath }, "Scene unloaded.");
            }),
            "scene.open-additive" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                {
                    throw NotFound($"Scene asset not found: {path}");
                }

                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                Emit("scene.loaded", $"Scene opened additively: {path}", new JObject { ["path"] = path });
                return Success(SceneObject(scene, path), "Scene opened additively.");
            }),
            "scene.set-active" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
                var scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    throw NotFound($"Loaded scene not found: {path}");
                }

                SceneManager.SetActiveScene(scene);
                Emit("scene.changed", $"Active scene set: {path}", new JObject { ["path"] = path });
                return Success(SceneObject(scene, path), "Active scene set.");
            }),
            "scene.list-loaded" => await OnMainThreadAsync(() =>
            {
                var scenes = new JArray();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var loaded = SceneManager.GetSceneAt(i);
                    scenes.Add(SceneObject(loaded, loaded.path));
                }

                return Success(new JObject
                {
                    ["scenes"] = scenes,
                    ["count"] = SceneManager.sceneCount,
                    ["activeScenePath"] = SceneManager.GetActiveScene().path,
                }, "Loaded scenes listed.");
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
            "gameobject.find" => await OnMainThreadAsync(() => FindGameObjects(arguments)),
            "gameobject.set-properties" => await OnMainThreadAsync(() => SetGameObjectProperties(arguments)),
            "sprite.create" => await OnMainThreadAsync(() =>
            {
                var gameObject = CreateSprite(arguments);
                Emit("hierarchy.changed", $"Sprite created: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name });
                return Success(GameObjectObject(gameObject), "Sprite created.");
            }),
            "sprite.set" => await OnMainThreadAsync(() => SetSpriteRenderer(arguments)),
            "component.update" => await OnMainThreadAsync(() => UpdateComponentTool(arguments)),
            "component.list" => await OnMainThreadAsync(() => ListComponents(arguments)),
            "component.get" => await OnMainThreadAsync(() => GetComponentProperties(arguments)),
            "component.add" => await OnMainThreadAsync(() => AddComponentTool(arguments)),
            "component.remove" => await OnMainThreadAsync(() => RemoveComponentTool(arguments)),
            "material.create" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? "Assets/Materials/CliMaterial.mat";
                var shader = ResolveShader(arguments.Value<string>("shader"), allowFallback: true);
                EnsureParentDirectory(path);
                var material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
                var createdMaterial = AssetDatabase.LoadAssetAtPath<Material>(path) ?? material;
                var colorText = arguments.Value<string>("color");
                if (!string.IsNullOrEmpty(colorText) && ColorUtility.TryParseHtmlString(colorText, out var color))
                {
                    createdMaterial.color = color;
                    if (createdMaterial.HasProperty("_Color"))
                    {
                        createdMaterial.SetColor("_Color", color);
                    }
                }

                EditorUtility.SetDirty(createdMaterial);
                AssetDatabase.SaveAssets();
                Emit("asset.changed", $"Material created: {path}", new JObject { ["path"] = path });
                return Success(MaterialObject(createdMaterial, path), "Material created.");
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
                    material.shader = ResolveShader(shaderName, allowFallback: false);
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
            "asset.import-texture" => await OnMainThreadAsync(() =>
            {
                var path = arguments.Value<string>("path") ?? throw new InvalidOperationException("path is required.");
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    throw new InvalidOperationException($"TextureImporter not found for: {path}. Ensure the file exists and editor has been refreshed.");

                var changed = false;
                var textureType = arguments.Value<string>("textureType") ?? "Sprite";
                var targetType = textureType switch
                {
                    "Default" => TextureImporterType.Default,
                    "NormalMap" => TextureImporterType.NormalMap,
                    "Cursor" => TextureImporterType.Cursor,
                    _ => TextureImporterType.Sprite,
                };
                if (importer.textureType != targetType) { importer.textureType = targetType; changed = true; }

                var spriteMode = arguments.Value<int?>("spriteMode");
                if (spriteMode.HasValue && importer.spriteImportMode != (SpriteImportMode)spriteMode.Value)
                {
                    importer.spriteImportMode = (SpriteImportMode)spriteMode.Value;
                    changed = true;
                }

                var maxSize = arguments.Value<int?>("maxTextureSize");
                if (maxSize.HasValue && importer.maxTextureSize != maxSize.Value)
                {
                    importer.maxTextureSize = maxSize.Value;
                    changed = true;
                }

                var filterModeStr = arguments.Value<string>("filterMode");
                if (!string.IsNullOrEmpty(filterModeStr))
                {
                    var fm = filterModeStr switch
                    {
                        "Point" => FilterMode.Point,
                        "Trilinear" => FilterMode.Trilinear,
                        _ => FilterMode.Bilinear,
                    };
                    if (importer.filterMode != fm) { importer.filterMode = fm; changed = true; }
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                }

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                return Success(new JObject
                {
                    ["path"] = path,
                    ["textureType"] = importer.textureType.ToString(),
                    ["spriteMode"] = (int)importer.spriteImportMode,
                    ["maxTextureSize"] = importer.maxTextureSize,
                    ["hasSpriteAsset"] = sprite != null,
                    ["reimported"] = changed,
                }, changed ? "Texture import settings updated and reimported." : "Texture already has correct settings.");
            }),
            "asset.manage" => await OnMainThreadAsync(() => ManageAsset(arguments)),
            "asset.create-scriptableobject" => await OnMainThreadAsync(() => CreateScriptableObjectAsset(arguments)),
            "scriptableobject.get" => await OnMainThreadAsync(() => GetScriptableObjectProperties(arguments)),
            "scriptableobject.list" => await OnMainThreadAsync(() => ListScriptableObjects(arguments)),
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
            "console.logs" => QueryConsoleLogs(arguments),
            "ui.canvas.create" => await OnMainThreadAsync(() =>
            {
                var canvas = EnsureCanvas(arguments.Value<string>("name") ?? "Canvas", arguments);
                Emit("hierarchy.changed", $"Canvas ready: {canvas.name}", new JObject { ["id"] = canvas.GetInstanceID(), ["name"] = canvas.name });
                return Success(GameObjectObject(canvas), "Canvas created.");
            }),
            "ui.button.create" => await OnMainThreadAsync(() =>
            {
                var button = CreateButton(arguments);
                Emit("hierarchy.changed", $"Button created: {button.name}", new JObject { ["id"] = button.GetInstanceID(), ["name"] = button.name });
                return Success(GameObjectObject(button), "Button created.");
            }),
            "ui.toggle.create" => await OnMainThreadAsync(() =>
            {
                var toggle = CreateToggle(arguments);
                Emit("hierarchy.changed", $"Toggle created: {toggle.name}", new JObject { ["id"] = toggle.GetInstanceID(), ["name"] = toggle.name });
                return Success(UiObject(toggle), "Toggle created.");
            }),
            "ui.slider.create" => await OnMainThreadAsync(() =>
            {
                var slider = CreateSlider(arguments);
                Emit("hierarchy.changed", $"Slider created: {slider.name}", new JObject { ["id"] = slider.GetInstanceID(), ["name"] = slider.name });
                return Success(UiObject(slider), "Slider created.");
            }),
            "ui.scrollrect.create" => await OnMainThreadAsync(() =>
            {
                var scrollRect = CreateScrollRect(arguments);
                Emit("hierarchy.changed", $"ScrollRect created: {scrollRect.name}", new JObject { ["id"] = scrollRect.GetInstanceID(), ["name"] = scrollRect.name });
                return Success(UiObject(scrollRect), "ScrollRect created.");
            }),
            "ui.inputfield.create" => await OnMainThreadAsync(() =>
            {
                var inputField = CreateInputField(arguments);
                Emit("hierarchy.changed", $"InputField created: {inputField.name}", new JObject { ["id"] = inputField.GetInstanceID(), ["name"] = inputField.name });
                return Success(UiObject(inputField), "InputField created.");
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
            "ui.panel.create" => await OnMainThreadAsync(() =>
            {
                var panel = CreatePanel(arguments);
                Emit("hierarchy.changed", $"Panel created: {panel.name}", new JObject { ["id"] = panel.GetInstanceID(), ["name"] = panel.name });
                return Success(GameObjectObject(panel), "Panel created.");
            }),
            "ui.layout.add" => await OnMainThreadAsync(() =>
            {
                var result = AddLayout(arguments);
                return Success(result, "Layout added.");
            }),
            "ui.recttransform.modify" => await OnMainThreadAsync(() =>
            {
                var result = ModifyRectTransform(arguments);
                return Success(result, "RectTransform modified.");
            }),
            "ui.screenshot.capture" => await OnMainThreadAsync(() => Success(CaptureScreenshot(arguments), "Screenshot captured.")),
            "editor.gameview.resize" => await OnMainThreadAsync(() =>
            {
                var result = ResizeGameView(arguments);
                return Success(result, "Game view resized.");
            }),
            "ui.toggle.set" => await OnMainThreadAsync(() =>
            {
                var toggle = FindGameObject(arguments).GetComponent<Toggle>() ?? throw new InvalidOperationException("Toggle component was not found.");
                toggle.isOn = arguments["isOn"]?.Value<bool?>() ?? toggle.isOn;
                Emit("component.changed", $"Toggle changed: {toggle.gameObject.name}", new JObject { ["id"] = toggle.gameObject.GetInstanceID(), ["isOn"] = toggle.isOn });
                return Success(UiObject(toggle.gameObject), "Toggle updated.");
            }),
            "ui.slider.set" => await OnMainThreadAsync(() =>
            {
                var slider = FindGameObject(arguments).GetComponent<Slider>() ?? throw new InvalidOperationException("Slider component was not found.");
                slider.value = arguments["value"]?.Value<float?>() ?? slider.value;
                Emit("component.changed", $"Slider changed: {slider.gameObject.name}", new JObject { ["id"] = slider.gameObject.GetInstanceID(), ["value"] = slider.value });
                return Success(UiObject(slider.gameObject), "Slider updated.");
            }),
            "ui.scrollrect.set" => await OnMainThreadAsync(() =>
            {
                var scrollRect = FindGameObject(arguments).GetComponent<ScrollRect>() ?? throw new InvalidOperationException("ScrollRect component was not found.");
                if (arguments["normalizedPosition"] is JArray normalizedPositionValues)
                {
                    scrollRect.normalizedPosition = ParseVector2(normalizedPositionValues, scrollRect.normalizedPosition);
                }
                else
                {
                    var horizontal = arguments["horizontalNormalizedPosition"]?.Value<float?>();
                    var vertical = arguments["verticalNormalizedPosition"]?.Value<float?>();
                    scrollRect.normalizedPosition = new Vector2(horizontal ?? scrollRect.horizontalNormalizedPosition, vertical ?? scrollRect.verticalNormalizedPosition);
                }

                scrollRect.StopMovement();
                Emit("component.changed", $"ScrollRect changed: {scrollRect.gameObject.name}", new JObject
                {
                    ["id"] = scrollRect.gameObject.GetInstanceID(),
                    ["normalizedPosition"] = new JArray(scrollRect.normalizedPosition.x, scrollRect.normalizedPosition.y),
                });
                return Success(UiObject(scrollRect.gameObject), "ScrollRect updated.");
            }),
            "ui.inputfield.set-text" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                var text = arguments.Value<string>("text") ?? string.Empty;
                SetInputFieldText(gameObject, text);
                Emit("component.changed", $"InputField changed: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["text"] = text });
                return Success(UiObject(gameObject), "InputField updated.");
            }),
            "ui.focus" => await OnMainThreadAsync(() =>
            {
                var gameObject = FindGameObject(arguments);
                FocusUiTarget(gameObject);
                Emit("ui.focused", $"UI focused: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name });
                return Success(UiObject(gameObject), "UI focus updated.");
            }),
            "ui.blur" => await OnMainThreadAsync(() =>
            {
                var previous = ClearUiFocus();
                Emit("ui.blurred", previous == null ? "UI focus cleared." : $"UI blurred: {previous.name}", new JObject
                {
                    ["id"] = previous != null ? previous.GetInstanceID() : 0,
                    ["name"] = previous != null ? previous.name : string.Empty,
                });
                return Success(new JObject
                {
                    ["cleared"] = true,
                    ["previousId"] = previous != null ? previous.GetInstanceID() : 0,
                    ["previousName"] = previous != null ? previous.name : string.Empty,
                    ["editorState"] = JObject.FromObject(EditorState()),
                }, "UI focus cleared.");
            }),
            "ui.click" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchTap(arguments, "ui.clicked", uiOnly: true), "UI click dispatched.");
            }),
            "ui.double-click" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDoubleTap(arguments, "ui.double_clicked", uiOnly: true), "UI double click dispatched.");
            }),
            "ui.long-press" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchLongPress(arguments, "ui.long_pressed", uiOnly: true), "UI long press dispatched.");
            }),
            "ui.drag" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "ui.dragged", uiOnly: true), "UI drag dispatched.");
            }),
            "ui.swipe" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "ui.swiped", uiOnly: true), "UI swipe dispatched.");
            }),
            "input.tap" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchTap(arguments, "input.tapped", uiOnly: false), "Input tap dispatched.");
            }),
            "input.double-tap" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDoubleTap(arguments, "input.double_tapped", uiOnly: false), "Input double tap dispatched.");
            }),
            "input.long-press" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchLongPress(arguments, "input.long_pressed", uiOnly: false), "Input long press dispatched.");
            }),
            "input.drag" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "input.dragged", uiOnly: false), "Input drag dispatched.");
            }),
            "input.swipe" => await OnMainThreadAsync(() =>
            {
                return Success(DispatchDrag(arguments, "input.swiped", uiOnly: false), "Input swipe dispatched.");
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
                return Success(RequestPlayMode(true), "Play mode transition requested.");
            }),
            "editor.stop" => await OnMainThreadAsync(() =>
            {
                return Success(RequestPlayMode(false), "Play mode transition requested.");
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
            "prefab.create" => await OnMainThreadAsync(() => CreatePrefab(arguments)),
            "prefab.instantiate" => await OnMainThreadAsync(() => InstantiatePrefab(arguments)),
            "prefab.apply" => await OnMainThreadAsync(() => ApplyPrefab(arguments)),
            "prefab.unpack" => await OnMainThreadAsync(() => UnpackPrefab(arguments)),
            _ => Failure($"Unsupported tool '{toolName}'.", ErrorCodes.UnknownTool),
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
            case "tests/last-run":
            {
                JObject? snapshot;
                lock (Gate) { snapshot = _lastTestRun; }
                return new { name = resourceName, data = (object?)snapshot };
            }
            case "packages/list":
                return await OnMainThreadAsync(() => new { name = resourceName, data = new { packages = GetInstalledPackages() } });
            case "project/info":
                return await OnMainThreadAsync(() => new { name = resourceName, data = ProjectInfo() });
            case "addressables/list":
                return await OnMainThreadAsync(() => new { name = resourceName, data = AddressablesList() });
            default:
                return new { name = resourceName, data = (object?)null };
        }
    }

    private static object EditorState()
    {
        var currentSelected = EnsureEventSystem().currentSelectedGameObject;
        return new
        {
            isPlaying = EditorApplication.isPlaying,
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
            isPaused = EditorApplication.isPaused,
            selectedObjectId = Selection.activeGameObject != null ? Selection.activeGameObject.GetInstanceID() : 0,
            eventSystemSelectedObjectId = currentSelected != null ? currentSelected.GetInstanceID() : 0,
            eventSystemSelectedObjectName = currentSelected != null ? currentSelected.name : string.Empty,
            activeScenePath = SceneManager.GetActiveScene().path,
        };
    }

    private static object ProjectInfo()
    {
        var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        var defines = string.Empty;
        try
        {
            defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
        }
        catch
        {
            // ignore platforms that reject the query
        }

        var renderPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        return new
        {
            unityVersion = Application.unityVersion,
            projectPath = Directory.GetCurrentDirectory(),
            productName = Application.productName,
            companyName = Application.companyName,
            activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
            buildTargetGroup = buildTargetGroup.ToString(),
            colorSpace = PlayerSettings.colorSpace.ToString(),
            scriptingDefineSymbols = defines,
            renderPipeline = renderPipeline != null ? renderPipeline.name : "Built-in",
            isPlaying = EditorApplication.isPlaying,
            scenesInBuild = EditorBuildSettings.scenes.Select(scene => new { path = scene.path, enabled = scene.enabled }).ToArray(),
        };
    }

    /// <summary>
    /// Addressables 패키지의 그룹/엔트리 목록을 리플렉션으로 수집한다.
    /// 패키지가 없거나 설정 에셋이 없으면 { available = false } 를 반환한다.
    /// </summary>
    /// <returns>그룹/엔트리 트리 또는 { available = false } 센티넬 JObject.</returns>
    private static JObject AddressablesList()
    {
        var defaultType = Type.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject, Unity.Addressables.Editor");
        if (defaultType == null)
        {
            return new JObject { ["available"] = false };
        }

        var settingsProp = defaultType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
        var settings = settingsProp?.GetValue(null);
        if (settings == null)
        {
            return new JObject { ["available"] = false };
        }

        var groupsProp = settings.GetType().GetProperty("groups", BindingFlags.Public | BindingFlags.Instance);
        if (groupsProp?.GetValue(settings) is not System.Collections.IEnumerable groups)
        {
            return new JObject { ["available"] = false };
        }

        var groupsArray = new JArray();
        foreach (var group in groups)
        {
            if (group == null) continue;
            var groupType = group.GetType();
            var groupName = groupType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(group) as string ?? string.Empty;

            var entriesArray = new JArray();
            if (groupType.GetProperty("entries", BindingFlags.Public | BindingFlags.Instance)?.GetValue(group) is System.Collections.IEnumerable entries)
            {
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    var entryType = entry.GetType();
                    var labelsArray = new JArray();
                    if (entryType.GetProperty("labels", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) is System.Collections.IEnumerable labels)
                    {
                        foreach (var label in labels)
                        {
                            if (label != null) labelsArray.Add(label.ToString());
                        }
                    }

                    entriesArray.Add(new JObject
                    {
                        ["address"] = entryType.GetProperty("address", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string ?? string.Empty,
                        ["guid"] = entryType.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string ?? string.Empty,
                        ["assetPath"] = entryType.GetProperty("AssetPath", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string ?? string.Empty,
                        ["labels"] = labelsArray,
                    });
                }
            }

            groupsArray.Add(new JObject { ["name"] = groupName, ["entries"] = entriesArray });
        }

        return new JObject { ["groups"] = groupsArray };
    }

    private static JObject RequestPlayMode(bool enabled)
    {
        EditorApplication.isPlaying = enabled;
        return new JObject
        {
            ["requestedIsPlaying"] = enabled,
            ["isPlaying"] = EditorApplication.isPlaying,
            ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
            ["isPaused"] = EditorApplication.isPaused,
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
            ["buildIndex"] = scene.buildIndex,
            ["isActive"] = scene.IsValid() && SceneManager.GetActiveScene() == scene,
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

        result["activeInHierarchy"] = gameObject.activeInHierarchy;
        result["tag"] = gameObject.tag;
        result["layer"] = gameObject.layer;
        result["layerName"] = LayerMask.LayerToName(gameObject.layer);
        result["isStatic"] = gameObject.isStatic;
        result["childCount"] = gameObject.transform.childCount;

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
            result["spritePath"] = spriteRenderer.sprite != null ? AssetDatabase.GetAssetPath(spriteRenderer.sprite) : string.Empty;
            result["color"] = "#" + ColorUtility.ToHtmlStringRGBA(spriteRenderer.color);
            result["sortingLayerName"] = spriteRenderer.sortingLayerName;
            result["sortingOrder"] = spriteRenderer.sortingOrder;
            result["flipX"] = spriteRenderer.flipX;
            result["flipY"] = spriteRenderer.flipY;
            result["rendererEnabled"] = spriteRenderer.enabled;
        }

        return result;
    }

    private static JObject UiObject(GameObject gameObject)
    {
        var result = GameObjectObject(gameObject);
        var canvas = gameObject.GetComponent<Canvas>();
        var selectable = gameObject.GetComponent<Selectable>();
        var toggle = gameObject.GetComponent<Toggle>();
        var slider = gameObject.GetComponent<Slider>();
        var scrollRect = gameObject.GetComponent<ScrollRect>();
        var inputField = BuildInputFieldObject(gameObject);

        result["isCanvas"] = canvas != null;
        result["isSelectable"] = selectable != null;
        result["selectableType"] = selectable != null ? selectable.GetType().Name : null;
        result["isSelected"] = IsSelectedByEventSystem(gameObject);
        result["text"] = GetTextValue(gameObject);

        if (toggle != null)
        {
            result["toggle"] = new JObject
            {
                ["isOn"] = toggle.isOn,
            };
        }

        if (slider != null)
        {
            result["slider"] = new JObject
            {
                ["value"] = slider.value,
                ["minValue"] = slider.minValue,
                ["maxValue"] = slider.maxValue,
                ["wholeNumbers"] = slider.wholeNumbers,
            };
        }

        if (scrollRect != null)
        {
            result["scrollRect"] = new JObject
            {
                ["horizontal"] = scrollRect.horizontal,
                ["vertical"] = scrollRect.vertical,
                ["inertia"] = scrollRect.inertia,
                ["movementType"] = scrollRect.movementType.ToString(),
                ["velocity"] = new JArray(scrollRect.velocity.x, scrollRect.velocity.y),
                ["normalizedPosition"] = new JArray(scrollRect.normalizedPosition.x, scrollRect.normalizedPosition.y),
            };
        }

        result["inputField"] = inputField;

        var image = gameObject.GetComponent<Image>();
        if (image != null)
        {
            result["image"] = new JObject
            {
                ["spritePath"] = image.sprite != null ? AssetDatabase.GetAssetPath(image.sprite) : string.Empty,
                ["color"] = "#" + ColorUtility.ToHtmlStringRGBA(image.color),
                ["type"] = image.type.ToString(),
                ["fillAmount"] = image.fillAmount,
                ["raycastTarget"] = image.raycastTarget,
                ["enabled"] = image.enabled,
            };
        }

        var button = gameObject.GetComponent<Button>();
        if (button != null)
        {
            result["button"] = new JObject { ["interactable"] = button.interactable };
        }

        var canvasGroup = gameObject.GetComponent<CanvasGroup>();
        if (canvasGroup != null)
        {
            result["canvasGroup"] = new JObject
            {
                ["alpha"] = canvasGroup.alpha,
                ["interactable"] = canvasGroup.interactable,
                ["blocksRaycasts"] = canvasGroup.blocksRaycasts,
            };
        }

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
        var requestedCategories = ParseRequestedCategories(arguments);
        var regex = arguments.Value<string>("regex");
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

            if (!string.IsNullOrWhiteSpace(regex))
            {
                System.Text.RegularExpressions.Regex compiledRegex;
                try
                {
                    compiledRegex = new System.Text.RegularExpressions.Regex(regex);
                }
                catch (Exception ex)
                {
                    return Failure($"Invalid regex: {ex.Message}");
                }

                var selectedNames = new HashSet<string>(requestedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                requestedNames = availableTests
                    .Where(test => selectedNames.Count == 0 || selectedNames.Contains(test.FullName))
                    .Where(test => compiledRegex.IsMatch(test.FullName))
                    .Select(test => test.FullName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (requestedNames.Length == 0)
                {
                    return Failure("No tests matched the regex filter.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(regex))
        {
            return Failure("regex filter is only supported for EditMode tests.");
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

            api.Execute(new ExecutionSettings(BuildTestFilter(requestedMode, requestedNames, requestedCategories)));
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

    /// <summary>
    /// 실행할 테스트를 좁히는 <see cref="Filter"/> 를 구성한다. 이름/카테고리 필터가 있으면 적용한다.
    /// </summary>
    /// <param name="mode">실행 모드(EditMode/PlayMode).</param>
    /// <param name="testNames">실행할 전체 테스트 이름 목록(없으면 전체).</param>
    /// <param name="categoryNames">NUnit 카테고리 필터(없으면 전체).</param>
    /// <returns>구성된 테스트 필터.</returns>
    private static Filter BuildTestFilter(TestMode mode, string[]? testNames, string[]? categoryNames)
    {
        var filter = new Filter { testMode = mode };
        if (testNames != null && testNames.Length > 0)
        {
            filter.testNames = testNames;
        }

        if (categoryNames != null && categoryNames.Length > 0)
        {
            filter.categoryNames = categoryNames;
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

    /// <summary>
    /// 요약의 tests 배열에서 실패 항목만 추려 {fullName,message} 배열로 변환한다.
    /// </summary>
    /// <param name="summary">passed/failed/... 와 tests 배열을 포함하는 테스트 실행 요약.</param>
    /// <returns>실패 테스트의 fullName/message 만 담은 JSON 배열.</returns>
    private static JArray BuildTestFailures(JObject summary)
    {
        var failures = new JArray();
        if (summary["tests"] is JArray tests)
        {
            foreach (var test in tests.OfType<JObject>())
            {
                if (string.Equals(test.Value<string>("status"), "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new JObject
                    {
                        ["fullName"] = test.Value<string>("name") ?? string.Empty,
                        ["message"] = test.Value<string>("message") ?? string.Empty,
                    });
                }
            }
        }

        return failures;
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
        var snapshot = new JObject
        {
            ["runId"] = runId,
            ["mode"] = TestModeName(mode),
            ["passed"] = summary.Value<int>("passed"),
            ["failed"] = summary.Value<int>("failed"),
            ["skipped"] = summary.Value<int>("skipped"),
            ["inconclusive"] = summary.Value<int>("inconclusive"),
            ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["failures"] = BuildTestFailures(summary),
        };
        lock (Gate) { _lastTestRun = snapshot; }

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

    /// <summary>
    /// tests.run 의 category 인자(쉼표 구분)를 NUnit 카테고리 배열로 파싱한다.
    /// </summary>
    /// <param name="arguments">category(string?) 인자를 포함하는 도구 인자.</param>
    /// <returns>공백을 제거한 카테고리 배열. 값이 없으면 null.</returns>
    private static string[]? ParseRequestedCategories(JObject arguments)
    {
        var category = arguments.Value<string>("category");
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var categories = category
            .Split(',')
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return categories.Length == 0 ? null : categories;
    }

    /// <summary>
    /// 콘솔 로그 이벤트(console.log)를 커서/레벨/텍스트 필터로 조회한다.
    /// </summary>
    /// <param name="arguments">sinceCursor(long), level(string?), contains(string?) 인자.</param>
    /// <returns>logs 배열, 마지막 커서, 에러/경고 개수를 담은 성공 응답.</returns>
    private static object QueryConsoleLogs(JObject arguments)
    {
        var sinceCursor = arguments.Value<long?>("sinceCursor") ?? 0;
        var levelFilter = arguments.Value<string>("level");
        var contains = arguments.Value<string>("contains");
        var logs = new JArray();
        long maxCursor = sinceCursor;
        int errorCount = 0;
        int warningCount = 0;
        lock (Gate)
        {
            foreach (var e in Events)
            {
                if (e.Type != "console.log" || e.Cursor <= sinceCursor) { continue; }
                var level = e.Data?["level"]?.Value<string>() ?? "Log";
                if (!string.IsNullOrEmpty(levelFilter) && !string.Equals(level, levelFilter, StringComparison.OrdinalIgnoreCase)) { continue; }
                var stack = e.Data?["stackTrace"]?.Value<string>() ?? string.Empty;
                if (!string.IsNullOrEmpty(contains)
                    && (e.Message?.IndexOf(contains, StringComparison.OrdinalIgnoreCase) ?? -1) < 0
                    && stack.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(level, "Exception", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(level, "Assert", StringComparison.OrdinalIgnoreCase)) { errorCount++; }
                else if (string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase)) { warningCount++; }
                if (e.Cursor > maxCursor) { maxCursor = e.Cursor; }
                logs.Add(new JObject
                {
                    ["cursor"] = e.Cursor,
                    ["level"] = level,
                    ["message"] = e.Message,
                    ["stackTrace"] = stack,
                    ["timestamp"] = e.Timestamp,
                });
            }
        }

        return Success(new JObject { ["logs"] = logs, ["cursor"] = maxCursor, ["errorCount"] = errorCount, ["warningCount"] = warningCount }, $"{logs.Count} console log(s).");
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
            return EditorUtility.InstanceIDToObject(id.Value) as GameObject ?? throw NotFound($"GameObject with instance ID {id.Value} was not found.");
        }

        var name = arguments.Value<string>("name") ?? throw MissingArg("Either id or name is required.");
        return GameObject.Find(name) ?? throw NotFound($"GameObject '{name}' was not found.");
    }

    private static InteractionTarget FindInteractionTarget(JObject arguments, Vector2 screenPosition, Vector3? worldPosition, bool uiOnly)
    {
        if (arguments["id"] != null || arguments["name"] != null)
        {
            var explicitTarget = FindGameObject(arguments);
            if (TryFindUiTarget(screenPosition, explicitTarget, out var uiTarget))
            {
                return uiTarget;
            }

            return new InteractionTarget(explicitTarget, null);
        }

        if (TryFindUiTarget(screenPosition, null, out var implicitUiTarget))
        {
            return implicitUiTarget;
        }

        if (!uiOnly && TryFindWorldTarget(arguments, screenPosition, worldPosition, out var worldTarget))
        {
            return worldTarget;
        }

        throw new InvalidOperationException(uiOnly
            ? "No UI target found. Provide id/name or a screen position that hits a UI element."
            : "No interaction target found. Provide id/name, normalizedPosition, position, or worldPosition.");
    }

    private static bool TryFindUiTarget(Vector2 screenPosition, GameObject? preferredTarget, out InteractionTarget target)
    {
        target = default;
        var eventSystem = EnsureEventSystem();
        var eventData = new PointerEventData(eventSystem)
        {
            position = screenPosition,
        };

        var raycastResults = new List<RaycastResult>();
        eventSystem.RaycastAll(eventData, raycastResults);
        if (raycastResults.Count == 0)
        {
            foreach (var raycaster in UnityEngine.Object.FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None))
            {
                raycaster.Raycast(eventData, raycastResults);
            }
        }

        if (raycastResults.Count == 0)
        {
            raycastResults.AddRange(
                UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None)
                    .Where(rectTransform =>
                    {
                        if (rectTransform == null || !rectTransform.gameObject.activeInHierarchy)
                        {
                            return false;
                        }

                        if (rectTransform.GetComponent<Canvas>() != null)
                        {
                            return false;
                        }

                        var canvas = rectTransform.GetComponentInParent<Canvas>();
                        if (canvas == null || !canvas.isActiveAndEnabled)
                        {
                            return false;
                        }

                        var scaledSize = Vector2.Scale(rectTransform.rect.size, rectTransform.lossyScale);
                        if (scaledSize.x <= 0f || scaledSize.y <= 0f)
                        {
                            return false;
                        }

                        var center = (Vector2)rectTransform.position;
                        var bounds = new Rect(center - (scaledSize * 0.5f), scaledSize);
                        return bounds.Contains(screenPosition);
                    })
                    .OrderByDescending(rectTransform => rectTransform.GetComponentsInParent<Transform>(true).Length)
                    .ThenBy(rectTransform => rectTransform.rect.size.sqrMagnitude)
                    .Select(rectTransform => new RaycastResult { gameObject = rectTransform.gameObject }));
        }

        if (raycastResults.Count == 0 && preferredTarget == null)
        {
            var nearestSelectable = UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsSortMode.None)
                .Where(selectable => selectable != null && selectable.gameObject.activeInHierarchy)
                .Select(selectable => new
                {
                    selectable,
                    distance = Vector2.Distance((Vector2)selectable.transform.position, screenPosition),
                })
                .OrderBy(entry => entry.distance)
                .FirstOrDefault();

            if (nearestSelectable != null && nearestSelectable.distance <= 512f)
            {
                raycastResults.Add(new RaycastResult { gameObject = nearestSelectable.selectable.gameObject });
            }
        }

        foreach (var raycast in raycastResults)
        {
            var hitObject = raycast.gameObject;
            if (hitObject == null)
            {
                continue;
            }

            if (preferredTarget != null)
            {
                if (hitObject != preferredTarget
                    && !hitObject.transform.IsChildOf(preferredTarget.transform)
                    && !preferredTarget.transform.IsChildOf(hitObject.transform))
                {
                    continue;
                }

                target = new InteractionTarget(preferredTarget, raycast);
                return true;
            }

            target = new InteractionTarget(ResolveUiTarget(hitObject), raycast);
            return true;
        }

        return false;
    }

    private static GameObject ResolveUiTarget(GameObject hitObject)
    {
        return ExecuteEvents.GetEventHandler<IDragHandler>(hitObject)
            ?? ExecuteEvents.GetEventHandler<IBeginDragHandler>(hitObject)
            ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObject)
            ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(hitObject)
            ?? ExecuteEvents.GetEventHandler<IPointerUpHandler>(hitObject)
            ?? hitObject;
    }

    private static bool TryFindWorldTarget(JObject arguments, Vector2 screenPosition, Vector3? worldPosition, out InteractionTarget target)
    {
        target = default;

        if (worldPosition.HasValue)
        {
            var overlap2D = Physics2D.OverlapPointAll(worldPosition.Value);
            var collider2D = overlap2D
                .Where(collider => collider != null)
                .OrderByDescending(collider => collider.transform.position.z)
                .FirstOrDefault();
            if (collider2D != null)
            {
                target = new InteractionTarget(collider2D.gameObject, null);
                return true;
            }

            var collider3D = Physics.OverlapSphere(worldPosition.Value, 0.05f)
                .Where(collider => collider != null)
                .OrderBy(collider => Vector3.Distance(collider.ClosestPoint(worldPosition.Value), worldPosition.Value))
                .FirstOrDefault();
            if (collider3D != null)
            {
                target = new InteractionTarget(collider3D.gameObject, null);
                return true;
            }
        }

        var camera = FindInteractionCamera(arguments);
        if (camera == null)
        {
            return false;
        }

        var ray = camera.ScreenPointToRay(screenPosition);
        var best2D = Physics2D.GetRayIntersectionAll(ray, Mathf.Infinity)
            .Where(hit => hit.collider != null)
            .OrderBy(hit => hit.distance)
            .FirstOrDefault();
        var best3D = Physics.RaycastAll(ray, Mathf.Infinity)
            .Where(hit => hit.collider != null)
            .OrderBy(hit => hit.distance)
            .FirstOrDefault();

        if (best2D.collider != null && (best3D.collider == null || best2D.distance <= best3D.distance))
        {
            target = new InteractionTarget(best2D.collider.gameObject, null);
            return true;
        }

        if (best3D.collider != null)
        {
            target = new InteractionTarget(best3D.collider.gameObject, null);
            return true;
        }

        return false;
    }

    private static Camera? FindInteractionCamera(JObject arguments)
    {
        var cameraName = arguments.Value<string>("cameraName");
        if (!string.IsNullOrWhiteSpace(cameraName))
        {
            var namedCameraObject = GameObject.Find(cameraName);
            if (namedCameraObject != null)
            {
                var namedCamera = namedCameraObject.GetComponent<Camera>();
                if (namedCamera != null)
                {
                    return namedCamera;
                }
            }
        }

        return Camera.main
            ?? UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).FirstOrDefault(camera => camera.enabled)
            ?? SceneView.lastActiveSceneView?.camera;
    }

    private static Vector2 ResolveScreenPosition(JObject arguments, string positionKey, string normalizedKey, string worldKey, Vector2 fallback)
    {
        if (arguments[positionKey] is JArray positionValues)
        {
            return ParseVector2(positionValues, fallback);
        }

        if (arguments[normalizedKey] is JArray normalizedValues)
        {
            var normalizedPosition = ParseVector2(normalizedValues, new Vector2(0.5f, 0.5f));
            var screenSize = ResolveScreenSize(arguments);
            return new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y);
        }

        if (arguments[worldKey] is JArray worldValues)
        {
            var worldPosition = ParseVector3(worldValues, Vector3.zero);
            var camera = FindInteractionCamera(arguments);
            if (camera != null)
            {
                var screenPosition = camera.WorldToScreenPoint(worldPosition);
                return new Vector2(screenPosition.x, screenPosition.y);
            }
        }

        return fallback;
    }

    private static Vector2 ResolveScreenSize(JObject arguments)
    {
        var camera = FindInteractionCamera(arguments);
        if (camera != null && camera.pixelWidth > 0f && camera.pixelHeight > 0f)
        {
            return new Vector2(camera.pixelWidth, camera.pixelHeight);
        }

        if (Screen.width > 0 && Screen.height > 0)
        {
            return new Vector2(Screen.width, Screen.height);
        }

        return new Vector2(1920f, 1080f);
    }

    private static Vector3? ResolveWorldPosition(JObject arguments, string worldKey, Vector2 screenPosition)
    {
        if (arguments[worldKey] is JArray worldValues)
        {
            return ParseVector3(worldValues, Vector3.zero);
        }

        var camera = FindInteractionCamera(arguments);
        if (camera == null)
        {
            return null;
        }

        var world = camera.ScreenToWorldPoint(new Vector3(screenPosition.x, screenPosition.y, Mathf.Abs(camera.transform.position.z)));
        return new Vector3(world.x, world.y, world.z);
    }

    private static void ApplyTransform(Transform transform, JObject arguments)
    {
        ApplyPosition(transform, arguments["position"] as JArray);
        ApplyRotation(transform, arguments["rotation"] as JArray);
        ApplyScale(transform, arguments["scale"] as JArray);
    }

    /// <summary>2D 스프라이트 GameObject 를 생성하고 스프라이트/정렬/색상/플립을 적용한다.</summary>
    /// <param name="arguments">name, sprite, color, sortingLayer, sortingOrder, flipX, flipY, position, collider 인자.</param>
    /// <returns>생성한 <see cref="GameObject"/>.</returns>
    private static GameObject CreateSprite(JObject arguments)
    {
        var name = arguments.Value<string>("name") ?? "Sprite";
        var gameObject = new GameObject(name);
        var spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
        var resolved = ResolveSpriteAsset(arguments.Value<string>("sprite"));
        spriteRenderer.sprite = resolved != null ? resolved : AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        spriteRenderer.color = Color.white;
        ApplySpriteRenderer(spriteRenderer, arguments, null, null);
        ApplyTransform(gameObject.transform, arguments);

        if (arguments["collider"]?.Value<bool?>() ?? true)
        {
            gameObject.AddComponent<BoxCollider2D>();
        }

        return gameObject;
    }

    /// <summary>기존 SpriteRenderer 의 스프라이트/정렬/색상/플립을 수정한다.</summary>
    /// <param name="arguments">대상(id/name)과 sprite, color, sortingLayer, sortingOrder, flipX, flipY 인자.</param>
    /// <returns><see cref="Success"/> 결과 객체.</returns>
    private static object SetSpriteRenderer(JObject arguments)
    {
        var gameObject = FindGameObject(arguments);
        var renderer = gameObject.GetComponent<SpriteRenderer>()
            ?? throw NotFound($"SpriteRenderer not found on '{gameObject.name}'.");
        var spriteSpec = arguments.Value<string>("sprite");
        if (!string.IsNullOrEmpty(spriteSpec))
        {
            var sprite = ResolveSpriteAsset(spriteSpec) ?? throw NotFound($"Sprite not found: {spriteSpec}");
            renderer.sprite = sprite;
        }

        ApplySpriteRenderer(renderer, arguments, null, null);
        EditorUtility.SetDirty(renderer);
        Emit("component.changed", $"SpriteRenderer set: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID() });
        return Success(GameObjectObject(gameObject), "SpriteRenderer updated.");
    }

    /// <summary>인자에 존재하는 필드만 SpriteRenderer 에 적용한다(색상/정렬 레이어/정렬 순서/플립).</summary>
    /// <param name="renderer">대상 <see cref="SpriteRenderer"/>.</param>
    /// <param name="arguments">color, sortingLayer, sortingOrder, flipX, flipY 인자.</param>
    /// <param name="applied">적용한 필드 목록(미사용 시 null).</param>
    /// <param name="skipped">건너뛴 필드 목록(미사용 시 null).</param>
    private static void ApplySpriteRenderer(SpriteRenderer renderer, JObject arguments, JArray applied, JArray skipped)
    {
        var colorText = arguments.Value<string>("color");
        if (!string.IsNullOrEmpty(colorText))
        {
            renderer.color = ParseColor(colorText, renderer.color);
        }

        var sortingLayer = arguments.Value<string>("sortingLayer");
        if (!string.IsNullOrEmpty(sortingLayer))
        {
            renderer.sortingLayerName = sortingLayer;
        }

        var sortingOrder = arguments.Value<int?>("sortingOrder");
        if (sortingOrder.HasValue)
        {
            renderer.sortingOrder = sortingOrder.Value;
        }

        var flipX = arguments.Value<bool?>("flipX");
        if (flipX.HasValue)
        {
            renderer.flipX = flipX.Value;
        }

        var flipY = arguments.Value<bool?>("flipY");
        if (flipY.HasValue)
        {
            renderer.flipY = flipY.Value;
        }
    }

    /// <summary>스프라이트 에셋 경로(또는 경로::서브스프라이트명)를 Sprite 로 로드한다.</summary>
    /// <param name="spec">에셋 경로 또는 "경로::서브스프라이트명" 형식의 문자열.</param>
    /// <returns>로드한 <see cref="Sprite"/>. 찾지 못하면 null.</returns>
    private static Sprite? ResolveSpriteAsset(string spec)
    {
        if (string.IsNullOrEmpty(spec))
        {
            return null;
        }

        var sep = spec.IndexOf("::", StringComparison.Ordinal);
        var path = sep >= 0 ? spec.Substring(0, sep) : spec;
        var subName = sep >= 0 ? spec.Substring(sep + 2) : null;
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (string.IsNullOrEmpty(subName))
        {
            if (sprite == null && AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            }

            return sprite;
        }

        var reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
        var named = reps.OfType<Sprite>().FirstOrDefault(s => s.name == subName);
        if (named != null)
        {
            return named;
        }

        var all = AssetDatabase.LoadAllAssetsAtPath(path);
        return all.OfType<Sprite>().FirstOrDefault(s => s.name == subName);
    }

    private static GameObject CreateButton(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var buttonObject = new GameObject(arguments.Value<string>("name") ?? "Button", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        ApplyRectTransform(buttonObject.GetComponent<RectTransform>(), arguments, new Vector2(160f, 48f));

        var image = buttonObject.GetComponent<Image>();
        image.color = ParseColor(arguments.Value<string>("color"), new Color(0.15f, 0.55f, 0.95f, 1f));

        var labelFontSize = arguments["fontSize"]?.Value<float>() ?? 24f;
        var labelFontStyle = ParseFontStyle(arguments.Value<string>("fontStyle"), FontStyle.Normal);
        var labelAlignment = ParseTextAnchor(arguments.Value<string>("alignment"), TextAnchor.MiddleCenter);
        var labelObject = CreateUiTextChild(buttonObject.transform, "Label", arguments.Value<string>("text") ?? buttonObject.name, ParseColor(arguments.Value<string>("textColor"), Color.white), labelFontStyle, labelAlignment, labelFontSize);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        EnsureEventSystem();
        return buttonObject;
    }

    private static GameObject CreateText(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var textObject = new GameObject(arguments.Value<string>("name") ?? "Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        ApplyRectTransform(textObject.GetComponent<RectTransform>(), arguments, new Vector2(240f, 48f));

        var fontSize = arguments["fontSize"]?.Value<float>() ?? 24f;
        var fontStyle = ParseFontStyle(arguments.Value<string>("fontStyle"), FontStyle.Normal);
        var alignment = ParseTextAnchor(arguments.Value<string>("alignment"), TextAnchor.MiddleCenter);
        ConfigureTmpText(textObject.GetComponent<TextMeshProUGUI>(), arguments.Value<string>("text") ?? textObject.name, ParseColor(arguments.Value<string>("color"), Color.white), fontStyle, alignment, fontSize);
        EnsureEventSystem();
        return textObject;
    }

    private static GameObject CreateImage(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var imageObject = new GameObject(arguments.Value<string>("name") ?? "Image", typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        ApplyRectTransform(imageObject.GetComponent<RectTransform>(), arguments, new Vector2(128f, 128f));
        var image = imageObject.GetComponent<Image>();
        var spritePath = arguments.Value<string>("spritePath");
        if (!string.IsNullOrEmpty(spritePath))
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (sprite == null)
            {
                var importer = AssetImporter.GetAtPath(spritePath) as TextureImporter;
                if (importer != null && importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.SaveAndReimport();
                    sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                }
            }
            if (sprite != null) image.sprite = sprite;
        }
        else
        {
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        }
        image.color = ParseColor(arguments.Value<string>("color"), Color.white);
        var imageTypeStr = arguments.Value<string>("imageType");
        if (!string.IsNullOrEmpty(imageTypeStr))
        {
            image.type = imageTypeStr switch
            {
                "Sliced" => Image.Type.Sliced,
                "Tiled" => Image.Type.Tiled,
                "Filled" => Image.Type.Filled,
                _ => Image.Type.Simple,
            };
        }

        if (arguments.Value<bool?>("preserveAspect") == true)
            image.preserveAspect = true;

        if (arguments.Value<bool?>("useNativeSize") == true && image.sprite != null)
            image.SetNativeSize();

        EnsureEventSystem();
        return imageObject;
    }

    private static GameObject CreateToggle(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var toggleObject = new GameObject(arguments.Value<string>("name") ?? "Toggle", typeof(RectTransform), typeof(Toggle));
        toggleObject.transform.SetParent(parent, false);
        ApplyRectTransform(toggleObject.GetComponent<RectTransform>(), arguments, new Vector2(240f, 40f));

        var backgroundObject = CreateUiImageChild(toggleObject.transform, "Background", ParseColor(arguments.Value<string>("backgroundColor"), new Color(0.16f, 0.16f, 0.16f, 1f)));
        ConfigureAnchoredRect(backgroundObject.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(24f, 24f), Vector2.zero);

        var checkmarkObject = CreateUiImageChild(backgroundObject.transform, "Checkmark", ParseColor(arguments.Value<string>("checkmarkColor"), new Color(0.2f, 0.8f, 0.35f, 1f)));
        StretchRect(checkmarkObject.GetComponent<RectTransform>(), new Vector2(4f, 4f), new Vector2(-4f, -4f));

        var labelObject = CreateUiTextChild(toggleObject.transform, "Label", arguments.Value<string>("text") ?? toggleObject.name, ParseColor(arguments.Value<string>("textColor"), Color.white), FontStyle.Normal, TextAnchor.MiddleLeft, 20f);
        StretchRect(labelObject.GetComponent<RectTransform>(), new Vector2(36f, 0f), Vector2.zero);

        var toggle = toggleObject.GetComponent<Toggle>();
        toggle.targetGraphic = backgroundObject.GetComponent<Image>();
        toggle.graphic = checkmarkObject.GetComponent<Image>();
        toggle.isOn = arguments["isOn"]?.Value<bool?>() ?? false;
        EnsureEventSystem();
        return toggleObject;
    }

    private static GameObject CreateSlider(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var sliderObject = new GameObject(arguments.Value<string>("name") ?? "Slider", typeof(RectTransform), typeof(Slider));
        sliderObject.transform.SetParent(parent, false);
        ApplyRectTransform(sliderObject.GetComponent<RectTransform>(), arguments, new Vector2(280f, 40f));

        var backgroundObject = CreateUiImageChild(sliderObject.transform, "Background", ParseColor(arguments.Value<string>("backgroundColor"), new Color(0.16f, 0.16f, 0.16f, 1f)));
        StretchRect(backgroundObject.GetComponent<RectTransform>(), new Vector2(0f, 12f), new Vector2(0f, -12f));

        var fillAreaObject = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaObject.transform.SetParent(sliderObject.transform, false);
        StretchRect(fillAreaObject.GetComponent<RectTransform>(), new Vector2(10f, 12f), new Vector2(-10f, -12f));

        var fillObject = CreateUiImageChild(fillAreaObject.transform, "Fill", ParseColor(arguments.Value<string>("fillColor"), new Color(0.18f, 0.6f, 0.95f, 1f)));
        StretchRect(fillObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero);

        var handleAreaObject = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleAreaObject.transform.SetParent(sliderObject.transform, false);
        StretchRect(handleAreaObject.GetComponent<RectTransform>(), new Vector2(10f, 0f), new Vector2(-10f, 0f));

        var handleObject = CreateUiImageChild(handleAreaObject.transform, "Handle", ParseColor(arguments.Value<string>("handleColor"), Color.white));
        ConfigureAnchoredRect(handleObject.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(20f, 20f), Vector2.zero);

        var slider = sliderObject.GetComponent<Slider>();
        slider.fillRect = fillObject.GetComponent<RectTransform>();
        slider.handleRect = handleObject.GetComponent<RectTransform>();
        slider.targetGraphic = handleObject.GetComponent<Image>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = arguments["minValue"]?.Value<float?>() ?? 0f;
        slider.maxValue = arguments["maxValue"]?.Value<float?>() ?? 1f;
        slider.wholeNumbers = arguments["wholeNumbers"]?.Value<bool?>() ?? false;
        slider.value = arguments["value"]?.Value<float?>() ?? slider.minValue;
        EnsureEventSystem();
        return sliderObject;
    }

    private static GameObject CreateScrollRect(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var scrollRectObject = new GameObject(arguments.Value<string>("name") ?? "ScrollRect", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scrollRectObject.transform.SetParent(parent, false);
        ApplyRectTransform(scrollRectObject.GetComponent<RectTransform>(), arguments, new Vector2(320f, 220f));

        var rootImage = scrollRectObject.GetComponent<Image>();
        rootImage.color = ParseColor(arguments.Value<string>("backgroundColor"), new Color(0.1f, 0.1f, 0.1f, 0.9f));

        var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewportObject.transform.SetParent(scrollRectObject.transform, false);
        StretchRect(viewportObject.GetComponent<RectTransform>(), new Vector2(8f, 8f), new Vector2(-8f, -8f));
        var viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = ParseColor(arguments.Value<string>("viewportColor"), new Color(0.14f, 0.14f, 0.14f, 0.95f));
        viewportObject.GetComponent<Mask>().showMaskGraphic = false;

        var contentObject = new GameObject("Content", typeof(RectTransform));
        contentObject.transform.SetParent(viewportObject.transform, false);
        var contentRect = contentObject.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);

        var itemCount = Math.Max(arguments["itemCount"]?.Value<int?>() ?? 8, 1);
        var itemHeight = arguments["itemHeight"]?.Value<float?>() ?? 36f;
        var contentHeight = itemCount * itemHeight;
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0f, contentHeight);

        for (var index = 0; index < itemCount; index++)
        {
            var itemObject = CreateUiTextChild(contentObject.transform, $"Item {index + 1}", (arguments.Value<string>("itemPrefix") ?? "Item") + " " + (index + 1), ParseColor(arguments.Value<string>("textColor"), Color.white), FontStyle.Normal, TextAnchor.MiddleLeft, 20f);
            var itemRect = itemObject.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0f, 1f);
            itemRect.anchorMax = new Vector2(1f, 1f);
            itemRect.pivot = new Vector2(0.5f, 1f);
            itemRect.anchoredPosition = new Vector2(0f, -index * itemHeight);
            itemRect.sizeDelta = new Vector2(0f, itemHeight);
        }

        var scrollRect = scrollRectObject.GetComponent<ScrollRect>();
        scrollRect.viewport = viewportObject.GetComponent<RectTransform>();
        scrollRect.content = contentRect;
        scrollRect.horizontal = arguments["horizontal"]?.Value<bool?>() ?? false;
        scrollRect.vertical = arguments["vertical"]?.Value<bool?>() ?? true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        if (arguments["normalizedPosition"] is JArray normalizedPositionValues)
        {
            scrollRect.normalizedPosition = ParseVector2(normalizedPositionValues, new Vector2(0f, 1f));
        }
        else
        {
            scrollRect.normalizedPosition = new Vector2(arguments["horizontalNormalizedPosition"]?.Value<float?>() ?? 0f, arguments["verticalNormalizedPosition"]?.Value<float?>() ?? 1f);
        }

        EnsureEventSystem();
        return scrollRectObject;
    }

    private static GameObject CreateInputField(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var inputObject = new GameObject(arguments.Value<string>("name") ?? "InputField", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        inputObject.transform.SetParent(parent, false);
        ApplyRectTransform(inputObject.GetComponent<RectTransform>(), arguments, new Vector2(320f, 44f));

        var image = inputObject.GetComponent<Image>();
        image.color = ParseColor(arguments.Value<string>("backgroundColor"), new Color(0.12f, 0.12f, 0.12f, 1f));

        var textAreaObject = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textAreaObject.transform.SetParent(inputObject.transform, false);
        StretchRect(textAreaObject.GetComponent<RectTransform>(), new Vector2(12f, 6f), new Vector2(-12f, -6f));

        var textObject = CreateUiTextChild(textAreaObject.transform, "Text", arguments.Value<string>("text") ?? string.Empty, ParseColor(arguments.Value<string>("textColor"), Color.white), FontStyle.Normal, TextAnchor.MiddleLeft, 20f);
        StretchRect(textObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero);

        var placeholderObject = CreateUiTextChild(textAreaObject.transform, "Placeholder", arguments.Value<string>("placeholder") ?? "Enter text", ParseColor(arguments.Value<string>("placeholderColor"), new Color(1f, 1f, 1f, 0.45f)), FontStyle.Italic, TextAnchor.MiddleLeft, 20f);
        StretchRect(placeholderObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero);

        var inputField = inputObject.GetComponent<TMP_InputField>();
        inputField.textViewport = textAreaObject.GetComponent<RectTransform>();
        inputField.textComponent = textObject.GetComponent<TextMeshProUGUI>();
        inputField.placeholder = placeholderObject.GetComponent<TextMeshProUGUI>();
        inputField.lineType = arguments["multiline"]?.Value<bool?>() ?? false ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
        inputField.text = arguments.Value<string>("text") ?? string.Empty;
        inputField.targetGraphic = image;
        EnsureEventSystem();
        return inputObject;
    }

    private static GameObject CreatePanel(JObject arguments)
    {
        var canvas = EnsureCanvas(arguments.Value<string>("canvasName") ?? "Canvas");
        var parent = ResolveUiParent(arguments, canvas);
        var panelObject = new GameObject(arguments.Value<string>("name") ?? "Panel", typeof(RectTransform));
        panelObject.transform.SetParent(parent, false);
        ApplyRectTransform(panelObject.GetComponent<RectTransform>(), arguments, new Vector2(200f, 200f));

        var colorStr = arguments.Value<string>("color");
        if (!string.IsNullOrEmpty(colorStr))
        {
            var image = panelObject.AddComponent<Image>();
            image.color = ParseColor(colorStr, Color.white);
        }

        EnsureEventSystem();
        return panelObject;
    }

    private static JObject AddLayout(JObject arguments)
    {
        var gameObject = FindGameObject(arguments);
        var layoutType = arguments.Value<string>("layoutType") ?? "Vertical";

        switch (layoutType)
        {
            case "Horizontal":
            {
                var layout = gameObject.GetComponent<HorizontalLayoutGroup>() ?? gameObject.AddComponent<HorizontalLayoutGroup>();
                ApplyLayoutGroupSettings(layout, arguments);
                return new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["layoutType"] = layoutType };
            }
            case "Vertical":
            {
                var layout = gameObject.GetComponent<VerticalLayoutGroup>() ?? gameObject.AddComponent<VerticalLayoutGroup>();
                ApplyLayoutGroupSettings(layout, arguments);
                return new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["layoutType"] = layoutType };
            }
            case "Grid":
            {
                var layout = gameObject.GetComponent<GridLayoutGroup>() ?? gameObject.AddComponent<GridLayoutGroup>();
                layout.cellSize = ParseVector2(arguments["cellSize"] as JArray, new Vector2(100f, 100f));
                layout.spacing = ParseVector2(arguments["gridSpacing"] as JArray, Vector2.zero);
                layout.childAlignment = ParseTextAnchor(arguments.Value<string>("childAlignment"), TextAnchor.UpperLeft);
                layout.padding = ParseRectOffset(arguments);
                return new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["layoutType"] = layoutType };
            }
            case "ContentSizeFitter":
            {
                var fitter = gameObject.GetComponent<ContentSizeFitter>() ?? gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ParseFitMode(arguments.Value<string>("horizontalFit"), ContentSizeFitter.FitMode.Unconstrained);
                fitter.verticalFit = ParseFitMode(arguments.Value<string>("verticalFit"), ContentSizeFitter.FitMode.Unconstrained);
                return new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["layoutType"] = layoutType };
            }
            default:
                throw new InvalidOperationException($"Unknown layoutType: {layoutType}");
        }
    }

    private static void ApplyLayoutGroupSettings(HorizontalOrVerticalLayoutGroup layout, JObject arguments)
    {
        layout.spacing = arguments["spacing"]?.Value<float>() ?? 0f;
        layout.childAlignment = ParseTextAnchor(arguments.Value<string>("childAlignment"), TextAnchor.UpperLeft);
        layout.childForceExpandWidth = arguments["childForceExpandWidth"]?.Value<bool>() ?? true;
        layout.childForceExpandHeight = arguments["childForceExpandHeight"]?.Value<bool>() ?? true;
        layout.childControlWidth = arguments["childControlWidth"]?.Value<bool>() ?? true;
        layout.childControlHeight = arguments["childControlHeight"]?.Value<bool>() ?? true;
        layout.padding = ParseRectOffset(arguments);
    }

    private static RectOffset ParseRectOffset(JObject arguments)
    {
        return new RectOffset(
            arguments["paddingLeft"]?.Value<int>() ?? 0,
            arguments["paddingRight"]?.Value<int>() ?? 0,
            arguments["paddingTop"]?.Value<int>() ?? 0,
            arguments["paddingBottom"]?.Value<int>() ?? 0
        );
    }

    private static ContentSizeFitter.FitMode ParseFitMode(string value, ContentSizeFitter.FitMode fallback)
    {
        return value switch
        {
            "Unconstrained" => ContentSizeFitter.FitMode.Unconstrained,
            "MinSize" => ContentSizeFitter.FitMode.MinSize,
            "PreferredSize" => ContentSizeFitter.FitMode.PreferredSize,
            _ => fallback,
        };
    }

    private static JObject ModifyRectTransform(JObject arguments)
    {
        var gameObject = FindGameObject(arguments);
        var rt = gameObject.GetComponent<RectTransform>();
        if (rt == null) throw new InvalidOperationException($"GameObject '{gameObject.name}' has no RectTransform.");

        if (arguments["anchorMin"] is JArray anchorMinArr) rt.anchorMin = ParseVector2(anchorMinArr, rt.anchorMin);
        if (arguments["anchorMax"] is JArray anchorMaxArr) rt.anchorMax = ParseVector2(anchorMaxArr, rt.anchorMax);
        if (arguments["pivot"] is JArray pivotArr) rt.pivot = ParseVector2(pivotArr, rt.pivot);
        if (arguments["anchoredPosition"] is JArray posArr) rt.anchoredPosition = ParseVector2(posArr, rt.anchoredPosition);
        if (arguments["size"] is JArray sizeArr) rt.sizeDelta = ParseVector2(sizeArr, rt.sizeDelta);
        if (arguments["offsetMin"] is JArray offMinArr) rt.offsetMin = ParseVector2(offMinArr, rt.offsetMin);
        if (arguments["offsetMax"] is JArray offMaxArr) rt.offsetMax = ParseVector2(offMaxArr, rt.offsetMax);

        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["anchorMin"] = new JArray(rt.anchorMin.x, rt.anchorMin.y),
            ["anchorMax"] = new JArray(rt.anchorMax.x, rt.anchorMax.y),
            ["pivot"] = new JArray(rt.pivot.x, rt.pivot.y),
            ["anchoredPosition"] = new JArray(rt.anchoredPosition.x, rt.anchoredPosition.y),
            ["sizeDelta"] = new JArray(rt.sizeDelta.x, rt.sizeDelta.y),
        };
    }

    private static JObject CaptureScreenshot(JObject arguments)
    {
        var width = arguments["width"]?.Value<int>() ?? 1920;
        var height = arguments["height"]?.Value<int>() ?? 1080;
        var outputPath = arguments.Value<string>("outputPath") ?? throw new InvalidOperationException("outputPath is required.");

        var renderTexture = new RenderTexture(width, height, 24);
        var previousActive = RenderTexture.active;

        // Route ScreenSpaceOverlay canvases through the main camera so overlay UI is captured;
        // SubmitRenderRequest / camera render does not composite overlay canvases on its own.
        var overlayCanvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(canvas => canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            .ToArray();
        var mainCamera = Camera.main;
        if (mainCamera != null)
        {
            foreach (var canvas in overlayCanvases)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = mainCamera;
            }
        }

        try
        {
            RenderTexture.active = renderTexture;
            GL.Clear(true, true, Color.clear);

            foreach (var camera in Camera.allCameras.OrderBy(cameraItem => cameraItem.depth))
            {
                RenderCameraToTexture(camera, renderTexture);
            }

            RenderTexture.active = renderTexture;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }
        finally
        {
            RenderTexture.active = previousActive;
            UnityEngine.Object.DestroyImmediate(renderTexture);
            if (mainCamera != null)
            {
                foreach (var canvas in overlayCanvases)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.worldCamera = null;
                }
            }
        }

        if (outputPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            AssetDatabase.Refresh();
        }

        return new JObject
        {
            ["outputPath"] = outputPath,
            ["width"] = width,
            ["height"] = height,
            ["mode"] = "renderRequest",
        };
    }

    private static void RenderCameraToTexture(Camera camera, RenderTexture renderTexture)
    {
        // URP/HDRP-correct on-demand render. The legacy camera.Render() path returns black
        // under a Scriptable Render Pipeline, so prefer SubmitRenderRequest when supported.
        var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = renderTexture };
        if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
        {
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
            return;
        }

        var previousTarget = camera.targetTexture;
        camera.targetTexture = renderTexture;
        camera.Render();
        camera.targetTexture = previousTarget;
    }

    private static JObject ResizeGameView(JObject arguments)
    {
        var width = arguments["width"]?.Value<int>() ?? 1920;
        var height = arguments["height"]?.Value<int>() ?? 1080;

        var gameViewType = Type.GetType("UnityEditor.GameView, UnityEditor");
        if (gameViewType == null) throw new InvalidOperationException("Cannot access GameView type.");

        var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
        if (gameView == null) throw new InvalidOperationException("Cannot open GameView window.");

        var gameViewSizesType = Type.GetType("UnityEditor.GameViewSizes, UnityEditor");
        if (gameViewSizesType == null) throw new InvalidOperationException("Cannot access GameViewSizes type.");

        var singletonProp = gameViewSizesType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        var instance = singletonProp?.GetValue(null);
        if (instance == null)
        {
            var singletonBase = Type.GetType("UnityEditor.ScriptableSingleton`1, UnityEditor");
            if (singletonBase != null)
            {
                var concreteBase = singletonBase.MakeGenericType(gameViewSizesType);
                var baseProp = concreteBase.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                instance = baseProp?.GetValue(null);
            }
        }
        if (instance == null) throw new InvalidOperationException("Cannot get GameViewSizes instance.");

        var currentGroupTypeProp = gameViewType.GetProperty("currentSizeGroupType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object currentGroupTypeVal;
        if (currentGroupTypeProp != null)
        {
            currentGroupTypeVal = currentGroupTypeProp.GetValue(gameView);
        }
        else
        {
            var gameViewSizeGroupType = Type.GetType("UnityEditor.GameViewSizeGroupType, UnityEditor");
            currentGroupTypeVal = Enum.ToObject(gameViewSizeGroupType!, 0);
        }

        var getGroupMethod = gameViewSizesType.GetMethod("GetGroup", BindingFlags.Public | BindingFlags.Instance);
        if (getGroupMethod == null) throw new InvalidOperationException("Cannot find GetGroup method.");

        var group = getGroupMethod.Invoke(instance, new[] { currentGroupTypeVal });
        if (group == null) throw new InvalidOperationException("Cannot resolve GameViewSizeGroup.");

        var getTotalCountMethod = group.GetType().GetMethod("GetTotalCount");
        var getGameViewSizeMethod = group.GetType().GetMethod("GetGameViewSize");
        var addCustomSizeMethod = group.GetType().GetMethod("AddCustomSize");

        if (getTotalCountMethod == null || getGameViewSizeMethod == null || addCustomSizeMethod == null)
            throw new InvalidOperationException("Cannot find required GameViewSizeGroup methods.");

        var gameViewSizeType = Type.GetType("UnityEditor.GameViewSize, UnityEditor");
        var gameViewSizeTypeEnum = Type.GetType("UnityEditor.GameViewSizeType, UnityEditor");

        var totalCount = (int)getTotalCountMethod.Invoke(group, null)!;
        var targetIndex = -1;

        for (var i = 0; i < totalCount; i++)
        {
            var size = getGameViewSizeMethod.Invoke(group, new object[] { i });
            if (size == null) continue;
            var wProp = size.GetType().GetProperty("width");
            var hProp = size.GetType().GetProperty("height");
            if (wProp == null || hProp == null) continue;
            var w = (int)wProp.GetValue(size)!;
            var h = (int)hProp.GetValue(size)!;
            if (w == width && h == height)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0 && gameViewSizeType != null && gameViewSizeTypeEnum != null)
        {
            var fixedResolution = Enum.Parse(gameViewSizeTypeEnum, "FixedResolution");
            var ctor = gameViewSizeType.GetConstructor(new[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) });
            if (ctor != null)
            {
                var newSize = ctor.Invoke(new[] { fixedResolution, width, height, $"{width}x{height}" });
                addCustomSizeMethod.Invoke(group, new[] { newSize });
                targetIndex = (int)getTotalCountMethod.Invoke(group, null)! - 1;
            }
        }

        if (targetIndex >= 0)
        {
            var selectedSizeIndexProp = gameViewType.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            selectedSizeIndexProp?.SetValue(gameView, targetIndex);
        }

        gameView.Repaint();

        return new JObject
        {
            ["width"] = width,
            ["height"] = height,
            ["sizeIndex"] = targetIndex,
        };
    }

    private static GameObject EnsureCanvas(string canvasName, JObject arguments = null)
    {
        var existing = GameObject.Find(canvasName);
        if (existing != null && existing.GetComponent<Canvas>() != null)
        {
            if (arguments != null)
            {
                var existingScaler = existing.GetComponent<CanvasScaler>();
                if (existingScaler != null) ApplyCanvasScalerSettings(existingScaler, arguments);
            }
            EnsureEventSystem();
            return existing;
        }

        var canvasObject = new GameObject(canvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        ApplyCanvasScalerSettings(scaler, arguments);
        EnsureEventSystem();
        return canvasObject;
    }

    private static void ApplyCanvasScalerSettings(CanvasScaler scaler, JObject arguments)
    {
        scaler.referenceResolution = ParseVector2(arguments?["referenceResolution"] as JArray, new Vector2(1920f, 1080f));
        scaler.screenMatchMode = (arguments?.Value<string>("screenMatchMode")) switch
        {
            "Shrink" => CanvasScaler.ScreenMatchMode.Shrink,
            "MatchWidthOrHeight" => CanvasScaler.ScreenMatchMode.MatchWidthOrHeight,
            _ => CanvasScaler.ScreenMatchMode.Expand,
        };
        scaler.matchWidthOrHeight = arguments?["matchWidthOrHeight"]?.Value<float>() ?? 0.5f;
    }

    private static EventSystem EnsureEventSystem()
    {
        var existing = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
        if (existing != null)
        {
            EnsureInputModule(existing.gameObject);
            return existing;
        }

        var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));
        EnsureInputModule(eventSystemObject);
        return eventSystemObject.GetComponent<EventSystem>();
    }

    private static Transform ResolveUiParent(JObject arguments, GameObject canvasObject)
    {
        var parentId = arguments["parentId"]?.Value<int?>();
        if (parentId.HasValue)
        {
            var parent = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
            if (parent != null && parent.GetComponent<RectTransform>() != null)
                return parent.transform;
        }

        var parentName = arguments.Value<string>("parentName");
        if (!string.IsNullOrEmpty(parentName))
        {
            var parent = GameObject.Find(parentName);
            if (parent != null && parent.GetComponent<RectTransform>() != null)
                return parent.transform;
        }

        return canvasObject.transform;
    }

    private static bool IsSelectedByEventSystem(GameObject gameObject)
    {
        var currentSelected = EnsureEventSystem().currentSelectedGameObject;
        return currentSelected == gameObject;
    }

    private static void FocusUiTarget(GameObject gameObject)
    {
        var eventSystem = EnsureEventSystem();
        if (eventSystem.currentSelectedGameObject == gameObject)
        {
            ActivateInputField(gameObject);
            return;
        }

        eventSystem.SetSelectedGameObject(gameObject);
        ActivateInputField(gameObject);
    }

    private static GameObject? ClearUiFocus()
    {
        var eventSystem = EnsureEventSystem();
        var previous = eventSystem.currentSelectedGameObject;
        if (previous != null)
        {
            DeactivateInputField(previous);
        }

        eventSystem.SetSelectedGameObject(null);
        return previous;
    }

    private static GameObject CreateUiImageChild(Transform parent, string name, Color color)
    {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        imageObject.GetComponent<Image>().color = color;
        return imageObject;
    }

    private static GameObject CreateUiTextChild(Transform parent, string name, string textValue, Color color, FontStyle fontStyle, TextAnchor alignment, float fontSize)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        ConfigureTmpText(textObject.GetComponent<TextMeshProUGUI>(), textValue, color, fontStyle, alignment, fontSize);
        return textObject;
    }

    private static void StretchRect(RectTransform rectTransform, Vector2 offsetMin, Vector2 offsetMax)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.offsetMin = offsetMin;
        rectTransform.offsetMax = offsetMax;
    }

    private static void ConfigureAnchoredRect(RectTransform rectTransform, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Vector2 anchoredPosition)
    {
        rectTransform.anchorMin = anchorMin;
        rectTransform.anchorMax = anchorMax;
        rectTransform.pivot = pivot;
        rectTransform.sizeDelta = sizeDelta;
        rectTransform.anchoredPosition = anchoredPosition;
    }

    private static JObject DispatchTap(JObject arguments, string eventType, bool uiOnly)
    {
        var position = ResolveScreenPosition(arguments, "position", "normalizedPosition", "worldPosition", Vector2.zero);
        var worldPosition = ResolveWorldPosition(arguments, "worldPosition", position);
        var target = FindInteractionTarget(arguments, position, worldPosition, uiOnly);
        var gameObject = target.GameObject;
        var pointerId = arguments["pointerId"]?.Value<int?>() ?? -1;
        var eventData = CreatePointerEventData(gameObject, target, position, pointerId);
        eventData.clickCount = 1;
        eventData.clickTime = (float)EditorApplication.timeSinceStartup;

        if (uiOnly)
        {
            FocusUiTarget(gameObject);
        }

        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerClickHandler);

        Emit(eventType, $"Pointer tap: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["pointerId"] = pointerId });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["position"] = new JArray(position.x, position.y),
            ["pointerId"] = pointerId,
        };
    }

    private static JObject DispatchDoubleTap(JObject arguments, string eventType, bool uiOnly)
    {
        var position = ResolveScreenPosition(arguments, "position", "normalizedPosition", "worldPosition", Vector2.zero);
        var worldPosition = ResolveWorldPosition(arguments, "worldPosition", position);
        var target = FindInteractionTarget(arguments, position, worldPosition, uiOnly);
        var gameObject = target.GameObject;
        var pointerId = arguments["pointerId"]?.Value<int?>() ?? -1;

        if (uiOnly)
        {
            FocusUiTarget(gameObject);
        }

        DispatchSingleTap(gameObject, target, position, 1, pointerId);
        DispatchSingleTap(gameObject, target, position, 2, pointerId);

        Emit(eventType, $"Pointer double tap: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["clickCount"] = 2, ["pointerId"] = pointerId });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["position"] = new JArray(position.x, position.y),
            ["clickCount"] = 2,
            ["pointerId"] = pointerId,
        };
    }

    private static JObject DispatchLongPress(JObject arguments, string eventType, bool uiOnly)
    {
        var position = ResolveScreenPosition(arguments, "position", "normalizedPosition", "worldPosition", Vector2.zero);
        var worldPosition = ResolveWorldPosition(arguments, "worldPosition", position);
        var target = FindInteractionTarget(arguments, position, worldPosition, uiOnly);
        var gameObject = target.GameObject;
        var durationMs = Math.Max(arguments["durationMs"]?.Value<int?>() ?? 600, 100);
        var pointerId = arguments["pointerId"]?.Value<int?>() ?? -1;
        var eventData = CreatePointerEventData(gameObject, target, position, pointerId);

        if (uiOnly)
        {
            FocusUiTarget(gameObject);
        }

        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);

        var deadline = EditorApplication.timeSinceStartup + (durationMs / 1000d);
        while (EditorApplication.timeSinceStartup < deadline)
        {
            Thread.Sleep(10);
        }

        eventData.clickTime = (float)EditorApplication.timeSinceStartup;
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);

        Emit(eventType, $"Pointer long press: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["durationMs"] = durationMs, ["pointerId"] = pointerId });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["position"] = new JArray(position.x, position.y),
            ["durationMs"] = durationMs,
            ["pointerId"] = pointerId,
        };
    }

    private static JObject DispatchDrag(JObject arguments, string eventType, bool uiOnly)
    {
        var from = ResolveScreenPosition(arguments, "from", "normalizedFrom", "worldFrom", Vector2.zero);
        var to = ResolveScreenPosition(arguments, "to", "normalizedTo", "worldTo", new Vector2(128f, 128f));
        var worldFrom = ResolveWorldPosition(arguments, "worldFrom", from);
        var target = FindInteractionTarget(arguments, from, worldFrom, uiOnly);
        var gameObject = target.GameObject;
        var pointerId = arguments["pointerId"]?.Value<int?>() ?? -1;
        var eventData = CreatePointerEventData(gameObject, target, from, pointerId);
        eventData.delta = Vector2.zero;
        eventData.useDragThreshold = false;
        eventData.pointerDrag = gameObject;

        if (uiOnly)
        {
            FocusUiTarget(gameObject);
        }

        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.initializePotentialDrag);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.beginDragHandler);

        eventData.delta = to - from;
        eventData.position = to;
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.dragHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.endDragHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);

        Emit(eventType, $"Pointer drag: {gameObject.name}", new JObject { ["id"] = gameObject.GetInstanceID(), ["name"] = gameObject.name, ["pointerId"] = pointerId });
        return new JObject
        {
            ["id"] = gameObject.GetInstanceID(),
            ["name"] = gameObject.name,
            ["from"] = new JArray(from.x, from.y),
            ["to"] = new JArray(to.x, to.y),
            ["pointerId"] = pointerId,
        };
    }

    private static void DispatchSingleTap(GameObject gameObject, InteractionTarget target, Vector2 position, int clickCount, int pointerId)
    {
        var eventData = CreatePointerEventData(gameObject, target, position, pointerId);
        eventData.clickCount = clickCount;
        eventData.clickTime = (float)EditorApplication.timeSinceStartup;
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerEnterHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(gameObject, eventData, ExecuteEvents.pointerClickHandler);
    }

    private static PointerEventData CreatePointerEventData(GameObject gameObject, InteractionTarget target, Vector2 position, int pointerId)
    {
        var eventData = new PointerEventData(EnsureEventSystem())
        {
            button = PointerEventData.InputButton.Left,
            pointerId = pointerId,
            position = position,
            pressPosition = position,
            pointerEnter = gameObject,
            pointerPress = gameObject,
        };

        if (target.UiRaycast.HasValue)
        {
            eventData.pointerCurrentRaycast = target.UiRaycast.Value;
            eventData.pointerPressRaycast = target.UiRaycast.Value;
        }

        return eventData;
    }

    private static void ApplyRectTransform(RectTransform rectTransform, JObject arguments, Vector2 defaultSize)
    {
        rectTransform.anchorMin = ParseVector2(arguments["anchorMin"] as JArray, new Vector2(0.5f, 0.5f));
        rectTransform.anchorMax = ParseVector2(arguments["anchorMax"] as JArray, new Vector2(0.5f, 0.5f));
        rectTransform.pivot = ParseVector2(arguments["pivot"] as JArray, new Vector2(0.5f, 0.5f));
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

    private static Vector3 ParseVector3(JArray? values, Vector3 fallback)
    {
        if (values == null || values.Count < 2)
        {
            return fallback;
        }

        return new Vector3(
            values[0].Value<float>(),
            values[1].Value<float>(),
            values.Count > 2 ? values[2].Value<float>() : fallback.z);
    }

    private static Color ParseColor(string colorText, Color fallback)
    {
        Color color;
        return !string.IsNullOrEmpty(colorText) && ColorUtility.TryParseHtmlString(colorText, out color) ? color : fallback;
    }

    private static FontStyle ParseFontStyle(string value, FontStyle fallback)
    {
        return value switch
        {
            "Bold" => FontStyle.Bold,
            "Italic" => FontStyle.Italic,
            "BoldAndItalic" => FontStyle.BoldAndItalic,
            "Normal" => FontStyle.Normal,
            _ => fallback,
        };
    }

    private static TextAnchor ParseTextAnchor(string value, TextAnchor fallback)
    {
        return value switch
        {
            "UpperLeft" => TextAnchor.UpperLeft,
            "UpperCenter" => TextAnchor.UpperCenter,
            "UpperRight" => TextAnchor.UpperRight,
            "MiddleLeft" => TextAnchor.MiddleLeft,
            "MiddleCenter" => TextAnchor.MiddleCenter,
            "MiddleRight" => TextAnchor.MiddleRight,
            "LowerLeft" => TextAnchor.LowerLeft,
            "LowerCenter" => TextAnchor.LowerCenter,
            "LowerRight" => TextAnchor.LowerRight,
            _ => fallback,
        };
    }

    private static Shader ResolveShader(string? shaderName, bool allowFallback)
    {
        var candidateNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(shaderName))
        {
            candidateNames.Add(shaderName);
        }

        if (allowFallback)
        {
            candidateNames.Add("Standard");
            candidateNames.Add("Universal Render Pipeline/Lit");
            candidateNames.Add("Sprites/Default");
            candidateNames.Add("Unlit/Color");
        }

        foreach (var candidateName in candidateNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var shader = Shader.Find(candidateName);
            if (shader != null)
            {
                return shader;
            }
        }

        if (string.IsNullOrWhiteSpace(shaderName))
        {
            throw new InvalidOperationException("No compatible shader found.");
        }

        throw new InvalidOperationException($"Shader not found: {shaderName}");
    }

    private static void EnsureInputModule(GameObject eventSystemObject)
    {
        if (eventSystemObject.GetComponent<BaseInputModule>() != null)
        {
            return;
        }

        var inputSystemModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
            ?? Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem.ForUI");

        if (inputSystemModuleType != null && typeof(BaseInputModule).IsAssignableFrom(inputSystemModuleType))
        {
            eventSystemObject.AddComponent(inputSystemModuleType);
            return;
        }

        eventSystemObject.AddComponent<StandaloneInputModule>();
    }

    private static string? GetTextValue(GameObject gameObject)
    {
        if (gameObject.TryGetComponent<TMP_Text>(out var tmpText))
        {
            return tmpText.text;
        }

        var text = gameObject.GetComponent<Text>();
        return text != null ? text.text : null;
    }

    private static JObject? BuildInputFieldObject(GameObject gameObject)
    {
        if (gameObject.TryGetComponent<TMP_InputField>(out var tmpInputField))
        {
            return new JObject
            {
                ["text"] = tmpInputField.text,
                ["interactable"] = tmpInputField.interactable,
                ["lineType"] = tmpInputField.lineType.ToString(),
                ["isFocused"] = tmpInputField.isFocused,
                ["caretPosition"] = tmpInputField.caretPosition,
                ["selectionAnchorPosition"] = tmpInputField.selectionAnchorPosition,
                ["selectionFocusPosition"] = tmpInputField.selectionFocusPosition,
                ["textComponentType"] = nameof(TextMeshProUGUI),
            };
        }

        var inputField = gameObject.GetComponent<InputField>();
        if (inputField == null)
        {
            return null;
        }

        return new JObject
        {
            ["text"] = inputField.text,
            ["interactable"] = inputField.interactable,
            ["lineType"] = inputField.lineType.ToString(),
            ["isFocused"] = inputField.isFocused,
            ["caretPosition"] = inputField.caretPosition,
            ["selectionAnchorPosition"] = inputField.selectionAnchorPosition,
            ["selectionFocusPosition"] = inputField.selectionFocusPosition,
            ["textComponentType"] = nameof(Text),
        };
    }

    private static void SetInputFieldText(GameObject gameObject, string text)
    {
        if (gameObject.TryGetComponent<TMP_InputField>(out var tmpInputField))
        {
            tmpInputField.text = text;
            tmpInputField.MoveTextEnd(false);
            return;
        }

        var inputField = gameObject.GetComponent<InputField>() ?? throw new InvalidOperationException("InputField component was not found.");
        inputField.text = text;
        inputField.MoveTextEnd(false);
    }

    private static void ActivateInputField(GameObject gameObject)
    {
        if (gameObject.TryGetComponent<TMP_InputField>(out var tmpInputField))
        {
            tmpInputField.ActivateInputField();
            tmpInputField.MoveTextEnd(false);
            return;
        }

        if (gameObject.TryGetComponent<InputField>(out var inputField))
        {
            inputField.ActivateInputField();
            inputField.MoveTextEnd(false);
        }
    }

    private static void DeactivateInputField(GameObject gameObject)
    {
        if (gameObject.TryGetComponent<TMP_InputField>(out var tmpInputField))
        {
            tmpInputField.DeactivateInputField();
            return;
        }

        if (gameObject.TryGetComponent<InputField>(out var inputField))
        {
            inputField.DeactivateInputField();
        }
    }

    private static void ConfigureTmpText(TextMeshProUGUI text, string textValue, Color color, FontStyle fontStyle, TextAnchor alignment, float fontSize)
    {
        text.font = LoadDefaultTmpFontAsset();
        text.text = textValue;
        text.color = color;
        text.fontStyle = ToTmpFontStyle(fontStyle);
        text.alignment = ToTmpAlignment(alignment);
        text.fontSize = fontSize;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
    }

    private static TMP_FontAsset LoadDefaultTmpFontAsset()
    {
        EnsureTmpResourcesImported();

        var defaultFontAsset = TMP_Settings.GetFontAsset();
        if (defaultFontAsset != null)
        {
            return defaultFontAsset;
        }

        var knownFontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        if (knownFontAsset != null)
        {
            if (TMP_Settings.instance != null && TMP_Settings.defaultFontAsset == null)
            {
                TMP_Settings.defaultFontAsset = knownFontAsset;
                EditorUtility.SetDirty(TMP_Settings.instance);
                AssetDatabase.SaveAssetIfDirty(TMP_Settings.instance);
            }

            return knownFontAsset;
        }

        var fontGuids = AssetDatabase.FindAssets("t:TMP_FontAsset LiberationSans");
        if (fontGuids.Length > 0)
        {
            var fontPath = AssetDatabase.GUIDToAssetPath(fontGuids[0]);
            var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath);
            if (fontAsset != null)
            {
                if (TMP_Settings.instance != null && TMP_Settings.defaultFontAsset == null)
                {
                    TMP_Settings.defaultFontAsset = fontAsset;
                    EditorUtility.SetDirty(TMP_Settings.instance);
                    AssetDatabase.SaveAssetIfDirty(TMP_Settings.instance);
                }

                return fontAsset;
            }
        }

        throw new InvalidOperationException("TMP default font asset could not be loaded.");
    }

    private static void EnsureTmpResourcesImported()
    {
        if (TryLoadTmpResources())
        {
            return;
        }

        TMP_PackageResourceImporter.ImportResources(importEssentials: true, importExamples: false, interactive: false);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (TryLoadTmpResources())
            {
                return;
            }

            Thread.Sleep(100);
        }
    }

    private static bool TryLoadTmpResources()
    {
        var settings = TMP_Settings.LoadDefaultSettings()
            ?? AssetDatabase.LoadAssetAtPath<TMP_Settings>("Assets/TextMesh Pro/Resources/TMP Settings.asset");

        var fontAsset = TMP_Settings.GetFontAsset()
            ?? AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        if (settings == null || fontAsset == null)
        {
            return false;
        }

        if (TMP_Settings.defaultFontAsset == null)
        {
            TMP_Settings.defaultFontAsset = fontAsset;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        return true;
    }

    private static FontStyles ToTmpFontStyle(FontStyle fontStyle)
    {
        return fontStyle switch
        {
            FontStyle.Bold => FontStyles.Bold,
            FontStyle.Italic => FontStyles.Italic,
            FontStyle.BoldAndItalic => FontStyles.Bold | FontStyles.Italic,
            _ => FontStyles.Normal,
        };
    }

    private static TextAlignmentOptions ToTmpAlignment(TextAnchor alignment)
    {
        return alignment switch
        {
            TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
            TextAnchor.UpperCenter => TextAlignmentOptions.Top,
            TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
            TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
            TextAnchor.MiddleCenter => TextAlignmentOptions.Center,
            TextAnchor.MiddleRight => TextAlignmentOptions.Right,
            TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
            TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
            TextAnchor.LowerRight => TextAlignmentOptions.BottomRight,
            _ => TextAlignmentOptions.Center,
        };
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
        if (_nextStartAttemptAt >= 0d && EditorApplication.timeSinceStartup >= _nextStartAttemptAt)
        {
            _nextStartAttemptAt = -1d;
            Start();
        }

        RecoverPendingCompilation();
        RecoverActivePlayModeTestRun();
        RecoverCompletedTestRuns();

        while (MainThreadActions.TryDequeue(out var action))
        {
            action();
        }
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
    {
        string message = stateChange switch
        {
            PlayModeStateChange.ExitingEditMode => "Play mode transition: exiting edit mode.",
            PlayModeStateChange.EnteredPlayMode => "Play mode entered.",
            PlayModeStateChange.ExitingPlayMode => "Play mode transition: exiting play mode.",
            PlayModeStateChange.EnteredEditMode => "Play mode exited.",
            _ => "Play mode state changed.",
        };

        Emit("editor.play_mode_changed", message, new JObject
        {
            ["stateChange"] = stateChange.ToString(),
            ["isPlaying"] = EditorApplication.isPlaying,
            ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
            ["isPaused"] = EditorApplication.isPaused,
        });
    }

    private static void OnLogReceived(string condition, string stackTrace, LogType type)
    {
        Emit("console.log", condition, new JObject { ["level"] = type.ToString(), ["stackTrace"] = stackTrace });
    }

    private const int MaxRetainedEvents = 5000;

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

            // Bounded ring buffer: cursor stays monotonic so /events?after= remains correct;
            // the /events 'floor' field lets a client detect it requested a pruned range.
            if (Events.Count > MaxRetainedEvents)
            {
                Events.RemoveRange(0, Events.Count - MaxRetainedEvents);
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonConvert.SerializeObject(payload, JsonSettings);
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
        return new { success = true, message, code = (string?)null, result, events = Array.Empty<object>() };
    }

    private static object Failure(string message) => Failure(message, ErrorCodes.Internal);

    /// <summary>실패 응답 봉투를 안정적 오류 코드와 함께 생성한다.</summary>
    /// <param name="message">사람이 읽을 수 있는 실패 메시지.</param>
    /// <param name="code">안정적 오류 코드(<see cref="ErrorCodes"/>).</param>
    /// <returns>{ success=false, message, code, result, events } 형태의 응답 봉투.</returns>
    private static object Failure(string message, string code)
    {
        return new { success = false, message, code, result = (object?)null, events = Array.Empty<object>() };
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
        File.WriteAllText(GetPendingCompilationPath(), JsonConvert.SerializeObject(state, JsonSettings));
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
        File.WriteAllText(GetPendingTestRunsPath(), JsonConvert.SerializeObject(pendingRuns, JsonSettings));
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

    /// <summary>현재 에디터 인스턴스를 instances.json 에 병합 업서트한다(덮어쓰지 않음). project:port 키로 항목을 갱신하고 default 별칭이 가장 최근 시작 인스턴스를 가리키게 한다.</summary>
    /// <param name="host">브리지 호스트.</param>
    /// <param name="port">브리지 포트.</param>
    private static void RegisterInstance(string host, int port)
    {
        try
        {
            var file = GetInstancesFilePath();
            lock (InstancesFileGate)
            {
                var root = ReadInstancesRoot(file);
                var baseUrl = $"http://{host}:{port}";
                var projectPath = Directory.GetCurrentDirectory();
                var key = $"{Path.GetFileName(projectPath.TrimEnd('/', '\\'))}:{port}";
                var now = DateTimeOffset.UtcNow.ToString("O");
                var entry = new JObject
                {
                    ["baseUrl"] = baseUrl,
                    ["projectPath"] = projectPath,
                    ["port"] = port,
                    ["unityVersion"] = Application.unityVersion,
                    ["sessionId"] = SessionId,
                    ["updatedAt"] = now,
                    ["alive"] = true,
                };
                root[key] = entry;
                root["default"] = (JObject)entry.DeepClone();
                _registeredInstanceKey = key;
                File.WriteAllText(file, JsonConvert.SerializeObject(root, JsonSettings));
            }
        }
        catch
        {
        }
    }

    /// <summary>~/.unity-cli/instances.json 절대 경로를 돌려주고 상위 디렉터리를 보장한다.</summary>
    /// <returns>instances.json 의 절대 경로.</returns>
    private static string GetInstancesFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = Path.Combine(home, ".unity-cli");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "instances.json");
    }

    /// <summary>instances.json 을 JObject 로 읽어온다. 없거나 깨졌으면 빈 JObject.</summary>
    /// <param name="file">instances.json 경로.</param>
    /// <returns>파싱된 루트 JObject 또는 빈 JObject.</returns>
    private static JObject ReadInstancesRoot(string file)
    {
        try { return File.Exists(file) ? (JObject)JToken.Parse(File.ReadAllText(file)) : new JObject(); }
        catch { return new JObject(); }
    }

    /// <summary>종료/도메인 리로드 시 등록된 인스턴스의 alive 를 false 로 표시한다(베스트 에포트).</summary>
    private static void MarkRegisteredInstanceDead()
    {
        if (string.IsNullOrEmpty(_registeredInstanceKey)) return;
        try
        {
            var file = GetInstancesFilePath();
            lock (InstancesFileGate)
            {
                var root = ReadInstancesRoot(file);
                if (root[_registeredInstanceKey] is JObject entry)
                {
                    entry["alive"] = false;
                    entry["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
                    if (root["default"] is JObject def
                        && string.Equals((string?)def["sessionId"], SessionId, StringComparison.Ordinal))
                    {
                        def["alive"] = false;
                    }
                    File.WriteAllText(file, JsonConvert.SerializeObject(root, JsonSettings));
                }
            }
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

    /// <summary>브리지 도구 실행 중 발생하는 계약 오류. <see cref="Code"/> 는 안정적 식별자, <see cref="Status"/> 는 매핑할 HTTP 상태 코드이다.</summary>
    private sealed class BridgeException : Exception
    {
        /// <summary>지정한 오류 코드, HTTP 상태, 메시지로 계약 오류를 생성한다.</summary>
        /// <param name="code">안정적 오류 코드.</param>
        /// <param name="status">매핑할 HTTP 상태 코드.</param>
        /// <param name="message">사람이 읽을 수 있는 오류 메시지.</param>
        public BridgeException(string code, int status, string message) : base(message)
        {
            Code = code;
            Status = status;
        }

        /// <summary>안정적 오류 코드(not_found, missing_arg, bad_arg 등).</summary>
        public string Code { get; }

        /// <summary>이 오류에 매핑할 HTTP 상태 코드.</summary>
        public int Status { get; }
    }

    /// <summary>브리지 도구 응답에 사용하는 안정적 오류 코드 상수.</summary>
    private static class ErrorCodes
    {
        /// <summary>대상을 찾지 못함(HTTP 404).</summary>
        public const string NotFound = "not_found";

        /// <summary>필수 인자 누락(HTTP 400).</summary>
        public const string MissingArg = "missing_arg";

        /// <summary>인자 값이 잘못됨(HTTP 400).</summary>
        public const string BadArg = "bad_arg";

        /// <summary>존재하지 않는 도구(HTTP 200, success=false).</summary>
        public const string UnknownTool = "unknown_tool";

        /// <summary>예기치 못한 내부 오류(HTTP 500).</summary>
        public const string Internal = "internal_error";
    }

    /// <summary>not_found(404) 계약 오류를 생성한다.</summary>
    /// <param name="message">오류 메시지.</param>
    /// <returns>생성한 <see cref="BridgeException"/>.</returns>
    private static BridgeException NotFound(string message) => new BridgeException(ErrorCodes.NotFound, 404, message);

    /// <summary>missing_arg(400) 계약 오류를 생성한다.</summary>
    /// <param name="message">오류 메시지.</param>
    /// <returns>생성한 <see cref="BridgeException"/>.</returns>
    private static BridgeException MissingArg(string message) => new BridgeException(ErrorCodes.MissingArg, 400, message);

    /// <summary>bad_arg(400) 계약 오류를 생성한다.</summary>
    /// <param name="message">오류 메시지.</param>
    /// <returns>생성한 <see cref="BridgeException"/>.</returns>
    private static BridgeException BadArg(string message) => new BridgeException(ErrorCodes.BadArg, 400, message);

    private readonly struct InteractionTarget
    {
        public InteractionTarget(GameObject gameObject, RaycastResult? uiRaycast)
        {
            GameObject = gameObject;
            UiRaycast = uiRaycast;
        }

        public GameObject GameObject { get; }
        public RaycastResult? UiRaycast { get; }
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
