using System.Globalization;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Drafting;

/// <summary>
/// Pure helpers that read AutoCAD's command-line text (English AutoCAD): which history lines are new after an input,
/// which are prompts, echoes or messages, which messages mean "your input was rejected", whether a prompt accepts
/// spaces, and hints that help the LLM correct a rejected input.
/// </summary>
public static partial class CommandLineText
{
    /// <summary>
    /// The lines of <paramref name="after"/> that were not yet in <paramref name="before"/>, where both are the last N
    /// lines of the command-line history (oldest first) read before and after an input. The last line of
    /// <paramref name="before"/> may have grown in place (the prompt plus the typed input); it is then returned as
    /// the first new line. When the two snapshots do not overlap at all, every line of <paramref name="after"/> is new.
    /// </summary>
    public static IReadOnlyList<string> NewLines(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count == 0)
        {
            return after.ToList();
        }

        // Find the smallest shift s such that before[s..] lines up with the start of after.
        for (var shift = 0; shift < before.Count; shift++)
        {
            var overlap = before.Count - shift;
            if (overlap > after.Count)
            {
                continue;
            }

            var matches = true;
            for (var k = 0; k < overlap - 1; k++)
            {
                if (!string.Equals(Clean(before[shift + k]), Clean(after[k]), StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            var lastBefore = Clean(before[^1]);
            var lastCandidate = Clean(after[overlap - 1]);
            if (string.Equals(lastBefore, lastCandidate, StringComparison.Ordinal))
            {
                return after.Skip(overlap).ToList();
            }

            if (lastBefore.Length > 0 && lastCandidate.StartsWith(lastBefore, StringComparison.Ordinal))
            {
                // The current prompt line grew (prompt + typed input): report it as new.
                return after.Skip(overlap - 1).ToList();
            }
        }

        // No clean overlap (a line further up changed, e.g. a notice was inserted or re-wrapped). Old lines must never be
        // read again as new: anchor on the latest line of 'after' that is (or grew from) the last non-empty line of
        // 'before' and return only what follows it. Without any anchor everything scrolled by (more lines than a
        // snapshot holds), so every line is new.
        var anchorIndex = before.Count - 1;
        while (anchorIndex >= 0 && Clean(before[anchorIndex]).Length == 0)
        {
            anchorIndex--;
        }

        if (anchorIndex >= 0)
        {
            var anchor = Clean(before[anchorIndex]);
            for (var j = after.Count - 1; j >= 0; j--)
            {
                var candidate = Clean(after[j]);
                if (string.Equals(candidate, anchor, StringComparison.Ordinal))
                {
                    return after.Skip(j + 1).ToList();
                }

                if (candidate.StartsWith(anchor, StringComparison.Ordinal))
                {
                    return after.Skip(j).ToList();
                }
            }
        }

        return after.ToList();
    }

    /// <summary>
    /// Puts a command-line history snapshot into chronological order (oldest first). AutoCAD 2027's
    /// Internal.Utils.GetLastCommandLines returns the NEWEST line first (seen in the first real run: the history read
    /// after DIMSCALE, DIMLUNIT, DIMDEC, DIMFRAC, DIMZIN listed DIMZIN first). The order is checked against
    /// <paramref name="lastPrompt"/> (LASTPROMPT, the current line): when the last line is the current one the snapshot is
    /// already oldest first; when the first line is, it is reversed; when neither matches, the observed newest-first
    /// order is assumed.
    /// </summary>
    public static IReadOnlyList<string> Chronological(IReadOnlyList<string> lines, string? lastPrompt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count < 2)
        {
            return lines.ToList();
        }

        var current = Clean(lastPrompt);
        if (current.Length > 0)
        {
            var first = Clean(lines[0]);
            var last = Clean(lines[^1]);
            var lastIsCurrent = string.Equals(last, current, StringComparison.Ordinal);
            var firstIsCurrent = string.Equals(first, current, StringComparison.Ordinal);
            if (lastIsCurrent && !firstIsCurrent)
            {
                return lines.ToList();
            }
        }

        return Enumerable.Reverse(lines).ToList();
    }

    /// <summary>
    /// True when <paramref name="line"/> is a notice of AutoCAD's system-variable monitor (SYSMON / SYSVARMONITOR):
    /// "**** System Variable Changed ****", "**** No System Variable Changed ****", "3 of the monitored system variables
    /// have changed from the preferred value. Use SYSVARMONITOR command to view changes." They are informational and
    /// never mean that an input was rejected.
    /// </summary>
    public static bool IsSystemNotice(string? line)
    {
        var text = Clean(line);
        return text.Length > 0 && SystemNoticePattern().IsMatch(text);
    }

    /// <summary>
    /// True when AutoCAD has finished reacting to an input: it reacted at all (<paramref name="reacted"/>), the last poll
    /// brought no new events (<paramref name="quiet"/>), nothing typed is still queued, and it is either back at the
    /// Command prompt or showing a NEW prompt (a line ending in ':'). The echo of the input just typed
    /// ("Specify included angle: -90") is not a prompt: AutoCAD is still working on that input.
    /// </summary>
    public static bool IsSettled(bool reacted, bool quiet, bool? inputPending, bool commandActive, string? lastPrompt) =>
        reacted && quiet && inputPending != true && (!commandActive || IsPrompt(lastPrompt));

    /// <summary>
    /// True when a command is really waiting for more input: it is active and the current line is a prompt. An active
    /// command whose current line still shows the echo of the last input is busy processing it, not waiting.
    /// </summary>
    public static bool IsWaitingForInput(bool commandActive, string? lastPrompt) => commandActive && IsPrompt(lastPrompt);

    /// <summary>Whitespace-normalised, trimmed text of a command-line line (null-safe).</summary>
    public static string Clean(string? line) =>
        string.IsNullOrWhiteSpace(line) ? string.Empty : WhitespaceRun().Replace(line.Trim(), " ");

    /// <summary>
    /// True when <paramref name="line"/> is a prompt waiting for input: it ends with ':' ("Specify next point or
    /// [Undo]:", "Select objects:", "Command:").
    /// </summary>
    public static bool IsPrompt(string? line)
    {
        var text = Clean(line);
        return text.Length > 0 && text[^1] == ':';
    }

    /// <summary>True when <paramref name="line"/> is the idle "Command:" prompt.</summary>
    public static bool IsCommandPrompt(string? line) => string.Equals(Clean(line), "Command:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The message in <paramref name="lines"/> that says AutoCAD rejected the input ("Invalid point.", "Point or option
    /// keyword required.", "Requires numeric distance or two points.", "Unknown command ...", "... not found."), or
    /// null when there is none. Prompts and echoes are skipped.
    /// </summary>
    public static string? FindRejection(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var raw in lines)
        {
            var line = Clean(raw);
            if (line.Length == 0 || IsPrompt(line) || IsSystemNotice(line))
            {
                continue;
            }

            if (RejectionPattern().IsMatch(line))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>True when one of <paramref name="lines"/> is AutoCAD's cancel mark ("*Cancel*").</summary>
    public static bool ContainsCancel(IEnumerable<string> lines) =>
        lines.Any(l => Clean(l).Contains("*Cancel*", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The informative messages among <paramref name="lines"/>: not prompts, not the echo of the input typed at
    /// <paramref name="promptBefore"/>, not empty ("1 found", "Current settings: Offset distance = 9\"", "Invalid point.").
    /// </summary>
    public static IReadOnlyList<string> Messages(IEnumerable<string> lines, string? promptBefore)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var prompt = Clean(promptBefore);
        var messages = new List<string>();
        foreach (var raw in lines)
        {
            var line = Clean(raw);
            if (line.Length == 0 || IsPrompt(line) || IsSystemNotice(line))
            {
                continue;
            }

            if (prompt.Length > 0 && line.StartsWith(prompt, StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("Command:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            messages.Add(line);
        }

        return messages;
    }

    /// <summary>
    /// True when <paramref name="prompt"/> is one of AutoCAD's text prompts, where spaces are part of the answer:
    /// "Enter text:", "MText:", "Enter attribute prompt:", "Enter default attribute value:", "Enter new attribute value:",
    /// "Enter new string:", "Enter string to change:", "Enter new text &lt;...&gt;:". At any other prompt a space acts like
    /// Enter, so an input with spaces is split into several answers.
    /// </summary>
    public static bool IsTextPrompt(string? prompt)
    {
        var text = Clean(prompt);
        return text.Length > 0 && TextPromptPattern().IsMatch(text);
    }

    /// <summary>
    /// True when typing <paramref name="input"/> at a prompt that does not take text would split it into several
    /// answers: it contains a space, a tab or a ';'.
    /// </summary>
    public static bool HasSeparator(string? input) =>
        !string.IsNullOrEmpty(input) && input.Any(c => char.IsWhiteSpace(c) || c == ';');

    /// <summary>
    /// A short hint that helps correct a rejected input, or null: feet-inch input in a drawing that does not accept it,
    /// a bare number at a point prompt (AutoCAD then uses the mouse direction), an unknown option keyword, etc.
    /// </summary>
    public static string? HintForRejection(string input, string? prompt, string? message, DrawingUnits? units)
    {
        ArgumentNullException.ThrowIfNull(input);
        var promptText = Clean(prompt);
        var messageText = Clean(message);
        if (units is not null && !units.AcceptsFeetInches && FeetInches.ContainsFeetInchNotation(input))
        {
            return $"This drawing uses LUNITS {units.LUnits.ToString(CultureInfo.InvariantCulture)}, where AutoCAD rejects feet-inch input like {input}. " +
                   "Type plain numbers in drawing units instead" +
                   (units.InchesPerUnit is 1.0 ? " (1 unit = 1 inch: 30' = 360, 9\" = 9)" : string.Empty) +
                   ", or switch the drawing to architectural units first with -UNITS (or SETVAR LUNITS 4).";
        }

        if (promptText.Contains("point", StringComparison.OrdinalIgnoreCase) && IsBareNumber(input))
        {
            return "A single number at a point prompt is a distance in the direction of the mouse, which the copilot does not control. " +
                   "Type a coordinate instead: absolute x,y, relative @dx,dy or polar @distance<angle.";
        }

        if (messageText.StartsWith("Invalid option keyword", StringComparison.OrdinalIgnoreCase)
            || messageText.StartsWith("Point or option keyword required", StringComparison.OrdinalIgnoreCase))
        {
            return "Answer with a value the prompt asks for, or an option shown in [brackets] typed by its capital letters (for [Close/Undo] type C or U).";
        }

        if (messageText.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase))
        {
            return "That command does not exist in this AutoCAD; check the spelling or use another command.";
        }

        if (messageText.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "The named item does not exist in this drawing yet; create or load it first (for example -LAYER New, -LINETYPE Load, -STYLE) or use an existing name.";
        }

        return null;
    }

    private static bool IsBareNumber(string input) =>
        double.TryParse(input.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(
        @"^(\*+\s*)?(invalid\b|requires\b|point or option keyword required|value must\b|unknown command\b|ambiguous response|" +
        @"unable to\b|cannot\b|can't\b|can not\b|that command may not be invoked transparently|\*\*|unrecognized\b|" +
        @"no (matching|valid)\b)|\bnot found\b|\bis not valid\b|\bout of range\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RejectionPattern();

    [GeneratedRegex(
        @"^\*+\s*(no\s+)?system\s+variables?\s+changed\s*\*+$|\bmonitored system variables?\b|\bSYSVARMONITOR\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SystemNoticePattern();

    [GeneratedRegex(
        @"^(enter text\b|text:|mtext:|enter attribute prompt\b|enter default attribute value\b|enter attribute value\b|enter new attribute value\b|" +
        @"enter new string\b|enter string to change\b|enter new text\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TextPromptPattern();
}
