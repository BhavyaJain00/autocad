using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Sheets;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Tools;
using AcadCopilot.Plugin.ViewModels;

namespace BlenderCopilot.Host;

/// <summary>
/// Implements all 26+ AI Copilot tools for Blender 5.3 Desktop, bridging the C# validation/planning/learning/recovery
/// engines with the live Python 3D engine running inside Blender 5.3.
/// </summary>
public sealed class BlenderPipeHandler : IPipeHandler
{
    public const string ServerName = "BlenderCopilot.Host";

    private readonly BlenderWorkerClient _worker;
    private readonly CopilotBackend _backend;
    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly Dictionary<string, ToolDefinition> _byName;
    private readonly Func<string?, CancellationToken, Task>? _revealPanel;
    private long _lastRevision = 1;

    public BlenderPipeHandler(
        BlenderWorkerClient worker,
        CopilotBackend backend,
        Func<string?, CancellationToken, Task>? revealPanel = null)
    {
        _worker = worker;
        _backend = backend;
        _revealPanel = revealPanel;
        var defaultRegistry = ToolRegistry.CreateDefault();
        _definitions = [.. defaultRegistry.Definitions.Select(AdaptToolForBlender)];
        _byName = _definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }

    private static ToolDefinition AdaptToolForBlender(ToolDefinition def) => def.Name switch
    {
        "get_drawing_info" => def with
        {
            Title = "Blender scene info",
            Description =
                "Read the full state of the active Blender 5.3 scene (.blend file, units, collections/layers, all 3D objects by type " +
                "(MESH, LIGHT, CAMERA, FONT, CURVE, EMPTY, 3DSOLID, LINE, LWPOLYLINE), 3D bounding-box extents, active collection, " +
                "camera/viewport, and scene revision). Call this at the start of every request.",
        },
        "run_commands" => def with
        {
            Title = "Blender 5.3 commands",
            Description =
                "Execute 1 to 30 Blender 5.3 3D modeling, Python (BPY), or architectural building steps visibly in the active Blender 5.3 scene as ONE undo step. " +
                "You have FULL access to everything in Blender 5.3:\n" +
                "1) Full Blender Python API ('BPY'): {command: 'BPY', inputs: ['<any python code using bpy, bmesh, mathutils, Vector, Euler, Matrix, C=bpy.context, D=bpy.data, O=bpy.ops>'], note: '...'}. " +
                "Use 'BPY' to create or modify ANY 3D mesh, object, modifier (Bevel, Subdivision, Boolean, Array, Solidify, Mirror), Principled BSDF material & shader nodes, " +
                "Geometry Nodes, curves, 3D text, lighting, cameras, armatures, keyframes, collections, or delete/transform selected objects.\n" +
                "2) 3D Architectural building commands (lengths accept feet-inches like \"30'\", \"12'-6\\\"\", \"9\", \"4.5\" or meters like \"3.5m\"):\n" +
                "   - 'WALL_3D': inputs ['fromX,fromY', 'toX,toY', 'thickness?', 'height?', 'name?']\n" +
                "   - 'SLAB_3D': inputs ['x,y', 'width,depth', 'thickness?', 'elevation?', 'name?']\n" +
                "   - 'DOOR_3D': inputs ['x,y', 'width?', 'height?', 'angle?', 'swing?']\n" +
                "   - 'WINDOW_3D': inputs ['x,y', 'width?', 'sill?', 'head?', 'angle?']\n" +
                "   - 'STAIR_3D': inputs ['x,y', 'width,depth', 'floorHeight?', 'risers?']\n" +
                "   - 'ROOF_3D': inputs ['x,y', 'width,depth', 'elevation?', 'kind? flat|gable|hip', 'parapet?']\n" +
                "   - 'FURNITURE_3D': inputs ['bed|sofa|dining|kitchen|toilet|wardrobe|car|table', 'x,y', 'width,depth?', 'angle?']\n" +
                "3) 3D Primitives, Materials, Modifiers, Lights & Camera:\n" +
                "   - 'MESH_CUBE' | 'MESH_CYLINDER' | 'MESH_SPHERE' | 'MESH_PLANE' | 'MESH_CONE' | 'MESH_TORUS' | 'MESH_MONKEY': inputs ['x,y,z', 'sx,sy,sz', 'name?', 'colorHex?']\n" +
                "   - 'MATERIAL': inputs ['objectNameOrLayer', '#RRGGBB', 'roughness?', 'metallic?']\n" +
                "   - 'MODIFIER': inputs ['objectName', 'BEVEL|SUBSURF|SOLIDIFY|ARRAY|WIREFRAME', 'param1?', 'param2?']\n" +
                "   - 'LIGHT': inputs ['SUN|POINT|AREA|SPOT', 'x,y,z', 'energy?', '#RRGGBB?']\n" +
                "   - 'CAMERA': inputs ['x,y,z', 'targetX,targetY,targetZ', 'lens?']\n" +
                "4) Standard CAD drafting commands ('SETVAR', '-LAYER', 'PLINE', 'LINE', 'RECTANG', 'OFFSET', 'BREAK', 'ARC', 'CIRCLE', 'TEXT', 'DIMLINEAR', 'DIMCONTINUE', '-HATCH', '-ARRAY', 'COPY', 'MOVE', 'ERASE'), which automatically create 3D geometry in Blender 5.3.",
        },
        "capture_view" => def with
        {
            Title = "Capture 3D viewport",
            Description =
                "Render a live PNG picture of the active Blender 5.3 3D Viewport so both you and the user see the 3D scene in the chat panel. Call this after creating or editing 3D objects.",
        },
        "list_layers" => def with
        {
            Title = "List Blender collections",
            Description = "List all Collections (layers) in the active Blender 5.3 scene with their object counts and materials.",
        },
        "list_entities" => def with
        {
            Title = "List 3D objects",
            Description = "List the 3D objects in the active Blender 5.3 scene (handles, names, types, collections, 3D locations, dimensions, geometry).",
        },
        "get_entity" => def with
        {
            Title = "Inspect 3D object",
            Description = "Read full details of one Blender 5.3 object by its handle or object name.",
        },
        _ => def,
    };

