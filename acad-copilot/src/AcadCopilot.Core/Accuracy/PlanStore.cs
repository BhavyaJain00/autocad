using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Recovery;

namespace AcadCopilot.Core.Accuracy;

/// <summary>A validated plan saved for a drawing.</summary>
/// <param name="Plan">The plan JSON as given to validate_plan.</param>
/// <param name="Drawing">The drawing's name.</param>
/// <param name="SavedUtc">When it was saved.</param>
/// <param name="ProcessId">AutoCAD's process id (to find the plan of a drawing saved under a new name).</param>
/// <param name="Vastu">Vastu hints were on.</param>
/// <param name="File">The file it came from.</param>
public sealed record StoredPlan(JsonObject Plan, string Drawing, DateTimeOffset SavedUtc, int ProcessId, bool Vastu, string File);

/// <summary>
/// The last validated plan per drawing: %LOCALAPPDATA%\AcadCopilot\plans\&lt;drawing&gt;_&lt;hash&gt;.json (atomic writes).
/// check_drawing compares the drawing with it.
/// </summary>
public sealed class PlanStore
{
    /// <summary>Creates a store over <paramref name="directory"/>.</summary>
    public PlanStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>The folder.</summary>
    public string Directory { get; }

    /// <summary>The store in %LOCALAPPDATA%\AcadCopilot\plans.</summary>
    public static PlanStore CreateDefault() => new(Path.Combine(AppPaths.Root, "plans"));

    /// <summary>The file of a drawing (key from <see cref="CheckpointStore.DrawingKey"/>).</summary>
    public string PathOf(string drawingKey) => Path.Combine(Directory, drawingKey + ".json");

    /// <summary>Saves <paramref name="plan"/> as the drawing's plan (replacing the previous one).</summary>
    public string Save(string drawingKey, string drawingName, JsonObject plan, int processId, bool vastu, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingKey);
        ArgumentNullException.ThrowIfNull(plan);
        System.IO.Directory.CreateDirectory(Directory);
        var file = PathOf(drawingKey);
        var json = new JsonObject
        {
            ["drawing"] = drawingName,
            ["savedUtc"] = now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["processId"] = processId,
            ["vastu"] = vastu,
            ["plan"] = plan.DeepClone(),
        };
        AtomicFile.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    /// <summary>The plan saved for <paramref name="drawingKey"/>, or null.</summary>
    public StoredPlan? Load(string drawingKey) => Read(PathOf(drawingKey));

    /// <summary>The newest plan saved from AutoCAD process <paramref name="processId"/> (a drawing renamed by SAVEAS), or null.</summary>
    public StoredPlan? LatestOfProcess(int processId)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return null;
        }

        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Read)
            .Where(p => p is not null && p.ProcessId == processId)
            .OrderByDescending(p => p!.SavedUtc)
            .FirstOrDefault();
    }

    private static StoredPlan? Read(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || json["plan"] is not JsonObject plan)
            {
                return null;
            }

            var saved = DateTimeOffset.TryParse(json["savedUtc"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var s) ? s : DateTimeOffset.MinValue;
            var pid = json["processId"] is JsonValue p && p.TryGetValue<int>(out var id) ? id : 0;
            var vastu = json["vastu"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            return new StoredPlan((JsonObject)plan.DeepClone(), json["drawing"]?.ToString() ?? string.Empty, saved, pid, vastu, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
