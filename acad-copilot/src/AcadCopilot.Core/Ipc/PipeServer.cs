using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Ipc;

/// <summary>
/// What the pipe server needs from its host (the AutoCAD add-in, or a fake in tests).
/// Called on thread-pool threads; implementations do their own marshalling to AutoCAD / WPF threads.
/// </summary>
public interface IPipeHandler
{
    /// <summary>Server info returned to a client after successful auth.</summary>
    AuthResult GetServerInfo();

    /// <summary>tools.list: all AutoCAD tools.</summary>
    Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>tools.call: run a tool. Tool failures are returned as <see cref="ToolResult.Success"/> = false, not thrown.</summary>
    Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken);

    /// <summary>ui.askUser: throw <see cref="UiUnavailableException"/> when the panel cannot show questions.</summary>
    Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken);

    /// <summary>ui.updatePlan: throw <see cref="UiUnavailableException"/> when the panel is not available.</summary>
    Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken);

    /// <summary>
    /// ui.askUser for a panel chat session (<see cref="RpcSessions"/>; null = no session). Hosts with chat tabs override
    /// it to show the card in that tab; the default ignores the session.
    /// </summary>
    Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken) =>
        AskUserAsync(request, cancellationToken);

    /// <summary>ui.updatePlan for a panel chat session (null = no session); the default ignores the session.</summary>
    Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken) =>
        UpdatePlanAsync(plan, cancellationToken);
}

/// <summary>
/// Named-pipe JSON-RPC server used by the add-in. Accepts any number of concurrent clients
/// (current Windows user only); each client must call <see cref="RpcMethods.Auth"/> with the token
/// before anything else, otherwise it is disconnected.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private const int MaxFailedAuthAttempts = 3;
    private readonly string _pipeName;
    private readonly byte[] _token;
    private readonly IPipeHandler _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<JsonRpcConnection, Session> _sessions = new();
    private NamedPipeServerStream? _listening;
    private Task? _acceptLoop;

    /// <summary>Creates the server (call <see cref="Start"/>).</summary>
    /// <param name="pipeName">Short or full pipe name (see <see cref="PipeNames"/>).</param>
    /// <param name="token">Secret clients must present.</param>
    /// <param name="handler">Request handler.</param>
    public PipeServer(string pipeName, string token, IPipeHandler handler)
    {
        _pipeName = PipeNames.ToShortName(pipeName);
        _token = Encoding.UTF8.GetBytes(token);
        _handler = handler;
    }

    /// <summary>Short pipe name.</summary>
    public string PipeName => _pipeName;

    /// <summary>Number of authenticated clients currently connected.</summary>
    public int AuthenticatedClientCount => _sessions.Values.Count(s => s.Authenticated);

    /// <summary>Creates the first listening pipe instance (throws if the name is taken) and starts accepting clients.</summary>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The pipe server is already started.");
        }

        _listening = CreateServerStream();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Sends a notification to every authenticated client (best effort).</summary>
    public async Task NotifyAllAsync(string method, JsonNode? parameters)
    {
        foreach (var (connection, session) in _sessions)
        {
            if (!session.Authenticated)
            {
                continue;
            }

            try
            {
                await connection.SendNotificationAsync(method, parameters?.DeepClone()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Client left.
            }
        }
    }

    /// <summary>Stops accepting and disconnects every client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Pipe accept loop ended with an error", ex);
            }
        }

        foreach (var connection in _sessions.Keys)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _stop.Dispose();
    }

    private NamedPipeServerStream CreateServerStream() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var stream = _listening ?? CreateServerStream();
            _listening = null;
            try
            {
                await stream.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                CopilotLog.Warn("Pipe accept failed", ex);
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            Accept(stream);
        }
    }

    private void Accept(NamedPipeServerStream stream)
    {
        var connection = new JsonRpcConnection(stream, $"pipe:{_pipeName}");
        var session = new Session();
        _sessions[connection] = session;
        connection.RequestHandler = (request, ct) => HandleAsync(connection, session, request, ct);
        _ = connection.Completion.ContinueWith(
            _ => _sessions.TryRemove(connection, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        connection.Start();
        CopilotLog.Info($"Pipe client connected to {_pipeName}.");
    }

    private async Task<JsonNode?> HandleAsync(JsonRpcConnection connection, Session session, JsonRpcRequest request, CancellationToken ct)
    {
        if (request.Method == RpcMethods.Auth)
        {
            var auth = CopilotJson.FromNode<AuthParams>(request.Params);
            var presented = Encoding.UTF8.GetBytes(auth.Token ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(presented, _token))
            {
                if (Interlocked.Increment(ref session.FailedAuth) >= MaxFailedAuthAttempts)
                {
                    _ = Task.Delay(100, CancellationToken.None).ContinueWith(_ => connection.DisposeAsync().AsTask(), TaskScheduler.Default);
                }

                CopilotLog.Warn($"Pipe client '{auth.ClientName}' presented a wrong token.");
                throw new JsonRpcException(RpcErrorCodes.Unauthorized, "Invalid token.");
            }

            session.SessionId = RpcSessions.Check(auth.SessionId);
            session.Authenticated = true;
            CopilotLog.Info($"Pipe client '{auth.ClientName}' {auth.ClientVersion} authenticated{(session.SessionId is null ? string.Empty : " for chat session " + session.SessionId)}.");
            return CopilotJson.ToNode(_handler.GetServerInfo());
        }

        if (!session.Authenticated)
        {
            throw new JsonRpcException(RpcErrorCodes.Unauthorized, "Call 'auth' first.");
        }

        try
        {
            switch (request.Method)
            {
                case RpcMethods.ToolsList:
                    return CopilotJson.ToNode(await _handler.ListToolsAsync(ct).ConfigureAwait(false));

                case RpcMethods.ToolsCall:
                    var call = CopilotJson.FromNode<ToolsCallParams>(request.Params);
                    call = call with { SessionId = RpcSessions.Check(call.SessionId) ?? session.SessionId };
                    return CopilotJson.ToNode(await _handler.CallToolAsync(call, ct).ConfigureAwait(false));

                case RpcMethods.UiAskUser:
                    var questions = CopilotJson.FromNode<QuestionRequest>(request.Params);
                    var askSession = RpcSessions.Read(request.Params) ?? session.SessionId;
                    return CopilotJson.ToNode(await _handler.AskUserAsync(questions, askSession, ct).ConfigureAwait(false));

                case RpcMethods.UiUpdatePlan:
                    var plan = CopilotJson.FromNode<PlanUpdate>(request.Params);
                    var planSession = RpcSessions.Read(request.Params) ?? session.SessionId;
                    await _handler.UpdatePlanAsync(plan, planSession, ct).ConfigureAwait(false);
                    return new JsonObject { ["ok"] = true };

                default:
                    throw new JsonRpcException(RpcErrorCodes.MethodNotFound, $"Method '{request.Method}' is not supported.");
            }
        }
        catch (UiUnavailableException ex)
        {
            throw new JsonRpcException(RpcErrorCodes.UiUnavailable, ex.Message);
        }
    }

    private sealed class Session
    {
        public volatile bool Authenticated;
        public int FailedAuth;
        public volatile string? SessionId;
    }
}
