using System.Text;
using System.Text.Json;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Recovery;

/// <summary>
/// Reads and writes job journals (<see cref="JobJournal"/>) in <c>%LOCALAPPDATA%\AcadCopilot\journal\</c>: one file per
/// session key, written atomically after every change, so a crash of AutoCAD (or of the CLI) at any moment leaves a
/// readable journal. Thread-safe. Journals not written for <see cref="MaxAge"/> are deleted by <see cref="CleanOld"/>.
/// </summary>
public sealed class JobJournalStore
{
    /// <summary>Journals older than this are deleted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Most runs kept per journal (oldest dropped).</summary>
    public const int MaxRuns = 200;

    /// <summary>Most plan tasks kept.</summary>
    public const int MaxTasks = 100;

    private readonly Lock _gate = new();
    private readonly ProcessIdentity _process;
    private readonly Func<int, DateTime, bool> _isAlive;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Creates a store.</summary>
    /// <param name="directory">The journal folder (created on first write).</param>
    /// <param name="process">The process writing journals (the AutoCAD process).</param>
    /// <param name="isAlive">Whether a process (id, start time) still runs; default <see cref="ProcessIdentity.IsAlive"/>.</param>
    /// <param name="utcNow">Clock; default <see cref="DateTime.UtcNow"/>.</param>
    public JobJournalStore(string directory, ProcessIdentity process, Func<int, DateTime, bool>? isAlive = null, Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(process);
        Directory = directory;
        _process = process;
        _isAlive = isAlive ?? ProcessIdentity.IsAlive;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The store in %LOCALAPPDATA%\AcadCopilot\journal for the current process.</summary>
    public static JobJournalStore CreateDefault() => new(System.IO.Path.Combine(AppPaths.Root, "journal"), ProcessIdentity.Current);

    /// <summary>The journal folder.</summary>
    public string Directory { get; }

    /// <summary>The session key used when the chat session is unknown: one journal per drawing ("drawing-house-g1_3fa2c19b").</summary>
    public static string DrawingSessionKey(JournalDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        return "drawing-" + CheckpointStore.DrawingKey(drawing.Path);
    }

    /// <summary>A file-name-safe session key (letters, digits, '-', '_'; at most 80 characters).</summary>
    public static string SafeKey(string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var builder = new StringBuilder(session.Length);
        foreach (var c in session.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        var key = builder.ToString();
        return key.Length > 80 ? key[..80] : key;
    }

    /// <summary>The file of a session's journal.</summary>
    public string PathOf(string session) => System.IO.Path.Combine(Directory, SafeKey(session) + ".json");

    /// <summary>The journal of <paramref name="session"/>, or null when there is none (or it cannot be read).</summary>
    public JobJournal? Load(string session)
    {
        lock (_gate)
        {
            return Read(PathOf(session));
        }
    }

    /// <summary>Every readable journal, newest first.</summary>
    public IReadOnlyList<JobJournal> LoadAll()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            return System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(Read)
                .OfType<JobJournal>()
                .OrderByDescending(j => j.UpdatedUtc)
                .ToList();
        }
    }

