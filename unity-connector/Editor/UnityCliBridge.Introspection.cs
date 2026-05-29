#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS8600, CS8602, CS8603, CS8604, CS8625
using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliBridge
{
    // Component introspection (list/get/add/remove) and GameObject query/mutation tools.
    // All methods run on the Unity main thread (invoked via OnMainThreadAsync from ExecuteToolAsync).
    public static partial class UnityCliBridgeServer
    {
        // --- component.list ------------------------------------------------------
        private static object ListComponents(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var includeValues = arguments["includeValues"]?.Value<bool?>() ?? false;
            var components = gameObject.GetComponents<Component>();
            var list = new JArray();

            foreach (var component in components)
            {
                if (component == null)
                {
                    // Missing script reference: surface it instead of hiding it.
                    list.Add(new JObject { ["type"] = "<missing>", ["enabled"] = JValue.CreateNull() });
                    continue;
                }

                var componentType = component.GetType();
                var entry = new JObject
                {
                    ["type"] = componentType.Name,
                    ["fullType"] = componentType.FullName,
                    ["instanceId"] = component.GetInstanceID(),
                    ["enabled"] = ComponentEnabledState(component),
                };

                if (includeValues)
                {
                    entry["properties"] = ReadComponentSerializedProperties(component);
                }

                list.Add(entry);
            }

            return Success(new JObject
            {
                ["id"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["count"] = components.Length,
                ["components"] = list,
            }, "Components listed.");
        }

        // --- component.get -------------------------------------------------------
        private static object GetComponentProperties(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var typeName = arguments.Value<string>("type") ?? throw MissingArg("type is required.");
            var componentType = ResolveComponentType(typeName);
            if (componentType == null)
            {
                return Failure($"Component type not found: {typeName}", ErrorCodes.NotFound);
            }

            var component = gameObject.GetComponent(componentType);
            if (component == null)
            {
                return Failure($"Component '{componentType.Name}' not found on '{gameObject.name}'.", ErrorCodes.NotFound);
            }

            return Success(new JObject
            {
                ["id"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["type"] = componentType.Name,
                ["fullType"] = componentType.FullName,
                ["properties"] = ReadComponentSerializedProperties(component),
            }, "Component read.");
        }

        // --- component.add -------------------------------------------------------
        private static object AddComponentTool(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var typeName = arguments.Value<string>("type") ?? throw MissingArg("type is required.");
            var componentType = ResolveComponentType(typeName);
            if (componentType == null)
            {
                return Failure($"Component type not found: {typeName}", ErrorCodes.NotFound);
            }

            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                return Failure($"Type is not a Component: {typeName}");
            }

            var allowDuplicate = arguments["allowDuplicate"]?.Value<bool?>() ?? false;
            if (!allowDuplicate && gameObject.GetComponent(componentType) != null)
            {
                return Failure($"Component already exists: {componentType.Name}. Pass allowDuplicate=true to add another.");
            }

            var component = gameObject.AddComponent(componentType);
            if (component == null)
            {
                return Failure($"Failed to add component: {typeName}");
            }

            var applied = new JArray();
            var skipped = new JArray();
            if (arguments["values"] is JObject values)
            {
                ApplyValuesToComponent(component, componentType, values, applied, skipped);
            }

            Emit(EventTypes.ComponentChanged, $"Component added: {gameObject.name}/{componentType.Name}",
                new JObject { ["id"] = gameObject.GetInstanceID(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["type"] = componentType.Name,
                ["applied"] = applied,
                ["skipped"] = skipped,
            }, "Component added.");
        }

        // --- component.update ----------------------------------------------------
        /// <summary>지정한 GameObject 의 컴포넌트를 가져오거나 없으면 추가한 뒤, 일반 프로퍼티와 [SerializeField] private 직렬화 필드까지 값을 기록하고 적용/건너뜀 목록을 돌려준다.</summary>
        /// <param name="arguments">id/name 타깃, type(기본 Transform), values(member=value JObject)</param>
        /// <returns>{ id, name, type, applied:[], skipped:[{name,reason}] } 또는 실패 시 <see cref="Failure"/></returns>
        private static object UpdateComponentTool(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var typeName = arguments.Value<string>("type") ?? "Transform";
            var componentType = ResolveComponentType(typeName);
            if (componentType == null)
            {
                return Failure($"Component type not found: {typeName}", ErrorCodes.NotFound);
            }

            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                return Failure($"Type is not a Component: {typeName}");
            }

            var component = gameObject.GetComponent(componentType) ?? gameObject.AddComponent(componentType);
            if (component == null)
            {
                return Failure($"Failed to add component: {typeName}");
            }

            var applied = new JArray();
            var skipped = new JArray();
            if (arguments["values"] is JObject values)
            {
                ApplyValuesToComponent(component, componentType, values, applied, skipped);
            }

            Emit(EventTypes.ComponentChanged, $"Component updated: {gameObject.name}/{componentType.Name}",
                new JObject { ["id"] = gameObject.GetInstanceID(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["type"] = componentType.Name,
                ["applied"] = applied,
                ["skipped"] = skipped,
            }, "Component updated.");
        }

        // --- component.remove ----------------------------------------------------
        private static object RemoveComponentTool(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var typeName = arguments.Value<string>("type") ?? throw MissingArg("type is required.");
            var componentType = ResolveComponentType(typeName);
            if (componentType == null)
            {
                return Failure($"Component type not found: {typeName}", ErrorCodes.NotFound);
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                return Failure("Transform/RectTransform cannot be removed.");
            }

            var matching = gameObject.GetComponents(componentType);
            if (matching == null || matching.Length == 0)
            {
                return Failure($"Component '{componentType.Name}' not found on '{gameObject.name}'.", ErrorCodes.NotFound);
            }

            var index = arguments["index"]?.Value<int?>() ?? 0;
            if (index < 0 || index >= matching.Length)
            {
                return Failure($"Index {index} out of range (0..{matching.Length - 1}).");
            }

            UnityEngine.Object.DestroyImmediate(matching[index]);
            Emit(EventTypes.ComponentChanged, $"Component removed: {gameObject.name}/{componentType.Name}",
                new JObject { ["id"] = gameObject.GetInstanceID(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetInstanceID(),
                ["name"] = gameObject.name,
                ["removed"] = true,
                ["type"] = componentType.Name,
                ["index"] = index,
            }, "Component removed.");
        }

        // --- gameobject.find -----------------------------------------------------
        private static object FindGameObjects(JObject arguments)
        {
            var tag = arguments.Value<string>("tag");
            var componentName = arguments.Value<string>("component");
            var nameContains = arguments.Value<string>("nameContains");
            var path = arguments.Value<string>("path");
            var activeOnly = arguments["activeOnly"]?.Value<bool?>() ?? false;
            var includeInactive = arguments["includeInactive"]?.Value<bool?>() ?? true;
            var limit = arguments["limit"]?.Value<int?>() ?? 200;

            if (!string.IsNullOrEmpty(tag) && !InternalEditorUtility.tags.Contains(tag))
            {
                return Failure($"Unknown tag: {tag}");
            }

            int? layer = null;
            var layerToken = arguments["layer"];
            if (layerToken != null && layerToken.Type != JTokenType.Null)
            {
                if (layerToken.Type == JTokenType.Integer)
                {
                    layer = layerToken.Value<int>();
                }
                else
                {
                    var layerName = layerToken.Value<string>();
                    var resolved = LayerMask.NameToLayer(layerName);
                    if (resolved < 0)
                    {
                        return Failure($"Unknown layer: {layerName}");
                    }

                    layer = resolved;
                }
            }

            Type componentType = null;
            if (!string.IsNullOrEmpty(componentName))
            {
                componentType = ResolveComponentType(componentName);
                if (componentType == null)
                {
                    return Failure($"Component type not found: {componentName}");
                }
            }

            var items = new JArray();
            var matched = 0;
            var truncated = false;

            foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject.hideFlags != HideFlags.None || !gameObject.scene.IsValid())
                {
                    continue;
                }

                if (activeOnly && !gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!includeInactive && !gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(tag) && !gameObject.CompareTag(tag))
                {
                    continue;
                }

                if (layer.HasValue && gameObject.layer != layer.Value)
                {
                    continue;
                }

                if (componentType != null && gameObject.GetComponent(componentType) == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(nameContains) && gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(path) && !MatchHierarchyPath(gameObject, path))
                {
                    continue;
                }

                matched++;
                if (items.Count < limit)
                {
                    items.Add(GameObjectObject(gameObject));
                }
                else
                {
                    truncated = true;
                }
            }

            return Success(new JObject
            {
                ["count"] = matched,
                ["truncated"] = truncated,
                ["items"] = items,
            }, $"Found {matched} GameObject(s).");
        }

        // --- gameobject.set-properties ------------------------------------------
        private static object SetGameObjectProperties(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var applied = new JArray();
            var changed = false;

            var active = arguments["active"]?.Value<bool?>();
            if (active.HasValue)
            {
                gameObject.SetActive(active.Value);
                applied.Add("active");
                changed = true;
            }

            var tag = arguments.Value<string>("tag");
            if (!string.IsNullOrEmpty(tag))
            {
                if (!InternalEditorUtility.tags.Contains(tag))
                {
                    return Failure($"Unknown tag: {tag}. Define it in Tags & Layers first.");
                }

                gameObject.tag = tag;
                applied.Add("tag");
                changed = true;
            }

            var layerToken = arguments["layer"];
            if (layerToken != null && layerToken.Type != JTokenType.Null)
            {
                int layer;
                if (layerToken.Type == JTokenType.Integer)
                {
                    layer = layerToken.Value<int>();
                }
                else
                {
                    layer = LayerMask.NameToLayer(layerToken.Value<string>());
                    if (layer < 0)
                    {
                        return Failure($"Unknown layer: {layerToken.Value<string>()}");
                    }
                }

                var recursive = arguments["recursiveLayer"]?.Value<bool?>() ?? false;
                if (recursive)
                {
                    foreach (var transform in gameObject.GetComponentsInChildren<Transform>(true))
                    {
                        transform.gameObject.layer = layer;
                    }
                }
                else
                {
                    gameObject.layer = layer;
                }

                applied.Add("layer");
                changed = true;
            }

            var isStatic = arguments["static"]?.Value<bool?>();
            if (isStatic.HasValue)
            {
                gameObject.isStatic = isStatic.Value;
                applied.Add("static");
                changed = true;
            }

            var newName = arguments.Value<string>("newName");
            if (!string.IsNullOrEmpty(newName))
            {
                gameObject.name = newName;
                applied.Add("newName");
                changed = true;
            }

            if (changed)
            {
                Emit(EventTypes.HierarchyChanged, $"GameObject updated: {gameObject.name}",
                    new JObject { ["id"] = gameObject.GetInstanceID() });
            }

            return Success(new JObject
            {
                ["applied"] = applied,
                ["gameObject"] = GameObjectObject(gameObject),
            }, "GameObject properties set.");
        }

        // --- shared helpers ------------------------------------------------------

        private static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var direct = Type.GetType(typeName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var byFullName = assembly.GetType(typeName);
                    if (byFullName != null)
                    {
                        return byFullName;
                    }
                }
                catch
                {
                    // ignore assemblies that refuse type resolution
                }
            }

            // Short-name fallback (e.g. "Rigidbody2D" or a project MonoBehaviour without namespace).
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                var match = types.FirstOrDefault(type => type.Name == typeName && typeof(Component).IsAssignableFrom(type));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static JToken ComponentEnabledState(Component component)
        {
            // Note: Collider2D derives from Behaviour, so it is covered by the Behaviour arm.
            return component switch
            {
                Behaviour behaviour => new JValue(behaviour.enabled),
                Renderer renderer => new JValue(renderer.enabled),
                Collider collider => new JValue(collider.enabled),
                _ => JValue.CreateNull(),
            };
        }

        private static JObject ReadComponentSerializedProperties(Component component)
        {
            var result = new JObject();
            try
            {
                using var serializedObject = new SerializedObject(component);
                var iterator = serializedObject.GetIterator();
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        result[iterator.propertyPath] = ReadSerializedProperty(iterator);
                    }
                    while (iterator.NextVisible(false));
                }
            }
            catch (Exception exception)
            {
                result["__error"] = exception.Message;
            }

            return result;
        }

        private static JToken ReadSerializedProperty(SerializedProperty property)
        {
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return new JValue(property.longValue);
                    case SerializedPropertyType.Boolean:
                        return new JValue(property.boolValue);
                    case SerializedPropertyType.Float:
                        return new JValue(property.doubleValue);
                    case SerializedPropertyType.String:
                        return new JValue(property.stringValue ?? string.Empty);
                    case SerializedPropertyType.LayerMask:
                        return new JValue(property.intValue);
                    case SerializedPropertyType.Enum:
                        var index = property.enumValueIndex;
                        var enumName = property.enumNames != null && index >= 0 && index < property.enumNames.Length
                            ? property.enumNames[index]
                            : null;
                        return new JObject { ["enumValue"] = index, ["enumName"] = enumName };
                    case SerializedPropertyType.Vector2:
                        return new JArray(property.vector2Value.x, property.vector2Value.y);
                    case SerializedPropertyType.Vector3:
                        return new JArray(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                    case SerializedPropertyType.Vector4:
                        return new JArray(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                    case SerializedPropertyType.Quaternion:
                        return new JArray(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                    case SerializedPropertyType.Rect:
                        return new JArray(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                    case SerializedPropertyType.Bounds:
                        return new JObject
                        {
                            ["center"] = new JArray(property.boundsValue.center.x, property.boundsValue.center.y, property.boundsValue.center.z),
                            ["size"] = new JArray(property.boundsValue.size.x, property.boundsValue.size.y, property.boundsValue.size.z),
                        };
                    case SerializedPropertyType.Color:
                        return new JValue("#" + ColorUtility.ToHtmlStringRGBA(property.colorValue));
                    case SerializedPropertyType.ObjectReference:
                        var reference = property.objectReferenceValue;
                        if (reference == null)
                        {
                            return JValue.CreateNull();
                        }

                        return new JObject
                        {
                            ["objectName"] = reference.name,
                            ["objectType"] = reference.GetType().Name,
                            ["assetPath"] = AssetDatabase.GetAssetPath(reference),
                            ["instanceId"] = reference.GetInstanceID(),
                        };
                    default:
                        if (property.isArray && property.propertyType != SerializedPropertyType.String)
                        {
                            return new JObject { ["isArray"] = true, ["length"] = property.arraySize };
                        }

                        return new JObject { ["unsupported"] = true, ["propertyType"] = property.propertyType.ToString() };
                }
            }
            catch (Exception exception)
            {
                return new JObject { ["error"] = exception.Message };
            }
        }

        private static void ApplyValuesToComponent(Component component, Type componentType, JObject values, JArray applied, JArray skipped)
        {
            SerializedObject serializedObject = null;
            try
            {
                serializedObject = new SerializedObject(component);
            }
            catch
            {
                serializedObject = null;
            }

            foreach (var pair in values)
            {
                var done = false;

                if (serializedObject != null)
                {
                    var property = serializedObject.FindProperty(pair.Key);
                    if (property != null)
                    {
                        if (TryWriteSerializedProperty(property, pair.Value, out var reason))
                        {
                            done = true;
                        }
                        else
                        {
                            skipped.Add(new JObject { ["name"] = pair.Key, ["reason"] = reason });
                            continue;
                        }
                    }
                }

                if (!done)
                {
                    var property = componentType.GetProperty(pair.Key);
                    if (property != null && property.CanWrite)
                    {
                        try
                        {
                            property.SetValue(component, pair.Value?.ToObject(property.PropertyType));
                            done = true;
                        }
                        catch (Exception exception)
                        {
                            skipped.Add(new JObject { ["name"] = pair.Key, ["reason"] = "convert_failed: " + exception.Message });
                            continue;
                        }
                    }
                }

                if (done)
                {
                    applied.Add(pair.Key);
                }
                else
                {
                    skipped.Add(new JObject { ["name"] = pair.Key, ["reason"] = "unknown_member" });
                }
            }

            if (serializedObject != null)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                serializedObject.Dispose();
            }
        }

        private static bool TryWriteSerializedProperty(SerializedProperty property, JToken token, out string reason)
        {
            reason = string.Empty;
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.longValue = token?.Value<long>() ?? 0L;
                        return true;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = token?.Value<bool>() ?? false;
                        return true;
                    case SerializedPropertyType.Float:
                        property.doubleValue = token?.Value<double>() ?? 0d;
                        return true;
                    case SerializedPropertyType.String:
                        property.stringValue = token?.Value<string>() ?? string.Empty;
                        return true;
                    case SerializedPropertyType.LayerMask:
                        property.intValue = token?.Value<int>() ?? 0;
                        return true;
                    case SerializedPropertyType.Enum:
                        if (token != null && token.Type == JTokenType.String)
                        {
                            var name = token.Value<string>();
                            var enumIndex = property.enumNames != null ? Array.IndexOf(property.enumNames, name) : -1;
                            if (enumIndex < 0)
                            {
                                reason = $"unknown_enum_value: {name}";
                                return false;
                            }

                            property.enumValueIndex = enumIndex;
                        }
                        else
                        {
                            property.enumValueIndex = token?.Value<int>() ?? 0;
                        }

                        return true;
                    case SerializedPropertyType.Vector2:
                    {
                        if (token is JArray array && array.Count >= 2)
                        {
                            property.vector2Value = new Vector2(array[0].Value<float>(), array[1].Value<float>());
                            return true;
                        }

                        reason = "expected [x,y]";
                        return false;
                    }
                    case SerializedPropertyType.Vector3:
                    {
                        if (token is JArray array && array.Count >= 3)
                        {
                            property.vector3Value = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
                            return true;
                        }

                        reason = "expected [x,y,z]";
                        return false;
                    }
                    case SerializedPropertyType.Color:
                        property.colorValue = ParseColor(token?.Value<string>() ?? string.Empty, Color.white);
                        return true;
                    case SerializedPropertyType.ObjectReference:
                    {
                        var assetPath = token?.Value<string>();
                        if (string.IsNullOrEmpty(assetPath))
                        {
                            property.objectReferenceValue = null;
                            return true;
                        }

                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (asset == null)
                        {
                            reason = $"asset_not_found: {assetPath}";
                            return false;
                        }

                        property.objectReferenceValue = asset;
                        return true;
                    }
                    default:
                        reason = "unsupported_write: " + property.propertyType;
                        return false;
                }
            }
            catch (Exception exception)
            {
                reason = "write_failed: " + exception.Message;
                return false;
            }
        }

        private static bool MatchHierarchyPath(GameObject gameObject, string pattern)
        {
            var patternSegments = pattern.Trim('/').Split('/');
            var actualSegments = GetHierarchyPath(gameObject).Split('/');
            if (patternSegments.Length > actualSegments.Length)
            {
                return false;
            }

            var offset = actualSegments.Length - patternSegments.Length;
            for (var i = 0; i < patternSegments.Length; i++)
            {
                if (patternSegments[i] == "*")
                {
                    continue;
                }

                if (!string.Equals(patternSegments[i], actualSegments[offset + i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetHierarchyPath(GameObject gameObject)
        {
            var builder = new StringBuilder(gameObject.name);
            var parent = gameObject.transform.parent;
            while (parent != null)
            {
                builder.Insert(0, parent.name + "/");
                parent = parent.parent;
            }

            return builder.ToString();
        }
    }
}
#endif
