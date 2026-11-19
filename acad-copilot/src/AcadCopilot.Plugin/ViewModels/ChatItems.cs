using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>One entry of the conversation list.</summary>
public abstract class ChatItem : ObservableObject
{
}

/// <summary>A message the user sent.</summary>
public sealed class UserMessageItem : ChatItem
{
    /// <summary>Creates the item.</summary>
    public UserMessageItem(string text, IReadOnlyList<string> attachmentPaths)
    {
        Text = text;
        AttachmentNames = [.. attachmentPaths.Select(Path.GetFileName).OfType<string>()];
    }

    /// <summary>Message text.</summary>
    public string Text { get; }

    /// <summary>True when there is text (a message may consist of attachments only).</summary>
    public bool HasText => Text.Length > 0;

    /// <summary>File names of attached files.</summary>
    public IReadOnlyList<string> AttachmentNames { get; }

    /// <summary>True when files were attached.</summary>
    public bool HasAttachments => AttachmentNames.Count > 0;

    /// <summary>"5 objects selected" when the message carried the user's AutoCAD selection, else null.</summary>
    public string? SelectionNote { get; init; }

    /// <summary>True when <see cref="SelectionNote"/> is shown.</summary>
    public bool HasSelectionNote => !string.IsNullOrEmpty(SelectionNote);

    /// <summary>True when the message came from the chat's queue.</summary>
    public bool FromQueue { get; init; }
}

/// <summary>Assistant text (Markdown), streamed or complete.</summary>
public sealed class AssistantMessageItem : ChatItem
{
    private string _text;
    private bool _isStreaming;

    /// <summary>Creates the item.</summary>
    public AssistantMessageItem(string text, bool isStreaming)
    {
        _text = text;
        _isStreaming = isStreaming;
    }

    /// <summary>Markdown text.</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    /// <summary>True while fragments are still arriving.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value);
    }

    /// <summary>Appends a streamed fragment.</summary>
    public void Append(string fragment)
    {
        if (fragment.Length > 0)
        {
            Text = _text + fragment;
        }
    }
}

/// <summary>One clickable option of a question card.</summary>
public sealed class OptionViewModel : ObservableObject
{
    private bool _isSelected;

    /// <summary>Creates the option.</summary>
    public OptionViewModel(QuestionViewModel owner, QuestionOption option)
    {
        Label = option.Label;
        Description = option.Description;
        Recommended = option.Recommended;
        IsMultiSelect = owner.MultiSelect;
        SelectCommand = new RelayCommand(() => owner.Choose(this), () => !owner.Card.IsAnswered);
    }

    /// <summary>Option text.</summary>
    public string Label { get; }

    /// <summary>Optional explanation.</summary>
    public string? Description { get; }

    /// <summary>True when there is an explanation.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>True for the recommended option.</summary>
    public bool Recommended { get; }

    /// <summary>" (Recommended)" for the recommended option, empty otherwise.</summary>
    public string RecommendedTag => Recommended ? " (Recommended)" : string.Empty;

    /// <summary>True when the question allows several options (check boxes instead of radio dots).</summary>
    public bool IsMultiSelect { get; }

    /// <summary>True when picked.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Picks (or toggles, for multi-select) this option.</summary>
    public RelayCommand SelectCommand { get; }
}

/// <summary>One question on a question card.</summary>
public sealed class QuestionViewModel : ObservableObject
{
    private bool _isOtherSelected;
    private string _customText = string.Empty;

    /// <summary>Creates the question.</summary>
    public QuestionViewModel(QuestionCardItem card, UserQuestion question)
    {
        Card = card;
        Header = string.IsNullOrWhiteSpace(question.Header) ? null : question.Header.Trim();
        Question = question.Question;
        AllowCustom = question.AllowCustom || question.Options.Count == 0;
        MultiSelect = question.MultiSelect;
        Options = [.. question.Options.Select(o => new OptionViewModel(this, o))];
        OtherCommand = new RelayCommand(() => IsOtherSelected = !IsOtherSelected, () => !Card.IsAnswered);
    }

    /// <summary>The card this question belongs to.</summary>
    public QuestionCardItem Card { get; }

    /// <summary>Short chip label.</summary>
    public string? Header { get; }

    /// <summary>True when there is a chip label.</summary>
    public bool HasHeader => Header is not null;

    /// <summary>Question text.</summary>
    public string Question { get; }

    /// <summary>Options.</summary>
    public IReadOnlyList<OptionViewModel> Options { get; }

    /// <summary>True when "Other..." is offered.</summary>
    public bool AllowCustom { get; }

