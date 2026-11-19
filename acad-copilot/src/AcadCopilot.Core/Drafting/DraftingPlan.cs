using System.Text;

namespace AcadCopilot.Core.Drafting;

/// <summary>One step of a <see cref="DraftingPlan"/>: what will be typed, and the policy findings for it.</summary>
public sealed record PlannedStep
{
    /// <summary>1-based step number.</summary>
    public required int Number { get; init; }

    /// <summary>The command exactly as it will be typed (after a dialog command was replaced by its command-line version).</summary>
    public required string Command { get; init; }

    /// <summary>The command as the LLM sent it.</summary>
    public required string RequestedCommand { get; init; }

    /// <summary>Canonical command name (alias resolved, no prefixes): "LAYER", "LINE".</summary>
    public required string CanonicalName { get; init; }

    /// <summary>Inputs to type, in order.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>The step's note, if any.</summary>
    public string? Note { get; init; }

    /// <summary>Set when <see cref="Command"/> differs from <see cref="RequestedCommand"/>: why.</summary>
    public string? RewriteNote { get; init; }

    /// <summary>Policy findings that are not Allowed (most severe first); empty when the step is allowed.</summary>
    public IReadOnlyList<CommandVerdict> Findings { get; init; } = [];

    /// <summary>True when the step must not run.</summary>
    public bool IsBlocked => Findings.Any(f => f.IsBlocked);

    /// <summary>True when the step needs the user's approval (and is not blocked).</summary>
    public bool NeedsApproval => !IsBlocked && Findings.Any(f => f.NeedsApproval);

    /// <summary>The step as one line: "QSAVE" or "ERASE ALL ⏎" (inputs shortened).</summary>
    public string Describe(int maxInputs = 6)
    {
        var text = new StringBuilder(Command);
        foreach (var input in Inputs.Take(maxInputs))
        {
            text.Append(' ').Append(input.Length == 0 ? "<Enter>" : input.Length > 30 ? input[..30] + "..." : input);
        }

        if (Inputs.Count > maxInputs)
        {
            text.Append(" ... (").Append(Inputs.Count - maxInputs).Append(" more inputs)");
        }

        return text.ToString();
    }
}

/// <summary>
/// A checked run_commands request, ready for the drafting engine: dialog commands replaced by their command-line
/// versions, every step classified by <see cref="CommandPolicy"/>. Build it with <see cref="Create"/>.
/// </summary>
public sealed record DraftingPlan
{
    /// <summary>The call's title (undo group and panel row).</summary>
    public required string Title { get; init; }

    /// <summary>The steps in order.</summary>
    public IReadOnlyList<PlannedStep> Steps { get; init; } = [];

    /// <summary>True when any step is blocked: nothing may be typed.</summary>
    public bool IsBlocked => Steps.Any(s => s.IsBlocked);

    /// <summary>True when the user must approve this call before anything is typed.</summary>
    public bool NeedsApproval => !IsBlocked && Steps.Any(s => s.NeedsApproval);

    /// <summary>Notes about replaced commands ("Step 2: typed -LAYER instead of LAYER ...").</summary>
    public IReadOnlyList<string> RewriteNotes => Steps.Where(s => s.RewriteNote is not null).Select(s => $"Step {s.Number}: {s.RewriteNote}").ToList();

    /// <summary>Every blocking finding as an actionable error line ("Step 3 (SHELL): runs another program ...").</summary>
    public IReadOnlyList<string> BlockedErrors =>
        Steps.SelectMany(s => s.Findings.Where(f => f.IsBlocked).Select(f => $"Step {s.Number} ({s.Command}): {f.Describe()}.")).ToList();

    /// <summary>The steps that need approval.</summary>
    public IReadOnlyList<PlannedStep> ApprovalSteps => Steps.Where(s => s.NeedsApproval).ToList();

    /// <summary>
    /// Plain-words text for the approval card listing the exact commands, e.g.
    /// "Outer walls: the AI wants to run 1 command that needs your OK: step 5 QSAVE (saves the drawing file)".
    /// </summary>
    public string ApprovalSummary()
    {
        var steps = ApprovalSteps;
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append(Title).Append(": the AI wants to run ").Append(steps.Count)
            .Append(steps.Count == 1 ? " command that needs your OK: " : " commands that need your OK: ");
        text.Append(string.Join("; ", steps.Select(s =>
        {
            var reasons = string.Join(", ", s.Findings.Where(f => f.NeedsApproval).Select(f => f.Reason).Distinct());
            return $"step {s.Number} {s.Describe()} ({reasons})";
        })));
        return text.ToString();
    }

    /// <summary>Builds the plan: rewrites dialog commands to their command-line versions and runs the policy on every step.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="aliases">Command aliases (the user's acad.pgp); null = built-in defaults.</param>
    public static DraftingPlan Create(RunCommandsRequest request, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        aliases ??= CommandAliases.Default;
        var steps = new List<PlannedStep>(request.Steps.Count);
        for (var i = 0; i < request.Steps.Count; i++)
        {
            var step = request.Steps[i];
            var requested = step.Command.Trim();
            var command = requested;
            string? rewriteNote = null;
            if (CommandPolicy.CommandLineVersion(requested, aliases) is { } version)
            {
                command = version;
                rewriteNote = $"typed {version} instead of {requested} because {requested} opens a dialog box, palette or interactive mode; use {version} yourself next time.";
            }

            var parsed = CommandPolicy.IsAutoLisp(command) ? null : CommandPolicy.Parse(command, aliases);
            steps.Add(new PlannedStep
            {
                Number = i + 1,
                Command = command,
                RequestedCommand = requested,
                CanonicalName = parsed?.Canonical ?? "AutoLISP",
                Inputs = step.Inputs,
                Note = step.Note,
                RewriteNote = rewriteNote,
                Findings = CommandPolicy.CheckStep(command, step.Inputs, aliases),
            });
        }

        return new DraftingPlan { Title = request.Title, Steps = steps };
    }
}
