#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS0618, CS8600, CS8602, CS8603, CS8604, CS8625
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge
{
    // Scene / world authoring tools: project tags & layers, Addressables entry management,
    // scene lighting (RenderSettings) and legacy NavMesh baking. All methods run on the Unity
    // main thread (invoked via OnMainThreadAsync from ExecuteToolAsync) and return the shared
    // Success/Failure envelope. Addressables is reached purely by reflection so this assembly does
    // not need a hard dependency on the Addressables editor package.
    public static partial class UnityCliBridgeServer
    {
        /// <summary>project.add-tag 도구: TagManager 의 tags 배열에 태그를 멱등하게 추가한다.</summary>
        /// <param name="arguments">tag(필수) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>{ tag, added } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object AddProjectTag(JObject arguments)
        {
            var tag = arguments.Value<string>("tag");
            if (string.IsNullOrEmpty(tag))
            {
                return Failure("tag is required.", ErrorCodes.MissingArg);
            }

            var tagManager = LoadTagManagerSerializedObject();
            if (tagManager == null)
            {
                return Failure("TagManager.asset not found.", ErrorCodes.NotFound);
            }

            var tagsProperty = tagManager.FindProperty("tags");
            for (var i = 0; i < tagsProperty.arraySize; i++)
            {
                if (tagsProperty.GetArrayElementAtIndex(i).stringValue == tag)
                {
                    return Success(new JObject { ["tag"] = tag, ["added"] = false }, "Tag already exists.");
                }
            }

            tagsProperty.arraySize++;
            tagsProperty.GetArrayElementAtIndex(tagsProperty.arraySize - 1).stringValue = tag;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            Emit(EventTypes.AssetChanged, $"Tag added: {tag}", new JObject { ["tag"] = tag });
            return Success(new JObject { ["tag"] = tag, ["added"] = true }, "Tag added.");
        }

        /// <summary>project.add-layer 도구: user 레이어(8..31) 슬롯에 레이어 이름을 멱등하게 설정한다.</summary>
        /// <param name="arguments">layer(필수), index(옵션 8..31) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>{ layer, index, added } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object AddProjectLayer(JObject arguments)
        {
            var layer = arguments.Value<string>("layer");
            if (string.IsNullOrEmpty(layer))
            {
                return Failure("layer is required.", ErrorCodes.MissingArg);
            }

            var tagManager = LoadTagManagerSerializedObject();
            if (tagManager == null)
            {
                return Failure("TagManager.asset not found.", ErrorCodes.NotFound);
            }

            var layersProperty = tagManager.FindProperty("layers");
            for (var i = 0; i < layersProperty.arraySize; i++)
            {
                if (layersProperty.GetArrayElementAtIndex(i).stringValue == layer)
                {
                    return Success(new JObject { ["layer"] = layer, ["index"] = i, ["added"] = false }, "Layer already exists.");
                }
            }

            var requestedIndex = arguments.Value<int?>("index");
            int targetIndex;
            if (requestedIndex.HasValue)
            {
                if (requestedIndex.Value < 8 || requestedIndex.Value > 31)
                {
                    return Failure("index must be in the user layer range 8..31.", ErrorCodes.BadArg);
                }

                if (!string.IsNullOrEmpty(layersProperty.GetArrayElementAtIndex(requestedIndex.Value).stringValue))
                {
                    return Failure($"Layer slot {requestedIndex.Value} is already occupied.", ErrorCodes.BadArg);
                }

                targetIndex = requestedIndex.Value;
            }
            else
            {
                targetIndex = -1;
                for (var i = 8; i < layersProperty.arraySize && i <= 31; i++)
                {
                    if (string.IsNullOrEmpty(layersProperty.GetArrayElementAtIndex(i).stringValue))
                    {
                        targetIndex = i;
                        break;
                    }
                }

                if (targetIndex < 0)
                {
                    return Failure("No free user layer slot (8..31) available.", ErrorCodes.BadArg);
                }
            }

            layersProperty.GetArrayElementAtIndex(targetIndex).stringValue = layer;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            Emit(EventTypes.AssetChanged, $"Layer added: {layer} (index {targetIndex})", new JObject { ["layer"] = layer, ["index"] = targetIndex });
            return Success(new JObject { ["layer"] = layer, ["index"] = targetIndex, ["added"] = true }, "Layer added.");
        }

        /// <summary>project.list-tags-layers 도구: 현재 태그 목록과 user 레이어(index->name) 목록을 읽는다.</summary>
        /// <param name="arguments">사용하지 않는 인자.</param>
        /// <returns>{ tags, layers } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object ListTagsAndLayers(JObject arguments)
        {
            var tagManager = LoadTagManagerSerializedObject();
            if (tagManager == null)
            {
                return Failure("TagManager.asset not found.", ErrorCodes.NotFound);
            }

            var tagsProperty = tagManager.FindProperty("tags");
            var tags = new JArray();
            for (var i = 0; i < tagsProperty.arraySize; i++)
            {
                tags.Add(tagsProperty.GetArrayElementAtIndex(i).stringValue);
            }

            var layersProperty = tagManager.FindProperty("layers");
            var layers = new JArray();
            for (var i = 8; i < layersProperty.arraySize && i <= 31; i++)
            {
                var name = layersProperty.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(name))
                {
                    layers.Add(new JObject { ["index"] = i, ["name"] = name });
                }
            }

            return Success(new JObject { ["tags"] = tags, ["layers"] = layers }, "Tags and user layers listed.");
        }

        /// <summary>asset.set-addressable 도구: 에셋을 Addressable 로 등록/이동하고 address/group 을 설정한다.</summary>
        /// <param name="arguments">path(필수), address(옵션), group(옵션) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>{ path, guid, address, group } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object SetAddressable(JObject arguments)
        {
            var path = arguments.Value<string>("path");
            if (string.IsNullOrEmpty(path))
            {
                return Failure("path is required.", ErrorCodes.MissingArg);
            }

            var settings = TryGetAddressableSettings(out var packageAvailable);
            if (!packageAvailable)
            {
                return Failure("Addressables package is not installed (addressables_unavailable).", ErrorCodes.NotFound);
            }

            if (settings == null)
            {
                return Failure("Addressable settings asset not found. Create it via Window/Asset Management/Addressables/Groups.", ErrorCodes.NotFound);
            }

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                return Failure($"Asset not found: {path}", ErrorCodes.NotFound);
            }

            var groupName = arguments.Value<string>("group");
            object group = null;
            if (!string.IsNullOrEmpty(groupName))
            {
                group = FindOrCreateAddressableGroup(settings, groupName);
            }

            group ??= settings.GetType().GetProperty("DefaultGroup", BindingFlags.Public | BindingFlags.Instance)?.GetValue(settings);
            if (group == null)
            {
                return Failure("No addressable group available (default group missing).", ErrorCodes.NotFound);
            }

            var entry = InvokeMemberWithDefaults(settings, "CreateOrMoveEntry", guid, group);
            if (entry == null)
            {
                return Failure("Failed to create the addressable entry.");
            }

            var address = arguments.Value<string>("address");
            if (!string.IsNullOrEmpty(address))
            {
                InvokeMemberWithDefaults(entry, "SetAddress", address);
            }

            EditorUtility.SetDirty((UnityEngine.Object)settings);
            AssetDatabase.SaveAssets();

            var actualAddress = entry.GetType().GetProperty("address", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry) as string;
            var parentGroup = entry.GetType().GetProperty("parentGroup", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
            var actualGroup = parentGroup?.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(parentGroup) as string;

            Emit(EventTypes.AssetChanged, $"Addressable set: {path}", new JObject { ["path"] = path, ["address"] = actualAddress });
            return Success(new JObject
            {
                ["path"] = path,
                ["guid"] = guid,
                ["address"] = actualAddress ?? string.Empty,
                ["group"] = actualGroup ?? string.Empty,
            }, "Addressable entry set.");
        }

        /// <summary>asset.remove-addressable 도구: 에셋의 Addressable 엔트리를 제거한다.</summary>
        /// <param name="arguments">path(필수) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>{ path, removed } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object RemoveAddressable(JObject arguments)
        {
            var path = arguments.Value<string>("path");
            if (string.IsNullOrEmpty(path))
            {
                return Failure("path is required.", ErrorCodes.MissingArg);
            }

            var settings = TryGetAddressableSettings(out var packageAvailable);
            if (!packageAvailable)
            {
                return Failure("Addressables package is not installed (addressables_unavailable).", ErrorCodes.NotFound);
            }

            if (settings == null)
            {
                return Failure("Addressable settings asset not found.", ErrorCodes.NotFound);
            }

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                return Failure($"Asset not found: {path}", ErrorCodes.NotFound);
            }

            var entry = InvokeMemberWithDefaults(settings, "FindAssetEntry", guid);
            var removed = false;
            if (entry != null)
            {
                var result = InvokeMemberWithDefaults(settings, "RemoveAssetEntry", guid);
                removed = result is bool boolean ? boolean : true;
                EditorUtility.SetDirty((UnityEngine.Object)settings);
                AssetDatabase.SaveAssets();
            }

            Emit(EventTypes.AssetChanged, $"Addressable removed: {path}", new JObject { ["path"] = path, ["removed"] = removed });
            return Success(new JObject { ["path"] = path, ["removed"] = removed }, removed ? "Addressable entry removed." : "No addressable entry for that asset.");
        }

        /// <summary>scene.set-lighting 도구: 제공된 항목만 RenderSettings 에 적용하고 활성 씬을 dirty 로 표시한다.</summary>
        /// <param name="arguments">ambient/fog/skybox 관련 옵션 인자를 담은 JSON 오브젝트.</param>
        /// <returns>{ applied } 를 담은 성공 응답 또는 실패 응답 봉투.</returns>
        private static object SetSceneLighting(JObject arguments)
        {
            var applied = new JArray();

            void ApplyColor(string key, Action<Color> setter)
            {
                var hex = arguments.Value<string>(key);
                if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var color))
                {
                    setter(color);
                    applied.Add(key);
                }
            }

            void ApplyFloat(string key, Action<float> setter)
            {
                var value = arguments.Value<float?>(key);
                if (value.HasValue)
                {
                    setter(value.Value);
                    applied.Add(key);
                }
            }

            var ambientMode = arguments.Value<string>("ambientMode");
            if (!string.IsNullOrEmpty(ambientMode))
            {
                // Unity 의 환경 광원 소스 "Color" 는 AmbientMode.Flat 과 동일하다.
                var normalized = ambientMode == "Color" ? "Flat" : ambientMode;
                if (Enum.TryParse<UnityEngine.Rendering.AmbientMode>(normalized, out var mode))
                {
                    RenderSettings.ambientMode = mode;
                    applied.Add("ambientMode");
                }
            }

            ApplyColor("ambientColor", color => RenderSettings.ambientLight = color);
            ApplyFloat("ambientIntensity", value => RenderSettings.ambientIntensity = value);
            ApplyColor("ambientSkyColor", color => RenderSettings.ambientSkyColor = color);
            ApplyColor("ambientEquatorColor", color => RenderSettings.ambientEquatorColor = color);
            ApplyColor("ambientGroundColor", color => RenderSettings.ambientGroundColor = color);

            var fog = arguments.Value<bool?>("fog");
            if (fog.HasValue)
            {
                RenderSettings.fog = fog.Value;
                applied.Add("fog");
            }

            ApplyColor("fogColor", color => RenderSettings.fogColor = color);

            var fogMode = arguments.Value<string>("fogMode");
            if (!string.IsNullOrEmpty(fogMode) && Enum.TryParse<FogMode>(fogMode, out var parsedFogMode))
            {
                RenderSettings.fogMode = parsedFogMode;
                applied.Add("fogMode");
            }

            ApplyFloat("fogDensity", value => RenderSettings.fogDensity = value);
            ApplyFloat("fogStartDistance", value => RenderSettings.fogStartDistance = value);
            ApplyFloat("fogEndDistance", value => RenderSettings.fogEndDistance = value);

            var skyboxPath = arguments.Value<string>("skyboxMaterial");
            if (!string.IsNullOrEmpty(skyboxPath))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(skyboxPath);
                if (material == null)
                {
                    return Failure($"Skybox material not found: {skyboxPath}", ErrorCodes.NotFound);
                }

                RenderSettings.skybox = material;
                applied.Add("skyboxMaterial");
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Emit(EventTypes.SceneChanged, $"Scene lighting updated ({applied.Count} settings).", new JObject { ["applied"] = applied });
            return Success(new JObject { ["applied"] = applied }, "Scene lighting applied.");
        }

        /// <summary>scene.bake-navmesh 도구: 레거시 NavMeshBuilder 로 현재 씬 NavMesh 를 동기 베이크한다.</summary>
        /// <param name="arguments">사용하지 않는 인자.</param>
        /// <returns>{ baked } 를 담은 성공 응답 봉투.</returns>
        private static object BakeNavMesh(JObject arguments)
        {
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Emit(EventTypes.SceneChanged, "NavMesh baked for the active scene.", new JObject { ["baked"] = true });
            return Success(new JObject { ["baked"] = true }, "NavMesh baked.");
        }

        /// <summary>ProjectSettings/TagManager.asset 을 SerializedObject 로 로드한다.</summary>
        /// <returns>TagManager SerializedObject, 로드 실패 시 null.</returns>
        private static SerializedObject LoadTagManagerSerializedObject()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0 || assets[0] == null)
            {
                return null;
            }

            return new SerializedObject(assets[0]);
        }

        /// <summary>AddressableAssetSettingsDefaultObject.Settings 를 리플렉션으로 얻는다.</summary>
        /// <param name="packageAvailable">Addressables 에디터 패키지 존재 여부(타입 확인) 출력.</param>
        /// <returns>설정 오브젝트, 설정이 없거나 패키지가 없으면 null.</returns>
        private static object TryGetAddressableSettings(out bool packageAvailable)
        {
            packageAvailable = false;
            var defaultType = Type.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject, Unity.Addressables.Editor");
            if (defaultType == null)
            {
                return null;
            }

            packageAvailable = true;
            var settingsProperty = defaultType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
            return settingsProperty?.GetValue(null);
        }

        /// <summary>이름으로 Addressable 그룹을 찾고, 없으면 리플렉션으로 생성을 시도한다.</summary>
        /// <param name="settings">AddressableAssetSettings 오브젝트.</param>
        /// <param name="groupName">찾거나 생성할 그룹 이름.</param>
        /// <returns>찾거나 생성한 그룹 오브젝트, 실패 시 null.</returns>
        private static object FindOrCreateAddressableGroup(object settings, string groupName)
        {
            var group = InvokeMemberWithDefaults(settings, "FindGroup", groupName);
            if (group != null)
            {
                return group;
            }

            try
            {
                var createMethod = settings.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "CreateGroup" && method.GetParameters().Length == 6);
                if (createMethod == null)
                {
                    return null;
                }

                var schemaType = Type.GetType("UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema, Unity.Addressables.Editor");
                var schemaTypes = schemaType != null ? new[] { schemaType } : Type.EmptyTypes;
                return createMethod.Invoke(settings, new object[] { groupName, false, false, true, null, schemaTypes });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>인스턴스 메서드를 리플렉션으로 호출하되, 선행 인자 이후의 매개변수는 기본값으로 채운다.</summary>
        /// <param name="target">호출 대상 오브젝트.</param>
        /// <param name="methodName">호출할 메서드 이름.</param>
        /// <param name="leading">앞에서부터 채울 실제 인자들.</param>
        /// <returns>메서드 반환값, 매칭 실패 시 null.</returns>
        private static object InvokeMemberWithDefaults(object target, string methodName, params object[] leading)
        {
            if (target == null)
            {
                return null;
            }

            var method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.Name == methodName)
                .Where(candidate => candidate.GetParameters().Length >= leading.Length)
                .Where(candidate => LeadingArgumentsAssignable(candidate.GetParameters(), leading))
                .OrderBy(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (method == null)
            {
                return null;
            }

            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i < leading.Length)
                {
                    args[i] = leading[i];
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else if (parameters[i].ParameterType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(parameters[i].ParameterType);
                }
                else
                {
                    args[i] = null;
                }
            }

            return method.Invoke(target, args);
        }

        /// <summary>선행 인자들이 해당 매개변수 타입에 대입 가능한지 검사한다.</summary>
        /// <param name="parameters">후보 메서드의 매개변수 목록.</param>
        /// <param name="leading">대입하려는 선행 인자들.</param>
        /// <returns>모든 non-null 선행 인자가 대입 가능하면 true.</returns>
        private static bool LeadingArgumentsAssignable(ParameterInfo[] parameters, object[] leading)
        {
            for (var i = 0; i < leading.Length; i++)
            {
                if (leading[i] == null)
                {
                    continue;
                }

                if (!parameters[i].ParameterType.IsInstanceOfType(leading[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
#endif
