using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class NewToolsIntegrationTests : IAsyncLifetime
{
    private readonly MockUnityBridgeServer _server = new();
    private int _port;
    private string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        await _server.StartAsync(port: _port);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    // ---------------------------------------------------------------
    // component.list / get / add / remove
    // ---------------------------------------------------------------

    [Fact]
    public async Task ComponentList_ReturnsTransform()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "list", "name=Hero");

        Assert.Equal(0, exit);
        Assert.Contains("Components listed.", console.StdoutText);
        Assert.Contains("Transform", console.StdoutText);
    }

    [Fact]
    public async Task ComponentAddThenGet_RoundTripsValues()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var addExit = await RunAsync(app, "component", "add", "name=Hero", "type=Health", "values={\"hp\":100}");
        Assert.Equal(0, addExit);
        Assert.Contains("Component added.", console.StdoutText);

        var getExit = await RunAsync(app, "component", "get", "name=Hero", "type=Health");
        Assert.Equal(0, getExit);
        Assert.Contains("hp", console.StdoutText);
        Assert.Contains("100", console.StdoutText);
    }

    [Fact]
    public async Task ComponentAdd_Duplicate_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        await RunAsync(app, "component", "add", "name=Hero", "type=Health");
        var second = await RunAsync(app, "component", "add", "name=Hero", "type=Health");

        Assert.Equal(1, second);
        Assert.Contains("already exists", console.StdoutText);
    }

    [Fact]
    public async Task ComponentRemove_RemovesComponent()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        await RunAsync(app, "component", "add", "name=Hero", "type=Health");
        var removeExit = await RunAsync(app, "component", "remove", "name=Hero", "type=Health");
        Assert.Equal(0, removeExit);
        Assert.Contains("Component removed.", console.StdoutText);

        var getExit = await RunAsync(app, "component", "get", "name=Hero", "type=Health");
        Assert.Equal(1, getExit);
    }

    [Fact]
    public async Task ComponentRemove_Transform_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "remove", "name=Hero", "type=Transform");

        Assert.Equal(1, exit);
        Assert.Contains("cannot be removed", console.StdoutText);
    }

    [Fact]
    public async Task ComponentUpdate_ReturnsAppliedAndSkipped()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "update", "name=Hero", "type=Health", "values={\"hp\":50}");

        Assert.Equal(0, exit);
        Assert.Contains("Component updated.", console.StdoutText);
        Assert.Contains("applied", console.StdoutText);
        Assert.Contains("hp", console.StdoutText);
        Assert.Contains("skipped", console.StdoutText);
    }

    [Fact]
    public async Task ComponentUpdate_AppliedField_ExitZero()
    {
        var setupConsole = new RecordingConsole();
        await RunAsync(new CliApplication(setupConsole), "gameobject", "create", "name=Hero");

        var console = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(console), "component", "update", "name=Hero", "type=Health", "values={\"hp\":50}", "--field=result.applied[0]");

        Assert.Equal(0, exit);
        Assert.Equal("hp", console.StdoutText.Trim());
    }

    [Fact]
    public async Task ComponentUpdate_ThenGet_RoundTripsValue()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var updateExit = await RunAsync(app, "component", "update", "name=Hero", "type=Health", "values={\"hp\":50}");
        Assert.Equal(0, updateExit);

        var getExit = await RunAsync(app, "component", "get", "name=Hero", "type=Health");
        Assert.Equal(0, getExit);
        Assert.Contains("hp", console.StdoutText);
        Assert.Contains("50", console.StdoutText);
    }

    [Fact]
    public async Task ComponentUpdate_MissingField_ExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "component", "update", "name=Hero", "type=Health", "values={\"hp\":50}", "--field=result.skipped[0].name");

        Assert.Equal(2, exit);
    }

    // ---------------------------------------------------------------
    // gameobject.find / set-properties
    // ---------------------------------------------------------------

    [Fact]
    public async Task GameObjectFind_ByNameContains_ReturnsMatches()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero1");
        await RunAsync(app, "gameobject", "create", "name=Hero2");
        await RunAsync(app, "gameobject", "create", "name=Villain");

        var findConsole = new RecordingConsole();
        var findApp = new CliApplication(findConsole);
        var exit = await RunAsync(findApp, "gameobject", "find", "nameContains=Hero");

        Assert.Equal(0, exit);
        Assert.Contains("\"count\": 2", findConsole.StdoutText);
    }

    [Fact]
    public async Task GameObjectSetProperties_RenamesAndTags()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var setExit = await RunAsync(app, "gameobject", "set-properties", "name=Hero", "newName=Boss", "tag=Enemy", "active=false");
        Assert.Equal(0, setExit);
        Assert.Contains("newName", console.StdoutText);
        Assert.Contains("tag", console.StdoutText);

        var getExit = await RunAsync(app, "gameobject", "get", "name=Boss");
        Assert.Equal(0, getExit);
    }

    // ---------------------------------------------------------------
    // project/info resource
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProjectInfoResource_ReturnsRenderPipeline()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "resource", "get", "project/info");

        Assert.Equal(0, exit);
        Assert.Contains("renderPipeline", console.StdoutText);
    }

    [Fact]
    public async Task AddressablesListResource_ReturnsGroupsFromMock()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "resource", "get", "addressables/list");

        Assert.Equal(0, exit);
        Assert.Contains("groups", console.StdoutText);
        Assert.Contains("address", console.StdoutText);
        Assert.Contains("labels", console.StdoutText);
    }

    [Fact]
    public async Task AddressablesListResource_FieldSelectorReadsGroupName()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "resource", "get", "addressables/list", "--field=data.groups[0].name");

        Assert.Equal(0, exit);
        Assert.Equal("Default Local Group", console.StdoutText.Trim());
    }

    [Fact]
    public async Task AddressablesListResource_FieldMiss_ReturnsExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "resource", "get", "addressables/list", "--field=data.doesNotExist");

        Assert.Equal(2, exit);
    }

    // ---------------------------------------------------------------
    // --field / --quiet / doctor
    // ---------------------------------------------------------------

    [Fact]
    public async Task FieldSelector_PrintsRawScalar()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--field=result.name");

        Assert.Equal(0, exit);
        Assert.Equal("Hero", console.StdoutText.Trim());
        Assert.DoesNotContain("{", console.StdoutText);
    }

    [Fact]
    public async Task FieldSelector_Miss_ReturnsExitCode2()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--field=result.doesNotExist");

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Quiet_SuppressesOutput_KeepsExitCode()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "gameobject", "create", "name=Hero", "--quiet");

        Assert.Equal(0, exit);
        Assert.True(string.IsNullOrEmpty(console.StdoutText));
    }

    [Fact]
    public async Task Doctor_ReportsReachableAndParity()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "doctor");

        Assert.Equal(0, exit);
        Assert.Contains("[PASS] bridge.reachable", console.StdoutText);
        Assert.Contains("tools.parity", console.StdoutText);
        Assert.Contains("events.contract", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // console.logs tool + logs wait
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConsoleLogs_Tool_FiltersByLevelAndCursor()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "console", "send", "message=hello", "level=info");
        await RunAsync(app, "console", "send", "message=boom", "level=Error");

        var queryConsole = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(queryConsole), "console", "logs", "level=Error");

        Assert.Equal(0, exit);
        Assert.Contains("boom", queryConsole.StdoutText);
        Assert.Contains("\"errorCount\": 1", queryConsole.StdoutText);
        Assert.DoesNotContain("hello", queryConsole.StdoutText);
    }

    [Fact]
    public async Task ConsoleLogs_SinceCursor_ExcludesOlder()
    {
        var app = new CliApplication(new RecordingConsole());
        await RunAsync(app, "console", "send", "message=old", "level=Error");

        var cursorConsole = new RecordingConsole();
        var cursorExit = await RunAsync(new CliApplication(cursorConsole), "console", "logs", "--field=result.cursor");
        Assert.Equal(0, cursorExit);
        var cursor = cursorConsole.StdoutText.Trim();

        await RunAsync(app, "console", "send", "message=new", "level=Error");

        var queryConsole = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(queryConsole), "console", "logs", $"sinceCursor={cursor}", "--field=result.errorCount");

        Assert.Equal(0, exit);
        Assert.Equal("1", queryConsole.StdoutText.Trim());
    }

    [Fact]
    public async Task LogsWait_ExpectNone_Passes_WhenNoError()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "logs", "wait", "level=Error", "timeoutMs=800", "expectNone=true");

        Assert.Equal(0, exit);
        Assert.Contains("No Error log appeared.", console.StdoutText);
    }

    [Fact]
    public async Task LogsWait_ExpectNone_Fails_WhenErrorAppears()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "console", "send", "message=kaboom", "level=Error");

        var waitConsole = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(waitConsole), "logs", "wait", "level=Error", "contains=kaboom", "timeoutMs=2000", "expectNone=true");

        Assert.Equal(1, exit);
        Assert.Contains("Unexpected Error log appeared.", waitConsole.StdoutText);
    }

    [Fact]
    public async Task LogsWait_WaitsForError_ReturnsZero()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "console", "send", "message=err1", "level=Error");

        var waitConsole = new RecordingConsole();
        var exit = await RunAsync(new CliApplication(waitConsole), "logs", "wait", "level=Error", "contains=err1", "timeoutMs=2000");

        Assert.Equal(0, exit);
        Assert.Contains("Error log observed.", waitConsole.StdoutText);
        Assert.Contains("err1", waitConsole.StdoutText);
    }

    [Fact]
    public async Task LogsWait_Timeout_NoError_ReturnsOne()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "logs", "wait", "level=Error", "timeoutMs=600");

        Assert.Equal(1, exit);
        Assert.Contains("Timed out waiting for a Error log.", console.StdoutText);
    }

    [Fact]
    public async Task LogsWait_BadUsage_ReturnsOne()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "logs");

        Assert.Equal(1, exit);
        Assert.Contains("Usage: unity-cli logs wait", console.StderrText);
    }

    // ---------------------------------------------------------------
    // asset.manage (create-folder / move / delete / rename / duplicate)
    // ---------------------------------------------------------------

    [Fact]
    public async Task AssetManage_CreateFolder_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=create-folder", "parent=Assets", "folderName=Generated");

        Assert.Equal(0, exit);
        Assert.Contains("Folder created.", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_Move_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=move", "from=Assets/A.prefab", "to=Assets/Sub/A.prefab");

        Assert.Equal(0, exit);
        Assert.Contains("Asset moved.", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_Rename_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=rename", "path=Assets/A.prefab", "newName=B");

        Assert.Equal(0, exit);
        Assert.Contains("Asset renamed.", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_Duplicate_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=duplicate", "path=Assets/A.prefab");

        Assert.Equal(0, exit);
        Assert.Contains("Asset duplicated.", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_Delete_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=delete", "path=Assets/A.prefab");

        Assert.Equal(0, exit);
        Assert.Contains("deleted", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_MissingOp_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage");

        Assert.Equal(1, exit);
        Assert.Contains("op is required", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_UnknownOp_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=frobnicate");

        Assert.Equal(1, exit);
        Assert.Contains("Unknown op", console.StdoutText);
    }

    [Fact]
    public async Task AssetManage_CreateFolder_MissingName_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=create-folder", "parent=Assets");

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task AssetManage_FieldSelector_PrintsRawScalar()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "asset", "manage", "op=create-folder", "folderName=Generated", "--field=result.path");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("{", console.StdoutText);
    }

    private Task<int> RunAsync(CliApplication app, params string[] args)
    {
        var fullArgs = new List<string> { $"--base-url={BaseUrl}" };
        fullArgs.AddRange(args);
        return app.RunAsync(fullArgs.ToArray(), CancellationToken.None);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
