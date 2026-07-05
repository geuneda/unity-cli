#if UNITY_EDITOR
#nullable enable
using System.Linq;
using System.Reflection;

namespace UnityCliBridge
{
    // Single source of truth for the bridge contract: tools (with argument metadata),
    // resources and event types. ToolNames, the /tools, /resources and /capabilities
    // endpoints all derive from here so the hand-synced lists can no longer drift.
    // Every tool listed here must have a matching arm in ExecuteToolAsync (UnityCliBridge.cs);
    // the BridgeCatalogConsistency tests enforce that parity.
    public static partial class UnityCliBridgeServer
    {
        internal sealed class ToolArg
        {
            public ToolArg(string name, string type, bool required, string description)
            {
                Name = name;
                Type = type;
                Required = required;
                Description = description;
            }

            public string Name { get; }
            public string Type { get; }
            public bool Required { get; }
            public string Description { get; }
        }

        internal sealed class ToolMeta
        {
            public ToolMeta(string name, string summary, ToolArg[] args)
            {
                Name = name;
                var dot = name.IndexOf('.');
                Category = dot >= 0 ? name.Substring(0, dot) : name;
                Summary = summary;
                Args = args;
            }

            public string Name { get; }
            public string Category { get; }
            public string Summary { get; }
            public ToolArg[] Args { get; }
        }

        internal sealed class ResourceMeta
        {
            public ResourceMeta(string name, string description)
            {
                Name = name;
                Description = description;
            }

            public string Name { get; }
            public string Description { get; }
        }

        private static ToolArg Arg(string name, string type, string description) => new ToolArg(name, type, false, description);
        private static ToolArg Req(string name, string type, string description) => new ToolArg(name, type, true, description);
        private static ToolMeta Tool(string name, string summary, params ToolArg[] args) => new ToolMeta(name, summary, args);

        private static readonly ToolArg[] TargetArgs =
        {
            Arg("id", "int", "Target GameObject instance id."),
            Arg("name", "string", "Target GameObject name (active-scene lookup; alternative to id)."),
        };

        private static ToolArg[] WithTarget(params ToolArg[] extra) => TargetArgs.Concat(extra).ToArray();

