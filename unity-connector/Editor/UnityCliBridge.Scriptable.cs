#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS8600, CS8602, CS8603, CS8604, CS8625
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge
{
    // ScriptableObject asset creation/inspection: asset.create-scriptableobject, scriptableobject.get, scriptableobject.list.
    // Reuses the shared TryWriteSerializedProperty / ReadSerializedProperty mappers (Introspection) for value IO.
    // All methods run on the Unity main thread (invoked via OnMainThreadAsync from ExecuteToolAsync).
    public static partial class UnityCliBridgeServer
    {
        /// <summary>지정 타입의 ScriptableObject 에셋을 생성하고 values 를 직렬화 프로퍼티로 주입한다.</summary>
        /// <param name="arguments">type(필수), path(필수), 선택적 values(JSON) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>생성 경로/타입/applied/skipped 를 담은 성공 응답, 타입 미해석 시 not_found 예외.</returns>
        private static object CreateScriptableObjectAsset(JObject arguments)
        {
            var typeName = arguments.Value<string>("type") ?? throw MissingArg("type is required.");
            var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
            var type = ResolveScriptableObjectType(typeName) ?? throw NotFound($"ScriptableObject type not found: {typeName}");

            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
            {
                return Failure($"Failed to create ScriptableObject instance: {typeName}");
            }

            EnsureParentDirectory(path);
            AssetDatabase.CreateAsset(instance, path);

            var applied = new JArray();
            var skipped = new JArray();
            if (arguments["values"] is JObject values)
            {
                ApplyValuesToObject(instance, type, values, applied, skipped);
                EditorUtility.SetDirty(instance);
            }

            AssetDatabase.SaveAssets();
            Emit(EventTypes.AssetChanged, $"ScriptableObject created: {path}", new JObject { ["path"] = path, ["type"] = type.Name });
            return Success(new JObject
            {
                ["path"] = path,
                ["type"] = type.Name,
                ["applied"] = applied,
                ["skipped"] = skipped,
            }, "ScriptableObject created.");
        }

        /// <summary>path 의 ScriptableObject 에셋을 로드해 직렬화 프로퍼티 전체를 읽어 반환한다.</summary>
        /// <param name="arguments">path(필수) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>path/type/properties 를 담은 성공 응답, 에셋 미발견 시 not_found 예외.</returns>
        private static object GetScriptableObjectProperties(JObject arguments)
        {
            var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) ?? throw NotFound($"ScriptableObject not found: {path}");
            return Success(new JObject
            {
                ["path"] = path,
                ["type"] = asset.GetType().Name,
                ["properties"] = ReadObjectSerializedProperties(asset),
            }, "ScriptableObject fetched.");
        }

        /// <summary>filter(기본 t:ScriptableObject)에 맞는 ScriptableObject 에셋 목록을 반환한다.</summary>
        /// <param name="arguments">선택적 filter 인자를 담은 JSON 오브젝트.</param>
        /// <returns>assets(path/type/name) 배열과 count 를 담은 성공 응답.</returns>
        private static object ListScriptableObjects(JObject arguments)
        {
            var filter = arguments.Value<string>("filter");
            if (string.IsNullOrEmpty(filter))
            {
                filter = "t:ScriptableObject";
            }

            var assets = new JArray();
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (asset == null)
                {
                    continue;
                }

                assets.Add(new JObject { ["path"] = path, ["type"] = asset.GetType().Name, ["name"] = asset.name });
            }

            return Success(new JObject { ["assets"] = assets, ["count"] = assets.Count }, "ScriptableObjects listed.");
        }

        /// <summary>full/short 타입명을 <see cref="ScriptableObject"/> 파생 타입으로 해석한다.</summary>
        /// <param name="typeName">해석할 타입명(어셈블리 한정 전체명, 전체명, 또는 짧은 이름).</param>
        /// <returns>해석된 타입, 실패 시 null.</returns>
        private static Type ResolveScriptableObjectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var direct = Type.GetType(typeName);
            if (direct != null && typeof(ScriptableObject).IsAssignableFrom(direct))
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var byFullName = assembly.GetType(typeName);
                    if (byFullName != null && typeof(ScriptableObject).IsAssignableFrom(byFullName))
                    {
                        return byFullName;
                    }
                }
                catch
                {
                    // ignore assemblies that refuse type resolution
                }
            }

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

                var match = types.FirstOrDefault(type => type.Name == typeName && typeof(ScriptableObject).IsAssignableFrom(type));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>UnityEngine.Object 의 보이는 직렬화 프로퍼티 전체를 JObject 로 읽는다.</summary>
        /// <param name="target">읽을 대상 오브젝트.</param>
        /// <returns>propertyPath 를 키로 하는 값 JObject. 실패 시 __error 항목 포함.</returns>
        private static JObject ReadObjectSerializedProperties(UnityEngine.Object target)
        {
            var result = new JObject();
            try
            {
                using var serializedObject = new SerializedObject(target);
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

        /// <summary>UnityEngine.Object 에 values 를 직렬화 프로퍼티(미존재 시 reflection 프로퍼티)로 주입한다.</summary>
        /// <param name="target">값을 쓸 대상 오브젝트.</param>
        /// <param name="targetType">대상 타입(프로퍼티 fallback 용).</param>
        /// <param name="values">member=value 쌍을 담은 JSON 오브젝트.</param>
        /// <param name="applied">성공한 멤버 이름이 추가되는 배열.</param>
        /// <param name="skipped">실패한 멤버 {name,reason} 가 추가되는 배열.</param>
        private static void ApplyValuesToObject(UnityEngine.Object target, Type targetType, JObject values, JArray applied, JArray skipped)
        {
            SerializedObject serializedObject = null;
            try
            {
                serializedObject = new SerializedObject(target);
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
                    var property = targetType.GetProperty(pair.Key);
                    if (property != null && property.CanWrite)
                    {
                        try
                        {
                            property.SetValue(target, pair.Value?.ToObject(property.PropertyType));
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
    }
}
#endif
