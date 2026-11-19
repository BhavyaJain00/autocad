using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Recovery;

public sealed class JobJournalTests
{
    private static readonly DateTime Start = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ProcessIdentity Old = new(1111, Start.AddHours(-2));
    private static readonly ProcessIdentity Current = new(2222, Start.AddHours(-1));
    private static readonly JournalDrawing House = new(@"C:\Projects\House-G1.dwg", "House-G1.dwg", IsNamed: true);

    private static PlanUpdate Plan(params (string Title, PlanTaskStatus Status)[] tasks) =>
        new() { Title = "G+1 house", Tasks = tasks.Select(t => new PlanTask { Title = t.Title, Status = t.Status }).ToList() };

    private static JobJournalStore Store(string directory, ProcessIdentity process, Func<int, DateTime, bool>? alive = null, Func<DateTime>? now = null) =>
        new(directory, process, alive ?? ((_, _) => false), now ?? (() => Start));

    [Fact]
    public void A_crash_mid_run_is_reported_with_what_was_done_and_what_remains()
    {
        using var temp = new TestDirectory();
        var before = Store(temp.Path, Old);
        before.RecordPlan("tab-1", House, Plan(("Setup", PlanTaskStatus.Completed), ("Outer walls", PlanTaskStatus.Completed), ("Inner walls", PlanTaskStatus.InProgress), ("Doors", PlanTaskStatus.Pending)));
        var setup = before.BeginRun("tab-1", House, "Outer walls", ["-LAYER", "RECTANG", "OFFSET"], checkpoint: null);
        before.EndRun("tab-1", setup, JournalRunStatus.Completed, [1, 2, 3], null);
        var run = before.BeginRun("tab-1", House, "Inner walls", ["-LAYER", "LINE", "LINE", "OFFSET"], "before: Inner walls");
        before.StepFinished("tab-1", run, 1);
        before.StepFinished("tab-1", run, 2);
        // AutoCAD crashes here: the run stays "running".

        var after = Store(temp.Path, Current);
        var offers = after.FindUnfinished(House, otherSessionsOnly: true);

        var info = Assert.Single(offers);
        Assert.Equal(ResumeReason.AutoCadStopped, info.Reason);
        Assert.True(info.IsUnfinished);
        Assert.Equal(["Setup", "Outer walls"], info.CompletedTasks);
        Assert.Equal(["Inner walls (in progress)", "Doors"], info.RemainingTasks);
        Assert.Equal(["Outer walls"], info.CompletedRuns);
        var interrupted = info.Interrupted!;
        Assert.Equal("Inner walls", interrupted.Title);
        Assert.Equal("interrupted", interrupted.State);
        Assert.Equal([1, 2], interrupted.CompletedSteps);
        Assert.Equal(["3 LINE", "4 OFFSET"], interrupted.RemainingSteps);
        Assert.Equal("before: Inner walls", interrupted.Checkpoint);

        var summary = info.Summarize();
        Assert.Contains("AutoCAD closed", summary, StringComparison.Ordinal);
        Assert.Contains("Remaining: Inner walls (in progress), Doors.", summary, StringComparison.Ordinal);
        Assert.Contains("'Inner walls' was interrupted after 2 of 4 steps; not typed: steps 3 LINE, 4 OFFSET.", summary, StringComparison.Ordinal);
        Assert.Contains("restore_checkpoint 'before: Inner walls'", info.Advice(), StringComparison.Ordinal);
        Assert.Equal("autocad_stopped", info.ToJson()["reason"]!.GetValue<string>());
        Assert.Equal(2, info.ToJson()["interruptedCall"]!["remainingSteps"]!.AsArray().Count);
    }

    [Fact]
    public void Journals_of_the_running_AutoCAD_are_offered_only_on_request()
    {
        using var temp = new TestDirectory();
        var store = Store(temp.Path, Current);
        var run = store.BeginRun("tab-1", House, "Room labels", ["TEXT", "TEXT"], null);
        store.EndRun("tab-1", run, JournalRunStatus.Cancelled, [1], "Stopped by the user");

        Assert.Empty(store.FindUnfinished(House, otherSessionsOnly: true));
        var info = Assert.Single(store.FindUnfinished(House));
        Assert.Equal(ResumeReason.SameSession, info.Reason);
        Assert.Equal("cancelled", info.Interrupted!.State);
        Assert.Contains("the AI stopped", info.Summarize(), StringComparison.Ordinal);
    }

