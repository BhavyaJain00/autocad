using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.McpBridge;

/// <summary>What the MCP client gets for one tool call: a text part, then any images.</summary>
/// <param name="Text">Result text (JSON for AutoCAD tools, with images described but never their base64 data).</param>
/// <param name="IsError">True when the call failed (MCP isError).</param>
public sealed record BridgeCallResult(string Text, bool IsError)
{
    /// <summary>Validated images sent after the text as MCP image content (empty for most tools).</summary>
    public IReadOnlyList<ToolImage> Images { get; init; } = [];
}

/// <summary>
/// The bridge's tool logic, independent of the MCP SDK types: lists AutoCAD tools (cached per connection) plus the
/// bridge's own tools, and routes calls to the add-in (tools.call), to the panel (ui.askUser / ui.updatePlan) or to the
/// local knowledge files (the learning tools of <see cref="KnowledgeTools"/>, which need neither AutoCAD nor a turn at
/// its command line, so they are answered here, also while AutoCAD is busy or closed).
/// </summary>
public sealed class BridgeToolService
{
    /// <summary>Largest image (decoded bytes) forwarded to the MCP client; bigger ones are dropped with a warning.</summary>
    public const int MaxImageBytes = 4 * 1024 * 1024;

    private readonly AcadLink _link;
    private readonly bool _panel;
    private readonly KnowledgeStore _knowledge;
    private readonly bool _readOnly;
    private readonly ResponseCache _cache;
    private readonly FifoGate _acadGate = new();
    private long _failedStages;
    private readonly Lock _cacheGate = new();
    private PipeClient? _cachedFor;
    private IReadOnlyList<ToolDefinition>? _cachedTools;
    private long _callCounter;
    private int _lastListIncludedAcad = -1;

    /// <summary>Creates the service.</summary>
    /// <param name="link">Connection to AutoCAD.</param>
    /// <param name="panel">Expose ask_user and update_plan (bridge launched by the panel).</param>
    /// <param name="knowledge">The knowledge base of the learning tools (default: <see cref="AppPaths.Knowledge"/>).</param>
    /// <param name="readOnly">Only list and run tools that read (the second-opinion reviewer, bridge flag --readonly).</param>
    /// <param name="cache">Cache of read results (default: a new one per service).</param>
    public BridgeToolService(AcadLink link, bool panel, KnowledgeStore? knowledge = null, bool readOnly = false, ResponseCache? cache = null)
    {
        _link = link;
        _panel = panel && !readOnly;
        _readOnly = readOnly;
        _cache = cache ?? new ResponseCache();
        _knowledge = knowledge ?? new KnowledgeStore(AppPaths.Knowledge);
        _link.ToolsChanged += _ => InvalidateCache();
    }

    /// <summary>
    /// Whether the last tools/list answer contained AutoCAD tools (null before the first list). Used to decide if the
    /// MCP client must be told that the list changed.
    /// </summary>
    public bool? LastListIncludedAcad => Volatile.Read(ref _lastListIncludedAcad) switch { 0 => false, 1 => true, _ => null };

    /// <summary>True when a change of the given kind makes the client's last tool list stale.</summary>
    public bool IsListStale(ToolsChangeReason reason) => reason switch
    {
        ToolsChangeReason.Connected => LastListIncludedAcad == false,
        ToolsChangeReason.Disconnected => LastListIncludedAcad == true,
        _ => true,
    };

    /// <summary>AutoCAD tools (when reachable) followed by the bridge's own tools (status, panel tools, learning tools).</summary>
    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<ToolDefinition>();
        try
        {
            var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var acadTools = await GetAcadToolsAsync(client, cancellationToken).ConfigureAwait(false);
            tools.AddRange(_readOnly ? acadTools.Where(t => t.Risk == ToolRisk.Read) : acadTools);
        }
        catch (Exception ex) when (ex is AcadUnavailableException or IOException or JsonRpcException)
        {
            CopilotLog.Info("tools/list without AutoCAD tools: " + ex.Message);
            _link.StartWatching();
        }

        Volatile.Write(ref _lastListIncludedAcad, tools.Count > 0 ? 1 : 0);
        tools.Add(BridgeTools.AcadStatusDefinition());
        if (_panel)
        {
            tools.Add(BridgeTools.AskUserDefinition());
            tools.Add(BridgeTools.UpdatePlanDefinition());
        }

