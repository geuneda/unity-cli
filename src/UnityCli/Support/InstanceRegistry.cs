using System.Text.Json.Nodes;

namespace UnityCli.Support;

public static class InstanceRegistry
{
    /// <summary>테스트에서 임시 instances.json 경로를 주입하기 위한 오버라이드. null 이면 ~/.unity-cli/instances.json.</summary>
    public static string? FilePathOverride { get; set; }

    private static string ResolveFilePath() =>
        FilePathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "instances.json");

    private static JsonObject? ReadRoot()
    {
        try
        {
            return File.Exists(ResolveFilePath()) ? JsonNode.Parse(File.ReadAllText(ResolveFilePath())) as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>default 별칭의 baseUrl 을 돌려준다. 없으면 null.</summary>
    /// <returns>default 인스턴스의 baseUrl 또는 null.</returns>
    public static string? ResolveDefaultBaseUrl() => ReadRoot()?["default"]?["baseUrl"]?.GetValue<string>();

    /// <summary>--project/--instance 셀렉터로 baseUrl 을 해석한다. instance 는 project:port 키, project 는 같은 projectPath/폴더명을 가진 가장 최근(updatedAt) 살아있는 인스턴스. 못 찾으면 null.</summary>
    /// <param name="instanceKey">정확히 일치시킬 project:port 인스턴스 키.</param>
    /// <param name="project">projectPath 또는 폴더명으로 매칭할 프로젝트 셀렉터.</param>
    /// <returns>해석된 baseUrl 또는 null.</returns>
    public static string? ResolveBaseUrl(string? instanceKey, string? project)
    {
        var root = ReadRoot();
        if (root is null) return null;
        if (!string.IsNullOrEmpty(instanceKey))
            return root[instanceKey]?["baseUrl"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(project))
        {
            var matches = root
                .Where(kv => kv.Key != "default" && kv.Value is JsonObject)
                .Select(kv => (JsonObject)kv.Value!)
                .Where(o => MatchesProject(o, project))
                .OrderByDescending(o => o["alive"]?.GetValue<bool>() ?? false)
                .ThenByDescending(o => o["updatedAt"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
            return matches.Length > 0 ? matches[0]["baseUrl"]?.GetValue<string>() : null;
        }
        return null;
    }

    private static bool MatchesProject(JsonObject entry, string project)
    {
        var path = entry["projectPath"]?.GetValue<string>();
        if (path is null) return false;
        return string.Equals(path, project, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(path.TrimEnd('/', '\\')), project, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>instances list 출력을 위해 default 별칭을 제외한 모든 인스턴스 항목을 그대로 돌려준다.</summary>
    /// <returns>등록된 인스턴스 항목 목록(default 별칭 제외).</returns>
    public static IReadOnlyList<JsonNode> ListInstances()
    {
        var root = ReadRoot();
        if (root is null) return Array.Empty<JsonNode>();
        return root.Where(kv => kv.Key != "default" && kv.Value is not null)
                   .Select(kv => kv.Value!.DeepClone())
                   .ToArray();
    }
}