    [Fact]
    public void Finished_dismissed_foreign_and_other_drawings_are_not_offered()
    {
        using var temp = new TestDirectory();
        var old = Store(temp.Path, Old);
        old.RecordPlan("done", House, Plan(("Setup", PlanTaskStatus.Completed), ("Walls", PlanTaskStatus.Skipped)));
        var run = old.BeginRun("no-plan", House, "Walls", ["LINE"], null);
        old.EndRun("no-plan", run, JournalRunStatus.Completed, [1], null);
        old.RecordPlan("dismissed", House, Plan(("Walls", PlanTaskStatus.Pending)));
        Assert.True(old.Dismiss("dismissed"));
        old.RecordPlan("other", new JournalDrawing(@"C:\Projects\Other.dwg", "Other.dwg", true), Plan(("Walls", PlanTaskStatus.Pending)));
        old.RecordPlan("unsaved", new JournalDrawing("Drawing1.dwg", "Drawing1.dwg", false), Plan(("Walls", PlanTaskStatus.Pending)));

        var current = Store(temp.Path, Current);
        Assert.Empty(current.FindUnfinished(House));
        Assert.Empty(current.FindUnfinished(new JournalDrawing("Drawing1.dwg", "Drawing1.dwg", false)));
        Assert.Single(current.FindUnfinished(new JournalDrawing(@"c:/projects/other.dwg", "other.dwg", true)));

        // An AutoCAD that still runs keeps its job.
        var alive = Store(temp.Path, Current, alive: (_, _) => true);
        Assert.Empty(alive.FindUnfinished(new JournalDrawing(@"C:\Projects\Other.dwg", "Other.dwg", true)));
    }

    [Fact]
    public void A_new_plan_on_the_same_drawing_retires_jobs_of_ended_sessions()
    {
        using var temp = new TestDirectory();
        Store(temp.Path, Old).RecordPlan("old-tab", House, Plan(("Walls", PlanTaskStatus.Pending)));
        var current = Store(temp.Path, Current);
        Assert.Single(current.FindUnfinished(House));

        current.RecordPlan("new-tab", House, Plan(("Walls", PlanTaskStatus.Completed), ("Doors", PlanTaskStatus.Pending)));

        var info = Assert.Single(current.FindUnfinished(House));
        Assert.Equal("new-tab", info.Session);
        Assert.True(current.Load("old-tab")!.Dismissed);
    }

    [Fact]
    public void Journals_are_written_atomically_survive_bad_files_and_old_ones_are_cleaned()
    {
        using var temp = new TestDirectory();
        var clock = Start;
        var store = Store(temp.Path, Current, now: () => clock);
        store.RecordPlan("tab:1/../x", House, Plan(("Walls", PlanTaskStatus.Pending)));
        Assert.True(File.Exists(store.PathOf("tab:1/../x")));
        Assert.Equal("tab_1____x", JobJournalStore.SafeKey("tab:1/../x"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.StartsWith("drawing-house-g1_", JobJournalStore.DrawingSessionKey(House), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(temp.Path, "broken.json"), "{ not json");
        Assert.Single(store.LoadAll());
        Assert.Null(store.Load("broken"));

        clock = Start.AddDays(8);
        File.SetLastWriteTimeUtc(Path.Combine(temp.Path, "broken.json"), Start);
        Assert.Equal(2, store.CleanOld(JobJournalStore.MaxAge));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Runs_are_capped_and_steps_recorded_once()
    {
        using var temp = new TestDirectory();
        var store = Store(temp.Path, Current);
        var first = store.BeginRun("s", House, "first", ["LINE", "LINE"], null);
        store.StepFinished("s", first, 2);
        store.StepFinished("s", first, 1);
        store.StepFinished("s", first, 2);
        store.StepFinished("s", 999, 1);
        Assert.Equal([1, 2], store.Load("s")!.Runs[0].CompletedSteps);

        for (var i = 0; i < JobJournalStore.MaxRuns + 3; i++)
        {
            store.BeginRun("s", House, "run " + i, ["LINE"], null);
        }

        var journal = store.Load("s")!;
        Assert.Equal(JobJournalStore.MaxRuns, journal.Runs.Count);
        Assert.Equal(JobJournalStore.MaxRuns + 4, journal.Runs[^1].Id);
        Assert.Equal(House.Path, journal.DrawingPath);
        Assert.Equal(Current.ProcessId, journal.ProcessId);
    }
}
