using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Ipc;

/// <summary>
/// Named-pipe JSON-RPC client used by the MCP bridge to talk to the add-in inside AutoCAD.
/// Create with <c>ConnectAsync</c>, which also authenticates.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;

    private PipeClient(JsonRpcConnection connection, AcadInstanceInfo instance)
    {
        _connection = connection;
        Instance = instance;
    }

    /// <summary>Raised (on a background thread) for server notifications: (method, params).</summary>
    public event Action<string, JsonNode?>? Notification;

    /// <summary>The instance this client is connected to.</summary>
    public AcadInstanceInfo Instance { get; }

    /// <summary>Server info returned by auth.</summary>
    public AuthResult Server { get; private set; } = null!;

    /// <summary>True until the pipe closes.</summary>
    public bool IsConnected => _connection.IsOpen;

    /// <summary>Completes when the pipe closes.</summary>
    public Task Completion => _connection.Completion;

    /// <summary>Panel chat session this client works for (sent with auth and every call), or null.</summary>
    public string? SessionId { get; private init; }

    /// <summary>Connects to <paramref name="instance"/> and authenticates with its token.</summary>
    /// <exception cref="TimeoutException">The pipe did not accept the connection in time.</exception>
    /// <exception cref="JsonRpcException">Authentication failed.</exception>
    public static Task<PipeClient> ConnectAsync(AcadInstanceInfo instance, string clientName, TimeSpan timeout, CancellationToken cancellationToken) =>
        ConnectAsync(instance, clientName, timeout, sessionId: null, cancellationToken);

    /// <summary>
    /// Connects to <paramref name="instance"/> and authenticates with its token, for the panel chat session
    /// <paramref name="sessionId"/> (null = not a panel tab).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is not a valid id (<see cref="RpcSessions.IsValid"/>).</exception>
    /// <exception cref="TimeoutException">The pipe did not accept the connection in time.</exception>
    /// <exception cref="JsonRpcException">Authentication failed.</exception>
    public static async Task<PipeClient> ConnectAsync(AcadInstanceInfo instance, string clientName, TimeSpan timeout, string? sessionId, CancellationToken cancellationToken)
    {
        if (sessionId is not null && !RpcSessions.IsValid(sessionId))
        {
            throw new ArgumentException($"Invalid session id '{sessionId}'.", nameof(sessionId));
        }

        var stream = new NamedPipeClientStream(
            ".",
            PipeNames.ToShortName(instance.Pipe),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var connection = new JsonRpcConnection(stream, $"client:{instance.Pid}");
        var client = new PipeClient(connection, instance) { SessionId = sessionId };
        connection.NotificationHandler = (method, parameters) => client.Notification?.Invoke(method, parameters);
        connection.Start();
        try
        {
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authTimeout.CancelAfter(timeout);
            var version = typeof(PipeClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var result = await connection.SendRequestAsync(
                RpcMethods.Auth,
                CopilotJson.ToNode(new AuthParams(instance.Token, clientName, version, sessionId)),
                authTimeout.Token).ConfigureAwait(false);
            client.Server = CopilotJson.FromNode<AuthResult>(result);
            return client;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>tools.list.</summary>
    public async Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await _connection.SendRequestAsync(RpcMethods.ToolsList, null, cancellationToken).ConfigureAwait(false);
        return CopilotJson.FromNode<ToolsListResult>(result).Tools;
    }

    /// <summary>tools.call. Tool failures come back as a result with Success = false.</summary>
    public async Task<ToolResult> CallToolAsync(string name, JsonObject? arguments, string? callId, CancellationToken cancellationToken)
    {
        var call = new ToolsCallParams { Name = name, Arguments = arguments, CallId = callId, SessionId = SessionId };
        var result = await _connection.SendRequestAsync(RpcMethods.ToolsCall, CopilotJson.ToNode(call), cancellationToken).ConfigureAwait(false);
        return CopilotJson.FromNode<ToolResult>(result);
    }

    /// <summary>ui.askUser: waits until the user answers in the panel.</summary>
    public async Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken)
    {
        var result = await _connection.SendRequestAsync(RpcMethods.UiAskUser, WithSession(CopilotJson.ToNode(request)), cancellationToken).ConfigureAwait(false);
        return CopilotJson.FromNode<QuestionResponse>(result);
    }

    /// <summary>ui.updatePlan.</summary>
    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) =>
        _connection.SendRequestAsync(RpcMethods.UiUpdatePlan, WithSession(CopilotJson.ToNode(plan)), cancellationToken);

    private JsonNode? WithSession(JsonNode? parameters)
    {
        if (SessionId is not null && parameters is JsonObject obj)
        {
            obj[RpcSessions.Member] = SessionId;
        }

        return parameters;
    }

    /// <summary>Closes the pipe.</summary>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
