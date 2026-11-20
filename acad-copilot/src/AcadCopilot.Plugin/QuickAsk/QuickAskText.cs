using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AcadCopilot.Plugin.Interaction;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>Where the point sent with a Quick Ask came from.</summary>
public enum QuickAskPointSource
{
    /// <summary>No point goes with the request.</summary>
    None,

    /// <summary>Where the cursor was in the drawing when the box opened.</summary>
    Cursor,

    /// <summary>A point the user clicked with Pick.</summary>
    Picked,
}

/// <summary>What one Quick Ask sends: the chat shows <see cref="DisplayText"/>, the AI receives <see cref="SentText"/>.</summary>
/// <param name="DisplayText">The request as the user sees it in the chat (with the point token).</param>
/// <param name="SentText">The request with its context lines (point, selection).</param>
/// <param name="SelectionNote">"5 objects selected" when the selection goes with it, else null.</param>
/// <param name="Subject">Short name of the job for the status bubble ("kitchen 10'x20'").</param>
public sealed record QuickAskMessage(string DisplayText, string SentText, string? SelectionNote, string Subject);

/// <summary>
/// Pure text helpers of Quick Ask: the message (request + "[point ...]" + "[selected in AutoCAD: ...]", the same tokens as
/// the panel's Point button and selection chip), the short subject shown in the status bubble ("kitchen 10'x20'") and
/// the bubble's lines.
/// </summary>
public static partial class QuickAskText
{
    /// <summary>Longest status line in the bubble.</summary>
    public const int MaxStatusLength = 46;

    /// <summary>Longest subject ("kitchen 10'x20'").</summary>
    public const int MaxSubjectLength = 30;

    /// <summary>The marker line that tells the AI where the request came from.</summary>
    public const string Origin = "(Sent with Quick Ask from the drawing area: do the whole job now, without asking more than needed.)";

    private static readonly string[] Fillers =
    [
        "please", "pls", "kindly", "can you", "could you", "would you", "will you", "i want you to", "i want to", "i want",
        "i need", "now", "just", "quickly", "go ahead and", "also",
    ];

    private static readonly string[] Verbs =
    [
        "make", "draw", "add", "create", "put", "place", "insert", "design", "build", "show", "give me", "do", "set up", "lay out", "layout",
        "move", "change", "fix", "resize", "extend", "copy", "mirror", "rotate", "delete", "erase", "remove", "dimension", "label",
        "hatch", "offset", "trim",
    ];

    private static readonly string[] Articles = ["the", "a", "an", "my", "this", "that", "these", "those", "me"];

    /// <summary>
    /// Builds the message of a Quick Ask: the typed request, then the point (labelled as the cursor position or the
    /// clicked point) and the selection block. Empty <paramref name="text"/> is not allowed.
    /// </summary>
    public static QuickAskMessage Compose(string text, PickedPoint? point, QuickAskPointSource pointSource, SelectionSnapshot? selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var request = OneSpaced(text);
        var display = request;
        var sent = new StringBuilder(request).Append("\n\n").Append(Origin);
        if (point is not null && pointSource != QuickAskPointSource.None)
        {
            var token = InteractionText.PointToken(point);
            display += " " + token;
            sent.Append('\n').Append(pointSource == QuickAskPointSource.Picked
                ? $"The user clicked this point for the request: {token}."
                : $"The user's cursor was here in the drawing when they asked (use it as the place, unless the request says otherwise): {token}.");
        }

        string? note = null;
        if (selection is { IsEmpty: false } selected)
        {
            sent.Append('\n').Append(InteractionText.SelectionBlock(selected));
            note = InteractionText.SelectionChip(selected);
        }

        return new QuickAskMessage(display, sent.ToString(), note, Subject(request));
    }

    /// <summary>
    /// The job in a few words, for the status bubble: "make the kitchen 10 by 20" -> "kitchen 10'x20'", "Draw a 4 ft wide
    /// staircase please" -> "4' wide staircase". Leading polite words, verbs and articles go; "A by B" / "A x B" sizes
    /// become A'xB' (feet, unless another unit is written).
    /// </summary>
    public static string Subject(string? text)
    {
        var line = OneSpaced(text ?? string.Empty).TrimEnd('.', '!', '?', ' ');
        if (line.Length == 0)
        {
            return "your request";
        }

        line = Sizes().Replace(line, SizeReplacement);
        line = FeetWords().Replace(line, m => m.Groups[1].Value + "'");

        // Strip leading fillers, verbs and articles, in any order, a few rounds.
        for (var round = 0; round < 4; round++)
        {
            var before = line;
            line = StripLeading(line, Fillers);
            line = StripLeading(line, Verbs);
            line = StripLeading(line, Articles);
            if (line == before)
            {
                break;
            }
        }

        line = StripTrailing(line, Fillers).Trim(' ', ',', '.');
        if (line.Length == 0)
        {
            return "your request";
        }

        return Shorten(line, MaxSubjectLength);
    }

