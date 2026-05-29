using System.Text.RegularExpressions;

namespace UnityCli.Tests;

/// <summary>
/// Source-text parity guards: the connector hand-maintains an ExecuteToolAsync switch and a
/// ToolCatalog/ResourceCatalog/EventTypes table. These tests fail loudly if they drift, so the
/// fast switch can stay without the contract silently lying to the CLI.
/// </summary>
public sealed class BridgeCatalogConsistencyTests
{
    private static string ConnectorDir => FindConnectorDir();

    private static string BridgeSource => File.ReadAllText(Path.Combine(ConnectorDir, "UnityCliBridge.cs"));

    private static string CatalogSource => File.ReadAllText(Path.Combine(ConnectorDir, "UnityCliBridge.Catalog.cs"));

    [Fact]
    public void SwitchArms_MatchToolCatalog()
    {
        var switchArms = new HashSet<string>(
            Regex.Matches(BridgeSource, "\"(?<name>[a-z][a-z0-9\\-]*(?:\\.[a-z0-9\\-]+)+)\"\\s*=>")
                .Select(match => match.Groups["name"].Value));

        var catalog = new HashSet<string>(
            Regex.Matches(CatalogSource, "Tool\\(\"(?<name>[a-z0-9.\\-]+)\"")
                .Select(match => match.Groups["name"].Value));

        var inSwitchOnly = switchArms.Except(catalog).OrderBy(x => x).ToArray();
        var inCatalogOnly = catalog.Except(switchArms).OrderBy(x => x).ToArray();

        Assert.True(
            inSwitchOnly.Length == 0 && inCatalogOnly.Length == 0,
            $"Tool drift. switch-only: [{string.Join(", ", inSwitchOnly)}]; catalog-only: [{string.Join(", ", inCatalogOnly)}]");
    }

    [Fact]
    public void EmittedEvents_AreDeclaredInEventTypes()
    {
        var declared = new HashSet<string>(
            Regex.Matches(CatalogSource, "public const string \\w+ = \"(?<ev>[a-z0-9._]+)\"")
                .Select(match => match.Groups["ev"].Value));

        var emittedLiterals = Regex.Matches(BridgeSource, "Emit\\(\"(?<ev>[a-z0-9._]+)\"")
            .Select(match => match.Groups["ev"].Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        var missing = emittedLiterals.Where(name => !declared.Contains(name)).ToArray();

        Assert.True(missing.Length == 0, $"Emit() literals missing from EventTypes (capabilities will drift): [{string.Join(", ", missing)}]");
    }

    [Fact]
    public void ResourceCases_AreDeclaredInResourceCatalog()
    {
        var catalog = new HashSet<string>(
            Regex.Matches(CatalogSource, "new ResourceMeta\\(\"(?<r>[a-z0-9/._]+)\"")
                .Select(match => match.Groups["r"].Value));

        var cases = Regex.Matches(BridgeSource, "case \"(?<r>[a-z0-9/._]+)\":")
            .Select(match => match.Groups["r"].Value)
            .Where(name => name.Contains('/'))
            .Distinct()
            .ToArray();

        var missing = cases.Where(name => !catalog.Contains(name)).OrderBy(x => x).ToArray();

        Assert.True(missing.Length == 0, $"BuildResourceAsync cases missing from ResourceCatalog: [{string.Join(", ", missing)}]");
    }

    private static string FindConnectorDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "unity-connector", "Editor", "UnityCliBridge.Catalog.cs");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "unity-connector", "Editor");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate unity-connector/Editor from the test base directory.");
    }
}
