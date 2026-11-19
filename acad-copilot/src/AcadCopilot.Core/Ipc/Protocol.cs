using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Ipc;

/// <summary>
/// Method names of the bridge &lt;-&gt; add-in protocol: newline-delimited JSON-RPC 2.0 over the named pipe
/// \\.\pipe\AcadCopilot-&lt;acadPid&gt;. The client (bridge) must call <see cref="Auth"/> first.
/// </summary>
public static class RpcMethods
{
    /// <summary>Client -&gt; server. Params <see cref="AuthParams"/>, result <see cref="AuthResult"/>. Must be the first call.</summary>
    public const string Auth = "auth";

    /// <summary>Client -&gt; server. No params, result <see cref="ToolsListResult"/>.</summary>
    public const string ToolsList = "tools.list";

    /// <summary>Client -&gt; server. Params <see cref="ToolsCallParams"/>, result <see cref="ToolResult"/>.</summary>
    public const string ToolsCall = "tools.call";

    /// <summary>
    /// Client -&gt; server. Params <see cref="Interaction.QuestionRequest"/> plus an optional "sessionId" member
    /// (<see cref="RpcSessions"/>), result <see cref="Interaction.QuestionResponse"/>.
    /// </summary>
    public const string UiAskUser = "ui.askUser";

    /// <summary>
    /// Client -&gt; server. Params <see cref="Interaction.PlanUpdate"/> plus an optional "sessionId" member
    /// (<see cref="RpcSessions"/>), result {"ok": true}.
    /// </summary>
    public const string UiUpdatePlan = "ui.updatePlan";

    /// <summary>Either direction, notification. Params {"id": &lt;request id&gt;}: cancel that in-flight request.</summary>
    public const string CancelRequest = "$/cancelRequest";

    /// <summary>Server -&gt; client notification: the tool list changed (re-run tools.list). No params.</summary>
    public const string ToolsChanged = "notifications.toolsChanged";

    /// <summary>Server -&gt; client notification: active document changed. Params <see cref="DocumentChangedParams"/>.</summary>
    public const string DocumentChanged = "notifications.documentChanged";
}

/// <summary>JSON-RPC error codes used on the pipe.</summary>
public static class RpcErrorCodes
{
    /// <summary>Invalid JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>Not a valid JSON-RPC message.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>Unknown method.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Bad params.</summary>
    public const int InvalidParams = -32602;

    /// <summary>Unexpected server exception.</summary>
    public const int InternalError = -32603;

    /// <summary>auth missing or wrong token.</summary>
    public const int Unauthorized = -32001;

    /// <summary>The panel UI is not available (ui.* methods).</summary>
    public const int UiUnavailable = -32003;

    /// <summary>AutoCAD did not run the request in time (modal dialog open, command active...).</summary>
    public const int AcadBusy = -32004;

    /// <summary>Request was cancelled.</summary>
    public const int RequestCancelled = -32800;
}

/// <summary>A JSON-RPC error (received from or to be sent to the peer).</summary>
public sealed class JsonRpcException : Exception
{
    /// <summary>Creates the error.</summary>
    public JsonRpcException(int code, string message, JsonNode? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>JSON-RPC error code (<see cref="RpcErrorCodes"/>).</summary>
    public int Code { get; }

    /// <summary>Optional structured error data (the JSON-RPC "data" member).</summary>
    public JsonNode? ErrorData { get; }
}

/// <summary>
/// Chat sessions on the pipe. Every CLI turn of a panel chat tab starts its own bridge with
/// <c>--session &lt;id&gt;</c>; the bridge sends that id in <see cref="AuthParams.SessionId"/>, in
/// <see cref="ToolsCallParams.SessionId"/> and as the "sessionId" member of the ui.* params, so the add-in can route
/// question cards, plan updates and approvals to the right tab and act on that tab's drawing. No id = a client outside
/// the panel (the user's own Claude Desktop / terminal session): the active tab and the active drawing.
/// </summary>
public static class RpcSessions
{
    /// <summary>Longest accepted session id.</summary>
    public const int MaxLength = 64;

    /// <summary>JSON member carrying the session id in ui.* params.</summary>
    public const string Member = "sessionId";

    /// <summary>True for a usable session id: 1-64 characters, letters, digits, '-' and '_' only.</summary>
    public static bool IsValid(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) &&
        sessionId.Length <= MaxLength &&
        sessionId.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>The "sessionId" member of request params, or null when absent or not a string.</summary>
    /// <exception cref="JsonRpcException">The member is present but not a valid id.</exception>
    public static string? Read(JsonNode? parameters)
    {
        if (parameters is not JsonObject obj || !obj.TryGetPropertyValue(Member, out var node) || node is null)
        {
            return null;
        }

        var value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : null;
        return Check(value);
    }

    /// <summary>Returns <paramref name="sessionId"/> when it is null or valid; throws an invalid-params error otherwise.</summary>
    /// <exception cref="JsonRpcException">Not a valid id.</exception>
    public static string? Check(string? sessionId) =>
        sessionId is null || IsValid(sessionId)
            ? sessionId
            : throw new JsonRpcException(RpcErrorCodes.InvalidParams, $"Invalid sessionId: use 1-{MaxLength} letters, digits, '-' or '_'.");
}

/// <summary>Params of <see cref="RpcMethods.Auth"/>.</summary>
/// <param name="Token">The token from the discovery file.</param>
/// <param name="ClientName">Client identification ("AcadCopilotMcp").</param>
/// <param name="ClientVersion">Client version.</param>
/// <param name="SessionId">Panel chat session this connection works for (bridge --session), or null.</param>
public sealed record AuthParams(string Token, string ClientName, string? ClientVersion = null, string? SessionId = null);

/// <summary>Result of <see cref="RpcMethods.Auth"/>.</summary>
public sealed record AuthResult
{
    /// <summary>Server name ("AcadCopilot.Plugin").</summary>
    public required string ServerName { get; init; }

    /// <summary>Add-in version.</summary>
    public required string ServerVersion { get; init; }

    /// <summary>AutoCAD process id.</summary>
    public int Pid { get; init; }

    /// <summary>AutoCAD version number ("2027").</summary>
    public string? AcadVersion { get; init; }

    /// <summary>Title of the active document, if any.</summary>
    public string? DocumentTitle { get; init; }
}

/// <summary>Result of <see cref="RpcMethods.ToolsList"/>.</summary>
public sealed record ToolsListResult
{
    /// <summary>All AutoCAD tools.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
}

/// <summary>Params of <see cref="RpcMethods.ToolsCall"/>.</summary>
public sealed record ToolsCallParams
{
    /// <summary>Tool name.</summary>
    public required string Name { get; init; }

    /// <summary>Arguments object (validated against the tool's schema by the add-in).</summary>
    public JsonObject? Arguments { get; init; }

    /// <summary>Caller-chosen id for logs/correlation.</summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Panel chat session (tab) the call belongs to (<see cref="RpcSessions"/>); null = not a panel tab. The pipe server
    /// fills it from the connection's <see cref="AuthParams.SessionId"/> when the request has none.
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>Params of <see cref="RpcMethods.DocumentChanged"/>.</summary>
public sealed record DocumentChangedParams
{
    /// <summary>Title of the active document, or null when none is open.</summary>
    public string? DocumentTitle { get; init; }

    /// <summary>Current model revision.</summary>
    public long Revision { get; init; }
}
