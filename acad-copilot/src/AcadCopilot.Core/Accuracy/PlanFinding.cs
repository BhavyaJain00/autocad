using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Accuracy;

/// <summary>Severities of plan and drawing findings.</summary>
public static class Severity
{
    /// <summary>Must be fixed before drawing (validate_plan) or before the stage is done (check_drawing).</summary>
    public const string Error = "error";

    /// <summary>Should be fixed or confirmed with the user.</summary>
    public const string Warning = "warning";

    /// <summary>Information or an optional preference (Vastu, half-inch sizes, notes).</summary>
    public const string Hint = "hint";

    internal static int Rank(string severity) => severity switch
    {
        Error => 0,
        Warning => 1,
        _ => 2,
    };
}

/// <summary>One finding of validate_plan or check_drawing.</summary>
/// <param name="Severity">error / warning / hint.</param>
/// <param name="Code">Short machine code ("gap", "room_small", "door_clash").</param>
/// <param name="Floor">Floor name, or "" for plan-wide findings.</param>
/// <param name="Message">What is wrong, where, and how to fix it (feet-inches).</param>
public sealed record PlanFinding(string Severity, string Code, string Floor, string Message)
{
    /// <summary>"[error] Ground floor: ...".</summary>
    public string Text() => $"[{Severity}] " + (Floor.Length > 0 ? Floor + ": " : string.Empty) + Message;

    /// <summary>JSON form.</summary>
    public JsonObject ToJson() => new()
    {
        ["severity"] = Severity,
        ["code"] = Code,
        ["floor"] = Floor,
        ["message"] = Message,
    };
}

/// <summary>Collects findings (deduplicated, in order).</summary>
public sealed class FindingList
{
    private readonly List<PlanFinding> _items = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>All findings, errors first (stable).</summary>
    public IReadOnlyList<PlanFinding> Items => [.. _items.Select((f, i) => (f, i)).OrderBy(x => Accuracy.Severity.Rank(x.f.Severity)).ThenBy(x => x.i).Select(x => x.f)];

    /// <summary>Number of errors.</summary>
    public int Errors => _items.Count(f => f.Severity == Accuracy.Severity.Error);

    /// <summary>Number of warnings.</summary>
    public int Warnings => _items.Count(f => f.Severity == Accuracy.Severity.Warning);

    /// <summary>Number of hints.</summary>
    public int Hints => _items.Count(f => f.Severity == Accuracy.Severity.Hint);

    /// <summary>Adds an error.</summary>
    public void Error(string code, string floor, string message) => Add(Accuracy.Severity.Error, code, floor, message);

    /// <summary>Adds a warning.</summary>
    public void Warning(string code, string floor, string message) => Add(Accuracy.Severity.Warning, code, floor, message);

    /// <summary>Adds a hint.</summary>
    public void Hint(string code, string floor, string message) => Add(Accuracy.Severity.Hint, code, floor, message);

    /// <summary>Adds a finding unless the same one exists.</summary>
    public void Add(string severity, string code, string floor, string message)
    {
        if (_seen.Add(severity + "|" + floor + "|" + message))
        {
            _items.Add(new PlanFinding(severity, code, floor, message));
        }
    }
}