    /// <summary>
    /// The working line: the plan task in progress ("Drawing kitchen walls..."), else what the chat is doing ("Typing
    /// AutoCAD commands: Kitchen walls..." -> "Drawing: Kitchen walls..."), else "Working on kitchen 10'x20'...".
    /// </summary>
    public static string Working(string subject, string? planTask, string? activity)
    {
        if (!string.IsNullOrWhiteSpace(planTask))
        {
            return Shorten(OneSpaced(planTask).TrimEnd('.'), MaxStatusLength - 3) + "...";
        }

        if (!string.IsNullOrWhiteSpace(activity))
        {
            var text = OneSpaced(activity).TrimEnd('.');
            const string typing = "Typing AutoCAD commands: ";
            if (text.StartsWith(typing, StringComparison.Ordinal))
            {
                return Shorten("Drawing: " + text[typing.Length..], MaxStatusLength - 3) + "...";
            }

            if (!text.Equals("Thinking", StringComparison.Ordinal) && !text.Equals("Waiting for your answer", StringComparison.Ordinal))
            {
                return Shorten(text, MaxStatusLength - 3) + "...";
            }
        }

        return Shorten("Working on " + subject, MaxStatusLength - 3) + "...";
    }

    /// <summary>"Waiting: the chat is still busy" (the request waits in the chat's queue).</summary>
    public static string Queued(int position) => position <= 1
        ? "Next in the chat's queue..."
        : string.Create(CultureInfo.InvariantCulture, $"Waiting in the chat's queue ({position})...");

    /// <summary>"Done - kitchen 10'x20'".</summary>
    public static string Done(string subject) => Shorten("Done - " + subject, MaxStatusLength);

    /// <summary>"Stopped - kitchen 10'x20'".</summary>
    public static string Stopped(string subject) => Shorten("Stopped - " + subject, MaxStatusLength);

    /// <summary>"Could not finish: &lt;first line of the error&gt;".</summary>
    public static string Failed(string? error)
    {
        var line = OneSpaced((error ?? string.Empty).Split('\n')[0]);
        return line.Length == 0 ? "Something went wrong" : Shorten("Could not finish: " + line, MaxStatusLength + 20);
    }

    /// <summary>"Undid 3 drawing steps" / "Undid the drawing step".</summary>
    public static string Undone(int calls) => calls == 1 ? "Undone" : string.Create(CultureInfo.InvariantCulture, $"Undone ({calls} drawing steps)");

    /// <summary>Text cut at a word boundary with an ellipsis when longer than <paramref name="max"/>.</summary>
    public static string Shorten(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':', '-', ' ') + "…";
    }

    private static string OneSpaced(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string StripLeading(string line, IEnumerable<string> words)
    {
        foreach (var word in words.OrderByDescending(w => w.Length))
        {
            if (line.Length > word.Length
                && line.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && !char.IsLetterOrDigit(line[word.Length]))
            {
                return line[word.Length..].TrimStart(' ', ',', ':');
            }
        }

        return line;
    }

    private static string StripTrailing(string line, IEnumerable<string> words)
    {
        foreach (var word in words.OrderByDescending(w => w.Length))
        {
            if (line.Length > word.Length
                && line.EndsWith(word, StringComparison.OrdinalIgnoreCase)
                && !char.IsLetterOrDigit(line[^(word.Length + 1)]))
            {
                return line[..^word.Length].TrimEnd(' ', ',');
            }
        }

        return line;
    }

    private static string SizeReplacement(Match match)
    {
        var a = match.Groups["a"].Value;
        var b = match.Groups["b"].Value;
        var unit = match.Groups["unit"].Value.Trim().ToLowerInvariant();
        return unit switch
        {
            "" or "'" or "ft" or "feet" or "foot" => $"{a}'x{b}'",
            "m" or "meter" or "meters" or "metre" or "metres" => $"{a}x{b} m",
            "mm" => $"{a}x{b} mm",
            "cm" => $"{a}x{b} cm",
            "in" or "inch" or "inches" or "\"" => $"{a}\"x{b}\"",
            _ => match.Value,
        };
    }

    // "10 by 20", "10x20", "10 X 20 ft", "10' x 20'", "10 into 20 feet" (Indian usage).
    [GeneratedRegex("""(?<a>\d+(?:\.\d+)?)'?\s*(?:by|x|X|×|into|\*)\s*(?<b>\d+(?:\.\d+)?)(?<unit>'|"|\s*(?:feet|foot|ft|meters?|metres?|mm|cm|m|inches|inch|in)\b)?""", RegexOptions.CultureInvariant)]
    private static partial Regex Sizes();

    // "4 ft", "4 feet" -> 4'.
    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\s*(?:feet|foot|ft)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FeetWords();
}
