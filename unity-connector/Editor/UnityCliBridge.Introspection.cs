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
                    ["instanceId"] = component.GetStableId(),
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
                ["id"] = gameObject.GetStableId(),
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
                ["id"] = gameObject.GetStableId(),
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
                new JObject { ["id"] = gameObject.GetStableId(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetStableId(),
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
                new JObject { ["id"] = gameObject.GetStableId(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetStableId(),
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
                new JObject { ["id"] = gameObject.GetStableId(), ["type"] = componentType.Name });

            return Success(new JObject
            {
                ["id"] = gameObject.GetStableId(),
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
                    new JObject { ["id"] = gameObject.GetStableId() });
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
                // 배열/리스트: 원소 값 배열로 되읽는다(합리적 상한 적용). 문자열은 배열로 취급하지 않는다.
                if (property.isArray && property.propertyType != SerializedPropertyType.String)
                {
                    const int maxElements = 64;
                    var elements = new JArray();
                    var count = Math.Min(property.arraySize, maxElements);
                    for (var i = 0; i < count; i++)
                    {
                        elements.Add(ReadSerializedProperty(property.GetArrayElementAtIndex(i)));
                    }

                    return elements;
                }

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
                    case SerializedPropertyType.Vector2Int:
                        return new JArray(property.vector2IntValue.x, property.vector2IntValue.y);
                    case SerializedPropertyType.Vector3Int:
                        return new JArray(property.vector3IntValue.x, property.vector3IntValue.y, property.vector3IntValue.z);
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
                            ["instanceId"] = reference.GetStableId(),
                        };
                    case SerializedPropertyType.Generic:
                        return ReadGenericProperty(property);
                    default:
                        return new JObject { ["unsupported"] = true, ["propertyType"] = property.propertyType.ToString() };
                }
            }
            catch (Exception exception)
            {
                return new JObject { ["error"] = exception.Message };
            }
        }

        /// <summary>중첩 직렬화 struct/class(Generic) 프로퍼티를 자식 필드 이름 -> 값 <see cref="JObject"/> 로 되읽는다.</summary>
        /// <param name="property">Generic 타입 <see cref="SerializedProperty"/></param>
        /// <returns>자식 필드를 담은 <see cref="JObject"/></returns>
        private static JObject ReadGenericProperty(SerializedProperty property)
        {
            var result = new JObject();
            var iterator = property.Copy();
            var end = property.GetEndProperty();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                result[iterator.name] = ReadSerializedProperty(iterator);
                enterChildren = false;
            }

            return result;
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

        /// <summary>JSON 토큰을 <see cref="SerializedProperty"/> 에 재귀적으로 기록한다. 배열/리스트, ObjectReference(에셋/씬 참조/null), 중첩 struct, 그리고 스칼라/벡터/색상 타입을 지원한다.</summary>
        /// <param name="property">기록 대상 프로퍼티</param>
        /// <param name="token">기록할 JSON 값</param>
        /// <param name="reason">실패 사유(성공 시 빈 문자열)</param>
        /// <returns>기록 성공 여부</returns>
        private static bool TryWriteSerializedProperty(SerializedProperty property, JToken token, out string reason)
        {
            reason = string.Empty;
            try
            {
                // 1) 배열/리스트: 문자열이 아닌 isArray 프로퍼티는 JArray 원소를 재귀 기록한다.
                if (property.isArray && property.propertyType != SerializedPropertyType.String)
                {
                    if (token is JArray array)
                    {
                        property.arraySize = array.Count;
                        for (var i = 0; i < array.Count; i++)
                        {
                            var element = property.GetArrayElementAtIndex(i);
                            if (!TryWriteSerializedProperty(element, array[i], out var elementReason))
                            {
                                reason = $"array_element[{i}]: {elementReason}";
                                return false;
                            }
                        }

                        return true;
                    }

                    reason = "expected_array";
                    return false;
                }

                // 2) ObjectReference: 에셋 경로/씬 참조/null 을 처리한다.
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    return TryWriteObjectReference(property, token, out reason);
                }

                // 3) 중첩 직렬화 struct/class: ref-spec 이 아닌 JObject 는 자식 필드를 재귀 기록한다.
                if (property.propertyType == SerializedPropertyType.Generic && token is JObject nested && !IsObjectReferenceSpec(nested))
                {
                    var failures = new System.Collections.Generic.List<string>();
                    foreach (var child in nested)
                    {
                        var childProperty = property.FindPropertyRelative(child.Key);
                        if (childProperty == null)
                        {
                            failures.Add($"missing_child: {child.Key}");
                            continue;
                        }

                        if (!TryWriteSerializedProperty(childProperty, child.Value, out var childReason))
                        {
                            failures.Add($"{child.Key}: {childReason}");
                        }
                    }

                    if (failures.Count > 0)
                    {
                        reason = string.Join("; ", failures);
                        return false;
                    }

                    return true;
                }

                // 4) 스칼라/벡터/색상 타입.
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
                    case SerializedPropertyType.Vector4:
                    {
                        if (token is JArray array && array.Count >= 4)
                        {
                            property.vector4Value = new Vector4(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), array[3].Value<float>());
                            return true;
                        }

                        reason = "expected [x,y,z,w]";
                        return false;
                    }
                    case SerializedPropertyType.Vector2Int:
                    {
                        if (token is JArray array && array.Count >= 2)
                        {
                            property.vector2IntValue = new Vector2Int(array[0].Value<int>(), array[1].Value<int>());
                            return true;
                        }

                        reason = "expected [x,y] (int)";
                        return false;
                    }
                    case SerializedPropertyType.Vector3Int:
                    {
                        if (token is JArray array && array.Count >= 3)
                        {
                            property.vector3IntValue = new Vector3Int(array[0].Value<int>(), array[1].Value<int>(), array[2].Value<int>());
                            return true;
                        }

                        reason = "expected [x,y,z] (int)";
                        return false;
                    }
                    case SerializedPropertyType.Quaternion:
                    {
                        if (token is JArray array && array.Count >= 4)
                        {
                            property.quaternionValue = new Quaternion(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), array[3].Value<float>());
                            return true;
                        }

                        if (token is JArray euler && euler.Count >= 3)
                        {
                            property.quaternionValue = Quaternion.Euler(euler[0].Value<float>(), euler[1].Value<float>(), euler[2].Value<float>());
                            return true;
                        }

                        reason = "expected [x,y,z,w] or [euler x,y,z]";
                        return false;
                    }
                    case SerializedPropertyType.Rect:
                    {
                        if (token is JArray array && array.Count >= 4)
                        {
                            property.rectValue = new Rect(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), array[3].Value<float>());
                            return true;
                        }

                        reason = "expected [x,y,width,height]";
                        return false;
                    }
                    case SerializedPropertyType.Bounds:
                        return TryWriteBounds(property, token, out reason);
                    case SerializedPropertyType.Color:
                        property.colorValue = ParseColor(token?.Value<string>() ?? string.Empty, Color.white);
                        return true;
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

        /// <summary>Bounds 프로퍼티에 { center:[x,y,z], size:[x,y,z] } JObject 또는 [cx,cy,cz,sx,sy,sz] JArray 를 기록한다.</summary>
        /// <param name="property">Bounds 타입 프로퍼티</param>
        /// <param name="token">center/size 를 담은 JSON 값</param>
        /// <param name="reason">실패 사유</param>
        /// <returns>기록 성공 여부</returns>
        private static bool TryWriteBounds(SerializedProperty property, JToken token, out string reason)
        {
            reason = string.Empty;
            if (token is JObject obj && obj["center"] is JArray center && obj["size"] is JArray size && center.Count >= 3 && size.Count >= 3)
            {
                property.boundsValue = new Bounds(
                    new Vector3(center[0].Value<float>(), center[1].Value<float>(), center[2].Value<float>()),
                    new Vector3(size[0].Value<float>(), size[1].Value<float>(), size[2].Value<float>()));
                return true;
            }

            if (token is JArray array && array.Count >= 6)
            {
                property.boundsValue = new Bounds(
                    new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>()),
                    new Vector3(array[3].Value<float>(), array[4].Value<float>(), array[5].Value<float>()));
                return true;
            }

            reason = "expected { center:[x,y,z], size:[x,y,z] } or [cx,cy,cz,sx,sy,sz]";
            return false;
        }

        /// <summary>JObject 가 ObjectReference 지정용 ref-spec(__ref/__asset/__null 키 중 하나 포함)인지 판별한다.</summary>
        /// <param name="token">검사할 JObject</param>
        /// <returns>ref-spec 이면 true</returns>
        private static bool IsObjectReferenceSpec(JObject token)
        {
            return token["__ref"] != null || token["__asset"] != null || token["__null"] != null;
        }

        /// <summary>ObjectReference 프로퍼티에 에셋 경로 문자열, { "__asset":"Assets/..." }, 씬 참조 { "__ref":"selector" }, null({ "__null":true } 또는 JSON null)을 기록한다.</summary>
        /// <param name="property">ObjectReference 타입 프로퍼티</param>
        /// <param name="token">참조 지정 JSON 값</param>
        /// <param name="reason">실패 사유</param>
        /// <returns>기록 성공 여부</returns>
        private static bool TryWriteObjectReference(SerializedProperty property, JToken token, out string reason)
        {
            reason = string.Empty;

            // JSON null -> 참조 해제.
            if (token == null || token.Type == JTokenType.Null)
            {
                property.objectReferenceValue = null;
                return true;
            }

            if (token is JObject spec)
            {
                if (spec["__null"] != null && spec["__null"].Type != JTokenType.Null && spec.Value<bool>("__null"))
                {
                    property.objectReferenceValue = null;
                    return true;
                }

                var assetToken = spec["__asset"];
                if (assetToken != null)
                {
                    var assetPath = assetToken.Value<string>();
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

                if (spec["__ref"] != null)
                {
                    return ResolveSceneObjectRef(spec, property, out reason);
                }

                reason = "unsupported_object_reference_spec";
                return false;
            }

            // 문자열: 에셋 경로(빈 문자열은 null 해제).
            var path = token.Value<string>();
            if (string.IsNullOrEmpty(path))
            {
                property.objectReferenceValue = null;
                return true;
            }

            var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (loaded == null)
            {
                reason = $"asset_not_found: {path}";
                return false;
            }

            property.objectReferenceValue = loaded;
            return true;
        }

        /// <summary>씬 오브젝트 참조 spec({ "__ref":"selector", "component":"옵션" })을 해석해 프로퍼티에 할당한다.</summary>
        /// <param name="spec">__ref 셀렉터와 옵션 component 를 담은 JObject</param>
        /// <param name="property">할당 대상 ObjectReference 프로퍼티</param>
        /// <param name="reason">실패 사유</param>
        /// <returns>할당 성공 여부</returns>
        private static bool ResolveSceneObjectRef(JObject spec, SerializedProperty property, out string reason)
        {
            reason = string.Empty;
            var selector = spec.Value<string>("__ref") ?? string.Empty;
            var componentName = spec.Value<string>("component");

            var gameObject = ResolveSceneGameObject(selector);
            if (gameObject == null)
            {
                reason = $"ref_not_found: {selector}";
                return false;
            }

            var expectedType = GetSerializedFieldType(property) ?? typeof(UnityEngine.Object);

            UnityEngine.Object resolved;
            if (typeof(GameObject).IsAssignableFrom(expectedType))
            {
                resolved = gameObject;
            }
            else if (typeof(Transform).IsAssignableFrom(expectedType))
            {
                resolved = gameObject.transform;
            }
            else if (typeof(Component).IsAssignableFrom(expectedType) || !string.IsNullOrEmpty(componentName))
            {
                var componentType = !string.IsNullOrEmpty(componentName)
                    ? ResolveComponentType(componentName)
                    : expectedType;
                if (componentType == null)
                {
                    reason = $"component_type_not_found: {componentName}";
                    return false;
                }

                resolved = gameObject.GetComponent(componentType);
            }
            else
            {
                resolved = gameObject;
            }

            if (resolved == null)
            {
                reason = $"ref_component_missing: {componentName ?? expectedType.Name}";
                return false;
            }

            if (!expectedType.IsInstanceOfType(resolved))
            {
                reason = "ref_type_mismatch";
                return false;
            }

            property.objectReferenceValue = resolved;
            return true;
        }

        /// <summary>씬에서 GameObject 를 셀렉터로 찾는다. 셀렉터 형식: name:Foo | path:Root/Child | id:12345 | 접두사가 없으면 name 으로 간주.</summary>
        /// <param name="selector">오브젝트 셀렉터 문자열</param>
        /// <returns>일치하는 첫 GameObject, 없으면 null</returns>
        private static GameObject ResolveSceneGameObject(string selector)
        {
            if (string.IsNullOrEmpty(selector))
            {
                return null;
            }

            var mode = "name";
            var value = selector;
            var separator = selector.IndexOf(':');
            if (separator > 0)
            {
                var prefix = selector.Substring(0, separator);
                if (prefix == "name" || prefix == "path" || prefix == "id")
                {
                    mode = prefix;
                    value = selector.Substring(separator + 1);
                }
            }

            switch (mode)
            {
                case "id":
                    if (!long.TryParse(value, out var id))
                    {
                        return null;
                    }

                    if (EntityIdCompat.IdToObject(id) is GameObject direct)
                    {
                        return direct;
                    }

                    foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                    {
                        if (IsSceneGameObject(candidate) && candidate.GetStableId() == id)
                        {
                            return candidate;
                        }
                    }

                    return null;
                case "path":
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                    {
                        if (IsSceneGameObject(candidate) && MatchHierarchyPath(candidate, value))
                        {
                            return candidate;
                        }
                    }

                    return null;
                default:
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                    {
                        if (IsSceneGameObject(candidate) && candidate.name == value)
                        {
                            return candidate;
                        }
                    }

                    return null;
            }
        }

        /// <summary>hideFlags 가 없고 유효한 씬에 속한(에셋/프리팹이 아닌) 씬 GameObject 인지 판별한다.</summary>
        /// <param name="gameObject">검사할 GameObject</param>
        /// <returns>씬 GameObject 이면 true</returns>
        private static bool IsSceneGameObject(GameObject gameObject)
        {
            return gameObject.hideFlags == HideFlags.None && gameObject.scene.IsValid();
        }

        /// <summary><see cref="SerializedProperty"/> 의 propertyPath 를 리플렉션으로 따라가 대상 필드의 실제 C# 타입을 구한다. 배열/리스트 원소면 원소 타입을 반환한다.</summary>
        /// <param name="property">대상 프로퍼티</param>
        /// <returns>필드 타입 또는 판별 실패 시 null</returns>
        private static Type GetSerializedFieldType(SerializedProperty property)
        {
            try
            {
                var currentType = property.serializedObject?.targetObject?.GetType();
                if (currentType == null)
                {
                    return null;
                }

                var normalized = property.propertyPath.Replace(".Array.data[", "[");
                foreach (var rawSegment in normalized.Split('.'))
                {
                    var segment = rawSegment;
                    var isElement = false;
                    var bracket = segment.IndexOf('[');
                    if (bracket >= 0)
                    {
                        isElement = true;
                        segment = segment.Substring(0, bracket);
                    }

                    var field = GetFieldRecursive(currentType, segment);
                    if (field == null)
                    {
                        return null;
                    }

                    currentType = field.FieldType;
                    if (isElement)
                    {
                        currentType = GetCollectionElementType(currentType);
                        if (currentType == null)
                        {
                            return null;
                        }
                    }
                }

                return currentType;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>배열/리스트 타입에서 원소 타입을 구한다.</summary>
        /// <param name="collectionType">배열 또는 제네릭 컬렉션 타입</param>
        /// <returns>원소 타입, 판별 실패 시 null</returns>
        private static Type GetCollectionElementType(Type collectionType)
        {
            if (collectionType.IsArray)
            {
                return collectionType.GetElementType();
            }

            if (collectionType.IsGenericType)
            {
                var arguments = collectionType.GetGenericArguments();
                if (arguments.Length == 1)
                {
                    return arguments[0];
                }
            }

            return null;
        }

        /// <summary>상속 계층을 거슬러 올라가며 public/비공개 인스턴스 필드를 찾는다([SerializeField] private 기반 클래스 필드 포함).</summary>
        /// <param name="type">시작 타입</param>
        /// <param name="name">필드 이름</param>
        /// <returns>일치하는 <see cref="System.Reflection.FieldInfo"/>, 없으면 null</returns>
        private static System.Reflection.FieldInfo GetFieldRecursive(Type type, string name)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var field = current.GetField(name, flags);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
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
