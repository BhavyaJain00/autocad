using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core;
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
    private readonly Lock _cacheGate = new();
    private PipeClient? _cachedFor;
    private IReadOnlyList<ToolDefinition>? _cachedTools;
    private long _callCounter;
    private int _lastListIncludedAcad = -1;

    /// <summary>Creates the service.</summary>
    /// <param name="link">Connection to AutoCAD.</param>
    /// <param name="panel">Expose ask_user and update_plan (bridge launched by the panel).</param>
    /// <param name="knowledge">The knowledge base of the learning tools (default: <see cref="AppPaths.Knowledge"/>).</param>
    public BridgeToolService(AcadLink link, bool panel, KnowledgeStore? knowledge = null)
    {
        _link = link;
        _panel = panel;
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
            tools.AddRange(await GetAcadToolsAsync(client, cancellationToken).ConfigureAwait(false));
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

        tools.AddRange(KnowledgeTools.BridgeDefinitions());
        return tools;
    }

    /// <summary>Runs one tool call. Never throws for tool or connection failures (they become IsError results).</summary>
    public async Task<BridgeCallResult> CallToolAsync(string name, JsonObject? arguments, CancellationToken cancellationToken)
    {
        try
        {
            switch (name)
            {
                case BridgeTools.AcadStatus:
                    return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                case BridgeTools.AskUser when _panel:
                    return await AskUserAsync(arguments, cancellationToken).ConfigureAwait(false);
                case BridgeTools.UpdatePlan when _panel:
                    return await UpdatePlanAsync(arguments, cancellationToken).ConfigureAwait(false);
                case var learning when KnowledgeTools.BridgeNames.Contains(learning):
                    return await Task.Run(() => ToCallResult(KnowledgeTools.Execute(_knowledge, learning, arguments)), cancellationToken).ConfigureAwait(false);
                default:
                    var client = await _link.GetClientAsync(cancellationToken).ConfigureAwait(false);
                    var callId = "bridge-" + Interlocked.Increment(ref _callCounter).ToString(CultureInfo.InvariantCulture);
                    var result = await client.CallToolAsync(name, arguments, callId, cancellationToken).ConfigureAwait(false);
                    return ToCallResult(result);
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