        tools.AddRange(_readOnly ? KnowledgeTools.BridgeDefinitions().Where(t => t.Risk == ToolRisk.Read) : KnowledgeTools.BridgeDefinitions());
        return tools;
    }

    /// <summary>Runs one tool call. Never throws for tool or connection failures (they become IsError results).</summary>
    public async Task<BridgeCallResult> CallToolAsync(string name, JsonObject? arguments, CancellationToken cancellationToken)
    {
        try
        {
            if (_readOnly && await RefuseInReadOnlyAsync(name, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            switch (name)
            {
                case BridgeTools.AcadStatus:
                    return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                case BridgeTools.AskUser when _panel:
                    return await AskUserAsync(arguments, cancellationToken).ConfigureAwait(false);
                case BridgeTools.UpdatePlan when _panel:
                    return await UpdatePlanAsync(arguments, cancellationToken).ConfigureAwait(false);
                case KnowledgeTools.RecallKnowledge:
                    return await Task.Run(() => RecallKnowledge(arguments), cancellationToken).ConfigureAwait(false);
                case var learning when KnowledgeTools.BridgeNames.Contains(learning):
                    return await Task.Run(() => ToCallResult(KnowledgeTools.Execute(_knowledge, learning, arguments)), cancellationToken).ConfigureAwait(false);
                default:
                    return await CallAcadAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AcadUnavailableException ex)
        {
            return new BridgeCallResult(ex.Message, IsError: true);
        }
        catch (JsonRpcException ex) when (ex.Code == RpcErrorCodes.UiUnavailable)
        {
            return new BridgeCallResult(ex.Message, IsError: true);
        }
        catch (JsonRpcException ex)
        {
            return new BridgeCallResult($"AutoCAD add-in error ({ex.Code}): {ex.Message}", IsError: true);
        }
        catch (IOException ex)
        {
            return new BridgeCallResult($"The connection to AutoCAD was lost during '{name}' ({ex.Message}). AutoCAD may have closed; check the model before retrying.", IsError: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"Tool '{name}' failed in the bridge", ex);
            return new BridgeCallResult($"Unexpected bridge error in '{name}': {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Converts an add-in result for the MCP client: the JSON text describes the images without their data, and the
    /// images that an LLM can take (supported type, valid base64, at most <see cref="MaxImageBytes"/>) travel separately.
    /// Images that cannot be sent are dropped and named in the result's warnings. The error flag is kept.
    /// </summary>
    public static BridgeCallResult ToCallResult(ToolResult result)
    {
        if (result.Images.Count == 0)
        {
            return new BridgeCallResult(result.ToJson(), !result.Success);
        }

        var images = new List<ToolImage>(result.Images.Count);
        var dropped = new List<string>();
        for (var i = 0; i < result.Images.Count; i++)
        {
            var image = result.Images[i];
            var problem = image.Validate()
                ?? (image.ByteLength > MaxImageBytes ? $"it is too large ({image.ByteLength / 1024} KB, limit {MaxImageBytes / 1024} KB)" : null);
            if (problem is null)
            {
                images.Add(image);
            }
            else
            {
                dropped.Add($"Image {i + 1} could not be sent: {problem}.");
                CopilotLog.Warn($"Dropped image {i + 1} of a tool result: {problem}.");
            }
        }

        var forClient = dropped.Count == 0 ? result : result with { Images = images, Warnings = [.. result.Warnings, .. dropped] };
        return new BridgeCallResult(forClient.ToTextJson(), !result.Success) { Images = images };
    }

    /// <summary>The cache of read results (tests look at it).</summary>
    public ResponseCache Cache => _cache;

    /// <summary>True when this bridge only lists and runs tools that read (--readonly).</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Name of the call that only fetches the drawing revision (the add-in answers an unknown tool at once, with the revision).</summary>
    internal const string RevisionProbe = "__acad_copilot_revision";

    /// <summary>The bridge's own tools that only read (allowed with --readonly).</summary>
    private static readonly HashSet<string> ReadOnlyBridgeTools = new(StringComparer.Ordinal)
    {
        BridgeTools.AcadStatus, KnowledgeTools.RecallKnowledge, KnowledgeTools.ListRecordings, KnowledgeTools.ReadRecording,
    };

    /// <summary>The refusal for a tool a read-only bridge may not run, or null when it reads.</summary>
    private async Task<BridgeCallResult?> RefuseInReadOnlyAsync(string name, CancellationToken cancellationToken)
    {
        if (ReadOnlyBridgeTools.Contains(name))
        {
            return null;
        }

        if (!BridgeTools.ReservedNames.Contains(name))
        {
            var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var tools = await GetAcadToolsAsync(client, cancellationToken).ConfigureAwait(false);
            if (tools.FirstOrDefault(t => t.Name == name) is { Risk: ToolRisk.Read })
            {
                return null;
            }
        }

        CopilotLog.Info($"Read-only bridge refused '{name}'.");
        return new BridgeCallResult(
            $"'{name}' is not available: this is a read-only review. You can only look at the drawing (get_drawing_info, list_layers, list_entities, get_entity, get_sysvars, capture_view, recall_knowledge). Report the problem as a finding instead.",
            IsError: true);
    }

    /// <summary>recall_knowledge: the user's knowledge plus the matching built-in guides, cached until a knowledge file changes.</summary>
    private BridgeCallResult RecallKnowledge(JsonObject? arguments)
    {
        var key = ResponseCache.Key(KnowledgeTools.RecallKnowledge, arguments);
        var stamp = KnowledgeStamp();
        if (_cache.TryGet(key, stamp, out var cached))
        {
            return ResponseCache.MarkCached(cached);
        }

        var query = arguments?["query"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var result = ToCallResult(DraftingGuides.AddTo(KnowledgeTools.Execute(_knowledge, KnowledgeTools.RecallKnowledge, arguments), query));
        _cache.Store(key, stamp, result);
        return result;
    }

    /// <summary>A stamp that changes whenever a knowledge file is added, removed or written.</summary>
    private long KnowledgeStamp()
    {
        try
        {
            var directory = new DirectoryInfo(_knowledge.Directory);
            if (!directory.Exists)
            {
                return 0;
            }

            long stamp = 17;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                stamp = unchecked((stamp * 31) + file.LastWriteTimeUtc.Ticks + file.Length + StringComparer.OrdinalIgnoreCase.GetHashCode(file.FullName));
            }

            return stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow.Ticks; // unknown: never a cache hit
        }
    }

    /// <summary>
    /// An AutoCAD tool call. All of this bridge's AutoCAD calls pass a FIFO gate, so calls the CLI sends together (the next
    /// run_commands stage queued behind the current one) reach AutoCAD in order, one at a time; a run_commands that waited
    /// behind a run_commands that failed is skipped, so a queued stage never builds on a broken one. Read results of
    /// <see cref="ResponseCache.DrawingTools"/> are served from the cache while the drawing revision is unchanged; every
    /// other call clears those entries.
    /// </summary>
    private async Task<BridgeCallResult> CallAcadAsync(string name, JsonObject? arguments, CancellationToken cancellationToken)
    {
        var failedBefore = Interlocked.Read(ref _failedStages);
        var isStage = name == AcadCopilot.Core.Drafting.DraftingTools.RunCommands;
        using var turn = await _acadGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (isStage && Interlocked.Read(ref _failedStages) > failedBefore)
        {
            Interlocked.Increment(ref _failedStages);
            var title = arguments?["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var t) ? $" '{t}'" : string.Empty;
            CopilotLog.Info($"Queued run_commands{title} skipped: a run_commands sent before it failed.");
            return new BridgeCallResult(
                ToolResult.Fail($"Skipped: this queued run_commands{title} was not typed because a run_commands call sent before it failed. Look at that result and the drawing (capture_view), fix the failed stage, then send this stage again.").ToJson(),
                IsError: true);
        }

        var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var cacheable = ResponseCache.DrawingTools.Contains(name);
        var key = cacheable ? ResponseCache.Key(name, arguments) : null;
        if (key is not null && _cache.Has(key))
        {
            var revision = await ProbeRevisionAsync(client, cancellationToken).ConfigureAwait(false);
            if (revision is { } current && _cache.TryGet(key, current, out var cached))
            {
                return ResponseCache.MarkCached(cached);
            }
        }

        if (!cacheable && !IsKnownReadTool(name))
        {
            // Anything else may change the drawing, the view or the current layer: start over.
            _cache.InvalidateDrawing();
        }

        var callId = "bridge-" + Interlocked.Increment(ref _callCounter).ToString(CultureInfo.InvariantCulture);
        ToolResult result;
        try
        {
            result = await client.CallToolAsync(name, arguments, callId, cancellationToken).ConfigureAwait(false);
        }
        catch when (isStage)
        {
            Interlocked.Increment(ref _failedStages);
            throw;
        }

        if (isStage && !result.Success)
        {
            Interlocked.Increment(ref _failedStages);
        }

        var callResult = ToCallResult(result);
        if (key is not null && result.Success)
        {
            _cache.Store(key, result.Revision, callResult);
        }

        return callResult;
    }

    private bool IsKnownReadTool(string name)
    {
        lock (_cacheGate)
        {
            return _cachedTools?.FirstOrDefault(t => t.Name == name) is { Risk: ToolRisk.Read };
        }
    }

    /// <summary>The add-in's drawing revision now, or null when it could not be read.</summary>
    private async Task<long?> ProbeRevisionAsync(PipeClient client, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await client.CallToolAsync(RevisionProbe, null, "bridge-revision", cancellationToken).ConfigureAwait(false);
            return probe.Revision;
        }
        catch (Exception ex) when (ex is IOException or JsonRpcException)
        {
            CopilotLog.Info("Revision probe failed: " + ex.Message);
            return null;
        }
    }

    private async Task<IReadOnlyList<ToolDefinition>> GetAcadToolsAsync(PipeClient client, CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (ReferenceEquals(_cachedFor, client) && _cachedTools is not null)
            {
                return _cachedTools;
            }
        }

        var tools = (await client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => !BridgeTools.ReservedNames.Contains(t.Name))
            .ToList();
        lock (_cacheGate)
        {
            _cachedFor = client;
            _cachedTools = tools;
        }

        return tools;
    }

    private void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedFor = null;
            _cachedTools = null;
        }
    }

    private async Task<BridgeCallResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        JsonObject status;
        try
        {
            var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var tools = await GetAcadToolsAsync(client, cancellationToken).ConfigureAwait(false);
            status = new JsonObject
            {
                ["connected"] = true,
                ["pid"] = client.Server.Pid,
                ["acadVersion"] = client.Server.AcadVersion,
                ["documentTitle"] = client.Server.DocumentTitle,
                ["addinVersion"] = client.Server.ServerVersion,
                ["acadToolCount"] = tools.Count,
                ["panelTools"] = _panel,
                ["chatSession"] = client.SessionId,
            };
        }
        catch (Exception ex) when (ex is AcadUnavailableException or IOException or JsonRpcException)
        {
            status = new JsonObject { ["connected"] = false, ["error"] = ex.Message, ["panelTools"] = _panel };
        }

        return new BridgeCallResult(status.ToJsonString(), IsError: false);
    }

    private async Task<BridgeCallResult> AskUserAsync(JsonObject? arguments, CancellationToken cancellationToken)
    {
        if (Invalid(BridgeTools.AskUserDefinition(), arguments) is { } invalid)
        {
            return invalid;
        }

        var request = CopilotJson.FromNode<QuestionRequest>(arguments);
        var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var response = await client.AskUserAsync(request, cancellationToken).ConfigureAwait(false);
        return new BridgeCallResult(CopilotJson.Serialize(response), IsError: false);
    }

    private async Task<BridgeCallResult> UpdatePlanAsync(JsonObject? arguments, CancellationToken cancellationToken)
    {
        if (Invalid(BridgeTools.UpdatePlanDefinition(), arguments) is { } invalid)
        {
            return invalid;
        }

        var plan = CopilotJson.FromNode<PlanUpdate>(arguments);
        var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.UpdatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        return new BridgeCallResult($"Plan updated: {plan.CompletedCount}/{plan.Tasks.Count} completed.", IsError: false);
    }

    private static BridgeCallResult? Invalid(ToolDefinition definition, JsonObject? arguments)
    {
        var errors = JsonSchemaValidator.Validate(definition.InputSchema, arguments ?? []);
        return errors.Count == 0
            ? null
            : new BridgeCallResult($"Invalid arguments for {definition.Name}: {string.Join(" ", errors)}", IsError: true);
    }

    /// <summary>Converts MCP argument elements to a JsonObject (null when there are none).</summary>
    public static JsonObject? ToJsonObject(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            result[key] = JsonSerializer.SerializeToNode(value);
        }

        return result;
    }
}
