using AcadCopilot.Core.Learning;

namespace AcadCopilot.Plugin.Learning;

/// <summary>What "Stop &amp; learn" produced.</summary>
/// <param name="FilePath">The saved recording (knowledge\recordings\...md), or null when nothing was saved.</param>
/// <param name="Actions">Actions recorded.</param>
/// <param name="StyleMessage">What the local style analysis merged into style.md, or null.</param>
/// <param name="Problem">Why nothing (or not everything) was saved, or null.</param>
public sealed record RecordingOutcome(string? FilePath, int Actions, string? StyleMessage, string? Problem)
{
    /// <summary>The recording's file name, or null.</summary>
    public string? FileName => FilePath is null ? null : System.IO.Path.GetFileName(FilePath);
}

/// <summary>
/// The single "Learn from me" recorder of the AutoCAD session, shared by every chat tab. Only the user starts it (the
/// panel's Start learning button); it captures the user's own work in AutoCAD and never the copilot's. Its members are
/// called on AutoCAD's main thread (the panel's thread); <see cref="Changed"/> is raised there too.
/// </summary>
public interface IUserActionRecorder
{
    /// <summary>State, counters or elapsed time changed.</summary>
    event EventHandler? Changed;

    /// <summary>Off, recording or paused.</summary>
    RecorderState State { get; }

    /// <summary>The user's actions recorded so far (commands and layer changes).</summary>
    int ActionCount { get; }

    /// <summary>Time recorded, without pauses.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Starts a new recording. Returns null, or why it cannot start.</summary>
    string? Start();

    /// <summary>Stops capturing; what was recorded stays.</summary>
    void Pause();

    /// <summary>Captures again.</summary>
    void Resume();

    /// <summary>Stops, saves the recording file and merges the style of the user's new objects into style.md.</summary>
    Task<RecordingOutcome> StopAndSaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops and throws the recording away (nothing is saved).</summary>
    void Discard();
}