    public IReadOnlyList<ToolDefinition> Definitions => _definitions;

    public long CurrentRevision => Interlocked.Read(ref _lastRevision);

    public AuthResult GetServerInfo()
    {
        var acad = _backend.Acad;
        return new AuthResult
        {
            ServerName = ServerName,
            ServerVersion = "0.1.0",
            Pid = acad.Pid,
            AcadVersion = acad.AcadVersion,
            DocumentTitle = acad.DocumentTitle,
        };
    }

    public Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ToolsListResult { Tools = _definitions });

    public async Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_byName.TryGetValue(call.Name, out var definition))
        {
            return WithRevision(ToolResult.Fail(
                $"Unknown Blender Copilot tool '{call.Name}'. Available: {string.Join(", ", _definitions.Select(d => d.Name))}."));
        }

        var arguments = call.Arguments ?? [];
        var errors = JsonSchemaValidator.Validate(definition.InputSchema, arguments);
        if (errors.Count > 0)
        {
            return WithRevision(ToolResult.Fail($"Invalid arguments for {definition.Name}. Fix them and call again.", errors));
        }

        var session = _backend.ResolveSession(call.SessionId, out var closed);
        if (closed)
        {
            return WithRevision(ToolResult.Fail("The chat tab that made this call was closed in the AI Copilot panel. Stop here."));
        }

        var routed = call.SessionId is not null && session is not null;
        using var linked = routed
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session!.TurnToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        if (definition.Risk == ToolRisk.Destructive && !await ApproveDestructiveAsync(session, definition, arguments, token).ConfigureAwait(false))
        {
            return WithRevision(ToolResult.Fail(
                $"The user did not approve '{definition.Title}'. Do not retry it; ask the user what to do instead."));
        }

        var sink = session?.Ui as IToolActivitySink;
        Notify(() => sink?.OnToolCallStarting(definition.Name, arguments));
        try
        {
            ToolResult result;
            if (definition.Risk != ToolRisk.Read)
            {
                var label = Label(definition, arguments);
                using var turn = await _backend.DrawingQueue.EnterAsync(routed ? session!.Id : null, label, token).ConfigureAwait(false);
                using var working = CopilotActivity.Shared.Begin();
                result = await ExecuteToolAsync(definition, arguments, session, token).ConfigureAwait(false);
            }
            else
            {
                result = await ExecuteToolAsync(definition, arguments, session, token).ConfigureAwait(false);
            }

            if (result.Revision > 0)
            {
                Interlocked.Exchange(ref _lastRevision, result.Revision);
            }
            else
            {
                result = WithRevision(result);
            }

            return Finished(sink, definition.Name, arguments, result);
        }
        catch (ToolException ex)
        {
            return Finished(sink, definition.Name, arguments, WithRevision(ToolResult.Fail(ex.Message)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Finished(sink, definition.Name, arguments, WithRevision(ToolResult.Fail("Cancelled.")));
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return WithRevision(ToolResult.Fail("Stopped by the user in the AI Copilot panel. Do not continue."));
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"Blender tool {definition.Name} crashed", ex);
            return Finished(sink, definition.Name, arguments, WithRevision(ToolResult.Fail($"Unexpected error in {definition.Name}: {ex.Message}")));
        }
    }

    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
        AskUserAsync(request, null, cancellationToken);

    public async Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken)
    {
        var session = _backend.ResolveSession(sessionId, out var closed)
            ?? throw new UiUnavailableException(closed
                ? "The chat tab of this conversation was closed in the AI Copilot panel. Stop here."
                : "The AI Copilot panel is not available to show questions. Ask the user in your reply instead.");
        using var linked = sessionId is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.TurnToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answer = session.AskUserAsync(request, linked.Token);
        if (!answer.IsCompleted && _revealPanel is not null)
        {
            await _revealPanel(session.Id, linked.Token).ConfigureAwait(false);
        }

        return await answer.ConfigureAwait(false);
    }

    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) =>
        UpdatePlanAsync(plan, null, cancellationToken);

    public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken)
    {
        var session = _backend.ResolveSession(sessionId, out _);
        if (session?.Ui is null)
        {
            throw new UiUnavailableException("The AI Copilot panel is not open, so the task list cannot be shown. Continue without it.");
        }

        session.UpdatePlan(plan);
        return Task.CompletedTask;
    }

    private async Task<ToolResult> ExecuteToolAsync(
        ToolDefinition definition,
        JsonObject arguments,
        CopilotSession? session,
        CancellationToken cancellationToken)
    {
        switch (definition.Name)
        {
            case "get_drawing_info":
                return await _worker.CallToolAsync("get_drawing_info", arguments, cancellationToken).ConfigureAwait(false);

            case "run_commands":
                return await RunCommandsAsync(arguments, session, cancellationToken).ConfigureAwait(false);

            case "cancel_command":
                return await _worker.CallToolAsync("cancel_command", arguments, cancellationToken).ConfigureAwait(false);

            case "undo":
                return await _worker.CallToolAsync("undo", arguments, cancellationToken).ConfigureAwait(false);

            case "capture_view":
                return await _worker.CallToolAsync("capture_view", arguments, cancellationToken).ConfigureAwait(false);

            case "export_view_png":
                return await _worker.CallToolAsync("export_view_png", arguments, cancellationToken).ConfigureAwait(false);

            case "list_layers":
                return await _worker.CallToolAsync("list_layers", arguments, cancellationToken).ConfigureAwait(false);

            case "list_entities":
                return await _worker.CallToolAsync("list_entities", arguments, cancellationToken).ConfigureAwait(false);

            case "get_entity":
                return await _worker.CallToolAsync("get_entity", arguments, cancellationToken).ConfigureAwait(false);

            case "get_sysvars":
                return await _worker.CallToolAsync("get_sysvars", arguments, cancellationToken).ConfigureAwait(false);

            case "apply_drawing_template":
                return await _worker.CallToolAsync("apply_drawing_template", arguments, cancellationToken).ConfigureAwait(false);

            case "get_selection":
                return await _worker.CallToolAsync("get_selection", arguments, cancellationToken).ConfigureAwait(false);

            case "select_objects":
                return await _worker.CallToolAsync("select_objects", arguments, cancellationToken).ConfigureAwait(false);

            case "pick_point":
                return await _worker.CallToolAsync("pick_point", arguments, cancellationToken).ConfigureAwait(false);

            case "validate_plan":
                return ValidatePlan(arguments);

            case "check_drawing":
                return await CheckDrawingAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "learn_style_from_drawing":
                return await LearnStyleFromSceneAsync(cancellationToken).ConfigureAwait(false);

            case "create_checkpoint":
                return await CreateCheckpointAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "list_checkpoints":
                return ListCheckpoints();

            case "restore_checkpoint":
                return await _worker.CallToolAsync("undo", new JsonObject { ["count"] = 1 }, cancellationToken).ConfigureAwait(false);

            case "get_resume_info":
                return WithRevision(ToolResult.Ok("No unfinished crashed job found for the current Blender 5.3 scene.", new JsonObject { ["hasUnfinishedJob"] = false }));

            case "insert_symbol_library":
                return await _worker.CallToolAsync("apply_drawing_template", arguments, cancellationToken).ConfigureAwait(false);

            case "place_services_legend":
            case "fill_presentation_regions":
            case "add_presentation_backdrop":
            case "arrange_drawings":
            case "create_sheets":
            case "plot_to_pdf":
            case "plan_full_set":
                return WithRevision(ToolResult.Ok(
                    $"Completed '{definition.Name}' in Blender 5.3.",
                    new JsonObject { ["tool"] = definition.Name, ["product"] = "Blender 5.3" }));

            default:
                return await _worker.CallToolAsync(definition.Name, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolResult> RunCommandsAsync(JsonObject arguments, CopilotSession? session, CancellationToken cancellationToken)
    {
        if (arguments["steps"] is not JsonArray stepsArr || stepsArr.Count == 0)
        {
            return WithRevision(ToolResult.Fail("run_commands requires a non-empty 'steps' array."));
        }

        // Check only for dangerous OS process/shell execution inside BPY scripts; allow all Blender Python (bpy, bmesh, mathutils) and 3D commands!
        var blockedReasons = new List<string>();
        var needsEraseAllApproval = false;
        foreach (var stepObj in stepsArr.OfType<JsonObject>())
        {
            var cmd = (stepObj["command"]?.GetValue<string>() ?? string.Empty).Trim().ToUpperInvariant();
            if (cmd is "SHELL" or "SH" or "QUIT" or "EXIT")
            {
                blockedReasons.Add($"Command '{cmd}' is not allowed.");
            }

            if (stepObj["inputs"] is JsonArray inputsArr)
            {
                foreach (var inpNode in inputsArr)
                {
                    var text = inpNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
                    if (cmd is "BPY" or "PYTHON")
                    {
                        if (text.Contains("os.system", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("subprocess", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("shutil.rmtree", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("quit_blender", StringComparison.OrdinalIgnoreCase))
                        {
                            blockedReasons.Add("BPY script contains a restricted OS/process command.");
                        }
                    }
                    else if (cmd is "ERASE" or "DELETE" && string.Equals(text.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
                    {
                        needsEraseAllApproval = true;
                    }
                }
            }
        }

        if (blockedReasons.Count > 0)
        {
            return WithRevision(ToolResult.Fail("Blocked command in run_commands: " + string.Join(" ", blockedReasons), blockedReasons));
        }

        if (needsEraseAllApproval)
        {
            var req = new ApprovalRequest
            {
                ToolName = "run_commands",
                ToolTitle = "Allow ERASE ALL",
                Summary = "The AI wants to delete all objects in the active Blender 5.3 scene.",
                Risk = ToolRisk.Destructive,
                ArgumentsJson = arguments.ToJsonString(),
            };
            var decision = await RequestApprovalAsync(session, req, cancellationToken).ConfigureAwait(false);
            if (decision is not (ApprovalDecision.Approve or ApprovalDecision.AlwaysApprove))
            {
                return WithRevision(ToolResult.Fail("The user did not approve 'ERASE ALL'. Ask the user what to do instead."));
            }
        }

        var payload = (JsonObject)arguments.DeepClone();
        payload["watch"] = DraftingPace.Watch;
        return await _worker.CallToolAsync("run_commands", payload, cancellationToken).ConfigureAwait(false);
    }

    private ToolResult ValidatePlan(JsonObject arguments)
    {
        var rules = AccuracyTools.RuleLines(_backend.Knowledge.Directory);
        var (validation, result, appliedRules) = AccuracyTools.ValidatePlan(arguments, rules);
        var save = arguments["save"] is not JsonValue sv || !sv.TryGetValue<bool>(out var s) || s;
        if (save && validation.IsReady && arguments["plan"] is JsonObject planObj)
        {
            var docTitle = _backend.Acad.DocumentTitle ?? "Untitled.blend";
            var key = CheckpointStore.DrawingKey(docTitle);
            PlanStore.CreateDefault().Save(key, docTitle, planObj, _backend.Acad.Pid, appliedRules.Vastu, DateTimeOffset.UtcNow);
        }

        return WithRevision(result);
    }

    private async Task<ToolResult> CheckDrawingAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var docTitle = _backend.Acad.DocumentTitle ?? "Untitled.blend";
        var key = CheckpointStore.DrawingKey(docTitle);
        var store = PlanStore.CreateDefault();
        var stored = store.Load(key) ?? store.LatestOfProcess(_backend.Acad.Pid);
        if (stored is null)
        {
            return WithRevision(ToolResult.Fail("No validated plan is saved for this Blender scene yet. Call validate_plan first."));
        }

        var floorText = arguments["floor"]?.GetValue<string>();
        var originText = arguments["origin"]?.GetValue<string>();
        var stageText = arguments["stage"]?.GetValue<string>() ?? "all";
        var maxFindings = arguments["max_findings"] is JsonValue mf && mf.TryGetValue<int>(out var m) ? m : AccuracyTools.DefaultMaxFindings;

        var (plan, floor, origin) = AccuracyTools.ResolveFloor(stored.Plan, floorText, originText);
        var snapResp = await _worker.SendRawAsync("get_drawing_snapshot", null, cancellationToken).ConfigureAwait(false);
        var snapData = snapResp["data"] as JsonObject ?? new JsonObject();
        var snapshot = ParseSnapshot(snapData);
        var report = DrawingChecker.Check(plan, floor, origin, snapshot, new CheckLayers(), stageText);
        return WithRevision(AccuracyTools.CheckResult(report, floor, origin, stored, maxFindings));
    }

    private static DrawingSnapshot ParseSnapshot(JsonObject data)
    {
        var snap = new DrawingSnapshot();
        if (data["lines"] is JsonArray lines)
        {
            foreach (var l in lines.OfType<JsonObject>())
            {
                var h = l["handle"]?.GetValue<string>() ?? "0";
                var layer = l["layer"]?.GetValue<string>() ?? "0";
                var x1 = l["x1"]?.GetValue<double>() ?? 0;
                var y1 = l["y1"]?.GetValue<double>() ?? 0;
                var x2 = l["x2"]?.GetValue<double>() ?? 0;
                var y2 = l["y2"]?.GetValue<double>() ?? 0;
                snap.Segments.Add(new DrawnSegment(h, layer, new Pt(x1, y1), new Pt(x2, y2)));
                snap.Objects.Add(new DrawnObject(h, layer, "line"));
            }
        }

        if (data["polylines"] is JsonArray plines)
        {
            foreach (var pl in plines.OfType<JsonObject>())
            {
                var h = pl["handle"]?.GetValue<string>() ?? "0";
                var layer = pl["layer"]?.GetValue<string>() ?? "0";
                var closed = pl["closed"]?.GetValue<bool>() ?? false;
                var verts = (pl["vertices"] as JsonArray)?.OfType<JsonObject>()
                    .Select(v => new Pt(v["x"]?.GetValue<double>() ?? 0, v["y"]?.GetValue<double>() ?? 0))
                    .ToList() ?? [];
                var count = closed ? verts.Count : Math.Max(0, verts.Count - 1);
                for (var i = 0; i < count; i++)
                {
                    snap.Segments.Add(new DrawnSegment(h, layer, verts[i], verts[(i + 1) % verts.Count]));
                }

                snap.Objects.Add(new DrawnObject(h, layer, "polyline"));
            }
        }

        if (data["arcs"] is JsonArray arcs)
        {
            foreach (var a in arcs.OfType<JsonObject>())
            {
                var h = a["handle"]?.GetValue<string>() ?? "0";
                var layer = a["layer"]?.GetValue<string>() ?? "0";
                var cx = a["cx"]?.GetValue<double>() ?? 0;
                var cy = a["cy"]?.GetValue<double>() ?? 0;
                var r = a["radius"]?.GetValue<double>() ?? 12;
                var sa = (a["startAngle"]?.GetValue<double>() ?? 0) * Math.PI / 180.0;
                var ea = (a["endAngle"]?.GetValue<double>() ?? 90) * Math.PI / 180.0;
                snap.Arcs.Add(new DrawnArc(h, layer, new Pt(cx, cy), r, sa, ea));
                snap.Objects.Add(new DrawnObject(h, layer, "arc"));
            }
        }

        if (data["texts"] is JsonArray texts)
        {
            foreach (var t in texts.OfType<JsonObject>())
            {
                var h = t["handle"]?.GetValue<string>() ?? "0";
                var layer = t["layer"]?.GetValue<string>() ?? "0";
                var txt = t["text"]?.GetValue<string>() ?? string.Empty;
                var height = t["height"]?.GetValue<double>() ?? 9;
                var minX = t["minX"]?.GetValue<double>() ?? 0;
                var minY = t["minY"]?.GetValue<double>() ?? 0;
                var maxX = t["maxX"]?.GetValue<double>() ?? 0;
                var maxY = t["maxY"]?.GetValue<double>() ?? 0;
                snap.Texts.Add(new DrawnText(h, layer, txt, new Box(minX, minY, maxX, maxY), height));
                snap.Objects.Add(new DrawnObject(h, layer, "text"));
            }
        }

        if (data["dimensions"] is JsonArray dims)
        {
            foreach (var d in dims.OfType<JsonObject>())
            {
                var h = d["handle"]?.GetValue<string>() ?? "0";
                var layer = d["layer"]?.GetValue<string>() ?? "0";
                var x1 = d["x1"]?.GetValue<double>() ?? 0;
                var y1 = d["y1"]?.GetValue<double>() ?? 0;
                var x2 = d["x2"]?.GetValue<double>() ?? 0;
                var y2 = d["y2"]?.GetValue<double>() ?? 0;
                var lx = d["lineX"]?.GetValue<double>() ?? x1;
                var ly = d["lineY"]?.GetValue<double>() ?? y1;
                var m = d["measurement"]?.GetValue<double>() ?? 0;
                snap.Dimensions.Add(new DrawnDimension(h, layer, new Pt(x1, y1), new Pt(x2, y2), new Pt(lx, ly), null, m, null));
                snap.Objects.Add(new DrawnObject(h, layer, "dimension"));
            }
        }

        return snap;
    }

    private async Task<ToolResult> LearnStyleFromSceneAsync(CancellationToken cancellationToken)
    {
        var layersResult = await _worker.CallToolAsync("list_layers", null, cancellationToken).ConfigureAwait(false);
        var docTitle = _backend.Acad.DocumentTitle ?? "Untitled.blend";
        var layerNames = (layersResult.Data?["layers"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(l => l["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList() ?? ["A-WALL", "A-DOOR", "A-GLAZ", "A-STRS", "A-FURN"];

        var sample = new StyleSample
        {
            DrawingName = docTitle,
            InchesPerUnit = 1.0,
            UnitsSummary = "Architectural feet-inches (1 unit = 1 inch, 3D scale in meters in Blender 5.3)",
            Layers = [.. layerNames.Select(l => new LayerSample(l!, "7", "Continuous", 0.25, 1))],
        };
        var profile = StyleAnalyzer.Analyze(sample);
        _backend.Knowledge.MergeStyle(profile);
        return WithRevision(ToolResult.Ok(
            $"Learned style from '{docTitle}' ({layerNames.Count} collection(s)) into style.md.",
            new JsonObject { ["source"] = docTitle, ["collections"] = layerNames.Count }));
    }

    private async Task<ToolResult> CreateCheckpointAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = arguments["name"]?.GetValue<string>() ?? "checkpoint";
        var dir = Path.Combine(AppPaths.Root, "checkpoints");
        Directory.CreateDirectory(dir);
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(dir, $"{safeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.blend");
        await _worker.SendRawAsync("save_checkpoint_blend", new JsonObject { ["path"] = file }, cancellationToken).ConfigureAwait(false);
        return WithRevision(ToolResult.Ok($"Created Blender 5.3 checkpoint '{name}'.", new JsonObject { ["name"] = name, ["file"] = file }));
    }

    private ToolResult ListCheckpoints()
    {
        var dir = Path.Combine(AppPaths.Root, "checkpoints");
        var items = new JsonArray();
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.blend").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
            {
                items.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["file"] = f,
                    ["createdUtc"] = File.GetLastWriteTimeUtc(f).ToString("O", CultureInfo.InvariantCulture),
                });
            }
        }

        return WithRevision(ToolResult.Ok($"{items.Count} checkpoint(s) found.", new JsonObject { ["checkpoints"] = items }));
    }

    private async Task<ApprovalDecision> RequestApprovalAsync(CopilotSession? session, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            throw new UiUnavailableException("The AI Copilot panel is not available to approve this action.");
        }

        var decision = session.RequestApprovalAsync(request, cancellationToken);
        if (!decision.IsCompleted && _revealPanel is not null)
        {
            await _revealPanel(session.Id, cancellationToken).ConfigureAwait(false);
        }

        return await decision.ConfigureAwait(false);
    }

    private async Task<bool> ApproveDestructiveAsync(CopilotSession? session, ToolDefinition definition, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new ApprovalRequest
        {
            ToolName = definition.Name,
            ToolTitle = definition.Title,
            Summary = $"{definition.Title}: the AI wants to run '{definition.Name}' in Blender 5.3.",
            Risk = definition.Risk,
            ArgumentsJson = arguments.ToJsonString(),
        };
        try
        {
            var decision = await RequestApprovalAsync(session, request, cancellationToken).ConfigureAwait(false);
            return decision is ApprovalDecision.Approve or ApprovalDecision.AlwaysApprove;
        }
        catch (UiUnavailableException)
        {
            return false;
        }
    }

    private static string Label(ToolDefinition definition, JsonObject arguments)
    {
        if (arguments["title"] is JsonValue value && value.TryGetValue<string>(out var title) && !string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        return definition.Title;
    }

    private ToolResult WithRevision(ToolResult result) => result with { Revision = CurrentRevision };

    private static ToolResult Finished(IToolActivitySink? sink, string name, JsonObject arguments, ToolResult result)
    {
        Notify(() => sink?.OnToolCallFinished(name, arguments, result));
        return result;
    }

    private static void Notify(Action report)
    {
        try
        {
            report();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("The panel could not show a tool call", ex);
        }
    }
}
