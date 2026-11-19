using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Drafting;

/// <summary>What kind of automatic fix the copilot applied to a run_commands step.</summary>
public enum InputFixKind
{
    /// <summary>A dialog command was replaced by its command-line ('-') version.</summary>
    CommandLineVersion,

    /// <summary>A coordinate was written with spaces or typographic characters ("10, 20", "10 20", "@10 &lt; 90").</summary>
    PointSyntax,

    /// <summary>A number or length was written with typographic characters (Unicode minus, curly quotes) or spaces.</summary>
    NumberSyntax,

    /// <summary>A length written in feet-inches or with a unit (mm, m, ft, in) was typed as drawing units.</summary>
    UnitConversion,

    /// <summary>An option keyword with a typo, wrong case or brackets was typed as the keyword's capital letters.</summary>
    OptionKeyword,

    /// <summary>An angle written with a degree sign or "deg" was typed as a plain number.</summary>
    AngleSyntax,

    /// <summary>The command still waited at a "finish" prompt after its inputs: the copilot pressed Enter.</summary>
    FinishEnter,
}

/// <summary>
/// One automatic syntax fix: what was asked, what was typed instead, and why. Fixes never change the meaning of the
/// geometry: they only rewrite how a value is written (or press the finishing Enter the LLM left out).
/// </summary>
public sealed record InputFix
{
    /// <summary>The kind of fix.</summary>
    public required InputFixKind Kind { get; init; }

    /// <summary>The input as the LLM wrote it (for <see cref="InputFixKind.FinishEnter"/>: the prompt; for
    /// <see cref="InputFixKind.CommandLineVersion"/>: the command).</summary>
    public required string Original { get; init; }

    /// <summary>What the copilot typed instead ("" = Enter).</summary>
    public required string Replacement { get; init; }

    /// <summary>Why, in plain words.</summary>
    public required string Reason { get; init; }

    /// <summary>One line for the result: "'30'' typed as '360' (LUNITS 2 ...)".</summary>
    public string Describe() => Kind switch
    {
        InputFixKind.FinishEnter => $"pressed Enter at '{Original}' ({Reason})",
        InputFixKind.CommandLineVersion => $"typed {Replacement} instead of {Original} ({Reason})",
        _ => $"'{Original}' typed as '{Replacement}' ({Reason})",
    };
}

/// <summary>
/// The pure rules of run_commands' self-healing: safe, syntax-only repairs of inputs that AutoCAD rejected (or that the
/// copilot would have to refuse), and the "finish" prompts where a forgotten trailing Enter may be pressed. The plug-in's
/// drafting engine applies them while typing and reports every applied fix ("auto-fixed: ...").
/// <para>Rules (each one only rewrites HOW a value is written, never WHAT it is):</para>
/// <list type="bullet">
/// <item><see cref="BeforeTyping"/>: typographic minus signs and quote marks become ASCII ("−90" -&gt; "-90",
/// "12′6″" -&gt; "12'6\""); spaces around ',' '&lt;' '@' in coordinates are removed ("10, 20" -&gt; "10,20"); two or three
/// plain numbers separated by spaces at a point prompt become a coordinate ("10 20" -&gt; "10,20"); a feet-inch length
/// with a space ("6 1/2\"") is written without one ("6-1/2\"").</item>
/// <item><see cref="AfterRejection"/>: lengths in feet-inches or with a unit (mm, cm, m, ft, in) that the drawing's
/// LUNITS does not take are converted to drawing units when INSUNITS gives the scale (never when bare numbers in the
/// same coordinate would become ambiguous); a degree sign or "deg" is removed from angles (AUNITS 0); an option keyword
/// with a typo, a lower-case letter that is not its capital, or brackets/quotes is typed as the keyword's capital letters
/// when exactly one option of the prompt fits.</item>
/// <item><see cref="IsFinishPrompt"/>: prompts where Enter only ends the command or selection (LINE/PLINE "Specify
/// next point", "Select objects:", TEXT "Enter text:", -HATCH "Specify internal point", TRIM/EXTEND, "&lt;Exit&gt;"
/// defaults, -LAYER/-LINETYPE main prompt, DIMCONTINUE/DIMBASELINE).</item>
/// <item><see cref="DashVersion"/>: the command-line version to retry when a command still opened a dialog box.</item>
/// </list>
/// Nothing is fixed at text prompts (TEXT's "Enter text:", attribute values): there every character is meant literally.
/// </summary>
public static partial class InputRepair
{
    /// <summary>Most Enters pressed automatically to finish one step.</summary>
    public const int MaxFinishEnters = 3;

