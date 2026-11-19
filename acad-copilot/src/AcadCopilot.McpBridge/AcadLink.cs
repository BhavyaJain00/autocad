using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Ipc;

namespace AcadCopilot.McpBridge;

/// <summary>Why <see cref="AcadLink.ToolsChanged"/> was raised.</summary>
public enum ToolsChangeReason
{
    /// <summary>A connection to AutoCAD was established.</summary>
    Connected,

    /// <summary>The connection to AutoCAD was lost.</summary>
    Disconnected,

    /// <summary>The add-in reported that its tool list changed.</summary>
    AddinNotification,
}

/// <summary>Thrown when no AutoCAD instance with the add-in can be reached. The message is shown to the LLM.</summary>
public sealed class AcadUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AcadUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Owns the bridge's pipe connection to the add-in: finds the AutoCAD instance through discovery files, connects
/// lazily, reconnects after AutoCAD restarts, and polls in the background while disconnected so the MCP client can
/// be told (tools/list_changed) when AutoCAD tools become available.
/// </summary>
public sealed class AcadLink : IAsyncDisposable
{
    private const string ClientName = "AcadCopilotMcp";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly BridgeOptions _options;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private PipeClient? _client;
    private Task? _watcher;

    /// <summary>Creates the link (no connection yet).</summary>
    public AcadLink(BridgeOptions options)
    {
        _options = options;
    }

    /// <summary>Raised when the set of AutoCAD tools may have changed (connected, disconnected, add-in notification).</summary>
    public event Action<ToolsChangeReason>? ToolsChanged;

    /// <summary>The live client, or null when disconnected.</summary>
    public PipeClient? Current
    {
        get
        {
            var client = Volatile.Read(ref _client);
            return client is { IsConnected: true } ? client : null;
        }
    }

    /// <summary>Returns a connected, authenticated client, connecting if needed.</summary>
    /// <exception cref="AcadUnavailableException">AutoCAD is not running / the add-in is not reachable.</exception>
    public async Task<PipeClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (Current is { } existing)
        {
            return existing;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current is { } raced)
            {
                return raced;
            }

            var instance = InstanceDiscovery.Find(_options.Pid, _options.InstancesDirectory);
            if (instance is null)
            {
                throw new AcadUnavailableException(_options.Pid is { } pid
                    ? $"AutoCAD (process {pid}) with the AI Copilot add-in is not running. Ask the user to reopen AutoCAD 2027 and the AI Copilot panel."
                    : "No running AutoCAD 2027 with the AI Copilot add-in was found. Ask the user to start AutoCAD 2027 (the add-in loads automatically) and try again.");
            }

            PipeClient client;
            try
            {
                client = await PipeClient.ConnectAsync(instance, ClientName, _options.ConnectTimeout, _options.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or JsonRpcException or UnauthorizedAccessException)
            {
                CopilotLog.Warn($"Could not connect to AutoCAD {instance.Pid}", ex);
                throw new AcadUnavailableException(
                    $"AutoCAD (process {instance.Pid}) was found but the AI Copilot add-in did not answer ({ex.Message}). AutoCAD may be busy or starting; try again in a moment.",
                    ex);
            }

            client.Notification += OnNotification;
            _ = client.Completion.ContinueWith(_ => OnDisconnected(client), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Volatile.Write(ref _client, client);
            CopilotLog.Info($"Connected to AutoCAD {instance.Pid} ({client.Server.AcadVersion}, document '{client.Server.DocumentTitle}').");
            RaiseToolsChanged(ToolsChangeReason.Connected);
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Starts a background loop that tries to connect every few seconds while disconnected and raises
    /// <see cref="ToolsChanged"/> when it succeeds. Safe to call more than once.
    /// </summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Current is null && InstanceDiscovery.Find(_options.Pid, _options.InstancesDirectory) is not null)
                {
                    try
                    {
                        await GetClientAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (AcadUnavailableException)
                    {
                        // Keep polling.
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>Stops polling and closes the pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_watcher is not null)
        {
            try
            {
                await _watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        if (Interlocked.Exchange(ref _client, null) is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private void OnNotification(string method, System.Text.Json.Nodes.JsonNode? parameters)
    {
        if (method == RpcMethods.ToolsChanged)
        {
            RaiseToolsChanged(ToolsChangeReason.AddinNotification);
        }
    }

    private void OnDisconnected(PipeClient client)
    {
        if (Interlocked.CompareExchange(ref _client, null, client) == client)
        {
            CopilotLog.Info($"Disconnected from AutoCAD {client.Instance.Pid}.");
            RaiseToolsChanged(ToolsChangeReason.Disconnected);
        }
    }

    private void RaiseToolsChanged(ToolsChangeReason reason)
    {
        try
        {
            ToolsChanged?.Invoke(reason);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("ToolsChanged handler failed", ex);
        }
    }
}
