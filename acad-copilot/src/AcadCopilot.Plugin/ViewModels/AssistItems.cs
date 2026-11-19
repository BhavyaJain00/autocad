using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Recovery;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>"Claude reached its usage limit, ChatGPT continues" (AI switching, agent B's <see cref="ProviderSwitched"/>).</summary>
public sealed class SwitchNoticeItem : ChatItem
{
    /// <summary>Creates the notice.</summary>
    public SwitchNoticeItem(string message, string? detail)
    {
        Message = message;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
    }

    /// <summary>The user-facing line.</summary>
    public string Message { get; }

    /// <summary>The original failure text (tool tip), or null.</summary>
    public string? Detail { get; }
}

/// <summary>One finding row of a review card.</summary>
/// <param name="IsMajor">True for a major finding.</param>
/// <param name="Text">Problem, place and fix.</param>
public sealed record ReviewFindingRow(bool IsMajor, string Text)
{
    /// <summary>"Major" / "Minor".</summary>
    public string Badge => IsMajor ? "Major" : "Minor";
}

/// <summary>
/// The second-opinion review card (agent B's <see cref="ReviewCompleted"/>): reviewer, verdict, findings and a
/// "Fix these" button that sends the report's fix prompt (or queues it while the chat is still working).
/// </summary>
public sealed class ReviewCardItem : ChatItem
{
    private readonly Action<ReviewCardItem> _fix;
    private bool _isFixRequested;

    /// <summary>Creates the card.</summary>
    /// <param name="report">The review.</param>
    /// <param name="fix">Sends (or queues) the fix request.</param>
    public ReviewCardItem(ReviewReport report, Action<ReviewCardItem> fix)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        _fix = fix;
        Findings = [.. report.Findings.Select(f => new ReviewFindingRow(f.Severity == ReviewSeverity.Major, f.Text))];
        FixCommand = new RelayCommand(() => _fix(this), () => !IsClean && !IsFixRequested);
    }

    /// <summary>The review.</summary>
    public ReviewReport Report { get; }

    /// <summary>"Second opinion · ChatGPT".</summary>
    public string Title => "Second opinion · " + Report.ReviewerName;

    /// <summary>The verdict.</summary>
    public string Summary => Report.Summary;

    /// <summary>"2 major · 3 minor" or "No problems found".</summary>
    public string CountText => IsClean
        ? "No problems found"
        : $"{Report.MajorCount} major · {Report.Findings.Count - Report.MajorCount} minor";

    /// <summary>The findings, most important first.</summary>
    public IReadOnlyList<ReviewFindingRow> Findings { get; }

    /// <summary>True when the reviewer found nothing.</summary>
    public bool IsClean => Report.IsClean;

    /// <summary>True when there are findings to fix.</summary>
    public bool HasFindings => !IsClean;

    /// <summary>True after "Fix these" was clicked.</summary>
    public bool IsFixRequested
    {
        get => _isFixRequested;
        set
        {
            if (Set(ref _isFixRequested, value))
            {
                OnPropertyChanged(nameof(FixButtonText));
                FixCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>"Fix these" / "Fixing..." .</summary>
    public string FixButtonText => IsFixRequested ? "Fix requested" : "Fix these";

    /// <summary>Sends the report's fix prompt.</summary>
    public RelayCommand FixCommand { get; }
}

/// <summary>
/// "Resume previous job" (agent A's <see cref="ResumeInfo"/>): an unfinished drafting job found in the job journal, with
/// Resume (sends a message that makes the AI read get_resume_info and continue) and Dismiss.
/// </summary>
public sealed class ResumeCardItem : ChatItem
{
    private readonly Action<ResumeCardItem> _resume;
    private readonly Action<ResumeCardItem> _dismiss;
    private string? _outcome;

    /// <summary>Creates the card.</summary>
    public ResumeCardItem(ResumeInfo info, Action<ResumeCardItem> resume, Action<ResumeCardItem> dismiss)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        _resume = resume;
        _dismiss = dismiss;
        ResumeCommand = new RelayCommand(() => _resume(this), () => IsOpen);
        DismissCommand = new RelayCommand(() => _dismiss(this), () => IsOpen);
    }

    /// <summary>The unfinished job.</summary>
    public ResumeInfo Info { get; }

    /// <summary>"Resume previous job".</summary>
    public string Title => Info.CardTitle;

    /// <summary>"G+1 house · House-G1.dwg".</summary>
    public string Subtitle => string.Join(" · ", new[] { Info.PlanTitle, Info.DrawingTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>True when <see cref="Subtitle"/> is shown.</summary>
    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>What happened and what is left.</summary>
    public string Summary => Info.Summarize();

    /// <summary>"3 done · 4 left".</summary>
    public string ProgressText
    {
        get
        {
            var parts = new List<string>();
            if (Info.CompletedTasks.Count > 0)
            {
                parts.Add($"{Info.CompletedTasks.Count} done");
            }

            if (Info.RemainingTasks.Count > 0)
            {
                parts.Add($"{Info.RemainingTasks.Count} left");
            }

            if (Info.Interrupted is { } run)
            {
                parts.Add($"'{run.Title}' stopped at step {run.CompletedSteps.Count} of {run.StepsTotal}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>The remaining tasks (at most 6 shown).</summary>
    public IReadOnlyList<string> RemainingTasks => [.. Info.RemainingTasks.Take(6)];

    /// <summary>True when there are remaining tasks to list.</summary>
    public bool HasRemainingTasks => Info.RemainingTasks.Count > 0;

    /// <summary>True until Resume or Dismiss was clicked.</summary>
    public bool IsOpen => _outcome is null;

    /// <summary>"Resumed" / "Dismissed" once decided, else null.</summary>
    public string? Outcome
    {
        get => _outcome;
        set
        {
            if (Set(ref _outcome, value))
            {
                OnPropertiesChanged(nameof(IsOpen), nameof(HasOutcome));
                ResumeCommand.NotifyCanExecuteChanged();
                DismissCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True once decided.</summary>
    public bool HasOutcome => _outcome is not null;

    /// <summary>Continues the job.</summary>
    public RelayCommand ResumeCommand { get; }

    /// <summary>Forgets the job.</summary>
    public RelayCommand DismissCommand { get; }

    /// <summary>The message Resume sends.</summary>
    public string ResumePrompt =>
        "Resume the previous job that did not finish. Call get_resume_info first, check the drawing, then continue with what is left. " + Info.Summarize();
}