        // Order matters only within this file: ToolNames/EventNames derive from the tables below.
        private static readonly ToolMeta[] ToolCatalog =
        {
            // scene
            Tool("scene.create", "Create a new single-mode scene and save it.", Arg("path", "string", "Scene asset path. Default Assets/Scenes/CliScene.unity.")),
            Tool("scene.load", "Open a scene in single mode.", Arg("path", "string", "Scene asset path. Default active scene path.")),
            Tool("scene.save", "Save the active scene."),
            Tool("scene.info", "Return the active scene summary."),
            Tool("scene.delete", "Delete a scene asset.", Req("path", "string", "Scene asset path to delete.")),
            Tool("scene.unload", "Close (unload) a scene; the active scene when no path is given.", Arg("path", "string", "Loaded scene path to unload. Default active scene.")),
            Tool("scene.open-additive", "Open a scene additively (multi-scene).", Req("path", "string", "Scene asset path to open additively.")),
            Tool("scene.set-active", "Set the active scene among the loaded scenes.", Req("path", "string", "Loaded scene path to make active.")),
            Tool("scene.list-loaded", "List all currently loaded scenes."),
            Tool("scene.set-lighting", "Apply RenderSettings lighting/fog to the active scene (only provided fields).",
                Arg("ambientMode", "string", "Skybox, Trilight, Flat or Color."),
                Arg("ambientColor", "color", "Flat/Color ambient light hex."),
                Arg("ambientIntensity", "float", "Ambient intensity multiplier."),
                Arg("ambientSkyColor", "color", "Trilight sky color hex."),
                Arg("ambientEquatorColor", "color", "Trilight equator color hex."),
                Arg("ambientGroundColor", "color", "Trilight ground color hex."),
                Arg("fog", "bool", "Enable fog."),
                Arg("fogColor", "color", "Fog color hex."),
                Arg("fogMode", "string", "Linear, Exponential or ExponentialSquared."),
                Arg("fogDensity", "float", "Exponential fog density."),
                Arg("fogStartDistance", "float", "Linear fog start distance."),
                Arg("fogEndDistance", "float", "Linear fog end distance."),
                Arg("skyboxMaterial", "string", "Skybox material asset path.")),
            Tool("scene.bake-navmesh", "Bake the legacy NavMesh for the active scene (synchronous)."),

            // gameobject
            Tool("gameobject.create", "Create a GameObject (optionally a primitive).",
                Arg("name", "string", "GameObject name. Default GameObject."),
                Arg("primitive", "string", "PrimitiveType (Cube, Sphere, Capsule, Cylinder, Plane, Quad)."),
                Arg("position", "vector3", "World position x,y,z."),
                Arg("rotation", "vector3", "Euler rotation x,y,z."),
                Arg("scale", "vector3", "Local scale x,y,z.")),
            Tool("gameobject.get", "Fetch a GameObject summary.", TargetArgs),
            Tool("gameobject.delete", "Destroy a GameObject.", TargetArgs),
            Tool("gameobject.duplicate", "Duplicate a GameObject.", WithTarget(Arg("name", "string", "Name for the copy. Default '<source> Copy'."))),
            Tool("gameobject.reparent", "Change a GameObject's parent.", WithTarget(Arg("parentId", "int", "New parent instance id. Omit/null to unparent."))),
            Tool("gameobject.move", "Set a GameObject position.", WithTarget(Arg("position", "vector3", "World position x,y,z."))),
            Tool("gameobject.rotate", "Set a GameObject rotation.", WithTarget(Arg("rotation", "vector3", "Euler rotation x,y,z."))),
            Tool("gameobject.scale", "Set a GameObject local scale.", WithTarget(Arg("scale", "vector3", "Local scale x,y,z."))),
            Tool("gameobject.set-transform", "Set position, rotation and scale at once.", WithTarget(Arg("position", "vector3", "World position."), Arg("rotation", "vector3", "Euler rotation."), Arg("scale", "vector3", "Local scale."))),
            Tool("gameobject.select", "Select a GameObject in the editor.", TargetArgs),
            Tool("gameobject.find", "Query GameObjects (incl. inactive) by tag/layer/component/name/path.",
                Arg("tag", "string", "Filter by tag."),
                Arg("layer", "string|int", "Filter by layer name or index."),
                Arg("component", "string", "Filter to objects having this component type."),
                Arg("nameContains", "string", "Case-insensitive name substring filter."),
                Arg("path", "string", "Hierarchy path filter ('/'-separated, '*' wildcard per segment)."),
                Arg("activeOnly", "bool", "Only activeInHierarchy objects. Default false."),
                Arg("includeInactive", "bool", "Include inactive objects. Default true."),
                Arg("limit", "int", "Max results. Default 200.")),
            Tool("gameobject.set-properties", "Set GameObject-level state (active/tag/layer/static/name).", WithTarget(
                Arg("active", "bool", "SetActive value."),
                Arg("tag", "string", "Tag (must exist; otherwise an error is returned)."),
                Arg("layer", "string|int", "Layer name or index."),
                Arg("static", "bool", "isStatic flag."),
                Arg("newName", "string", "Rename the GameObject."),
                Arg("recursiveLayer", "bool", "Apply layer to children too. Default false."))),

            // sprite
            Tool("sprite.create", "Create a 2D sprite GameObject.",
                Arg("name", "string", "Name. Default Sprite."),
                Arg("sprite", "string", "Sprite asset path; supports path::subName for sheet subsprites."),
                Arg("color", "color", "Tint hex e.g. #FF8A00FF."),
                Arg("sortingLayer", "string", "Sorting layer name."),
                Arg("sortingOrder", "int", "Order in layer."),
                Arg("flipX", "bool", "Flip horizontally."),
                Arg("flipY", "bool", "Flip vertically."),
                Arg("position", "vector3", "World position."),
                Arg("collider", "bool", "Add a BoxCollider2D. Default true.")),
            Tool("sprite.set", "Modify an existing SpriteRenderer.", WithTarget(
                Arg("sprite", "string", "Sprite asset path; supports path::subName."),
                Arg("color", "color", "Tint hex."),
                Arg("sortingLayer", "string", "Sorting layer name."),
                Arg("sortingOrder", "int", "Order in layer."),
                Arg("flipX", "bool", "Flip horizontally."),
                Arg("flipY", "bool", "Flip vertically."))),

            // component
            Tool("component.update", "Add-or-get a component and set properties/serialized fields.", WithTarget(
                Arg("type", "string", "Component type name. Default Transform."),
                Arg("values", "json", "Object of member=value to assign."))),
            Tool("component.list", "List the components on a GameObject.", WithTarget(
                Arg("includeValues", "bool", "Include a serialized-field dump per component. Default false."))),
            Tool("component.get", "Read a component's serialized properties (incl. [SerializeField] private).", WithTarget(
                Req("type", "string", "Component type name to read."))),
            Tool("component.add", "Add a component to a GameObject.", WithTarget(
                Req("type", "string", "Component type to add."),
                Arg("values", "json", "Object of member=value to assign after adding."),
                Arg("allowDuplicate", "bool", "Allow adding when one already exists. Default false."))),
            Tool("component.remove", "Remove a component from a GameObject.", WithTarget(
                Req("type", "string", "Component type to remove."),
                Arg("index", "int", "Which one when multiple exist. Default 0."))),

            // material
            Tool("material.create", "Create a material asset.",
                Arg("path", "string", "Asset path. Default Assets/Materials/CliMaterial.mat."),
                Arg("shader", "string", "Shader name."),
                Arg("color", "color", "Base color hex.")),
            Tool("material.assign", "Assign a material to a renderer.", WithTarget(Req("materialPath", "string", "Material asset path."))),
            Tool("material.modify", "Modify an existing material asset.", Req("path", "string", "Material asset path."), Arg("shader", "string", "Shader name."), Arg("color", "color", "Base color hex.")),
            Tool("material.info", "Read a material asset summary.", Req("path", "string", "Material asset path.")),

            // asset
            Tool("asset.list", "List asset paths matching a filter.", Arg("filter", "string", "AssetDatabase filter e.g. t:Prefab.")),
            Tool("asset.add-to-scene", "Instantiate a prefab asset into the scene.", Req("assetPath", "string", "Prefab asset path.")),
            Tool("asset.import-texture", "Change TextureImporter settings.", Req("path", "string", "Texture asset path."),
                Arg("textureType", "string", "Sprite (default), Default, NormalMap, Cursor."),
                Arg("spriteMode", "int", "1 Single (default), 2 Multiple."),
                Arg("maxTextureSize", "int", "Max texture size."),
                Arg("filterMode", "string", "Bilinear (default), Point, Trilinear.")),
            Tool("asset.manage", "Manage assets (create-folder|move|delete|rename|duplicate).",
                Req("op", "string", "Operation: create-folder, move, delete, rename, duplicate."),
                Arg("parent", "string", "create-folder: parent folder. Default Assets."),
                Arg("folderName", "string", "create-folder: new folder name."),
                Arg("from", "string", "move: source asset path."),
                Arg("to", "string", "move/duplicate: target asset path."),
                Arg("path", "string", "delete/rename/duplicate: asset path."),
                Arg("paths", "json", "delete: array of asset paths (alternative to path)."),
                Arg("newName", "string", "rename: new asset name (no extension change).")),
            Tool("asset.set-addressable", "Mark an asset Addressable (create/move entry, set address/group).",
                Req("path", "string", "Asset path to make addressable."),
                Arg("address", "string", "Addressable address key. Default the asset path."),
                Arg("group", "string", "Target group name (found or created). Default the default group.")),
            Tool("asset.remove-addressable", "Remove an asset's Addressable entry if present.",
                Req("path", "string", "Asset path to un-addressable.")),

            // scriptableobject
            Tool("asset.create-scriptableobject", "Create a ScriptableObject asset and inject serialized values.",
                Req("type", "string", "ScriptableObject type name (full or short)."),
                Req("path", "string", "Asset path e.g. Assets/Configs/Foo.asset."),
                Arg("values", "json", "Object of member=value to assign after creation.")),
            Tool("scriptableobject.get", "Read a ScriptableObject asset's serialized properties.", Req("path", "string", "ScriptableObject asset path.")),
            Tool("scriptableobject.list", "List ScriptableObject assets matching a filter.", Arg("filter", "string", "AssetDatabase filter. Default t:ScriptableObject.")),

            // package
            Tool("package.list", "List installed packages."),
            Tool("package.add", "Install a package via UPM.", Req("name", "string", "Package name, optionally name@version."), Arg("version", "string", "Version (optional).")),

            // tests
            Tool("tests.list", "List registered tests.", Arg("mode", "string", "EditMode, PlayMode or All.")),
            Tool("tests.run", "Run tests and wait for completion.", Arg("mode", "string", "EditMode or PlayMode."), Arg("name", "string", "Single test full name."), Arg("names", "json", "Array of test full names."), Arg("category", "string", "Comma-separated NUnit categories (Filter.categoryNames)."), Arg("regex", "string", "Client-side full-name regex filter (EditMode only).")),

            // console
            Tool("console.get", "Return observed console logs."),
            Tool("console.clear", "Clear the console log buffer."),
            Tool("console.send", "Emit a console log.", Arg("message", "string", "Log message. Default unity-cli."), Arg("level", "string", "info, warning or error.")),
            Tool("console.logs", "Query observed console logs from a cursor with optional level/text filters.",
                Arg("sinceCursor", "long", "Only logs with event cursor greater than this. Default 0."),
                Arg("level", "string", "Filter by LogType: Log, Warning, Error, Assert, Exception."),
                Arg("contains", "string", "Case-insensitive substring filter over message and stack trace.")),

            // ui creation
            Tool("ui.canvas.create", "Create a Canvas with a CanvasScaler.", Arg("name", "string", "Canvas name."), Arg("referenceResolution", "vector2", "CanvasScaler reference resolution."), Arg("screenMatchMode", "string", "Expand (default), Shrink, MatchWidthOrHeight."), Arg("matchWidthOrHeight", "float", "0..1 match.")),
            Tool("ui.button.create", "Create a Button with a TMP label.", UiCreateArgs(Arg("text", "string", "Label text."), Arg("fontSize", "float", "Label font size."), Arg("fontStyle", "string", "Normal, Bold, Italic, BoldAndItalic."), Arg("alignment", "string", "TextAnchor alignment."), Arg("textColor", "color", "Label color."))),
            Tool("ui.text.create", "Create a TextMeshProUGUI text.", UiCreateArgs(Arg("text", "string", "Text content."), Arg("fontSize", "float", "Font size."), Arg("fontStyle", "string", "Font style."), Arg("alignment", "string", "Alignment."), Arg("color", "color", "Text color."))),
            Tool("ui.image.create", "Create an Image.", UiCreateArgs(Arg("spritePath", "string", "Sprite asset path."), Arg("color", "color", "Tint."), Arg("imageType", "string", "Simple, Sliced, Tiled, Filled."), Arg("preserveAspect", "bool", "Preserve aspect."), Arg("useNativeSize", "bool", "Use native size."))),
            Tool("ui.toggle.create", "Create a Toggle.", UiCreateArgs(Arg("text", "string", "Label."), Arg("isOn", "bool", "Initial state."))),
            Tool("ui.slider.create", "Create a Slider.", UiCreateArgs(Arg("minValue", "float", "Min."), Arg("maxValue", "float", "Max."), Arg("value", "float", "Initial value."), Arg("wholeNumbers", "bool", "Whole numbers only."))),
            Tool("ui.scrollrect.create", "Create a ScrollRect with content items.", UiCreateArgs(Arg("itemCount", "int", "Number of content items."), Arg("itemHeight", "float", "Item height."), Arg("horizontal", "bool", "Horizontal scroll."), Arg("vertical", "bool", "Vertical scroll."))),
            Tool("ui.inputfield.create", "Create a TMP_InputField.", UiCreateArgs(Arg("text", "string", "Initial text."), Arg("placeholder", "string", "Placeholder text."), Arg("multiline", "bool", "Multiline."))),
            Tool("ui.panel.create", "Create an empty RectTransform panel (Image if color set).", UiCreateArgs(Arg("color", "color", "Background color (adds Image)."))),
            Tool("ui.layout.add", "Add a layout/fitter component to a RectTransform.", WithTarget(
                Arg("layoutType", "string", "Horizontal, Vertical, Grid, ContentSizeFitter."),
                Arg("spacing", "float", "Spacing."),
                Arg("childAlignment", "string", "TextAnchor child alignment."),
                Arg("childForceExpandWidth", "bool", "Force expand width."),
                Arg("childForceExpandHeight", "bool", "Force expand height."),
                Arg("childControlWidth", "bool", "Control width."),
                Arg("childControlHeight", "bool", "Control height."),
                Arg("paddingLeft", "int", "Padding left."),
                Arg("paddingRight", "int", "Padding right."),
                Arg("paddingTop", "int", "Padding top."),
                Arg("paddingBottom", "int", "Padding bottom."),
                Arg("cellSize", "vector2", "Grid cell size."),
                Arg("gridSpacing", "vector2", "Grid spacing."),
                Arg("horizontalFit", "string", "ContentSizeFitter horizontal fit."),
                Arg("verticalFit", "string", "ContentSizeFitter vertical fit."))),
            Tool("ui.recttransform.modify", "Modify a RectTransform (only provided fields).", WithTarget(
                Arg("anchorMin", "vector2", "Anchor min."),
                Arg("anchorMax", "vector2", "Anchor max."),
                Arg("pivot", "vector2", "Pivot."),
                Arg("anchoredPosition", "vector2", "Anchored position."),
                Arg("size", "vector2", "Size delta."),
                Arg("offsetMin", "vector2", "Offset min."),
                Arg("offsetMax", "vector2", "Offset max."))),
            Tool("ui.screenshot.capture", "Capture the Game View (play mode) or a render to PNG.", Req("outputPath", "string", "Output PNG path."), Arg("width", "int", "Width hint. Default 1920."), Arg("height", "int", "Height hint. Default 1080."), Arg("source", "string", "game (default) or scene.")),

            // ui state
            Tool("ui.toggle.set", "Set a Toggle value.", WithTarget(Arg("isOn", "bool", "Value."))),
            Tool("ui.slider.set", "Set a Slider value.", WithTarget(Arg("value", "float", "Value."))),
            Tool("ui.scrollrect.set", "Set a ScrollRect normalized position.", WithTarget(Arg("normalizedPosition", "vector2", "x,y in 0..1."), Arg("horizontalNormalizedPosition", "float", "0..1."), Arg("verticalNormalizedPosition", "float", "0..1."))),
            Tool("ui.inputfield.set-text", "Set an input field's text.", WithTarget(Arg("text", "string", "Text."))),
            Tool("ui.focus", "Focus a UI element via EventSystem.", TargetArgs),
            Tool("ui.blur", "Clear UI focus."),

            // ui pointer
            Tool("ui.click", "Click a UI element.", WithTarget(Arg("normalizedPosition", "vector2", "Screen position 0..1 (alt to id/name)."), Arg("pointerId", "int", "Pointer id."))),
            Tool("ui.double-click", "Double-click a UI element.", WithTarget(Arg("normalizedPosition", "vector2", "Screen position 0..1."), Arg("pointerId", "int", "Pointer id."))),
            Tool("ui.long-press", "Long-press a UI element.", WithTarget(Arg("normalizedPosition", "vector2", "Screen position 0..1."), Arg("durationMs", "int", "Hold duration ms."), Arg("pointerId", "int", "Pointer id."))),
            Tool("ui.drag", "Drag on a UI element.", WithTarget(Arg("from", "vector2", "Start 0..1."), Arg("to", "vector2", "End 0..1."), Arg("pointerId", "int", "Pointer id."))),
            Tool("ui.swipe", "Swipe across the UI.", Arg("normalizedFrom", "vector2", "Start 0..1."), Arg("normalizedTo", "vector2", "End 0..1."), Arg("pointerId", "int", "Pointer id.")),

            // input (world)
            Tool("input.tap", "Tap at a world position.", Arg("worldPosition", "vector3", "World position.")),
            Tool("input.double-tap", "Double-tap at a world position.", Arg("worldPosition", "vector3", "World position."), Arg("pointerId", "int", "Pointer id.")),
            Tool("input.long-press", "Long-press at a world position.", Arg("worldPosition", "vector3", "World position."), Arg("durationMs", "int", "Hold ms."), Arg("pointerId", "int", "Pointer id.")),
            Tool("input.drag", "Drag between world positions.", Arg("worldFrom", "vector3", "Start."), Arg("worldTo", "vector3", "End."), Arg("pointerId", "int", "Pointer id.")),
            Tool("input.swipe", "Swipe between world positions.", Arg("worldFrom", "vector3", "Start."), Arg("worldTo", "vector3", "End."), Arg("pointerId", "int", "Pointer id.")),

            // menu / editor
            Tool("menu.execute", "Execute an editor menu item.", Req("path", "string", "Menu path e.g. Assets/Refresh.")),
            Tool("editor.play", "Enter play mode and wait until playing."),
            Tool("editor.stop", "Exit play mode and wait until stopped."),
            Tool("editor.pause", "Toggle or set pause.", Arg("enabled", "bool", "Pause state; omit to toggle.")),
            Tool("editor.refresh", "Refresh the AssetDatabase."),
            Tool("editor.compile", "Request script compilation and wait."),
            Tool("editor.gameview.resize", "Resize the Game View.", Arg("width", "int", "Width."), Arg("height", "int", "Height.")),

            // project settings
            Tool("project.add-tag", "Add a tag to the project (idempotent).", Req("tag", "string", "Tag name to add.")),
            Tool("project.add-layer", "Set a user layer name (8..31) in the first free or given slot.",
                Req("layer", "string", "Layer name to add."),
                Arg("index", "int", "Specific user layer slot 8..31. Default first free slot.")),
            Tool("project.remove-tag", "Remove a tag from the project if present (idempotent).", Req("tag", "string", "Tag name to remove.")),
            Tool("project.remove-layer", "Clear a user layer (8..31) by name if present (idempotent).", Req("layer", "string", "Layer name to remove.")),
            Tool("project.list-tags-layers", "List defined tags and user layers (index->name)."),

            // prefab
            Tool("prefab.create", "Save a scene GameObject as a prefab asset (variant if the source is already a prefab instance).", WithTarget(
                Req("path", "string", "Prefab asset path ending in .prefab."))),
            Tool("prefab.instantiate", "Instantiate a prefab asset into the active scene.",
                Req("path", "string", "Prefab asset path."),
                Arg("name", "string", "Name for the instance. Default the prefab name."),
                Arg("position", "vector3", "World position x,y,z."),
                Arg("rotation", "vector3", "Euler rotation x,y,z."),
                Arg("scale", "vector3", "Local scale x,y,z.")),
            Tool("prefab.apply", "Apply a prefab instance's overrides back to its source asset.", TargetArgs),
            Tool("prefab.unpack", "Unpack a prefab instance into plain GameObjects.", WithTarget(
                Arg("completely", "bool", "Unpack nested prefabs too (Completely). Default false (OutermostRoot)."))),
        };