    /// <summary>True when several options may be picked.</summary>
    public bool MultiSelect { get; }

    /// <summary>Hint shown under multi-select questions.</summary>
    public bool ShowMultiSelectHint => MultiSelect && Options.Count > 0;

    /// <summary>True when the free-text field is open.</summary>
    public bool IsOtherSelected
    {
        get => _isOtherSelected;
        set
        {
            if (Set(ref _isOtherSelected, value))
            {
                if (value && !MultiSelect)
                {
                    foreach (var option in Options)
                    {
                        option.IsSelected = false;
                    }
                }

                OnPropertyChanged(nameof(HasAnswer));
                Card.OnAnswerChanged();
            }
        }
    }

    /// <summary>Free-text answer.</summary>
    public string CustomText
    {
        get => _customText;
        set
        {
            if (Set(ref _customText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasAnswer));
                Card.OnAnswerChanged();
            }
        }
    }

    /// <summary>Opens/closes the free-text field.</summary>
    public RelayCommand OtherCommand { get; }

    /// <summary>True when the question has an answer.</summary>
    public bool HasAnswer =>
        Options.Any(o => o.IsSelected) || (IsOtherSelected && !string.IsNullOrWhiteSpace(CustomText));

    /// <summary>Picks an option (single select: replaces; multi-select: toggles).</summary>
    public void Choose(OptionViewModel option)
    {
        if (Card.IsAnswered)
        {
            return;
        }

        if (MultiSelect)
        {
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var other in Options)
            {
                other.IsSelected = ReferenceEquals(other, option);
            }

            _isOtherSelected = false;
            OnPropertyChanged(nameof(IsOtherSelected));
        }

        OnPropertyChanged(nameof(HasAnswer));
        Card.OnOptionChosen(this);
    }

    /// <summary>The answer in contract form.</summary>
    public QuestionAnswer ToAnswer() => new()
    {
        Question = Question,
        Selected = [.. Options.Where(o => o.IsSelected).Select(o => o.Label)],
        Custom = IsOtherSelected && !string.IsNullOrWhiteSpace(CustomText) ? CustomText.Trim() : null,
    };

    /// <summary>One-line answer shown once answered.</summary>
    public string AnswerText
    {
        get
        {
            var parts = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (IsOtherSelected && !string.IsNullOrWhiteSpace(CustomText))
            {
                parts.Add(CustomText.Trim());
            }

            return parts.Count == 0 ? "No answer" : string.Join(", ", parts);
        }
    }

    /// <summary>Label shown before the answer ("Bedrooms" or the question).</summary>
    public string AnswerLabel => Header ?? Question;

    /// <summary>Raises change notifications for the answer texts once the card is answered.</summary>
    public void RaiseAnswered() => OnPropertiesChanged(nameof(AnswerText), nameof(HasAnswer));
}

/// <summary>Clarifying questions from the AI (ask_user), answered in the panel.</summary>
public sealed class QuestionCardItem : ChatItem
{
    private readonly Action<QuestionResponse> _complete;
    private bool _isAnswered;
    private bool _isSkipped;

    /// <summary>Creates the card; <paramref name="complete"/> receives the response exactly once.</summary>
    public QuestionCardItem(QuestionRequest request, Action<QuestionResponse> complete)
    {
        _complete = complete;
        Questions = [.. request.Questions.Select(q => new QuestionViewModel(this, q))];
        SubmitCommand = new RelayCommand(Submit, () => !IsAnswered && Questions.All(q => q.HasAnswer));
        SkipCommand = new RelayCommand(() => Cancel(), () => !IsAnswered);
    }

    /// <summary>The questions.</summary>
    public IReadOnlyList<QuestionViewModel> Questions { get; }

    /// <summary>Card title.</summary>
    public string Title => Questions.Count == 1 ? "Question" : $"{Questions.Count} questions";

    /// <summary>
    /// True when an explicit Submit button is needed (several questions, multi-select or a typed answer);
    /// a single single-choice question is answered by clicking an option.
    /// </summary>
    public bool ShowSubmit => !IsAnswered && (Questions.Count != 1 || Questions[0].MultiSelect || Questions[0].IsOtherSelected || Questions[0].Options.Count == 0);

    /// <summary>True once answered or skipped (the card collapses).</summary>
    public bool IsAnswered
    {
        get => _isAnswered;
        private set
        {
            if (Set(ref _isAnswered, value))
            {
                OnPropertyChanged(nameof(ShowSubmit));
                OnPropertyChanged(nameof(IsOpen));
                RefreshCommands();
            }
        }
    }

