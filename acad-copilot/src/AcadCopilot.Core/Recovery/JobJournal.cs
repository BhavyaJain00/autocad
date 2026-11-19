using System.Diagnostics;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Recovery;

/// <summary>How a journaled run_commands call ended (or that it is still being typed).</summary>
public enum JournalRunStatus
{
    /// <summary>Being typed (if the journal is read after AutoCAD stopped, it was interrupted).</summary>
    Running,

    /// <summary>Every step was typed.</summary>
    Completed,

    /// <summary>Stopped early by a problem.</summary>
    Stopped,

    /// <summary>Stopped by the user (Stop, Esc) or the CLI went away.</summary>
    Cancelled,

    /// <summary>Failed with an error.</summary>
    Failed,

    /// <summary>Nothing was typed (blocked, declined, AutoCAD busy).</summary>
    NotRun,
}

/// <summary>One task of the plan (update_plan) as journaled.</summary>
public sealed class JournalTask
{
    /// <summary>Task text.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Its status.</summary>
    public PlanTaskStatus Status { get; set; }
}

/// <summary>One run_commands call as journaled: written before typing starts and after every step.</summary>
public sealed class JournalRun
{
    /// <summary>Number of the run within the journal (1, 2, ...).</summary>
    public int Id { get; set; }

    /// <summary>The call's title ("Inner walls").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Number of steps in the call.</summary>
    public int StepsTotal { get; set; }

    /// <summary>The command of each step, in order ("-LAYER", "LINE"...).</summary>
    public List<string> Commands { get; set; } = [];

    /// <summary>1-based numbers of the steps that finished.</summary>
    public List<int> CompletedSteps { get; set; } = [];

    /// <summary>How it ended.</summary>
    public JournalRunStatus Status { get; set; }

    /// <summary>Why it stopped, if it did.</summary>
    public string? StoppedReason { get; set; }

    /// <summary>Checkpoint created before it, if any.</summary>
    public string? Checkpoint { get; set; }

    /// <summary>When typing started (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>When it ended (UTC); null while running or when interrupted.</summary>
    public DateTime? EndedUtc { get; set; }
}

/// <summary>
/// The persisted progress of one drafting job (one chat session, or one drawing when the session is unknown):
/// <c>%LOCALAPPDATA%\AcadCopilot\journal\&lt;session&gt;.json</c>. Holds the plan's tasks (update_plan), every run_commands
/// call with the steps that finished, the drawing and the AutoCAD process that wrote it, so after AutoCAD or the CLI
/// stopped mid-drawing get_resume_info can say what was finished and what remains.
/// </summary>
public sealed class JobJournal
{
    /// <summary>File format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The session key (file name without .json).</summary>
    public string Session { get; set; } = string.Empty;

    /// <summary>Full path of the drawing (its title for a drawing never saved).</summary>
    public string? DrawingPath { get; set; }

    /// <summary>File name of the drawing ("House-G1.dwg").</summary>
    public string? DrawingTitle { get; set; }

    /// <summary>False for a drawing that was never saved (Drawing1.dwg): it cannot be found again after AutoCAD closed.</summary>
    public bool DrawingIsNamed { get; set; }

    /// <summary>The AutoCAD process that wrote the journal.</summary>
    public int ProcessId { get; set; }

    /// <summary>When that process started (UTC), to tell a reused process id apart.</summary>
    public DateTime ProcessStartUtc { get; set; }

    /// <summary>When the journal was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When it was last written (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The plan's title, if it had one.</summary>
    public string? PlanTitle { get; set; }

    /// <summary>The plan's tasks (latest update_plan).</summary>
    public List<JournalTask> Tasks { get; set; } = [];

    /// <summary>The run_commands calls, oldest first (at most <see cref="JobJournalStore.MaxRuns"/>).</summary>
    public List<JournalRun> Runs { get; set; } = [];

    /// <summary>True when the user dismissed it or a newer job on the same drawing replaced it: never offered again.</summary>
    public bool Dismissed { get; set; }
}

/// <summary>The AutoCAD process that writes journals: its id and start time (process ids are reused).</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="StartUtc">Process start time (UTC).</param>
public sealed record ProcessIdentity(int ProcessId, DateTime StartUtc)
{
    /// <summary>The current process.</summary>
    public static ProcessIdentity Current { get; } = Create();

    /// <summary>True when the process with this id and start time still runs (start times compared to the second).</summary>
    public static bool IsAlive(int processId, DateTime startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - startUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when the journal was written by this identity.</summary>
    public bool Wrote(JobJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.ProcessId == ProcessId && Math.Abs((journal.ProcessStartUtc - StartUtc).TotalSeconds) < 2;
    }

    private static ProcessIdentity Create()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime());
    }
}

/// <summary>The drawing a journal belongs to.</summary>
/// <param name="Path">Full path (the title for a drawing never saved).</param>
/// <param name="Title">File name.</param>
/// <param name="IsNamed">False for a drawing never saved.</param>
public sealed record JournalDrawing(string Path, string Title, bool IsNamed)
{
    /// <summary>True when <paramref name="journal"/> is about this drawing (path compared case-insensitively).</summary>
    public bool Matches(JobJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.DrawingPath is { } path && string.Equals(Normalize(path), Normalize(Path), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Trim().Replace('/', '\\');
}
