#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS8600, CS8602, CS8603, CS8604, CS8625
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge
{
    // Prefab lifecycle tools (create/instantiate/apply/unpack). All run on the Unity main
    // thread via OnMainThreadAsync from ExecuteToolAsync. Reuses FindGameObject /
    // EnsureParentDirectory / ApplyTransform / GameObjectObject / Emit / Success / Failure
    // from UnityCliBridge.cs.
    public static partial class UnityCliBridgeServer
    {
        /// <summary>씬 GameObject 를 프리팹 에셋으로 저장하고 인스턴스로 연결한다(원본이 이미 프리팹 인스턴스면 자동으로 Variant 가 된다).</summary>
        /// <param name="arguments">대상 GameObject(id/name)와 .prefab 으로 끝나는 path 를 담은 인자.</param>
        /// <returns>성공 시 프리팹 정보(<see cref="PrefabResult"/>)를 담은 응답 봉투, 실패 시 <see cref="Failure(string)"/>.</returns>
        private static object CreatePrefab(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("path must end with .prefab.");
            }

            EnsureParentDirectory(path);
            var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, path, InteractionMode.AutomatedAction, out var success);
            if (!success || saved == null)
            {
                return Failure($"Prefab save failed: {path}.");
            }

            Emit("asset.changed", $"Prefab created: {path}", new JObject { ["path"] = path });
            return Success(PrefabResult(gameObject, path), "Prefab created.");
        }

        /// <summary>프리팹 에셋을 활성 씬에 인스턴스화한다.</summary>
        /// <param name="arguments">프리팹 에셋 path, 선택적 name 과 position/rotation/scale 트랜스폼 인자.</param>
        /// <returns>성공 시 인스턴스 정보(<see cref="PrefabResult"/>)를 담은 응답 봉투, 실패 시 <see cref="Failure(string)"/>.</returns>
        private static object InstantiatePrefab(JObject arguments)
        {
            var path = arguments.Value<string>("path") ?? throw MissingArg("path is required.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                return Failure($"Prefab not found: {path}.");
            }

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
            {
                return Failure("Prefab instantiation failed.");
            }

            var name = arguments.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
            {
                instance.name = name;
            }

            ApplyTransform(instance.transform, arguments);
            Emit("hierarchy.changed", $"Prefab instantiated: {instance.name}", new JObject { ["id"] = instance.GetStableId(), ["name"] = instance.name, ["path"] = path });
            return Success(PrefabResult(instance, path), "Prefab instantiated.");
        }

        /// <summary>프리팹 인스턴스의 오버라이드를 원본 에셋에 적용한다.</summary>
        /// <param name="arguments">대상 GameObject(id/name) 인자.</param>
        /// <returns>성공 시 프리팹 정보(<see cref="PrefabResult"/>)를 담은 응답 봉투, 인스턴스가 아니면 <see cref="Failure(string)"/>.</returns>
        private static object ApplyPrefab(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return Failure($"GameObject '{gameObject.name}' is not a prefab instance.");
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject) ?? gameObject;
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            Emit("asset.changed", $"Prefab overrides applied: {assetPath}", new JObject { ["path"] = assetPath });
            return Success(PrefabResult(root, assetPath), "Prefab overrides applied.");
        }

        /// <summary>프리팹 인스턴스를 일반 GameObject 로 언팩한다.</summary>
        /// <param name="arguments">대상 GameObject(id/name)와 중첩 프리팹까지 언팩할지 여부(completely) 인자.</param>
        /// <returns>성공 시 GameObject 정보(<see cref="GameObjectObject"/>)를 담은 응답 봉투, 인스턴스가 아니면 <see cref="Failure(string)"/>.</returns>
        private static object UnpackPrefab(JObject arguments)
        {
            var gameObject = FindGameObject(arguments);
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return Failure($"GameObject '{gameObject.name}' is not a prefab instance.");
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject) ?? gameObject;
            var completely = arguments["completely"]?.Value<bool?>() ?? false;
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.AutomatedAction);
            Emit("hierarchy.changed", $"Prefab unpacked: {root.name}", new JObject { ["id"] = root.GetStableId(), ["completely"] = completely });
            return Success(GameObjectObject(root), "Prefab unpacked.");
        }

        /// <summary>프리팹 도구 응답용으로 GameObject 요약에 프리팹 에셋 경로/타입/인스턴스 여부를 덧붙인다.</summary>
        /// <param name="gameObject">요약할 GameObject.</param>
        /// <param name="assetPath">관련 프리팹 에셋 경로.</param>
        /// <returns>prefabAssetPath/prefabAssetType/isPrefabInstance 가 추가된 <see cref="GameObjectObject"/> JObject.</returns>
        private static JObject PrefabResult(GameObject gameObject, string assetPath)
        {
            var result = GameObjectObject(gameObject);
            result["prefabAssetPath"] = assetPath;
            result["prefabAssetType"] = PrefabUtility.GetPrefabAssetType(gameObject).ToString();
            result["isPrefabInstance"] = PrefabUtility.IsPartOfPrefabInstance(gameObject);
            return result;
        }
    }
}
#endif
