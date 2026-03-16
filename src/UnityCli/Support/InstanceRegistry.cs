using System.Text.Json.Nodes;

namespace UnityCli.Support;

public static class InstanceRegistry
{
    public static string? ResolveDefaultBaseUrl()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var file = Path.Combine(home, ".unity-cli", "instances.json");
            if (!File.Exists(file))
            {
                return null;
            }

            var json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            return json?["default"]?["baseUrl"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
