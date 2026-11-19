using System.Text;
using System.Text.RegularExpressions;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Panel;

/// <summary>A tool call of the current job as the chat saw it.</summary>
/// <param name="CallId">CLI call id.</param>
/// <param name="Name">Tool name ("run_commands").</param>
/// <param name="Arguments">Short argument summary ("title: Outer walls, steps: 5 items").</param>
public sealed record JournalCall(string CallId, string Name, string Arguments)
{
    /// <summary>Null while running, then the outcome.</summary>
    public bool? Success { get; init; }

    /// <summary>Result summary ("Typed 5 commands, created 12 objects").</summary>
    public string? Result { get; init; }
}

/// <summary>
/// What a chat session remembers about its conversation and the job in progress, so another provider can take the job
/// over (AI switching), a manual provider change keeps the context, and a reviewer knows what was asked: the recent user
/// and assistant messages, the latest Task Progress list, the tool calls of the current job and the last drawing facts.
/// Thread-safe; everything is size-capped.
/// </summary>
public sealed partial class JobJournal
{
    /// <summary>Most conversation entries kept.</summary>
    public const int MaxHistory = 10;

    /// <summary>Most characters kept per conversation entry.</summary>
    public const int MaxEntryLength = 1_500;

    /// <summary>Most tool calls kept for the current job.</summary>
    public const int MaxCalls = 60;

    private readonly Lock _gate = new();
    private readonly List<(string Role, string Text)> _history = [];
    private readonly List<JournalCall> _calls = [];
    private string? _request;
    private PlanUpdate? _plan;
    private bool _planChangedInJob;
    private string? _drawingFacts;
    private int _historyBeforeJob;

    /// <summary>The user's message that started the current job.</summary>
    public string? Request
    {
        get
        {
            lock (_gate)
            {
                return _request;
            }
        }
    }

    /// <summary>The latest Task Progress list (any job), or null.</summary>
    public PlanUpdate? Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    /// <summary>True when the conversation had messages before the current job.</summary>
    public bool HasEarlierConversation
    {
        get
        {
            lock (_gate)
            {
                return _historyBeforeJob > 0;
            }
        }
    }