    /// <summary>
    /// Records the plan (update_plan) of <paramref name="session"/>. A plan with tasks also retires older unfinished
    /// journals of the same drawing written by AutoCAD sessions that have ended: the new plan carries the job on.
    /// </summary>
    public void RecordPlan(string session, JournalDrawing? drawing, PlanUpdate plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            var journal = Open(session, drawing);
            journal.PlanTitle = string.IsNullOrWhiteSpace(plan.Title) ? journal.PlanTitle : plan.Title.Trim();
            journal.Tasks = plan.Tasks.Take(MaxTasks).Select(t => new JournalTask { Title = t.Title, Status = t.Status }).ToList();
            journal.Dismissed = false;
            Write(journal);
            if (plan.Tasks.Count > 0 && journal.DrawingPath is not null)
            {
                RetireOthers(journal);
            }
        }
    }

    /// <summary>Records that a run_commands call starts typing; returns its run id.</summary>
    public int BeginRun(string session, JournalDrawing? drawing, string title, IReadOnlyList<string> commands, string? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(commands);
        lock (_gate)
        {
            var journal = Open(session, drawing);
            var id = journal.Runs.Count == 0 ? 1 : journal.Runs.Max(r => r.Id) + 1;
            journal.Runs.Add(new JournalRun
            {
                Id = id,
                Title = title,
                StepsTotal = commands.Count,
                Commands = commands.ToList(),
                Status = JournalRunStatus.Running,
                Checkpoint = checkpoint,
                StartedUtc = _utcNow(),
            });
            if (journal.Runs.Count > MaxRuns)
            {
                journal.Runs.RemoveRange(0, journal.Runs.Count - MaxRuns);
            }

            journal.Dismissed = false;
            Write(journal);
            return id;
        }
    }

    /// <summary>Records that step <paramref name="step"/> (1-based) of run <paramref name="runId"/> finished.</summary>
    public void StepFinished(string session, int runId, int step)
    {
        lock (_gate)
        {
            if (Read(PathOf(session)) is not { } journal || journal.Runs.FirstOrDefault(r => r.Id == runId) is not { } run)
            {
                return;
            }

            if (!run.CompletedSteps.Contains(step))
            {
                run.CompletedSteps.Add(step);
                run.CompletedSteps.Sort();
                Write(journal);
            }
        }
    }

    /// <summary>Records how run <paramref name="runId"/> ended; <paramref name="completedSteps"/> replaces the steps recorded so far.</summary>
    public void EndRun(string session, int runId, JournalRunStatus status, IEnumerable<int>? completedSteps, string? reason)
    {
        lock (_gate)
        {
            if (Read(PathOf(session)) is not { } journal || journal.Runs.FirstOrDefault(r => r.Id == runId) is not { } run)
            {
                return;
            }

            run.Status = status;
            run.StoppedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            run.EndedUtc = _utcNow();
            if (completedSteps is not null)
            {
                run.CompletedSteps = completedSteps.Distinct().Order().ToList();
            }

            Write(journal);
        }
    }

    /// <summary>Marks a journal dismissed (the user closed the "Resume previous job" card, or the job is abandoned).</summary>
    public bool Dismiss(string session)
    {
        lock (_gate)
        {
            if (Read(PathOf(session)) is not { } journal)
            {
                return false;
            }

            journal.Dismissed = true;
            Write(journal);
            return true;
        }
    }

    /// <summary>
    /// The unfinished jobs of <paramref name="drawing"/>, newest first: not dismissed, plan tasks or a run left over, and
    /// not being worked on by another AutoCAD that still runs. A drawing never saved only matches journals of this
    /// AutoCAD session. Pass <paramref name="otherSessionsOnly"/> to leave out the jobs of the running AutoCAD (start-up).
    /// </summary>
    public IReadOnlyList<ResumeInfo> FindUnfinished(JournalDrawing drawing, bool otherSessionsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        var result = new List<ResumeInfo>();
        foreach (var journal in LoadAll())
        {
            if (journal.Dismissed || !drawing.Matches(journal))
            {
                continue;
            }

            var ours = _process.Wrote(journal);
            if (ours && otherSessionsOnly)
            {
                continue;
            }

            if (!ours && (!journal.DrawingIsNamed || !drawing.IsNamed))
            {
                continue;
            }

            if (!ours && _isAlive(journal.ProcessId, journal.ProcessStartUtc))
            {
                // Another AutoCAD that still runs owns this job.
                continue;
            }

            var info = ResumeInfo.From(journal, ours ? ResumeReason.SameSession : ResumeReason.AutoCadStopped);
            if (info.IsUnfinished)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>Deletes journals not written for <paramref name="maxAge"/> (and unreadable ones that old); returns how many.</summary>
    public int CleanOld(TimeSpan maxAge)
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return 0;
            }

            var now = _utcNow();
            var removed = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").ToList())
            {
                try
                {
                    var updated = Read(file)?.UpdatedUtc ?? File.GetLastWriteTimeUtc(file);
                    if (now - updated > maxAge)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use: next time.
                }
            }

            return removed;
        }
    }

    private JobJournal Open(string session, JournalDrawing? drawing)
    {
        var now = _utcNow();
        var journal = Read(PathOf(session)) ?? new JobJournal
        {
            Session = SafeKey(session),
            CreatedUtc = now,
        };

        if (!_process.Wrote(journal))
        {
            // Another AutoCAD session continues this job: from now on it is ours.
            journal.ProcessId = _process.ProcessId;
            journal.ProcessStartUtc = _process.StartUtc;
        }

        if (drawing is not null)
        {
            journal.DrawingPath = drawing.Path;
            journal.DrawingTitle = drawing.Title;
            journal.DrawingIsNamed = drawing.IsNamed;
        }

        return journal;
    }

    private void RetireOthers(JobJournal current)
    {
        var drawing = new JournalDrawing(current.DrawingPath!, current.DrawingTitle ?? string.Empty, current.DrawingIsNamed);
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (Read(file) is not { } other
                || other.Dismissed
                || string.Equals(other.Session, current.Session, StringComparison.OrdinalIgnoreCase)
                || !drawing.Matches(other)
                || _process.Wrote(other)
                || _isAlive(other.ProcessId, other.ProcessStartUtc))
            {
                continue;
            }

            other.Dismissed = true;
            Write(other, touch: false);
        }
    }

    private void Write(JobJournal journal, bool touch = true)
    {
        if (touch)
        {
            journal.UpdatedUtc = _utcNow();
        }

        System.IO.Directory.CreateDirectory(Directory);
        AtomicFile.WriteAllText(PathOf(journal.Session), JsonSerializer.Serialize(journal, CopilotJson.Indented));
    }

    private static JobJournal? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<JobJournal>(File.ReadAllText(path), CopilotJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
