#if UNITY_EDITOR
#nullable enable
#pragma warning disable CS8600, CS8602, CS8603, CS8604, CS8625
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge
{
    // Asset management (create-folder/move/delete/rename/duplicate) via AssetDatabase.
    // All methods run on the Unity main thread (invoked via OnMainThreadAsync from ExecuteToolAsync).
    public static partial class UnityCliBridgeServer
    {
        /// <summary>asset.manage 도구의 진입점으로 op 값에 따라 적절한 에셋 처리 메서드로 분기한다.</summary>
        /// <param name="arguments">op 및 작업별 인자를 담은 JSON 오브젝트.</param>
        /// <returns>성공 시 <see cref="Success(object, string)"/>, 실패 시 <see cref="Failure(string)"/> 응답 봉투.</returns>
        private static object ManageAsset(JObject arguments)
        {
            var op = arguments.Value<string>("op");
            if (string.IsNullOrEmpty(op))
                return Failure("op is required (create-folder|move|delete|rename|duplicate).");

            return op switch
            {
                "create-folder" => CreateAssetFolder(arguments),
                "move" => MoveAsset(arguments),
                "delete" => DeleteAssetTool(arguments),
                "rename" => RenameAsset(arguments),
                "duplicate" => DuplicateAsset(arguments),
                _ => Failure($"Unknown op: {op}. Expected create-folder|move|delete|rename|duplicate."),
            };
        }

        /// <summary>parent 폴더 아래에 folderName 폴더를 생성한다.</summary>
        /// <param name="arguments">parent(기본 Assets)와 folderName 인자를 담은 JSON 오브젝트.</param>
        /// <returns>생성된 폴더의 guid/path를 담은 성공 응답, 실패 시 실패 응답.</returns>
        private static object CreateAssetFolder(JObject arguments)
        {
            var parent = arguments.Value<string>("parent");
            if (string.IsNullOrEmpty(parent))
                parent = "Assets";
            var folderName = arguments.Value<string>("folderName");
            if (string.IsNullOrEmpty(folderName))
                return Failure("folderName is required for create-folder.");

            var guid = AssetDatabase.CreateFolder(parent, folderName);
            if (string.IsNullOrEmpty(guid))
                return Failure($"Failed to create folder '{folderName}' under '{parent}'.");

            var path = AssetDatabase.GUIDToAssetPath(guid);
            Emit(EventTypes.AssetChanged, $"Folder created: {path}", new JObject { ["path"] = path });
            return Success(new JObject { ["guid"] = guid, ["path"] = path }, "Folder created.");
        }

        /// <summary>from 경로의 에셋을 to 경로로 이동한다.</summary>
        /// <param name="arguments">from과 to 경로 인자를 담은 JSON 오브젝트.</param>
        /// <returns>from/to를 담은 성공 응답, 검증/이동 실패 시 AssetDatabase 오류 문자열을 담은 실패 응답.</returns>
        private static object MoveAsset(JObject arguments)
        {
            var from = arguments.Value<string>("from");
            if (string.IsNullOrEmpty(from))
                return Failure("from is required for move.");
            var to = arguments.Value<string>("to");
            if (string.IsNullOrEmpty(to))
                return Failure("to is required for move.");

            var validation = AssetDatabase.ValidateMoveAsset(from, to);
            if (!string.IsNullOrEmpty(validation))
                return Failure(validation);

            var error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error))
                return Failure(error);

            Emit(EventTypes.AssetChanged, $"Asset moved: {from} -> {to}", new JObject { ["from"] = from, ["to"] = to });
            return Success(new JObject { ["from"] = from, ["to"] = to }, "Asset moved.");
        }

        /// <summary>path 단일 경로 또는 paths 배열의 에셋을 삭제한다.</summary>
        /// <param name="arguments">path(단일) 또는 paths(배열) 인자를 담은 JSON 오브젝트.</param>
        /// <returns>삭제된 경로 목록을 담은 성공 응답, 실패한 경로가 있으면 실패 응답.</returns>
        private static object DeleteAssetTool(JObject arguments)
        {
            if (arguments["paths"] is JArray pathsArray)
            {
                var paths = pathsArray
                    .Select(token => token?.Value<string>())
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Select(value => value!)
                    .ToArray();
                if (paths.Length == 0)
                    return Failure("paths must contain at least one asset path.");

                var failed = new List<string>();
                AssetDatabase.DeleteAssets(paths, failed);
                if (failed.Count > 0)
                    return Failure($"Failed to delete: {string.Join(", ", failed)}");

                Emit(EventTypes.AssetChanged, $"Asset(s) deleted: {paths.Length}", new JObject { ["count"] = paths.Length });
                return Success(new JObject { ["deleted"] = new JArray(paths) }, "Asset(s) deleted.");
            }

            var path = arguments.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return Failure("path or paths is required for delete.");

            if (!AssetDatabase.DeleteAsset(path))
                return Failure($"Failed to delete asset: {path}");

            Emit(EventTypes.AssetChanged, $"Asset deleted: {path}", new JObject { ["path"] = path });
            return Success(new JObject { ["deleted"] = new JArray(path) }, "Asset(s) deleted.");
        }

        /// <summary>path 에셋의 이름을 newName으로 변경한다.</summary>
        /// <param name="arguments">path와 newName 인자를 담은 JSON 오브젝트.</param>
        /// <returns>path/newName을 담은 성공 응답, 실패 시 AssetDatabase 오류 문자열을 담은 실패 응답.</returns>
        private static object RenameAsset(JObject arguments)
        {
            var path = arguments.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return Failure("path is required for rename.");
            var newName = arguments.Value<string>("newName");
            if (string.IsNullOrEmpty(newName))
                return Failure("newName is required for rename.");

            var error = AssetDatabase.RenameAsset(path, newName);
            if (!string.IsNullOrEmpty(error))
                return Failure(error);

            Emit(EventTypes.AssetChanged, $"Asset renamed: {path} -> {newName}", new JObject { ["path"] = path, ["newName"] = newName });
            return Success(new JObject { ["path"] = path, ["newName"] = newName }, "Asset renamed.");
        }

        /// <summary>path 에셋을 to 경로(생략 시 고유 경로 자동 생성)로 복제한다.</summary>
        /// <param name="arguments">path와 선택적 to 인자를 담은 JSON 오브젝트.</param>
        /// <returns>from/to를 담은 성공 응답, 복제 실패 시 실패 응답.</returns>
        private static object DuplicateAsset(JObject arguments)
        {
            var path = arguments.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return Failure("path is required for duplicate.");

            var to = arguments.Value<string>("to");
            if (string.IsNullOrEmpty(to))
                to = AssetDatabase.GenerateUniqueAssetPath(path);

            if (!AssetDatabase.CopyAsset(path, to))
                return Failure($"Failed to duplicate asset: {path}");

            Emit(EventTypes.AssetChanged, $"Asset duplicated: {path} -> {to}", new JObject { ["from"] = path, ["to"] = to });
            return Success(new JObject { ["from"] = path, ["to"] = to }, "Asset duplicated.");
        }
    }
}
#endif
