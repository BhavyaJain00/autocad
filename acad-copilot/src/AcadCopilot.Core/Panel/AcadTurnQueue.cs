using System.Globalization;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Panel;

/// <summary>Where a chat session stands in the AutoCAD turn queue.</summary>
public enum AcadTurnState
{
    /// <summary>Not waiting for AutoCAD and not drawing.</summary>
    None,

    /// <summary>A drawing action of this chat waits for another chat's action to finish.</summary>
    Waiting,

    /// <summary>This chat's action is typing into AutoCAD now.</summary>
    Drawing,
}

/// <summary>A chat session's place in the <see cref="AcadTurnQueue"/>.</summary>
/// <param name="State">Waiting, drawing or neither.</param>
/// <param name="Position">1 = drawing now, 2 = next ("2nd in line"), ...; 0 when <see cref="AcadTurnState.None"/>.</param>
/// <param name="Label">What the action is ("Outer walls"), when known.</param>
public readonly record struct AcadTurnStatus(AcadTurnState State, int Position, string? Label = null)
{
    /// <summary>Neither waiting nor drawing.</summary>
    public static AcadTurnStatus Idle => default;

    /// <summary>True while this chat's action types into AutoCAD.</summary>
    public bool IsDrawing => State == AcadTurnState.Drawing;

    /// <summary>True while this chat's action waits for its turn.</summary>
    public bool IsWaiting => State == AcadTurnState.Waiting;

    /// <summary>"Drawing", "Waiting for AutoCAD (2nd in line)", or null.</summary>
    public string? Text => State switch
    {
        AcadTurnState.Drawing => "Drawing",
        AcadTurnState.Waiting => $"Waiting for AutoCAD ({Ordinal(Position)} in line)",
        _ => null,
    };

    /// <summary>Short form for a tab: "Drawing", "2nd in line", or null.</summary>
    public string? ShortText => State switch
    {
        AcadTurnState.Drawing => "Drawing",
        AcadTurnState.Waiting => $"{Ordinal(Position)} in line",
        _ => null,
    };

    /// <summary>"1st", "2nd", "3rd", "4th", "11th", "21st".</summary>
    public static string Ordinal(int number)
    {
        var suffix = (number % 100) is 11 or 12 or 13
            ? "th"
            : (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return number.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>One entry of the queue (for diagnostics and the panel).</summary>
/// <param name="Owner">Chat session id, or null for a client outside the panel.</param>
/// <param name="Label">What the action is.</param>
/// <param name="Position">1 = running, 2 = next, ...</param>
public sealed record AcadTurnEntry(string? Owner, string Label, int Position)
{
    /// <summary>True for the action that is typing now.</summary>
    public bool IsRunning => Position == 1;
}

/// <summary>
/// AutoCAD has one command line, so actions that type into it (run_commands, undo, cancel_command, and reads that have
/// to switch the active drawing first) take turns: first come, first served, one at a time. Chats keep thinking and
/// planning in parallel; only their drawing actions wait here. Cancelling a waiting entry removes it from the queue
/// at once (Stop in that chat). Thread-safe; contains no AutoCAD code.
/// </summary>
public sealed class AcadTurnQueue
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Ticket> _waiting = new();
    private Ticket? _running;

    /// <summary>Raised (on the thread that changed the queue, never under its lock) after every change.</summary>
    public event EventHandler? Changed;

    /// <summary>Number of entries (running plus waiting).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count + (_running is null ? 0 : 1);
            }
        }
    }

    /// <summary>
    /// Waits for the turn of <paramref name="owner"/> and returns the lease; dispose it when the action is done (the
    /// next entry starts then). Cancelling <paramref name="cancellationToken"/> while waiting removes the entry and
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="owner">Chat session id (null = a client outside the panel).</param>
    /// <param name="label">What the action is ("run_commands: Outer walls").</param>
    /// <param name="cancellationToken">Stop.</param>
    public async Task<IDisposable> EnterAsync(string? owner, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ticket = new Ticket(this, owner, label);
        bool granted;
        lock (_gate)
        {
            granted = _running is null && _waiting.Count == 0;
            if (granted)
            {
                _running = ticket;
                ticket.Granted.TrySetResult();
            }
            else
            {
                _waiting.AddLast(ticket);
            }
        }

        RaiseChanged();
        if (granted)
        {
            return ticket;
        }

        using (cancellationToken.Register(() => Abandon(ticket, cancellationToken)))
        {
            await ticket.Granted.Task.ConfigureAwait(false);
        }

        return ticket;
    }

    /// <summary>The running entry first, then the waiting ones in order.</summary>
    public IReadOnlyList<AcadTurnEntry> Snapshot()
    {
        lock (_gate)
        {
            var entries = new List<AcadTurnEntry>(_waiting.Count + 1);
            var position = 1;
            if (_running is not null)
            {
                entries.Add(new AcadTurnEntry(_running.Owner, _running.Label, position));
            }

            position++;
            foreach (var ticket in _waiting)
            {
                entries.Add(new AcadTurnEntry(ticket.Owner, ticket.Label, position++));
            }

            return entries;
        }
    }

    /// <summary>Where <paramref name="owner"/> stands: drawing, waiting (its first entry), or idle.</summary>
    public AcadTurnStatus StatusOf(string? owner)
    {
        AcadTurnEntry? first = null;
        foreach (var entry in Snapshot())
        {
            if (string.Equals(entry.Owner, owner, StringComparison.Ordinal))
            {
                first = entry;
                break;
            }
        }

        return first switch
        {
            null => AcadTurnStatus.Idle,
            { IsRunning: true } => new AcadTurnStatus(AcadTurnState.Drawing, 1, first.Label),
            _ => new AcadTurnStatus(AcadTurnState.Waiting, first.Position, first.Label),
        };
    }

    private void Abandon(Ticket ticket, CancellationToken cancellationToken)
    {
        bool removed;
        lock (_gate)
        {
            removed = _waiting.Remove(ticket);
        }

        if (removed)
        {
            ticket.Granted.TrySetCanceled(cancellationToken);
            RaiseChanged();
        }
    }

    private void Release(Ticket ticket)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_running, ticket))
            {
                _waiting.Remove(ticket);
                return;
            }

            _running = null;
            while (_waiting.First?.Value is { } next)
            {
                _waiting.RemoveFirst();
                if (next.Granted.TrySetResult())
                {
                    _running = next;
                    break;
                }
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("AutoCAD turn queue listener failed", ex);
        }
    }

    private sealed class Ticket : IDisposable
    {
        private readonly AcadTurnQueue _queue;
        private int _released;

        public Ticket(AcadTurnQueue queue, string? owner, string label)
        {
            _queue = queue;
            Owner = owner;
            Label = label;
        }

        public string? Owner { get; }

        public string Label { get; }

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _queue.Release(this);
            }
        }
    }
}