    /// <summary>True while waiting for the user.</summary>
    public bool IsOpen => !IsAnswered;

    /// <summary>True when skipped/cancelled rather than answered.</summary>
    public bool IsSkipped
    {
        get => _isSkipped;
        private set => Set(ref _isSkipped, value);
    }

    /// <summary>Sends the answers.</summary>
    public RelayCommand SubmitCommand { get; }

    /// <summary>Skips the questions (the AI is told they were cancelled).</summary>
    public RelayCommand SkipCommand { get; }

    /// <summary>Called by questions when their answer changes.</summary>
    public void OnAnswerChanged()
    {
        OnPropertyChanged(nameof(ShowSubmit));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called when an option was clicked: a lone single-choice question submits immediately.</summary>
    public void OnOptionChosen(QuestionViewModel question)
    {
        OnAnswerChanged();
        if (Questions.Count == 1 && !question.MultiSelect && question.HasAnswer)
        {
            Submit();
        }
    }

    /// <summary>Completes the card as cancelled (Stop, New chat, Skip or the CLI abandoned the call).</summary>
    public void Cancel()
    {
        if (IsAnswered)
        {
            return;
        }

        IsSkipped = true;
        IsAnswered = true;
        _complete(new QuestionResponse { Cancelled = true });
    }

    private void Submit()
    {
        if (IsAnswered || !Questions.All(q => q.HasAnswer))
        {
            return;
        }

        IsAnswered = true;
        foreach (var question in Questions)
        {
            question.RaiseAnswered();
        }

        _complete(new QuestionResponse { Answers = [.. Questions.Select(q => q.ToAnswer())] });
    }

    private void RefreshCommands()
    {
        SubmitCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        foreach (var question in Questions)
        {
            question.OtherCommand.NotifyCanExecuteChanged();
            foreach (var option in question.Options)
            {
                option.SelectCommand.NotifyCanExecuteChanged();
            }
        }
    }
}

/// <summary>
/// Approval prompt shown before a risky AutoCAD step, for example run_commands asking before SAVE, PURGE or ERASE ALL
/// (title "Save the drawing (QSAVE)"). Nothing runs until the user chooses.
/// </summary>
public sealed class ApprovalCardItem : ChatItem
{
    private readonly Action<ApprovalDecision> _complete;
    private bool _isResolved;
    private string _decisionText = string.Empty;
    private bool _isRejected;
    private bool _showDetails;

    /// <summary>Creates the card; <paramref name="complete"/> receives the decision exactly once.</summary>
    public ApprovalCardItem(ApprovalRequest request, Action<ApprovalDecision> complete)
    {
        _complete = complete;
        ToolTitle = request.ToolTitle;
        ToolName = request.ToolName;
        Summary = request.Summary;
        Risk = request.Risk;
        ArgumentsText = PrettyJson(request.ArgumentsJson);
        ApproveCommand = new RelayCommand(() => Resolve(ApprovalDecision.Approve), () => !IsResolved);
        RejectCommand = new RelayCommand(() => Resolve(ApprovalDecision.Reject), () => !IsResolved);
        AlwaysCommand = new RelayCommand(() => Resolve(ApprovalDecision.AlwaysApprove), () => !IsResolved && ShowAlways);
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
    }

    /// <summary>What needs approval ("Save the drawing (QSAVE)").</summary>
    public string ToolTitle { get; }

    /// <summary>Tool name, or "tool:title" for an approval asked inside a call ("run_commands:Save the drawing (QSAVE)").</summary>
    public string ToolName { get; }

    /// <summary>What will happen.</summary>
    public string Summary { get; }

    /// <summary>Risk class.</summary>
    public ToolRisk Risk { get; }

    /// <summary>True for destructive tools (no "Always allow").</summary>
    public bool IsDestructive => Risk == ToolRisk.Destructive;

    /// <summary>"Always allow" is offered only for non-destructive tools.</summary>
    public bool ShowAlways => !IsDestructive;

    /// <summary>Pretty-printed arguments, or null.</summary>
    public string? ArgumentsText { get; }

    /// <summary>True when arguments are available.</summary>
    public bool HasArguments => ArgumentsText is not null;

    /// <summary>True when the argument details are shown.</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set => Set(ref _showDetails, value);
    }

