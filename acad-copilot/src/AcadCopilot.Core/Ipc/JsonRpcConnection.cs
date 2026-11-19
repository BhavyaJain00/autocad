using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Ipc;

/// <summary>An incoming JSON-RPC request.</summary>
/// <param name="Id">Request id (number or string), echoed in the response.</param>
/// <param name="Method">Method name.</param>
/// <param name="Params">Params (object, array or null).</param>
public sealed record JsonRpcRequest(JsonNode Id, string Method, JsonNode? Params);

/// <summary>
/// A bidirectional JSON-RPC 2.0 peer over a stream, one JSON message per line (UTF-8, '\n').
/// Both sides can send requests and notifications. Incoming requests run concurrently; their
/// cancellation tokens fire on <see cref="RpcMethods.CancelRequest"/> or when the connection closes.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _name;
    private long _nextId;
    private int _started;
    private int _disposed;

    /// <summary>Creates a connection over <paramref name="stream"/>; call <see cref="Start"/> to begin reading.</summary>
    /// <param name="stream">Duplex stream (named pipe, or any stream pair in tests).</param>
    /// <param name="name">Name used in log lines.</param>
    public JsonRpcConnection(Stream stream, string name)
    {
        _stream = stream;
        _name = name;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
    }

    /// <summary>Handles incoming requests. Return the result node; throw <see cref="JsonRpcException"/> for protocol errors.</summary>
    public Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>Handles incoming notifications (method, params). Exceptions are logged and swallowed.</summary>
    public Action<string, JsonNode?>? NotificationHandler { get; set; }

    /// <summary>Completes when the connection has closed (peer hung up, disposed, or read error).</summary>
    public Task Completion => _completion.Task;

    /// <summary>True until the connection closes.</summary>
    public bool IsOpen => !_completion.Task.IsCompleted;

    /// <summary>Starts the background read loop. Set the handlers first.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("The connection is already started.");
        }

        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Sends a request and awaits its result. Cancelling sends <see cref="RpcMethods.CancelRequest"/> to the peer.</summary>
    /// <exception cref="JsonRpcException">The peer returned an error.</exception>
    /// <exception cref="IOException">The connection closed before a response arrived.</exception>
    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        if (!IsOpen)
        {
            _pending.TryRemove(key, out _);
            throw new IOException($"The connection '{_name}' is closed.");
        }

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Parent is null ? parameters : parameters.DeepClone();
        }

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => OnLocalCancel(key, id, tcs, cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>Sends a notification (no response).</summary>
    public Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters.Parent is null ? parameters : parameters.DeepClone();
        }

        return WriteAsync(message, cancellationToken);
    }

    /// <summary>Closes the connection: pending requests fail, incoming requests are cancelled.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Already broken.
        }

        Close();
    }

    private void OnLocalCancel(string key, long id, TaskCompletionSource<JsonNode?> tcs, CancellationToken token)
    {
        if (tcs.TrySetCanceled(token))
        {
            _pending.TryRemove(key, out _);
            _ = SendNotificationAsync(RpcMethods.CancelRequest, new JsonObject { ["id"] = id }).ContinueWith(
                t => CopilotLog.Warn($"[{_name}] could not send cancel for request {id}", t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    message = null;
                }

                if (message is null)
                {
                    await TrySendErrorAsync(null, RpcErrorCodes.ParseError, "Invalid JSON-RPC message.").ConfigureAwait(false);
                    continue;
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (IOException ex)
        {
            CopilotLog.Info($"[{_name}] connection closed: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Disposed while reading.
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"[{_name}] read loop failed", ex);
        }
        finally
        {
            Close();
        }
    }

    private void Dispatch(JsonObject message)
    {
        var hasMethod = message.TryGetPropertyValue("method", out var methodNode);
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = hasMethod ? CopilotJson.TryGetString(methodNode) : null;

        if (method is not null && hasId)
        {
            _ = HandleRequestAsync(new JsonRpcRequest(idNode!.DeepClone(), method, message["params"]?.DeepClone()));
            return;
        }

        if (method is not null)
        {
            HandleNotification(method, message["params"]?.DeepClone());
            return;
        }

        if (hasId && _pending.TryRemove(idNode!.ToJsonString().Trim('"'), out var tcs))
        {
            if (message["error"] is JsonObject error)
            {
                var code = CopilotJson.TryGetDouble(error["code"], out var c) ? (int)c : RpcErrorCodes.InternalError;
                var text = CopilotJson.TryGetString(error["message"]) ?? "Unknown error.";
                tcs.TrySetException(new JsonRpcException(code, text, error["data"]?.DeepClone()));
            }
            else
            {
                tcs.TrySetResult(message["result"]?.DeepClone());
            }
        }
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method == RpcMethods.CancelRequest)
        {
            var target = parameters?["id"]?.ToJsonString().Trim('"');
            if (target is not null && _incoming.TryGetValue(target, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Request already finished.
                }
            }

            return;
        }

        try
        {
            NotificationHandler?.Invoke(method, parameters);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn($"[{_name}] notification handler for '{method}' failed", ex);
        }
    }

    private async Task HandleRequestAsync(JsonRpcRequest request)
    {
        var key = request.Id.ToJsonString().Trim('"');
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _incoming[key] = cts;
        JsonObject response;
        try
        {
            var handler = RequestHandler ?? throw new JsonRpcException(RpcErrorCodes.MethodNotFound, $"Method '{request.Method}' is not supported.");
            var result = await handler(request, cts.Token).ConfigureAwait(false);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id.DeepClone(), ["result"] = result?.Parent is null ? result : result.DeepClone() };
        }
        catch (JsonRpcException ex)
        {
            response = ErrorResponse(request.Id, ex.Code, ex.Message, ex.ErrorData);
        }
        catch (OperationCanceledException)
        {
            response = ErrorResponse(request.Id, RpcErrorCodes.RequestCancelled, "The request was cancelled.", null);
        }
        catch (JsonException ex)
        {
            response = ErrorResponse(request.Id, RpcErrorCodes.InvalidParams, "Invalid params: " + ex.Message, null);
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"[{_name}] request '{request.Method}' failed", ex);
            response = ErrorResponse(request.Id, RpcErrorCodes.InternalError, ex.Message, null);
        }
        finally
        {
            _incoming.TryRemove(key, out _);
        }

        try
        {
            await WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            CopilotLog.Info($"[{_name}] could not send response to '{request.Method}': connection closed.");
        }
    }

    private Task TrySendErrorAsync(JsonNode? id, int code, string message)
    {
        try
        {
            return WriteAsync(ErrorResponse(id, code, message, null), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return Task.CompletedTask;
        }
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message, JsonNode? data)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data.Parent is null ? data : data.DeepClone();
        }

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error };
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen)
            {
                throw new IOException($"The connection '{_name}' is closed.");
            }

            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException($"The connection '{_name}' is closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Close()
    {
        if (!_completion.TrySetResult())
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }

        foreach (var (key, tcs) in _pending)
        {
            if (_pending.TryRemove(key, out _))
            {
                tcs.TrySetException(new IOException($"The connection '{_name}' closed before a response arrived."));
            }
        }

        foreach (var cts in _incoming.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished meanwhile.
            }
        }

        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // Already broken.
        }
    }
}