        private static ToolArg[] UiCreateArgs(params ToolArg[] extra)
        {
            var common = new[]
            {
                Arg("canvasName", "string", "Owning canvas name. Default Canvas (created if missing)."),
                Arg("name", "string", "Element name."),
                Arg("parentName", "string", "Parent RectTransform name."),
                Arg("parentId", "int", "Parent instance id."),
                Arg("anchoredPosition", "vector2", "Anchored position."),
                Arg("size", "vector2", "Size delta."),
                Arg("anchorMin", "vector2", "Anchor min."),
                Arg("anchorMax", "vector2", "Anchor max."),
                Arg("pivot", "vector2", "Pivot."),
            };
            return common.Concat(extra).ToArray();
        }

        // Derived: the canonical tool-name list used by /health, /capabilities and /tools.
        private static readonly string[] ToolNames = ToolCatalog.Select(tool => tool.Name).ToArray();

        private static readonly ResourceMeta[] ResourceCatalog =
        {
            new ResourceMeta("editor/state", "Editor play/pause/selection state."),
            new ResourceMeta("scene/active", "Active scene summary."),
            new ResourceMeta("scene/hierarchy", "Scene hierarchy."),
            new ResourceMeta("ui/hierarchy", "UI hierarchy for active canvases."),
            new ResourceMeta("console/logs", "Observed console logs."),
            new ResourceMeta("tests/catalog", "Known tests."),
            new ResourceMeta("tests/last-run", "Last completed test run summary."),
            new ResourceMeta("packages/list", "Installed packages."),
            new ResourceMeta("project/info", "Project, build target, render pipeline and scenes-in-build info."),
            new ResourceMeta("addressables/list", "Addressables groups and entries (reflection; available:false when package absent)."),
        };

