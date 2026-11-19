using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Tools;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Recovery;

public sealed class CheckpointTests
{
    private static DrawingFingerprint Print(params long[] handles) => DrawingFingerprint.Of(handles, 3);

    private static CheckpointInfo Point(string name, DrawingFingerprint print, bool mark = true, string? file = null) => new()
    {
        Name = name,
        CreatedUtc = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
        Fingerprint = print,
        HasUndoMark = mark,
        SnapshotFile = file,
    };

    [Fact]
    public void Fingerprints_ignore_order_and_see_any_different_handle()
    {
        Assert.Equal(Print(1, 2, 3), Print(3, 1, 2));
        Assert.NotEqual(Print(1, 2, 3), Print(1, 2, 4));
        Assert.NotEqual(Print(1, 2), Print(1, 2, 3));
        Assert.NotEqual(DrawingFingerprint.Of([1, 2], 3), DrawingFingerprint.Of([1, 2], 4));
        Assert.Equal("3 objects, 3 layers", Print(1, 2, 3).Describe());
    }

    [Fact]
    public void Names_are_cleaned_and_compared_case_insensitively()
    {
        Assert.Equal("walls done", CheckpointNames.Normalize("  walls   done "));
        Assert.Null(CheckpointNames.Normalize("   "));
        Assert.Null(CheckpointNames.Normalize("a\nb"));
        Assert.Equal(CheckpointNames.MaxLength, CheckpointNames.Normalize(new string('x', 200))!.Length);
        Assert.Equal("before: Outer walls", CheckpointNames.ForRun("Outer walls"));
        Assert.Equal("before_Outer_walls", CheckpointNames.FileSafe("before: Outer walls"));
        Assert.Equal("checkpoint", CheckpointNames.FileSafe(":::"));

        var catalog = new CheckpointCatalog();
        catalog.Add(Point("Walls Done", Print(1)));
        Assert.NotNull(catalog.Find("walls done"));
    }

    [Fact]
    public void Restore_plan_goes_back_mark_by_mark_and_checks_every_state()
    {
        var catalog = new CheckpointCatalog();
        catalog.Add(Point("A", Print(1)));
        catalog.Add(Point("B", Print(1, 2)));
        catalog.Add(Point("C", Print(1, 2, 3)));

        var plan = catalog.PlanUndoRestore("a")!;
        Assert.Equal("A", plan.Name);
        Assert.Equal(3, plan.BackCount);
        Assert.Equal([Print(1, 2, 3), Print(1, 2), Print(1)], plan.Expected);
        Assert.Equal(1, catalog.PlanUndoRestore("C")!.BackCount);

        // Going back to A uses up all three marks; A gets a new one, B and C keep only their files.
        catalog.MarksUsed(3);
        catalog.Remarked("A", Print(1));
        Assert.True(catalog.Find("A")!.HasUndoMark);
        Assert.False(catalog.Find("B")!.HasUndoMark);
        Assert.Null(catalog.PlanUndoRestore("B"));
        Assert.Equal(1, catalog.MarkCount);
        Assert.Equal(1, catalog.PlanUndoRestore("A")!.BackCount);
    }

    [Fact]
    public void A_replaced_checkpoint_leaves_its_mark_to_be_passed()
    {
        var catalog = new CheckpointCatalog();
        catalog.Add(Point("walls", Print(1)));
        catalog.Add(Point("doors", Print(1, 2)));
        catalog.Add(Point("walls", Print(1, 2, 3)));

        Assert.Equal(2, catalog.Checkpoints.Count);
        Assert.Equal(3, catalog.MarkCount);
        Assert.Equal(2, catalog.PlanUndoRestore("doors")!.BackCount);
        Assert.Equal(1, catalog.PlanUndoRestore("walls")!.BackCount);
    }

    [Fact]
    public void Checkpoints_without_marks_or_after_invalidation_need_the_file()
    {
        var catalog = new CheckpointCatalog();
        catalog.Add(Point("file only", Print(1), mark: false, file: @"C:\x.dwg"));
        catalog.Add(Point("marked", Print(1, 2)));
        Assert.Null(catalog.PlanUndoRestore("file only"));
        Assert.Null(catalog.PlanUndoRestore("missing"));
        Assert.NotNull(catalog.PlanUndoRestore("marked"));

        catalog.InvalidateMarks();
        Assert.Equal(0, catalog.MarkCount);
        Assert.Null(catalog.PlanUndoRestore("marked"));
        Assert.All(catalog.Checkpoints, c => Assert.False(c.HasUndoMark));

        catalog.SnapshotRemoved("file only");
        Assert.Null(catalog.Find("file only")!.SnapshotFile);
    }