    /// <summary>The tool calls of the current job (oldest first).</summary>
    public IReadOnlyList<JournalCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Successful run_commands calls of the current job.</summary>
    public int DrawingStages
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count(c => c.Name == Drafting.DraftingTools.RunCommands && c.Success == true);
            }
        }
    }

    /// <summary>
    /// True when the current job drew something and its Task Progress list (updated during the job, at least 3 tasks) has
    /// nothing pending or in progress: a finished drawing worth an automatic review.
    /// </summary>
    public bool FinishedDrawing
    {
        get
        {
            lock (_gate)
            {
                return _planChangedInJob
                    && _plan is { Tasks.Count: >= 3 } plan
                    && plan.Tasks.All(t => t.Status is PlanTaskStatus.Completed or PlanTaskStatus.Skipped or PlanTaskStatus.Failed)
                    && _calls.Any(c => c.Name == Drafting.DraftingTools.RunCommands && c.Success == true);
            }
        }
    }

    /// <summary>Starts a job: records the user's message and forgets the previous job's tool calls.</summary>
    public void BeginJob(string request)
    {
        lock (_gate)
        {
            _historyBeforeJob = _history.Count;
            _request = Cut(request, MaxEntryLength * 2);
            _calls.Clear();
            _planChangedInJob = false;
            AddLocked("User", request);
        }
    }

    /// <summary>Records an assistant message (text shown in the chat).</summary>
    public void AddAssistant(string text)
    {
        lock (_gate)
        {
            AddLocked("Assistant", text);
        }
    }

    /// <summary>Records the Task Progress list (update_plan).</summary>
    public void SetPlan(PlanUpdate plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            _plan = plan;
            _planChangedInJob = true;
        }
    }

    /// <summary>Records what an event of the running turn says about the job (messages, tool calls and results).</summary>
    public void Observe(AgentEvent agentEvent)
    {
        lock (_gate)
        {
            switch (agentEvent)
            {
                case TextDone done when !string.IsNullOrWhiteSpace(done.Text):
                    AddLocked("Assistant", done.Text);
                    break;
                case ToolCallStarted started:
                    if (_calls.Any(c => c.CallId == started.CallId))
                    {
                        break;
                    }

                    _calls.Add(new JournalCall(started.CallId, started.Name, Cut(started.Summary, 160)));
                    if (_calls.Count > MaxCalls)
                    {
                        _calls.RemoveAt(0);
                    }

                    break;
                case ToolCallFinished finished:
                    var index = _calls.FindIndex(c => c.CallId == finished.CallId);
                    if (index < 0)
                    {
                        break;
                    }

                    var call = _calls[index] with { Success = finished.Success, Result = Cut(finished.Success ? finished.Summary : finished.Error ?? finished.Summary, 240) };
                    _calls[index] = call;
                    if (finished.Success && call.Name is "get_drawing_info")
                    {
                        _drawingFacts = call.Result;
                    }

                    break;
            }
        }
    }

    /// <summary>Forgets everything (New chat).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            _calls.Clear();
            _request = null;
            _plan = null;
            _planChangedInJob = false;
            _drawingFacts = null;
            _historyBeforeJob = 0;
        }
    }

    /// <summary>
    /// The compact context block for another AI: earlier conversation (approved plan tables are in there), the task list,
    /// what the current job did so far and the last drawing facts. At most about 9,000 characters.
    /// </summary>
    /// <param name="includeCurrentJob">
    /// Include the current job's tool calls and its messages after the request (a switch in the middle of a job; the
    /// request itself is given separately).
    /// </param>
    /// <param name="includeCurrentRequest">
    /// Without <paramref name="includeCurrentJob"/>: also list the current job's messages (a review), or only what came
    /// before it (false: a new provider gets the user's new message separately).
    /// </param>
    public string DescribeContext(bool includeCurrentJob, bool includeCurrentRequest = true)
    {
        lock (_gate)
        {
            var builder = new StringBuilder();
            var earlier = includeCurrentJob || !includeCurrentRequest ? _history.Take(_historyBeforeJob).ToList() : [.. _history];
            if (includeCurrentJob)
            {
                // Messages of the current job after the request (e.g. the room table the user approved in this turn).
                earlier.AddRange(_history.Skip(_historyBeforeJob + 1));
            }

            var budget = 4_500;
            var shown = new List<string>();
            foreach (var (role, text) in Enumerable.Reverse(earlier))
            {
                var line = $"{role}: {text}";
                if (line.Length > budget)
                {
                    if (shown.Count == 0)
                    {
                        shown.Add(Cut(line, budget));
                    }

                    break;
                }

                budget -= line.Length;
                shown.Add(line);
            }

            if (shown.Count > 0)
            {
                builder.Append("CONVERSATION SO FAR (oldest first; an approved room table or plan is in here):\n");
                foreach (var line in Enumerable.Reverse(shown))
                {
                    builder.Append(line).Append("\n\n");
                }
            }

            if (_plan is { Tasks.Count: > 0 } plan)
            {
                builder.Append("TASK PROGRESS (latest update_plan").Append(plan.Title is { Length: > 0 } title ? ": " + title : string.Empty).Append("):\n");
                foreach (var task in plan.Tasks)
                {
                    builder.Append("- [").Append(task.Status switch
                    {
                        PlanTaskStatus.Completed => "done",
                        PlanTaskStatus.InProgress => "in progress",
                        PlanTaskStatus.Failed => "failed",
                        PlanTaskStatus.Skipped => "skipped",
                        _ => "todo",
                    }).Append("] ").Append(task.Title).Append('\n');
                }

                builder.Append('\n');
            }

            if (includeCurrentJob && _calls.Count > 0)
            {
                builder.Append("DONE IN THIS JOB (tool calls, oldest first; run_commands that succeeded are in the drawing):\n");
                foreach (var call in _calls.Where(c => c.Name is not ("capture_view" or "list_entities" or "get_entity" or "get_sysvars" or "list_layers")).TakeLast(30))
                {
                    builder.Append("- ").Append(call.Name);
                    if (call.Arguments.Length > 0)
                    {
                        builder.Append(" (").Append(call.Arguments).Append(')');
                    }

                    builder.Append(": ").Append(call.Success switch { true => "ok", false => "FAILED", null => "cut off (check the drawing)" });
                    if (!string.IsNullOrWhiteSpace(call.Result))
                    {
                        builder.Append(" - ").Append(call.Result);
                    }

                    builder.Append('\n');
                }

                builder.Append('\n');
            }

            if (_drawingFacts is { Length: > 0 } facts)
            {
                builder.Append("LAST DRAWING FACTS (get_drawing_info): ").Append(facts).Append("\n\n");
            }

            return builder.ToString().TrimEnd();
        }
    }

    private void AddLocked(string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var cut = Cut(text, MaxEntryLength);
        if (_history.Count > 0 && _history[^1].Role == role && _history[^1].Text == cut)
        {
            return;
        }

        _history.Add((role, cut));
        while (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
            _historyBeforeJob = Math.Max(0, _historyBeforeJob - 1);
        }
    }

    private static string Cut(string? text, int max)
    {
        var trimmed = Whitespace().Replace(text ?? string.Empty, " ").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>The prompts that hand a job to another provider (or back to the same one after a failure).</summary>
public static class JobHandover
{
    /// <summary>Marker the instructions explain ("A message starting with HANDOVER ...").</summary>
    public const string Marker = "HANDOVER";

    /// <summary>Most characters of a handover prompt.</summary>
    public const int MaxLength = 12_000;

    /// <summary>The user-facing line for a switch ("Claude reached its limit - continuing with ChatGPT.").</summary>
    public static string SwitchMessage(string from, string to, SwitchReason reason) => reason switch
    {
        SwitchReason.Limit => $"{from} reached its limit - continuing with {to}.",
        SwitchReason.SignIn => $"{from} is not signed in any more - continuing with {to}.",
        _ => $"{from} failed again - continuing with {to}.",
    };

    /// <summary>The prompt for <paramref name="to"/> continuing the job <paramref name="from"/> could not finish.</summary>
    public static string ForSwitch(JobJournal journal, string from, string to, SwitchReason reason, string failure)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var why = reason switch
        {
            SwitchReason.Limit => $"{from} reached its usage limit",
            SwitchReason.SignIn => $"{from}'s sign-in expired",
            _ => $"{from} failed",
        };
        var builder = new StringBuilder()
            .Append(Marker).Append(" from ").Append(from).Append(" to ").Append(to).Append(" (").Append(why).Append(": ").Append(Short(failure, 200)).Append(").\n")
            .Append("You are ").Append(to).Append(", continuing the SAME job in the same AutoCAD drawing. Work already done is in the drawing.\n\n")
            .Append("ORIGINAL REQUEST: ").Append(journal.Request ?? "(see the conversation)").Append("\n\n");
        var context = journal.DescribeContext(includeCurrentJob: true);
        if (context.Length > 0)
        {
            builder.Append(context).Append("\n\n");
        }

        builder.Append(
            "WHAT TO DO NOW: call get_drawing_info and capture_view to see the drawing as it is, compare it with the task list, " +
            "call update_plan with the full list (finished stages completed), then continue with the first unfinished stage. " +
            "The user already approved this job: do not ask again, do not start over and do not redraw finished stages. " +
            "If the last stage looks incomplete, fix it first. Tell the user in one line where you are continuing from.");
        return Fit(builder.ToString());
    }

    /// <summary>The prompt that retries the job with the same provider after a failure.</summary>
    public static string ForRetry(JobJournal journal, string failure)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var builder = new StringBuilder()
            .Append("Your previous attempt stopped with an error (").Append(Short(failure, 200)).Append("). Continue the same job.\n\n")
            .Append("ORIGINAL REQUEST: ").Append(journal.Request ?? "(see the conversation)").Append("\n\n");
        var context = journal.DescribeContext(includeCurrentJob: true);
        if (context.Length > 0)
        {
            builder.Append(context).Append("\n\n");
        }

        builder.Append("Look at the drawing (get_drawing_info, capture_view), then carry on from the first unfinished stage without redoing finished ones or asking again.");
        return Fit(builder.ToString());
    }

    /// <summary>
    /// The prompt when the user picked another provider for an ongoing conversation: the context so far, then the user's
    /// new message.
    /// </summary>
    public static string ForProviderChange(JobJournal journal, string from, string to, string message)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var context = journal.DescribeContext(includeCurrentJob: false, includeCurrentRequest: false);
        if (context.Length == 0)
        {
            return message;
        }

        var head = new StringBuilder()
            .Append(Marker).Append(": this chat was handled by ").Append(from).Append(" until now; you are ").Append(to)
            .Append(" taking over. Context (for you only, do not repeat it):\n\n")
            .Append(context)
            .Append("\n\nTHE USER'S NEW MESSAGE:\n")
            .ToString();
        var room = MaxLength - message.Length;
        return (room > 500 ? (head.Length <= room ? head : head[..(room - 1)] + "\n") : string.Empty) + message;
    }

    private static string Short(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }

    private static string Fit(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

/// <summary>Recognises chat messages that ask for a second-opinion review or for fixing its findings.</summary>
public static partial class ReviewRequests
{
    /// <summary>True for short messages such as "review this plan", "please review the drawing", "second opinion".</summary>
    public static bool IsReviewRequest(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 120 && ReviewPattern().IsMatch(text.Trim());

    /// <summary>True for short messages such as "fix these", "fix them", "fix the review findings".</summary>
    public static bool IsFixRequest(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 80 && FixPattern().IsMatch(text.Trim());

    /// <summary>What to focus on ("the first floor plan"), from a review request, or null.</summary>
    public static string? FocusOf(string text)
    {
        var match = ReviewPattern().Match(text.Trim());
        var focus = match.Success ? match.Groups["focus"].Value.Trim(' ', '.', '!', '?') : string.Empty;
        return focus.Length == 0 || focus is "this" or "it" or "this plan" or "the plan" or "the drawing" or "this drawing" or "my drawing" or "my plan" ? null : focus;
    }

    [GeneratedRegex(@"^(?:(?:please|pls|can you|could you|now)[,\s]+)*(?:(?:do\s+a\s+|give\s+(?:me\s+)?a\s+)?(?:second[-\s]opinion|senior[-\s]architect\s+review)(?:\s+(?:on|of|for))?|(?:review|re-?check|cross-?check)(?:\s+(?:of|on))?)(?<focus>\s+(?:this|the|my|it|that)\b(?:(?!\band\b|\bthen\b)[^\n]){0,50})?[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReviewPattern();

    [GeneratedRegex(@"^(?:(?:please|pls|ok|okay|yes|now)[,\s]+)*fix\s+(?:these|them|those|it|all|the\s+(?:review\s+)?(?:findings|issues|problems))(?:\s+(?:findings|issues|problems))?[.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixPattern();
}