    /// <summary>
    /// A syntax-only rewrite of <paramref name="input"/> before it is typed at <paramref name="prompt"/>, or null when it
    /// is fine as it is. Never applied at text prompts or to Enter ("").
    /// </summary>
    /// <param name="input">The input as the LLM wrote it.</param>
    /// <param name="prompt">The prompt it will be typed at (LASTPROMPT).</param>
    public static InputFix? BeforeTyping(string input, string? prompt)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || CommandLineText.IsTextPrompt(prompt))
        {
            return null;
        }

        var text = input;
        var reasons = new List<string>();
        var kind = InputFixKind.NumberSyntax;

        var ascii = ToAsciiMarks(text);
        if (!string.Equals(ascii, text, StringComparison.Ordinal) && IsValueLike(ascii))
        {
            text = ascii;
            reasons.Add("typographic minus or quote marks written as plain ASCII");
        }

        if (text.Any(char.IsWhiteSpace) && IsValueLike(text))
        {
            var tight = SpaceAroundSeparators().Replace(text, m => m.Value.Trim());
            if (!string.Equals(tight, text, StringComparison.Ordinal))
            {
                text = tight;
                kind = InputFixKind.PointSyntax;
                reasons.Add("spaces removed around , < @ (a space acts like Enter in AutoCAD)");
            }

            if (text.Any(char.IsWhiteSpace) && IsPointPrompt(prompt) && SpaceSeparatedNumbers().IsMatch(text))
            {
                text = WhitespaceRun().Replace(text.Trim(), ",");
                kind = InputFixKind.PointSyntax;
                reasons.Add("coordinates separated by spaces written as x,y");
            }

            if (text.Any(char.IsWhiteSpace) && TryTightenLengths(text, out var lengths))
            {
                text = lengths;
                reasons.Add("feet-inch length written without spaces (12'-6 1/2\" -> 12'6-1/2\")");
            }
        }

        return string.Equals(text, input, StringComparison.Ordinal) || text.Any(char.IsWhiteSpace)
            ? null
            : new InputFix { Kind = kind, Original = input, Replacement = text, Reason = string.Join("; ", reasons) };
    }

    /// <summary>
    /// A syntax-only rewrite of an input AutoCAD rejected at <paramref name="prompt"/> with <paramref name="message"/>,
    /// to be typed once at the same prompt, or null when no safe fix is known (the rejection is then reported to the AI).
    /// </summary>
    /// <param name="input">The input that was rejected (after <see cref="BeforeTyping"/>).</param>
    /// <param name="prompt">The prompt it was typed at.</param>
    /// <param name="message">AutoCAD's rejection message ("Invalid point.").</param>
    /// <param name="units">The drawing's unit settings.</param>
    /// <param name="angleUnits">AUNITS (0 = decimal degrees); null when unknown.</param>
    public static InputFix? AfterRejection(string input, string? prompt, string? message, DrawingUnits units, int? angleUnits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(units);
        if (input.Length == 0 || CommandLineText.IsTextPrompt(prompt))
        {
            return null;
        }

        if (TryFixKeyword(input, prompt, out var keyword, out var keywordReason))
        {
            return new InputFix { Kind = InputFixKind.OptionKeyword, Original = input, Replacement = keyword, Reason = keywordReason };
        }

        var text = input;
        var reasons = new List<string>();
        var kind = InputFixKind.AngleSyntax;
        if (angleUnits == 0 && TryStripDegrees(text, out var plainAngle))
        {
            text = plainAngle;
            reasons.Add("degree sign removed (angles are typed as plain degrees with AUNITS 0)");
        }

        if (TryConvertLengths(text, units, out var converted, out var conversionReason))
        {
            text = converted;
            kind = reasons.Count == 0 ? InputFixKind.UnitConversion : kind;
            reasons.Add(conversionReason);
        }

        return reasons.Count == 0 || string.Equals(text, input, StringComparison.Ordinal)
            ? null
            : new InputFix { Kind = kind, Original = input, Replacement = text, Reason = string.Join("; ", reasons) };
    }

    /// <summary>
    /// True when pressing Enter at <paramref name="prompt"/> only finishes the command or the current selection, so a
    /// step left waiting there after its inputs may be finished with Enter instead of being cancelled with Esc.
    /// </summary>
    /// <param name="prompt">The prompt the command waits at (LASTPROMPT).</param>
    /// <param name="canonicalCommand">The step's canonical command name ("LINE", "LAYER").</param>
    public static bool IsFinishPrompt(string? prompt, string? canonicalCommand)
    {
        var text = CommandLineText.Clean(prompt);
        if (!CommandLineText.IsPrompt(text) || CommandLineText.IsCommandPrompt(text))
        {
            return false;
        }

        if (DefaultIsExit().IsMatch(text))
        {
            return true;
        }

        if (text.Contains('<', StringComparison.Ordinal) && !DimensionContinuePrompt().IsMatch(text))
        {
            // Any other <default> would be accepted by Enter: that changes what is drawn.
            return false;
        }

        if (FinishPrompt().IsMatch(text) || DimensionContinuePrompt().IsMatch(text))
        {
            return true;
        }

        var command = (canonicalCommand ?? string.Empty).TrimStart('-').ToUpperInvariant();
        return command is "LAYER" or "LINETYPE" && text.StartsWith("Enter an option", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fix recorded when Enter finished a step at <paramref name="prompt"/>.</summary>
    public static InputFix FinishEnter(string prompt) => new()
    {
        Kind = InputFixKind.FinishEnter,
        Original = CommandLineText.Clean(prompt),
        Replacement = string.Empty,
        Reason = "the inputs ended without the finishing \"\"; Enter only ends the command or selection here",
    };

    /// <summary>
    /// The command-line version ("-NAME") to retry when <paramref name="command"/> opened a dialog box although dialogs
    /// were switched off, or null when it already was a '-' command, is AutoLISP, or its '-' version is not allowed
    /// without approval. The caller must still check that AutoCAD knows the '-' command.
    /// </summary>
    public static string? DashVersion(string command, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CommandPolicy.IsAutoLisp(command))
        {
            return null;
        }

        var parsed = CommandPolicy.Parse(command, aliases);
        if (parsed.Dash || parsed.CanonicalDash || parsed.Transparent || !CommandPolicy.IsCommandNameText(parsed.Canonical))
        {
            return null;
        }

        var dash = "-" + parsed.Canonical;
        return CommandPolicy.ClassifyCommand(dash, aliases).IsAllowed ? dash : null;
    }

    /// <summary>The fix recorded when <paramref name="replacement"/> was typed instead of <paramref name="original"/>.</summary>
    public static InputFix CommandLineVersion(string original, string replacement, string reason) => new()
    {
        Kind = InputFixKind.CommandLineVersion,
        Original = original,
        Replacement = replacement,
        Reason = reason,
    };

    /// <summary>
    /// True when <paramref name="prompt"/> asks for a point (first/next/second point, corner, center, insertion or base
    /// point, displacement...), where "10 20" can only mean the coordinate 10,20.
    /// </summary>
    public static bool IsPointPrompt(string? prompt)
    {
        var text = CommandLineText.Clean(prompt);
        return text.Length > 0 && !CommandLineText.IsTextPrompt(text) && PointPrompt().IsMatch(text);
    }

    /// <summary>
    /// The option keywords of <paramref name="prompt"/> ("Specify next point or [Close/Undo]:" -&gt; Close, Undo) with the
    /// capital letters that select them (Close -&gt; C, eXit -&gt; X, LType -&gt; LT, chord Length -&gt; L).
    /// </summary>
    public static IReadOnlyList<(string Option, string Capitals)> Options(string? prompt)
    {
        var text = CommandLineText.Clean(prompt);
        var open = text.LastIndexOf('[');
        var close = open < 0 ? -1 : text.IndexOf(']', open);
        if (open < 0 || close < 0)
        {
            return [];
        }

        var list = new List<(string, string)>();
        foreach (var raw in text[(open + 1)..close].Split('/'))
        {
            var option = raw.Trim();
            var capitals = new string(option.Where(c => char.IsUpper(c) || char.IsDigit(c) || c == '?').ToArray());
            if (option.Length > 0 && capitals.Length > 0)
            {
                list.Add((option, capitals));
            }
        }

        return list;
    }

    /// <summary>
    /// Converts every length of <paramref name="input"/> written in feet-inches or with a unit to drawing units, when the
    /// drawing does not take that notation and its scale is known. Handles plain lengths, x,y[,z] coordinates (with @, #
    /// or *) and polar distance&lt;angle (the angle is kept). Returns false when nothing needs converting, the scale is
    /// unknown, or a bare number in the same coordinate would become ambiguous.
    /// </summary>
    public static bool TryConvertLengths(string input, DrawingUnits units, out string converted, out string reason)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(units);
        converted = input;
        reason = string.Empty;
        if (units.InchesPerUnit is not { } inchesPerUnit || input.Length == 0)
        {
            return false;
        }

        var text = input.Trim();
        var prefix = text.Length > 0 && text[0] is '@' or '#' or '*' ? text[..1] : string.Empty;
        var body = text[prefix.Length..];
        string[] parts;
        string? angle = null;
        var polar = body.IndexOf('<', StringComparison.Ordinal);
        if (polar >= 0)
        {
            angle = body[(polar + 1)..];
            parts = [body[..polar]];
        }
        else
        {
            parts = body.Split(',');
        }

        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var anyConverted = false;
        var bareNonZero = false;
        var output = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (TryParseLengthWithUnit(part, units.AcceptsFeetInches, out var inches))
            {
                output[i] = FormatUnits(inches / inchesPerUnit);
                anyConverted = true;
            }
            else if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                output[i] = part;
                bareNonZero |= number != 0;
            }
            else
            {
                return false;
            }
        }

        if (!anyConverted || (bareNonZero && Math.Abs(inchesPerUnit - 1.0) > 1e-12))
        {
            return false;
        }

        converted = prefix + string.Join(",", output) + (angle is null ? string.Empty : "<" + angle);
        reason = units.AcceptsFeetInches
            ? $"metric lengths are typed in drawing units here (1 unit = 1 {units.UnitName})"
            : $"{units.FormatName} units (LUNITS {units.LUnits.ToString(CultureInfo.InvariantCulture)}) do not take feet-inches or unit names; " +
              $"converted to drawing units (1 unit = 1 {units.UnitName})";
        return true;
    }

    // A length that carries its unit: feet-inch marks or words (12'6", 6in, 12ft) - only when the drawing does not take
    // them - or a metric unit (300mm, 2.5m, 30cm). Bare numbers are not lengths with a unit.
    private static bool TryParseLengthWithUnit(string text, bool acceptsFeetInches, out double inches)
    {
        inches = 0;
        if (text.Length == 0)
        {
            return false;
        }

        var metric = MetricLength().Match(text);
        if (metric.Success)
        {
            var value = double.Parse(metric.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var millimetres = metric.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "mm" => 1.0,
                "cm" => 10.0,
                "m" => 1000.0,
                _ => double.NaN,
            };
            if (double.IsNaN(millimetres))
            {
                return false;
            }

            inches = value * millimetres / 25.4;
            return true;
        }

        if (acceptsFeetInches)
        {
            return false;
        }

        var hasUnit = FeetInches.ContainsFeetInchNotation(text) || ImperialWord().IsMatch(text);
        return hasUnit && FeetInches.TryParse(text, out inches);
    }

    private static string FormatUnits(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool TryStripDegrees(string input, out string result)
    {
        result = input;
        var polar = input.IndexOf('<', StringComparison.Ordinal);
        var head = polar >= 0 ? input[..(polar + 1)] : string.Empty;
        var angle = polar >= 0 ? input[(polar + 1)..] : input;
        var match = DegreeAngle().Match(angle.Trim());
        if (!match.Success)
        {
            return false;
        }

        result = head + match.Groups["value"].Value;
        return true;
    }

    private static bool TryFixKeyword(string input, string? prompt, out string keyword, out string reason)
    {
        keyword = string.Empty;
        reason = string.Empty;
        var options = Options(prompt);
        if (options.Count == 0)
        {
            return false;
        }

        var stripped = input.Trim().Trim('[', ']', '"', '\'', '<', '>', '.', ':', ' ');
        var underscore = stripped.StartsWith('_') ? "_" : string.Empty;
        var word = stripped.TrimStart('_');
        if (word.Length == 0 || !word.All(c => char.IsLetter(c) || c == ' '))
        {
            return false;
        }

        var compact = word.Replace(" ", string.Empty, StringComparison.Ordinal);

        // 1. Wrapped in brackets or quotes, or written in full: the option itself.
        var exact = options.Where(o => string.Equals(o.Capitals, compact, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(Compact(o.Option), compact, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(MainWord(o.Option), compact, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 1)
        {
            return false;
        }

        if (exact.Count == 1)
        {
            keyword = underscore + exact[0].Capitals;
            reason = $"option {exact[0].Option} of the prompt is typed by its capital letters {exact[0].Capitals}";
            return !string.Equals(keyword, input, StringComparison.OrdinalIgnoreCase);
        }

        // 2. One letter that is not a capital of any option but starts exactly one option (e for eXit).
        if (compact.Length == 1)
        {
            if (options.Any(o => string.Equals(o.Capitals, compact, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var starting = options.Where(o => MainWord(o.Option).StartsWith(compact, StringComparison.OrdinalIgnoreCase)).ToList();
            if (starting.Count != 1)
            {
                return false;
            }

            keyword = underscore + starting[0].Capitals;
            reason = $"'{compact}' is not a keyword of this prompt; option {starting[0].Option} is selected by {starting[0].Capitals}";
            return true;
        }

        // 3. A misspelt option (one edit for short words, two for long ones), when exactly one option is that close.
        if (compact.Length < 3)
        {
            return false;
        }

        var allowed = compact.Length >= 7 ? 2 : 1;
        var scored = options
            .Select(o => (o.Option, o.Capitals, Distance: Math.Min(EditDistance(compact, MainWord(o.Option)), EditDistance(compact, Compact(o.Option)))))
            .Where(o => o.Distance <= allowed)
            .OrderBy(o => o.Distance)
            .ToList();
        if (scored.Count == 0 || (scored.Count > 1 && scored[1].Distance == scored[0].Distance))
        {
            return false;
        }

        keyword = underscore + scored[0].Capitals;
        reason = $"'{word}' looks like a typo of option {scored[0].Option}, typed by its capital letters {scored[0].Capitals}";
        return true;
    }

    private static string Compact(string option) => option.Replace(" ", string.Empty, StringComparison.Ordinal);

    // The word of an option that holds its capitals ("chord Length" -> "Length", "draW boundary" -> "draW").
    private static string MainWord(string option)
    {
        var words = option.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.FirstOrDefault(w => w.Any(char.IsUpper)) ?? option;
    }

    /// <summary>Optimal string alignment distance (Levenshtein plus adjacent transpositions), case-insensitive.</summary>
    public static int EditDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    // "12'-6 1/2\"" -> "12'6-1/2\"" for every comma/polar component that is a feet-inch length with a space, when it
    // can be written back exactly (to 1/64 inch).
    private static bool TryTightenLengths(string input, out string result)
    {
        result = input;
        var prefix = input.Length > 0 && input[0] is '@' or '#' or '*' ? input[..1] : string.Empty;
        var body = input[prefix.Length..];
        var polar = body.IndexOf('<', StringComparison.Ordinal);
        var parts = polar >= 0 ? [body[..polar], body[polar..]] : body.Split(',');
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!part.Any(char.IsWhiteSpace) || part.StartsWith('<'))
            {
                continue;
            }

            if (!FeetInches.ContainsFeetInchNotation(part) || !FeetInches.TryParse(part, out var inches))
            {
                return false;
            }

            var written = FeetInches.FormatForInput(inches, 64);
            if (!FeetInches.TryParse(written, out var back) || Math.Abs(back - inches) > 1e-9)
            {
                return false;
            }

            parts[i] = written;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        result = prefix + (polar >= 0 ? string.Concat(parts) : string.Join(",", parts));
        return true;
    }

    private static string ToAsciiMarks(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '−' or '‐' or '‑' or '‒' or '–' or '—' or '﹣' or '－' => '-',
                '′' or '’' or '‘' => '\'',
                '″' or '“' or '”' => '"',
                '，' => ',',
                ' ' or ' ' or ' ' => ' ',
                _ => c,
            });
        }

        return builder.ToString();
    }

    // Only digits, signs, decimal points, separators, feet-inch marks, fraction bars, '<', '@', '#', '*' and spaces:
    // a number, length or coordinate, never a name or a keyword.
    private static bool IsValueLike(string text) => text.Any(char.IsAsciiDigit) && ValueLike().IsMatch(text);

    [GeneratedRegex(@"^[\s@#*+\-0-9.,<'""/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueLike();

    [GeneratedRegex(@"\s*[,<]\s*|^\s*[@#*]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAroundSeparators();

    [GeneratedRegex(@"^\s*[-+]?(\d+\.?\d*|\.\d+)(\s+[-+]?(\d+\.?\d*|\.\d+)){1,2}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceSeparatedNumbers();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^(?<value>[-+]?(\d+\.?\d*|\.\d+))\s*(?<unit>mm|cm|m)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetricLength();

    [GeneratedRegex(@"\d\s*(ft\.?|feet|foot|in\.?|inch|inches)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ImperialWord();

    [GeneratedRegex(@"^(?<value>[-+]?(\d+\.?\d*|\.\d+))\s*(°|º|deg|degs|degree|degrees)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DegreeAngle();

    [GeneratedRegex(
        @"\b(point|corner|cent(er|re)|end ?point|vertex|location|insertion|base|displacement|origin|position)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PointPrompt();

    [GeneratedRegex(@"<\s*e?x(it)?\s*>\s*:$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DefaultIsExit();

    [GeneratedRegex(
        @"^(specify next point\b|enter next point\b|specify end ?point of arc\b|select objects( to join)?:$|select objects or \[|" +
        @"enter text:$|specify internal point\b|pick internal point\b|select object to (trim|extend)\b|select objects to (trim|extend)\b|" +
        @"enter next line of annotation text:$|select continued dimension:$|select base dimension:$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FinishPrompt();

    [GeneratedRegex(@"^specify (a )?second extension line origin or \[select/undo\] <select>:$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DimensionContinuePrompt();
}