    [Fact]
    public void Catalog_keeps_at_most_the_newest_checkpoints()
    {
        var catalog = new CheckpointCatalog();
        for (var i = 0; i < CheckpointCatalog.MaxCheckpoints + 5; i++)
        {
            catalog.Add(Point("cp" + i, Print(i)));
        }

        Assert.Equal(CheckpointCatalog.MaxCheckpoints, catalog.Checkpoints.Count);
        Assert.Null(catalog.Find("cp0"));
        Assert.Equal(CheckpointCatalog.MaxCheckpoints + 5, catalog.PlanUndoRestore("cp5")!.BackCount + 5);
    }

    [Fact]
    public void Store_keeps_snapshots_per_drawing_and_prunes_the_oldest()
    {
        using var temp = new TestDirectory();
        var store = new CheckpointStore(temp.Path);
        var key = CheckpointStore.DrawingKey(@"C:\Projects\House G+1.dwg");
        Assert.StartsWith("house_g_1_", key, StringComparison.Ordinal);
        Assert.NotEqual(key, CheckpointStore.DrawingKey(@"D:\Other\House G+1.dwg"));
        Assert.Equal(key, CheckpointStore.DrawingKey(@"c:/projects/house g+1.dwg"));

        var start = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < CheckpointStore.MaxPerDrawing + 2; i++)
        {
            var file = store.NewSnapshotPath(key, "cp " + i, start.AddMinutes(i));
            File.WriteAllText(file, "dwg");
            var removed = store.Record(key, @"C:\Projects\House G+1.dwg", Point("cp " + i, Print(i), mark: false, file: file) with { CreatedUtc = start.AddMinutes(i) });
            Assert.Equal(i >= CheckpointStore.MaxPerDrawing ? 1 : 0, removed.Count);
        }

        var listed = store.List(key);
        Assert.Equal(CheckpointStore.MaxPerDrawing, listed.Count);
        Assert.Equal("cp 2", listed[0].Name);
        Assert.All(listed, c => Assert.True(File.Exists(c.SnapshotFile)));
        Assert.Equal(Print(2), listed[0].Fingerprint);
        Assert.Equal(CheckpointStore.MaxPerDrawing, Directory.GetFiles(store.FolderOf(key), "*.dwg").Length);

        // Same name again: replaced, its old file deleted.
        var again = store.NewSnapshotPath(key, "cp 5", start.AddHours(1));
        File.WriteAllText(again, "dwg2");
        store.Record(key, "x", Point("CP 5", Print(99), mark: false, file: again) with { CreatedUtc = start.AddHours(1) });
        Assert.Single(store.List(key), c => c.Name.Equals("cp 5", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CheckpointStore.MaxPerDrawing, Directory.GetFiles(store.FolderOf(key), "*.dwg").Length);

        Assert.Equal(0, store.CleanOld(CheckpointStore.MaxAge, DateTime.UtcNow));
        Assert.Equal(1, store.CleanOld(TimeSpan.FromDays(7), DateTime.UtcNow.AddDays(8)));
        Assert.Empty(store.List(key));
    }

    [Fact]
    public void Recovery_tool_definitions_are_well_formed()
    {
        var all = RecoveryTools.All();
        Assert.Equal(["create_checkpoint", "list_checkpoints", "restore_checkpoint", "get_resume_info"], all.Select(d => d.Name));
        Assert.All(all, d => Assert.Empty(d.Lint()));
        Assert.All(all, d => Assert.DoesNotContain(d.Name, BridgeTools.ReservedNames));
        Assert.Equal(ToolRisk.Destructive, RecoveryTools.RestoreCheckpointDefinition().Risk);
        Assert.Equal(ToolRisk.Write, RecoveryTools.CreateCheckpointDefinition().Risk);
        Assert.Equal(ToolRisk.Read, RecoveryTools.GetResumeInfoDefinition().Risk);
        Assert.Empty(JsonSchemaValidator.Validate(RecoveryTools.RestoreCheckpointDefinition().InputSchema, new System.Text.Json.Nodes.JsonObject { ["name"] = "walls", ["fromFile"] = true }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(RecoveryTools.CreateCheckpointDefinition().InputSchema, new System.Text.Json.Nodes.JsonObject()));
    }
}