    /// <summary>True once decided (the card collapses).</summary>
    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (Set(ref _isResolved, value))
            {
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    /// <summary>True while waiting for the user.</summary>
    public bool IsPending => !IsResolved;

    /// <summary>True when rejected (or cancelled).</summary>
    public bool IsRejected
    {
        get => _isRejected;
        private set => Set(ref _isRejected, value);
    }

    /// <summary>Text shown after the decision.</summary>
    public string DecisionText
    {
        get => _decisionText;
        private set => Set(ref _decisionText, value);
    }

    /// <summary>Approve once.</summary>
    public RelayCommand ApproveCommand { get; }

    /// <summary>Reject.</summary>
    public RelayCommand RejectCommand { get; }

    /// <summary>Approve and stop asking for this tool in this chat.</summary>
    public RelayCommand AlwaysCommand { get; }

    /// <summary>Shows/hides the argument details.</summary>
    public RelayCommand ToggleDetailsCommand { get; }

    /// <summary>Resolves the card as rejected because the turn was stopped.</summary>
    public void Cancel(string reason)
    {
        if (!IsResolved)
        {
            Complete(ApprovalDecision.Reject, reason);
        }
    }

    /// <summary>Resolves the card with the user's decision.</summary>
    public void Resolve(ApprovalDecision decision)
    {
        if (IsResolved)
        {
            return;
        }

        Complete(decision, decision switch
        {
            ApprovalDecision.Approve => "Approved",
            ApprovalDecision.AlwaysApprove => "Approved (always for this chat)",
            _ => "Rejected",
        });
    }

    private void Complete(ApprovalDecision decision, string text)
    {
        DecisionText = text;
        IsRejected = decision == ApprovalDecision.Reject;
        IsResolved = true;
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        AlwaysCommand.NotifyCanExecuteChanged();
        _complete(decision);
    }

    private static string? PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

/// <summary>An inline error (turn failed).</summary>
public sealed class ErrorItem : ChatItem
{
    /// <summary>Creates the item.</summary>
    public ErrorItem(string title, string message, bool showSignIn, string? providerName, RelayCommand? signInCommand)
    {
        Title = title;
        Message = message;
        ShowSignIn = showSignIn && signInCommand is not null;
        SignInText = providerName is null ? "Sign in" : $"Sign in to {providerName}";
        SignInCommand = signInCommand;
    }

    /// <summary>Short title ("Not signed in").</summary>
    public string Title { get; }

    /// <summary>Explanation.</summary>
    public string Message { get; }

    /// <summary>True when the Sign in button is shown.</summary>
    public bool ShowSignIn { get; }

    /// <summary>Button text.</summary>
    public string SignInText { get; }

    /// <summary>Opens the CLI's login terminal.</summary>
    public RelayCommand? SignInCommand { get; }
}

/// <summary>Footer under a finished turn ("Claude · Default · 12 s · 3.1k tokens").</summary>
public sealed class TurnFooterItem : ChatItem
{
    /// <summary>Creates the footer.</summary>
    public TurnFooterItem(string text, TurnOutcome outcome)
    {
        Text = text;
        Outcome = outcome;
    }

    /// <summary>Footer text.</summary>
    public string Text { get; }

    /// <summary>How the turn ended.</summary>
    public TurnOutcome Outcome { get; }

    /// <summary>True when completed normally.</summary>
    public bool IsCompleted => Outcome == TurnOutcome.Completed;

    /// <summary>True when the user stopped it.</summary>
    public bool IsStopped => Outcome == TurnOutcome.Stopped;

    /// <summary>True when it failed.</summary>
    public bool IsFailed => Outcome == TurnOutcome.Failed;
}

/// <summary>How a turn ended.</summary>
public enum TurnOutcome
{
    /// <summary>Normal end.</summary>
    Completed,

    /// <summary>The user pressed Stop.</summary>
    Stopped,

    /// <summary>Error.</summary>
    Failed,
}

/// <summary>One row of the task checklist.</summary>
public sealed class PlanTaskViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    public PlanTaskViewModel(int number, PlanTask task)
    {
        Number = number;
        Title = task.Title;
        Status = task.Status;
    }

    /// <summary>1-based position.</summary>
    public int Number { get; }

    /// <summary>Task text.</summary>
    public string Title { get; }

    /// <summary>Status.</summary>
    public PlanTaskStatus Status { get; }

    /// <summary>Pending.</summary>
    public bool IsPending => Status == PlanTaskStatus.Pending;

    /// <summary>In progress (spinner).</summary>
    public bool IsInProgress => Status == PlanTaskStatus.InProgress;

    /// <summary>Completed (check).</summary>
    public bool IsCompleted => Status == PlanTaskStatus.Completed;

    /// <summary>Failed (cross).</summary>
    public bool IsFailed => Status == PlanTaskStatus.Failed;

    /// <summary>Skipped (struck through).</summary>
    public bool IsSkipped => Status == PlanTaskStatus.Skipped;
}

/// <summary>The "Task Progress (n/m)" checklist.</summary>
public sealed class PlanViewModel : ObservableObject
{
    private string? _title;
    private bool _isExpanded = true;