        // All event types the bridge can emit. capabilities.events derives from here, so it can
        // never silently drift from the actual Emit() call sites (checked by the parity tests).
        public static class EventTypes
        {
            public const string BridgeStarted = "bridge.started";
            public const string SceneChanged = "scene.changed";
            public const string SceneLoaded = "scene.loaded";
            public const string SceneSaved = "scene.saved";
            public const string HierarchyChanged = "hierarchy.changed";
            public const string TransformChanged = "transform.changed";
            public const string SelectionChanged = "selection.changed";
            public const string ComponentChanged = "component.changed";
            public const string AssetChanged = "asset.changed";
            public const string PackageChanged = "package.changed";
            public const string ConsoleLog = "console.log";
            public const string UiFocused = "ui.focused";
            public const string UiBlurred = "ui.blurred";
            public const string UiClicked = "ui.clicked";
            public const string UiDoubleClicked = "ui.double_clicked";
            public const string UiLongPressed = "ui.long_pressed";
            public const string UiDragged = "ui.dragged";
            public const string UiSwiped = "ui.swiped";
            public const string InputTapped = "input.tapped";
            public const string InputDoubleTapped = "input.double_tapped";
            public const string InputLongPressed = "input.long_pressed";
            public const string InputDragged = "input.dragged";
            public const string InputSwiped = "input.swiped";
            public const string MenuExecuted = "menu.executed";
            public const string EditorPlayModeChanged = "editor.play_mode_changed";
            public const string EditorPauseChanged = "editor.pause_changed";
            public const string EditorRefreshed = "editor.refreshed";
            public const string EditorCompilationStarted = "editor.compilation_started";
            public const string EditorCompiled = "editor.compiled";
            public const string TestsStarted = "tests.started";
            public const string TestsCompleted = "tests.completed";
        }

        private static readonly string[] EventNames = typeof(EventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null))
            .ToArray();
    }
}
#endif
