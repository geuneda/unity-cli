using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

/// <summary>
/// prefab.create / instantiate / apply / unpack 도구를 모의 브리지에 대해 검증한다.
/// 성공(0), 실패(1, 프리팹 인스턴스가 아님), --field 미스(2) 종료 코드를 함께 확인한다.
/// </summary>
[Collection("MockBridge")]
public sealed class PrefabToolsIntegrationTests : IAsyncLifetime
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

    [Fact]
    public async Task PrefabCreate_FromGameObject_ReturnsRegularType()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Hero");
        var exit = await RunAsync(app, "prefab", "create", "name=Hero", "path=Assets/Prefabs/Hero.prefab");

        Assert.Equal(0, exit);
        Assert.Contains("Prefab created.", console.StdoutText);
        Assert.Contains("Assets/Prefabs/Hero.prefab", console.StdoutText);
        Assert.Contains("Regular", console.StdoutText);
    }

    [Fact]
    public async Task PrefabInstantiate_AddsInstanceToScene()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exit = await RunAsync(app, "prefab", "instantiate", "path=Assets/Prefabs/Hero.prefab", "name=HeroInstance");

        Assert.Equal(0, exit);
        Assert.Contains("Prefab instantiated.", console.StdoutText);
        Assert.Contains("HeroInstance", console.StdoutText);
        Assert.Contains("prefabAssetPath", console.StdoutText);

        var getConsole = new RecordingConsole();
        var getExit = await RunAsync(new CliApplication(getConsole), "gameobject", "get", "name=HeroInstance");
        Assert.Equal(0, getExit);
    }

    [Fact]
    public async Task PrefabApply_OnNonPrefab_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Plain");
        var exit = await RunAsync(app, "prefab", "apply", "name=Plain");

        Assert.Equal(1, exit);
        Assert.Contains("not a prefab instance", console.StdoutText);
    }

    [Fact]
    public async Task PrefabApply_OnInstance_Succeeds()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "prefab", "instantiate", "path=Assets/Prefabs/Hero.prefab", "name=Inst");
        var exit = await RunAsync(app, "prefab", "apply", "name=Inst");

        Assert.Equal(0, exit);
        Assert.Contains("Prefab overrides applied.", console.StdoutText);
    }

    [Fact]
    public async Task PrefabUnpack_OnInstance_Succeeds_ThenNotInstance()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "prefab", "instantiate", "path=Assets/Prefabs/Hero.prefab", "name=Inst2");
        var unpackExit = await RunAsync(app, "prefab", "unpack", "name=Inst2", "completely=true");
        Assert.Equal(0, unpackExit);
        Assert.Contains("Prefab unpacked.", console.StdoutText);

        var applyConsole = new RecordingConsole();
        var applyExit = await RunAsync(new CliApplication(applyConsole), "prefab", "apply", "name=Inst2");
        Assert.Equal(1, applyExit);
        Assert.Contains("not a prefab instance", applyConsole.StdoutText);
    }

    [Fact]
    public async Task PrefabUnpack_OnNonPrefab_Fails()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "gameobject", "create", "name=Plain2");
        var exit = await RunAsync(app, "prefab", "unpack", "name=Plain2");

        Assert.Equal(1, exit);
        Assert.Contains("not a prefab instance", console.StdoutText);
    }

    [Fact]
    public async Task FieldSelector_OnPrefabCreate()
    {
        var setupConsole = new RecordingConsole();
        await RunAsync(new CliApplication(setupConsole), "gameobject", "create", "name=H");

        var hitConsole = new RecordingConsole();
        var hitExit = await RunAsync(new CliApplication(hitConsole), "prefab", "create", "name=H", "path=Assets/Prefabs/H.prefab", "--field=result.prefabAssetType");
        Assert.Equal(0, hitExit);
        Assert.Equal("Regular", hitConsole.StdoutText.Trim());

        var missConsole = new RecordingConsole();
        var missExit = await RunAsync(new CliApplication(missConsole), "prefab", "create", "name=H", "path=Assets/Prefabs/H.prefab", "--field=result.nope");
        Assert.Equal(2, missExit);
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
