using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcadCopilot.McpBridge;

/// <summary>
/// Cache of deterministic, expensive read results of one bridge (one CLI call of one chat): get_drawing_info,
/// list_layers, list_entities pages, get_entity and recall_knowledge. Every entry carries the stamp it is valid for: the
/// drawing revision (AutoCAD bumps it on every object change, undo and drawing switch) for drawing tools, and the knowledge
/// files' stamp for recall_knowledge. The bridge serves an entry only when the current stamp is the same and the entry is
/// young enough, and clears the drawing entries after every call that may change the drawing.
/// </summary>
public sealed class ResponseCache
{
    /// <summary>Most entries kept (the oldest go first).</summary>
    public const int MaxEntries = 64;

    /// <summary>Largest result text kept (bigger results are not cached).</summary>
    public const int MaxTextLength = 400_000;

    /// <summary>
    /// get_drawing_info also reports the view and the active command, which change without a revision change (ZOOM, the
    /// user starting a command): its entries live only briefly.
    /// </summary>
    public static readonly TimeSpan DrawingInfoMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>Lifetime of the other entries (layers, entities, knowledge).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _hits;
    private long _misses;

    /// <summary>Creates the cache.</summary>
    /// <param name="time">Clock (tests replace it).</param>
    public ResponseCache(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The AutoCAD tools whose results depend only on the drawing (and their arguments).</summary>
    public static IReadOnlySet<string> DrawingTools { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "get_drawing_info", "list_layers", "list_entities", "get_entity" };

    /// <summary>Entries now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Results served from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that had to call AutoCAD (or read the files).</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>The cache key of a call: tool name plus its arguments with the object keys sorted.</summary>
    public static string Key(string name, JsonObject? arguments) => name + "|" + Canonical(arguments);

    /// <summary>
    /// A young-enough entry for <paramref name="key"/> stored with <paramref name="stamp"/>; a different stamp drops the
    /// entry (the drawing or the knowledge changed).
    /// </summary>
    public bool TryGet(string key, long stamp, out BridgeCallResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                var maxAge = key.StartsWith("get_drawing_info|", StringComparison.Ordinal) ? DrawingInfoMaxAge : MaxAge;
                if (entry.Stamp == stamp && _time.GetUtcNow() - entry.StoredAt <= maxAge)
                {
                    Interlocked.Increment(ref _hits);
                    result = entry.Result;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        Interlocked.Increment(ref _misses);
        result = null!;
        return false;
    }

    /// <summary>True when there is any entry for <paramref name="key"/> (whatever its stamp).</summary>
    public bool Has(string key)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>Stores a successful result for <paramref name="key"/> at <paramref name="stamp"/>; failures and huge results are skipped.</summary>
    public void Store(string key, long stamp, BridgeCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsError || result.Images.Count > 0 || result.Text.Length > MaxTextLength)
        {
            return;
        }

        lock (_gate)
        {
            _entries[key] = new Entry(stamp, _time.GetUtcNow(), result);
            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.MinBy(e => e.Value.StoredAt).Key;
                _entries.Remove(oldest);
            }
        }
    }

    /// <summary>Forgets the drawing results (after a call that may change the drawing); knowledge results stay.</summary>
    public void InvalidateDrawing()
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => DrawingTools.Contains(k[..k.IndexOf('|', StringComparison.Ordinal)])).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// The result as served from the cache: its JSON gets "cached": true (other text is returned unchanged), so the AI
    /// knows nothing changed since it last asked.
    /// </summary>
    public static BridgeCallResult MarkCached(BridgeCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            if (JsonNode.Parse(result.Text) is JsonObject json)
            {
                json["cached"] = true;
                return result with { Text = json.ToJsonString() };
            }
        }
        catch (JsonException)
        {
            // Not JSON: unchanged.
        }

        return result;
    }

    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    private sealed record Entry(long Stamp, DateTimeOffset StoredAt, BridgeCallResult Result);
}

/// <summary>
/// First-in-first-out gate for the calls one bridge sends to AutoCAD: when a CLI sends several calls at once (the next
/// run_commands stage queued behind the current one), they reach AutoCAD in the order they were sent, one at a time.
/// </summary>
public sealed class FifoGate
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _waiting;

    /// <summary>Calls inside or waiting for the gate.</summary>
    public int Pending => Volatile.Read(ref _waiting);

    /// <summary>Waits until every call that entered before has left; dispose the result to let the next one in.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tail;
            _tail = mine.Task;
        }

        Interlocked.Increment(ref _waiting);
        try
        {
            await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Keep the chain intact: this slot opens as soon as the one before it does.
            _ = previous.ContinueWith(_ => mine.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Interlocked.Decrement(ref _waiting);
            throw;
        }

        return new Releaser(this, mine);
    }

    private sealed class Releaser(FifoGate owner, TaskCompletionSource slot) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref owner._waiting);
                slot.TrySetResult();
            }
        }
    }
}
