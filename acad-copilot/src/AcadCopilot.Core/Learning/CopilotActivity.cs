namespace AcadCopilot.Core.Learning;

/// <summary>
/// Knows when the copilot itself works in AutoCAD (a tool call that types commands, switches drawings or changes the
/// drawing), so the "Learn from me" recorder never mistakes the AI's work for the user's. The plug-in opens a scope with
/// <see cref="Begin"/> around every such tool call; <see cref="IsActive"/> stays true for a short tail after the last scope
/// ends, because AutoCAD reports the end of a typed command (or the copilot's own Esc) slightly later. Thread-safe.
/// </summary>
public sealed class CopilotActivity
{
    /// <summary>How long after the copilot's last action events still count as the copilot's.</summary>
    public static readonly TimeSpan DefaultTail = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _tail;
    private int _depth;
    private long _lastEndTicks = long.MinValue;

    /// <summary>Creates a flag (tests); the plug-in uses <see cref="Shared"/>.</summary>
    /// <param name="clock">Current time.</param>
    /// <param name="tail">How long after the last scope <see cref="IsActive"/> stays true.</param>
    public CopilotActivity(Func<DateTimeOffset>? clock = null, TimeSpan? tail = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tail = tail ?? DefaultTail;
    }

    /// <summary>The flag of this AutoCAD session.</summary>
    public static CopilotActivity Shared { get; } = new();

    /// <summary>True while at least one copilot scope is open.</summary>
    public bool IsBusy => Volatile.Read(ref _depth) > 0;

    /// <summary>True while the copilot works, or worked a moment ago (<see cref="DefaultTail"/>).</summary>
    public bool IsActive
    {
        get
        {
            if (IsBusy)
            {
                return true;
            }

            var last = Interlocked.Read(ref _lastEndTicks);
            return last != long.MinValue && _clock().UtcTicks - last < _tail.Ticks;
        }
    }

    /// <summary>Marks the copilot as working until the returned scope is disposed (scopes may nest and overlap).</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _depth);
        return new Scope(this);
    }

    private void End()
    {
        Interlocked.Exchange(ref _lastEndTicks, _clock().UtcTicks);
        Interlocked.Decrement(ref _depth);
    }

    private sealed class Scope(CopilotActivity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End();
            }
        }
    }
}
