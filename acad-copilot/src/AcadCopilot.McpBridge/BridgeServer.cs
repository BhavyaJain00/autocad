using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.McpBridge;

/// <summary>Glue between <see cref="BridgeToolService"/> and the official MCP C# SDK server.</summary>
public static class BridgeServer
{
    /// <summary>MCP server name reported in serverInfo.</summary>
    public const string ServerName = "acad";

    /// <summary>Instructions sent to MCP clients on initialize.</summary>
    public const string Instructions =
        "Drafts in the drawing open in the user's AutoCAD 2027 through the AI Copilot add-in, using " +
        "AutoCAD's own commands the way a person does. " +
        "1) Start with get_drawing_info: drawing units (INSUNITS/LUNITS: whether 1 unit is an inch, a foot or a " +
        "millimetre), extents, current layer, current view, and whether a command is still active. All coordinates " +
        "and lengths in every tool are in drawing units. " +
        "2) Draw and edit with run_commands: each step is one AutoCAD command (LINE, PLINE, OFFSET, TRIM, -HATCH, " +
        "DIMLINEAR, -LAYER, ZOOM...) and the inputs typed at its prompts (\"\" presses Enter). The user watches every " +
        "command and value appear in the command line. One call is one undo step; the result lists the prompts AutoCAD " +
        "showed, inputs it rejected, commands left waiting (cancelled with Esc) and the entities created, modified or " +
        "erased. Prefer the dash forms of commands that open dialogs (-LAYER, -HATCH, -INSERT). Saving, plotting, " +
        "purging and erasing everything ask the user first; scripts, LISP, plug-ins and file commands are blocked. " +
        "3) Check your work like a drafter: capture_view returns a picture of the current view (PNG) to look at, and " +
        "list_entities, get_entity, list_layers and get_sysvars return exact geometry and settings. Look after every " +
        "major step and fix what is wrong before continuing. " +
        "4) undo reverts your last run_commands calls; cancel_command presses Esc if a command is stuck. " +
        "If tools report that AutoCAD is not reachable, call acad_status. " +
        "5) Learn: recall_knowledge before drawing and follow the user's rules and style; remember_rule when the user " +
        "corrects you or states a preference, save_recipe after a stage works, save_lesson after fixing a mistake, " +
        "learn_style_from_drawing on the user's own finished drawing (local Markdown files the user can edit). " +
        "6) Also: pick_point / get_selection / select_objects (the user's clicks and selection), create_checkpoint / " +
        "restore_checkpoint / get_resume_info (recovery), and the services and presentation tools (see their descriptions).";

    /// <summary>Instructions sent to MCP clients on initialize when connected to Blender 5.3 Desktop.</summary>
    public const string BlenderInstructions =
        "Models and builds 3D scenes & 3D architecture live in the user's Blender 5.3 Desktop through AI Copilot. " +
        "1) Start with get_drawing_info to inspect the active Blender 5.3 scene (.blend file, collections, 3D objects, extents, camera, revision). " +
        "2) Model and build with run_commands: you have FULL access to Blender 5.3! Use step command 'BPY' with inputs ['<python code using bpy, bmesh, mathutils>'] " +
        "to execute any Blender Python API script (meshes, modifiers, Principled BSDF materials/shaders, geometry nodes, lighting, cameras, animations, collections), " +
        "OR use 3D architectural commands ('WALL_3D', 'SLAB_3D', 'DOOR_3D', 'WINDOW_3D', 'STAIR_3D', 'ROOF_3D', 'FURNITURE_3D', 'MESH_CUBE', 'MESH_CYLINDER', 'MESH_SPHERE', 'MATERIAL', 'MODIFIER', 'LIGHT', 'CAMERA'). " +
        "One run_commands call is one Undo step in Blender 5.3. " +
        "3) After creating or modifying 3D geometry, call capture_view to render a live PNG snapshot of the Blender 5.3 3D Viewport and verify your work visually.";

    /// <summary>Bridge version (assembly informational version).</summary>
    public static string Version { get; } =
        typeof(BridgeServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>Creates the MCP server options with tools/list and tools/call handlers backed by <paramref name="service"/>.</summary>
    public static McpServerOptions CreateOptions(BridgeToolService service) => new()
    {
        ServerInfo = new Implementation { Name = ServerName, Version = Version },
        ServerInstructions = AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase) ? BlenderInstructions : Instructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = async (request, cancellationToken) =>
            {
                var tools = await service.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                return new ListToolsResult { Tools = [.. tools.Select(ToMcpTool)] };
            },
            CallToolHandler = async (request, cancellationToken) =>
            {
                var name = request.Params?.Name ?? throw new McpProtocolException("Missing tool name.", McpErrorCode.InvalidParams);
                var arguments = BridgeToolService.ToJsonObject(request.Params.Arguments);
                var result = await service.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                return ToMcpResult(result);
            },
        },
    };

    /// <summary>Creates the server on <paramref name="transport"/> and wires tools/list_changed notifications.</summary>
    public static McpServer Create(ITransport transport, BridgeToolService service, AcadLink link)
    {
        var server = McpServer.Create(transport, CreateOptions(service));
        link.ToolsChanged += reason =>
        {
            if (service.IsListStale(reason))
            {
                _ = NotifyToolsChangedAsync(server);
            }
        };
        return server;
    }

    /// <summary>
    /// Maps a bridge result to MCP: one text block (the JSON result without image data) followed by one image block
    /// per image; isError is kept.
    /// </summary>
    public static CallToolResult ToMcpResult(BridgeCallResult result)
    {
        var content = new List<ContentBlock>(1 + result.Images.Count) { new TextContentBlock { Text = result.Text } };
        foreach (var image in result.Images)
        {
            // Data holds the base64 text as UTF-8 bytes (the SDK writes it into the JSON as is); base64 is pure ASCII.
            content.Add(new ImageContentBlock { Data = Encoding.ASCII.GetBytes(image.Base64Data), MimeType = image.MimeType });
        }

        return new CallToolResult { Content = content, IsError = result.IsError };
    }

    /// <summary>Maps a AutoCAD tool definition to an MCP tool.</summary>
    public static Tool ToMcpTool(ToolDefinition definition) => new()
    {
        Name = definition.Name,
        Title = definition.Title,
        Description = definition.Description,
        InputSchema = JsonSerializer.SerializeToElement(definition.InputSchema),
        Annotations = new ToolAnnotations
        {
            Title = definition.Title,
            ReadOnlyHint = definition.Risk == ToolRisk.Read,
            DestructiveHint = definition.Risk == ToolRisk.Destructive,
            OpenWorldHint = false,
        },
    };

    private static async Task NotifyToolsChangedAsync(McpServer server)
    {
        try
        {
            await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The client may not be initialized yet or already gone.
            CopilotLog.Info("tools/list_changed not sent: " + ex.Message);
        }
    }
}