    /// <summary>Creates an empty checklist.</summary>
    public PlanViewModel()
    {
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Rows.</summary>
    public ObservableCollection<PlanTaskViewModel> Tasks { get; } = [];

    /// <summary>Optional title from the AI.</summary>
    public string? Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value))
            {
                OnPropertyChanged(nameof(HasTitle));
            }
        }
    }

    /// <summary>True when a title was given.</summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>Completed tasks.</summary>
    public int Done => Tasks.Count(t => t.IsCompleted);

    /// <summary>All tasks.</summary>
    public int Total => Tasks.Count;

    /// <summary>"Task Progress (2/5)".</summary>
    public string Header => string.Create(CultureInfo.InvariantCulture, $"Task Progress ({Done}/{Total})");

    /// <summary>0..1 for the progress bar.</summary>
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;

    /// <summary>True when there is at least one task.</summary>
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>True when all tasks are completed or skipped.</summary>
    public bool IsFinished => HasTasks && Tasks.All(t => t.IsCompleted || t.IsSkipped);

    /// <summary>The task currently in progress.</summary>
    public string? CurrentTaskText => Tasks.FirstOrDefault(t => t.IsInProgress)?.Title;

    /// <summary>Line under the header: "Now: Outer walls" while a task runs, otherwise the plan's title.</summary>
    public string? SubtitleText => CurrentTaskText is { } current ? "Now: " + current : Title;

    /// <summary>True when <see cref="SubtitleText"/> is shown.</summary>
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(SubtitleText);

    /// <summary>True when the rows are shown.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Expands/collapses.</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Replaces the checklist.</summary>
    public void Update(PlanUpdate plan)
    {
        Title = plan.Title;
        Tasks.Clear();
        var number = 1;
        foreach (var task in plan.Tasks)
        {
            Tasks.Add(new PlanTaskViewModel(number++, task));
        }

        OnPropertiesChanged(nameof(Done), nameof(Total), nameof(Header), nameof(Fraction), nameof(HasTasks), nameof(IsFinished), nameof(CurrentTaskText), nameof(SubtitleText), nameof(HasSubtitle));
    }

    /// <summary>Clears the checklist.</summary>
    public void Clear() => Update(new PlanUpdate());

    /// <summary>Copies the current checklist into a conversation item.</summary>
    public PlanCardItem Snapshot()
    {
        var copy = new PlanViewModel();
        copy.Update(new PlanUpdate
        {
            Title = Title,
            Tasks = [.. Tasks.Select(t => new PlanTask { Title = t.Title, Status = t.Status })],
        });
        copy.IsExpanded = false;
        return new PlanCardItem(copy);
    }
}

/// <summary>A finished checklist kept in the conversation.</summary>
public sealed class PlanCardItem : ChatItem
{
    /// <summary>Creates the item.</summary>
    public PlanCardItem(PlanViewModel plan)
    {
        Plan = plan;
    }

    /// <summary>The checklist.</summary>
    public PlanViewModel Plan { get; }
}

/// <summary>A file attached to the next message.</summary>
public sealed class AttachmentViewModel
{
    /// <summary>Creates the attachment.</summary>
    public AttachmentViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        IsPdf = extension == ".pdf";
        IsImage = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    /// <summary>Absolute path.</summary>
    public string Path { get; }

    /// <summary>File name.</summary>
    public string Name { get; }

    /// <summary>True for PDFs.</summary>
    public bool IsPdf { get; }

    /// <summary>True for images.</summary>
    public bool IsImage { get; }
}

/// <summary>Formatting helpers for tool names and durations.</summary>
public static class ToolNames
{
    /// <summary>"run_commands" -> "Run commands"; "mcp__acad__get_drawing_info" -> "Get drawing info".</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Tool";
        }

        var (_, bare) = ToolNameHelper.Split(name);
        if (!bare.Contains('_', StringComparison.Ordinal) && !bare.Contains('-', StringComparison.Ordinal))
        {
            return bare;
        }

        var words = bare.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words).ToLowerInvariant();
        return text.Length == 0 ? bare : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>"0.4 s", "12 s", "2 min 5 s".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 10)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s");
        }

        if (duration.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalSeconds} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds} s");
    }

    /// <summary>"950", "3.4k", "1.2M".</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        < 1000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1000.0:0.#}k"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.#}M"),
    };
}
