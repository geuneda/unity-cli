using System.Net;
using System.Net.Sockets;
using UnityCli.Cli;
using UnityCli.Runtime;

namespace UnityCli.Tests;

[Collection("MockBridge")]
public sealed class CliExtendedIntegrationTests : IAsyncLifetime
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
    // 1. UI Commands
    // ---------------------------------------------------------------

    [Fact]
    public async Task UiCanvasCreate_ReturnsSuccessAndCanvasName()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "ui.canvas.create", "name=MainCanvas");

        Assert.Equal(0, exitCode);
        Assert.Contains("MainCanvas", console.StdoutText);
        Assert.Contains("Canvas created.", console.StdoutText);
    }

    [Fact]
    public async Task UiButtonCreate_OnExistingCanvas_ReturnsSuccess()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=BtnCanvas");
        var exitCode = await RunAsync(app, "tool", "call", "ui.button.create", "canvasName=BtnCanvas", "name=StartBtn", "text=Start");

        Assert.Equal(0, exitCode);
        Assert.Contains("StartBtn", console.StdoutText);
        Assert.Contains("Button created.", console.StdoutText);
    }

    [Fact]
    public async Task UiToggleCreateAndSet_TogglesValue()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=ToggleCanvas");
        await RunAsync(app, "tool", "call", "ui.toggle.create", "canvasName=ToggleCanvas", "name=SoundToggle");
        var exitCode = await RunAsync(app, "tool", "call", "ui.toggle.set", "name=SoundToggle", "isOn=true");

        Assert.Equal(0, exitCode);
        Assert.Contains("Toggle set.", console.StdoutText);
        Assert.Contains("\"isOn\": true", console.StdoutText);
    }

    [Fact]
    public async Task UiSliderCreateAndSet_SetsValue()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=SliderCanvas");
        await RunAsync(app, "tool", "call", "ui.slider.create", "canvasName=SliderCanvas", "name=VolumeSlider", "minValue=0", "maxValue=100");
        var exitCode = await RunAsync(app, "tool", "call", "ui.slider.set", "name=VolumeSlider", "value=75");

        Assert.Equal(0, exitCode);
        Assert.Contains("Slider set.", console.StdoutText);
    }

    [Fact]
    public async Task UiScrollRectCreateAndSet_SetsNormalizedPosition()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=ScrollCanvas");
        await RunAsync(app, "tool", "call", "ui.scrollrect.create", "canvasName=ScrollCanvas", "name=LogScroll");
        var exitCode = await RunAsync(app, "tool", "call", "ui.scrollrect.set", "name=LogScroll", "normalizedPosition=[0.5,0.8]");

        Assert.Equal(0, exitCode);
        Assert.Contains("ScrollRect set.", console.StdoutText);
    }

    [Fact]
    public async Task UiInputFieldCreateAndSetText_UpdatesText()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=InputCanvas");
        await RunAsync(app, "tool", "call", "ui.inputfield.create", "canvasName=InputCanvas", "name=NameInput", "placeholder=Enter name");
        var exitCode = await RunAsync(app, "tool", "call", "ui.inputfield.set-text", "name=NameInput", "text=Player1");

        Assert.Equal(0, exitCode);
        Assert.Contains("InputField text set.", console.StdoutText);
        Assert.Contains("Player1", console.StdoutText);
    }

    [Fact]
    public async Task UiTextAndImageCreate_ReturnSuccess()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=MiscCanvas");
        var textExit = await RunAsync(app, "tool", "call", "ui.text.create", "canvasName=MiscCanvas", "name=TitleText", "text=Hello");
        var imageExit = await RunAsync(app, "tool", "call", "ui.image.create", "canvasName=MiscCanvas", "name=BgImage", "color=#FF0000FF");

        Assert.Equal(0, textExit);
        Assert.Equal(0, imageExit);
        Assert.Contains("Text created.", console.StdoutText);
        Assert.Contains("Image created.", console.StdoutText);
    }

    [Fact]
    public async Task UiFocusAndBlur_SetsAndClearsFocus()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=FocusCanvas");
        await RunAsync(app, "tool", "call", "ui.button.create", "canvasName=FocusCanvas", "name=FocusBtn");
        var focusExit = await RunAsync(app, "tool", "call", "ui.focus", "name=FocusBtn");
        var blurExit = await RunAsync(app, "tool", "call", "ui.blur");

        Assert.Equal(0, focusExit);
        Assert.Equal(0, blurExit);
        Assert.Contains("Focused.", console.StdoutText);
        Assert.Contains("Blurred.", console.StdoutText);
        Assert.Contains("\"isSelected\": true", console.StdoutText);
    }

    [Fact]
    public async Task UiClickWithPointerId_DoubleClick_LongPress_AllReturnSuccess()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var clickExit = await RunAsync(app, "tool", "call", "ui.click", "name=AnyButton", "pointerId=1");
        var doubleClickExit = await RunAsync(app, "tool", "call", "ui.double-click", "name=AnyButton");
        var longPressExit = await RunAsync(app, "tool", "call", "ui.long-press", "name=AnyButton", "durationMs=800");

        Assert.Equal(0, clickExit);
        Assert.Equal(0, doubleClickExit);
        Assert.Equal(0, longPressExit);
        Assert.Contains("Clicked.", console.StdoutText);
        Assert.Contains("\"pointerId\": 1", console.StdoutText);
        Assert.Contains("Double-clicked.", console.StdoutText);
        Assert.Contains("\"clickCount\": 2", console.StdoutText);
        Assert.Contains("Long-pressed.", console.StdoutText);
        Assert.Contains("\"durationMs\": 800", console.StdoutText);
    }

    [Fact]
    public async Task UiDragAndSwipe_ReturnSuccess()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=DragCanvas");
        await RunAsync(app, "tool", "call", "ui.button.create", "canvasName=DragCanvas", "name=DragTarget");
        var dragExit = await RunAsync(app, "tool", "call", "ui.drag", "name=DragTarget", "from=10 20", "to=100 200", "pointerId=0");
        var swipeExit = await RunAsync(app, "tool", "call", "ui.swipe", "normalizedFrom=0.1 0.9", "normalizedTo=0.9 0.1");

        Assert.Equal(0, dragExit);
        Assert.Equal(0, swipeExit);
        Assert.Contains("Dragged.", console.StdoutText);
        Assert.Contains("Swiped.", console.StdoutText);
    }

    [Fact]
    public async Task UiHierarchyResource_ListsCreatedElements()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        await RunAsync(app, "tool", "call", "ui.canvas.create", "name=HierCanvas");
        await RunAsync(app, "tool", "call", "ui.button.create", "canvasName=HierCanvas", "name=HierBtn");
        var exitCode = await RunAsync(app, "resource", "get", "ui/hierarchy");

        Assert.Equal(0, exitCode);
        Assert.Contains("HierCanvas", console.StdoutText);
        Assert.Contains("HierBtn", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 2. Input Commands
    // ---------------------------------------------------------------

    [Fact]
    public async Task InputTap_ReturnsHitNameAndWorldPosition()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "input.tap", "worldPosition=5 0 3");

        Assert.Equal(0, exitCode);
        Assert.Contains("Tapped.", console.StdoutText);
        Assert.Contains("5 0 3", console.StdoutText);
    }

    [Fact]
    public async Task InputDoubleTap_WithPointerId_ReturnsClickCount()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "input.double-tap", "worldPosition=1 2 3", "pointerId=2");

        Assert.Equal(0, exitCode);
        Assert.Contains("Double-tapped.", console.StdoutText);
        Assert.Contains("\"clickCount\": 2", console.StdoutText);
        Assert.Contains("\"pointerId\": 2", console.StdoutText);
    }

    [Fact]
    public async Task InputLongPress_WithDurationAndPointerId_ReturnsDuration()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "input.long-press", "worldPosition=0 0 0", "durationMs=1200", "pointerId=3");

        Assert.Equal(0, exitCode);
        Assert.Contains("Long-pressed.", console.StdoutText);
        Assert.Contains("\"durationMs\": 1200", console.StdoutText);
        Assert.Contains("\"pointerId\": 3", console.StdoutText);
    }

    [Fact]
    public async Task InputDrag_ReturnsFromAndToPositions()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "input.drag", "worldFrom=0 0 0", "worldTo=10 5 0");

        Assert.Equal(0, exitCode);
        Assert.Contains("Dragged.", console.StdoutText);
        Assert.Contains("0 0 0", console.StdoutText);
        Assert.Contains("10 5 0", console.StdoutText);
    }

    [Fact]
    public async Task InputSwipe_ReturnsSwipeData()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "input.swipe", "worldFrom=0 0 0", "worldTo=5 5 0", "pointerId=1");

        Assert.Equal(0, exitCode);
        Assert.Contains("Swiped.", console.StdoutText);
        Assert.Contains("\"pointerId\": 1", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 3. Async Command Flows
    // ---------------------------------------------------------------

    [Fact]
    public async Task EditorCompile_ReturnsCompilationIdAndSuccess()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "editor", "compile");

        Assert.Equal(0, exitCode);
        Assert.Contains("compilationId", console.StdoutText);
        Assert.Contains("\"success\": true", console.StdoutText);
    }

    [Fact]
    public async Task TestsRun_CompletesWithRunSummary()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tests", "run", "mode=EditMode");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"passed\":", console.StdoutText);
        Assert.Contains("\"failed\": 0", console.StdoutText);
        Assert.Contains("\"total\":", console.StdoutText);
    }

    [Fact]
    public async Task EditorPlayAndStop_ReportsModeChange()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var playExit = await RunAsync(app, "editor", "play");
        Assert.Equal(0, playExit);
        Assert.Contains("Play mode entered.", console.StdoutText);
        Assert.Contains("\"isPlaying\": true", console.StdoutText);

        var stopExit = await RunAsync(app, "editor", "stop");
        Assert.Equal(0, stopExit);
        Assert.Contains("Play mode exited.", console.StdoutText);
        Assert.Contains("\"isPlaying\": false", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 4. Error Cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task UnknownCommand_PrintsErrorAndReturnsNonZero()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "totally-unknown-command", "do-something");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported tool", console.StdoutText);
    }

    [Fact]
    public async Task MissingArguments_LessThanTwoArgs_PrintsError()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "gameobject");

        Assert.Equal(1, exitCode);
        Assert.Contains("Expected a command group and action", console.StderrText);
    }

    [Fact]
    public async Task ToolCallForNonExistentTool_ReturnsFailure()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "tool", "call", "nonexistent.tool");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported tool", console.StdoutText);
    }

    [Fact]
    public async Task HelpCommand_PrintsUsage()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(new[] { "help" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("unity-cli", console.StdoutText);
        Assert.Contains("status", console.StdoutText);
        Assert.Contains("tool call", console.StdoutText);
        Assert.Contains("--base-url", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 5. Global Options
    // ---------------------------------------------------------------

    [Fact]
    public async Task JsonFlag_WithStatus_OutputsJsonFormat()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "--json", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"name\":", console.StdoutText);
        Assert.Contains("\"version\":", console.StdoutText);
        Assert.Contains("\"state\":", console.StdoutText);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task TimeoutMs_IsRespected_DoesNotCrash()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "--timeout-ms=30000", "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("mock-unity-bridge", console.StdoutText);
    }

    [Fact]
    public async Task NoArgs_PrintsHelp()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await app.RunAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("unity-cli", console.StdoutText);
        Assert.Contains("global options:", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 6. Sprite and Editor
    // ---------------------------------------------------------------

    [Fact]
    public async Task SpriteCreate_ReturnsSuccessWithColor()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "sprite", "create", "name=FireSprite", "color=#FF4500FF");

        Assert.Equal(0, exitCode);
        Assert.Contains("FireSprite", console.StdoutText);
        Assert.Contains("#FF4500FF", console.StdoutText);
    }

    [Fact]
    public async Task EditorGameViewResize_UpdatesDimensions()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "editor", "gameview.resize", "width=1920", "height=1080");

        Assert.Equal(0, exitCode);
        Assert.Contains("1920", console.StdoutText);
        Assert.Contains("1080", console.StdoutText);
    }

    [Fact]
    public async Task AssetImportTexture_ReturnsImportedTrue()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var exitCode = await RunAsync(app, "asset", "import-texture", "path=Assets/Textures/hero.png");

        Assert.Equal(0, exitCode);
        Assert.Contains("hero.png", console.StdoutText);
        Assert.Contains("\"imported\": true", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // 7. Material and Asset Extended
    // ---------------------------------------------------------------

    [Fact]
    public async Task MaterialCreateModifyInfoAssign_ChainWorksEndToEnd()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var createMat = await RunAsync(app, "material", "create", "path=Assets/Materials/Metal.mat", "name=Metal", "shader=Standard", "color=#888888FF");
        Assert.Equal(0, createMat);
        Assert.Contains("Material created.", console.StdoutText);
        Assert.Contains("Metal", console.StdoutText);

        var modifyMat = await RunAsync(app, "material", "modify", "path=Assets/Materials/Metal.mat", "color=#CCCCCCFF");
        Assert.Equal(0, modifyMat);
        Assert.Contains("Material modified.", console.StdoutText);
        Assert.Contains("#CCCCCCFF", console.StdoutText);

        var infoMat = await RunAsync(app, "material", "info", "path=Assets/Materials/Metal.mat");
        Assert.Equal(0, infoMat);
        Assert.Contains("Material info fetched.", console.StdoutText);

        await RunAsync(app, "gameobject", "create", "name=Cube");
        var assignMat = await RunAsync(app, "material", "assign", "materialPath=Assets/Materials/Metal.mat", "name=Cube");
        Assert.Equal(0, assignMat);
        Assert.Contains("Material assigned.", console.StdoutText);
    }

    [Fact]
    public async Task PackageAdd_InstallsAndListsPackage()
    {
        var console = new RecordingConsole();
        var app = new CliApplication(console);

        var addExit = await RunAsync(app, "package", "add", "name=com.unity.addressables", "version=2.0.0");
        Assert.Equal(0, addExit);
        Assert.Contains("Package added.", console.StdoutText);
        Assert.Contains("com.unity.addressables", console.StdoutText);

        var listExit = await RunAsync(app, "package", "list");
        Assert.Equal(0, listExit);
        Assert.Contains("com.unity.addressables", console.StdoutText);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

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
