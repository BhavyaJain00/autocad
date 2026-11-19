using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Agents;

/// <summary>Severity of a review finding.</summary>
public enum ReviewSeverity
{
    /// <summary>Makes the plan wrong or unusable.</summary>
    Major,

    /// <summary>Drafting quality.</summary>
    Minor,
}

/// <summary>One finding of a second-opinion review.</summary>
/// <param name="Severity">Major or minor.</param>
/// <param name="Text">Problem, place and fix ("Bedroom 2 door swings into the passage -> hinge it on the east jamb").</param>
public sealed record ReviewFinding(ReviewSeverity Severity, string Text);

/// <summary>
/// The result of a second-opinion review (<see cref="CopilotInstructions.Reviewer"/> answer format): a one-line summary
/// and the findings. <see cref="FixPrompt"/> is the message the review card's "Fix these" button sends to the chat.
/// </summary>
public sealed partial record ReviewReport
{
    /// <summary>Most findings kept.</summary>
    public const int MaxFindings = 12;

    /// <summary>The provider that reviewed.</summary>
    public required ProviderId Reviewer { get; init; }

    /// <summary>Its display name ("ChatGPT").</summary>
    public required string ReviewerName { get; init; }

    /// <summary>One-line verdict.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Findings, most important first.</summary>
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

    /// <summary>The reviewer's full answer (Markdown).</summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>True when the reviewer found nothing to fix.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>Major findings.</summary>
    public int MajorCount => Findings.Count(f => f.Severity == ReviewSeverity.Major);

    /// <summary>Markdown for the chat (the card's text where no card is shown).</summary>
    public string Markdown
    {
        get
        {
            var builder = new StringBuilder("**Second opinion (").Append(ReviewerName).Append(")**");
            if (Summary.Length > 0)
            {
                builder.Append(": ").Append(Summary);
            }

            foreach (var finding in Findings)
            {
                builder.Append("\n- ").Append(finding.Severity == ReviewSeverity.Major ? "**Major:** " : "Minor: ").Append(finding.Text);
            }

            if (!IsClean)
            {
                builder.Append("\n\nSay \"fix these\" (or use Fix these) to let the copilot correct them.");
            }

            return builder.ToString();
        }
    }

    /// <summary>The message that asks the drafting AI to fix the findings ("Fix these").</summary>
    public string FixPrompt
    {
        get
        {
            if (IsClean)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("Fix these findings of the second-opinion review (by ").Append(ReviewerName)
                .Append(") in the drawing. Check each against the drawing first; skip any that are wrong and say why. Group the fixes into a few run_commands stages, update_plan, check with capture_view, and do not ask before fixing:");
            var number = 1;
            foreach (var finding in Findings)
            {
                builder.Append('\n').Append(number++).Append(". [").Append(finding.Severity == ReviewSeverity.Major ? "major" : "minor").Append("] ").Append(finding.Text);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Reads the reviewer's answer: "SUMMARY: ..." and list lines "- [major] ..." / "- [minor] ..." (also "*", "1." and
    /// lines without a severity, which count as minor). Without a SUMMARY line the first non-list line is the summary.
    /// </summary>
    public static ReviewReport Parse(ProviderId reviewer, string reviewerName, string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        string? summary = null;
        var findings = new List<ReviewFinding>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var summaryMatch = SummaryLine().Match(line);
            if (summaryMatch.Success)
            {
                summary ??= Clean(summaryMatch.Groups["s"].Value);
                continue;
            }

            var item = ListItem().Match(line);
            if (!item.Success)
            {
                if (summary is null && findings.Count == 0 && !line.StartsWith('#'))
                {
                    summary = Clean(line);
                }

                continue;
            }

            var body = item.Groups["b"].Value.Trim();
            var severity = ReviewSeverity.Minor;
            var tag = SeverityTag().Match(body);
            if (tag.Success)
            {
                severity = tag.Groups["t"].Value.StartsWith("maj", StringComparison.OrdinalIgnoreCase) ||
                           tag.Groups["t"].Value.StartsWith("crit", StringComparison.OrdinalIgnoreCase) ||
                           tag.Groups["t"].Value.StartsWith("high", StringComparison.OrdinalIgnoreCase)
                    ? ReviewSeverity.Major
                    : ReviewSeverity.Minor;
                body = body[tag.Length..].Trim();
            }

            body = Clean(body);
            if (body.Length > 0 && findings.Count < MaxFindings)
            {
                findings.Add(new ReviewFinding(severity, body));
            }
        }

        return new ReviewReport
        {
            Reviewer = reviewer,
            ReviewerName = reviewerName,
            Summary = summary ?? (findings.Count == 0 ? "No problems found." : $"{findings.Count} finding(s)."),
            Findings = [.. findings.OrderBy(f => f.Severity)],
            RawText = raw,
        };
    }

    private static string Clean(string text) => text.Replace("**", string.Empty, StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"^\**\s*summary\s*\**\s*:\s*\**(?<s>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^(?:[-*•]|\d{1,2}[.)])\s+(?<b>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\**(?:\[(?<t>major|minor|critical|high|medium|low|info)\]|(?<t>major|minor|critical|high|medium|low|info)\s*:)\**\s*[:\-–]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeverityTag();
}
