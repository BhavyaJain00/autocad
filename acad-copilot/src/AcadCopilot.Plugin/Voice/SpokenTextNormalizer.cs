using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// Turns dictated text into the way a drafting prompt is typed (pure, no Windows speech types):
/// <list type="bullet">
/// <item>measures: "30 by 40 feet" -> 30' x 40', "9 inch" -> 9", "four and a half inch" -> 4 1/2",
/// "10 feet 6 inches" -> 10'-6", "two and a half feet" -> 2'-6", "30 into 40 feet" -> 30' x 40' (Indian usage),
/// "1200 square feet" -> 1200 sq ft; numbers may be digits or words ("thirty", "forty-five", "twelve hundred");</item>
/// <item>"G plus one" -> G+1;</item>
/// <item>common drafting words split by the recognizer: "stair case" -> staircase, "set back" -> setback,
/// "bed room" -> bedroom, "east facing" -> east-facing, "auto cad" -> AutoCAD.</item>
/// </list>
/// Already typed forms (30' x 40', 10'-6", 4 1/2") stay as they are, so normalizing twice changes nothing.
/// Bare numbers ("2 bedrooms", "one toilet") are left alone.
/// </summary>
public static class SpokenTextNormalizer
{
    private static readonly Regex TokenPattern = new(
        @"(?<num>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+\.\d+|\d+/\d+|\d+)|(?<word>[A-Za-z]+)|(?<sym>['""’”″′×*+\-])|(?<space>\s+)|(?<other>.)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly HashSet<string> FeetWords = new(StringComparer.OrdinalIgnoreCase) { "feet", "foot", "ft", "feets" };
    private static readonly HashSet<string> InchWords = new(StringComparer.OrdinalIgnoreCase) { "inch", "inches", "inchs" };
    private static readonly HashSet<string> DimensionSeparators = new(StringComparer.OrdinalIgnoreCase) { "by", "x", "into", "cross" };

    // Words the recognizer tends to split or join differently than a drafter types them (whole words, any case).
    private static readonly (Regex Pattern, string Replacement)[] WordFixes =
    [
        (Word(@"stair\s+cases?"), "staircase"),
        (Word(@"set\s+backs"), "setbacks"),
        (Word(@"set\s+back"), "setback"),
        (Word(@"bed\s+rooms"), "bedrooms"),
        (Word(@"bed\s+room"), "bedroom"),
        (Word(@"bath\s+rooms"), "bathrooms"),
        (Word(@"bath\s+room"), "bathroom"),
        (Word(@"wash\s+rooms"), "washrooms"),
        (Word(@"wash\s+room"), "washroom"),
        (Word(@"auto\s*cad"), "AutoCAD"),
        (Word(@"(north|south|east|west)\s+facing"), "$1-facing"),
    ];

    /// <summary>Normalizes <paramref name="text"/> (null -> empty).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var (pattern, replacement) in WordFixes)
        {
            result = pattern.Replace(result, match => KeepCase(match.Value, match.Result(replacement)));
        }

        return new Parser(result).Run();
    }

    private static Regex Word(string pattern) => new(@"\b" + pattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "Bed room" -> "Bedroom": keeps a capital first letter.
    private static string KeepCase(string original, string replacement) =>
        replacement.Length > 0 && char.IsUpper(original[0]) && char.IsLower(replacement[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;

    private enum TokenKind
    {
        Number,
        Word,
        Symbol,
        Other,
    }

    private enum MeasureUnit
    {
        None,
        Feet,
        Inch,
        FeetInch,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    private readonly record struct Measure(MeasureUnit Unit, decimal Value, decimal Inches, int Next);

    /// <summary>Scans the significant tokens (spaces dropped) and rewrites the spans that are measures.</summary>
    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<Token> _tokens = [];

        public Parser(string text)
        {
            _text = text;
            foreach (Match match in TokenPattern.Matches(text))
            {
                var kind = match.Groups["num"].Success ? TokenKind.Number
                    : match.Groups["word"].Success ? TokenKind.Word
                    : match.Groups["sym"].Success ? TokenKind.Symbol
                    : match.Groups["space"].Success ? (TokenKind?)null
                    : TokenKind.Other;
                if (kind is { } k)
                {
                    _tokens.Add(new Token(k, match.Value, match.Index, match.Index + match.Length));
                }
            }
        }

        public string Run()
        {
            var output = new StringBuilder(_text.Length);
            var copied = 0;
            var i = 0;
            while (i < _tokens.Count)
            {
                if (TryRewrite(i, out var replacement, out var next))
                {
                    output.Append(_text, copied, _tokens[i].Start - copied).Append(replacement);
                    copied = _tokens[next - 1].End;
                    i = next;
                }
                else
                {
                    i++;
                }
            }

            output.Append(_text, copied, _text.Length - copied);
            return output.ToString();
        }

        private bool TryRewrite(int i, out string replacement, out int next)
        {
            replacement = string.Empty;
            next = i;
            if (TryFloors(i, out replacement, out next))
            {
                return true;
            }

            if (!TryMeasure(i, out var first))
            {
                return false;
            }

            if (first.Unit == MeasureUnit.None && TryArea(first.Next, out var areaUnit, out var afterArea))
            {
                replacement = FormatNumber(first.Value) + " " + areaUnit;
                next = afterArea;
                return true;
            }

            var chain = new List<Measure> { first };
            var j = first.Next;
            while (IsSeparator(j) && TryMeasure(j + 1, out var more))
            {
                chain.Add(more);
                j = more.Next;
            }

            if (chain.Count == 1 && first.Unit == MeasureUnit.None)
            {
                return false;
            }

            // A trailing unit applies to the bare numbers before it ("30 by 40 feet"), a leading one to those after it ("30 feet by 40").
            for (var k = 0; k < chain.Count; k++)
            {
                if (chain[k].Unit != MeasureUnit.None)
                {
                    continue;
                }

                var unit = chain.Skip(k + 1).Select(m => m.Unit).FirstOrDefault(u => u != MeasureUnit.None);
                if (unit == MeasureUnit.None)
                {
                    unit = chain.Take(k).Select(m => m.Unit).LastOrDefault(u => u != MeasureUnit.None);
                }

                if (unit == MeasureUnit.FeetInch)
                {
                    unit = MeasureUnit.Feet;
                }

                chain[k] = chain[k] with { Unit = unit };
            }

            replacement = string.Join(" x ", chain.Select(Format));
            next = j;
            return true;
        }

        // "G plus one", "ground plus 2", "G + 1" -> "G+1".
        private bool TryFloors(int i, out string replacement, out int next)
        {
            replacement = string.Empty;
            next = i;
            if (!IsWord(i, "g", "ground") || !(IsWord(i + 1, "plus") || IsSymbol(i + 1, "+")) || IsAdjacentPlus(i))
            {
                return false;
            }

            if (!TryNumber(i + 2, out var floors, out var after) || floors != decimal.Truncate(floors) || floors < 0 || floors > 20)
            {
                return false;
            }

            replacement = "G+" + FormatNumber(floors);
            next = after;
            return true;
        }

        // "G+1" typed already: leave it.
        private bool IsAdjacentPlus(int i) =>
            IsSymbol(i + 1, "+") && _tokens[i].End == _tokens[i + 1].Start && i + 2 < _tokens.Count && _tokens[i + 1].End == _tokens[i + 2].Start;

        private bool TryMeasure(int i, out Measure measure)
        {
            measure = default;
            if (!TryNumber(i, out var value, out var j))
            {
                return false;
            }

            var unit = TryUnit(j, out var afterUnit);
            if (unit == MeasureUnit.Feet)
            {
                // "10 feet 6 inches", "10 feet and 6 inches", "10'-6"".
                var k = afterUnit;
                if (IsSymbol(k, "-") || IsWord(k, "and"))
                {
                    k++;
                }

                if (TryNumber(k, out var inches, out var afterInches) && TryUnit(afterInches, out var afterInchUnit) == MeasureUnit.Inch && inches < 12)
                {
                    measure = new Measure(MeasureUnit.FeetInch, value, inches, afterInchUnit);
                    return true;
                }

                measure = new Measure(MeasureUnit.Feet, value, 0, afterUnit);
                return true;
            }

            measure = unit == MeasureUnit.Inch
                ? new Measure(MeasureUnit.Inch, value, 0, afterUnit)
                : new Measure(MeasureUnit.None, value, 0, j);
            return true;
        }

        private MeasureUnit TryUnit(int i, out int next)
        {
            next = i;
            var j = i;
            if (IsSymbol(j, "-") && j + 1 < _tokens.Count && _tokens[j + 1].Kind == TokenKind.Word)
            {
                j++; // "9-inch wall", "30-foot road"
            }

            if (j >= _tokens.Count)
            {
                return MeasureUnit.None;
            }

            var token = _tokens[j];
            if (token.Kind == TokenKind.Word)
            {
                if (FeetWords.Contains(token.Text))
                {
                    next = j + 1;
                    return MeasureUnit.Feet;
                }

                if (InchWords.Contains(token.Text))
                {
                    next = j + 1;
                    return MeasureUnit.Inch;
                }

                return MeasureUnit.None;
            }

            // ' and " right after the number (not an apostrophe: "the 90's").
            if (token.Kind == TokenKind.Symbol && j == i && (j == 0 || _tokens[j - 1].End == token.Start) && !FollowedByLetter(token))
            {
                if (token.Text is "'" or "’" or "′")
                {
                    next = j + 1;
                    return MeasureUnit.Feet;
                }

                if (token.Text is "\"" or "”" or "″")
                {
                    next = j + 1;
                    return MeasureUnit.Inch;
                }
            }

            return MeasureUnit.None;
        }

        private bool FollowedByLetter(Token token) => token.End < _text.Length && char.IsLetter(_text[token.End]);

        // "square feet", "sq ft", "sq. ft.", "sqft", "square metres".
        private bool TryArea(int i, out string unit, out int next)
        {
            unit = string.Empty;
            next = i;
            if (IsWord(i, "sqft"))
            {
                unit = "sq ft";
                next = i + 1;
                return true;
            }

            if (!IsWord(i, "square", "sq"))
            {
                return false;
            }

            var j = i + 1;
            if (IsOther(j, "."))
            {
                j++;
            }

            if (j < _tokens.Count && _tokens[j].Kind == TokenKind.Word && FeetWords.Contains(_tokens[j].Text))
            {
                unit = "sq ft";
            }
            else if (IsWord(j, "m", "meter", "meters", "metre", "metres"))
            {
                unit = "sq m";
            }
            else
            {
                return false;
            }

            next = j + 1;
            return true;
        }

        private bool IsSeparator(int i) =>
            i < _tokens.Count && ((_tokens[i].Kind == TokenKind.Word && DimensionSeparators.Contains(_tokens[i].Text)) || IsSymbol(i, "×") || IsSymbol(i, "*"));

        /// <summary>A number in digits ("30", "4.5", "1/2", "4 1/2", "1,200") or words, with an optional "and a half".</summary>
        private bool TryNumber(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (i >= _tokens.Count)
            {
                return false;
            }

            var token = _tokens[i];
            if (token.Kind == TokenKind.Number)
            {
                if (!TryParseDigits(token.Text, out value))
                {
                    return false;
                }

                next = i + 1;
                if (value == decimal.Truncate(value) && !token.Text.Contains('/') && next < _tokens.Count && _tokens[next].Kind == TokenKind.Number &&
                    _tokens[next].Text.Contains('/') && TryParseDigits(_tokens[next].Text, out var fraction) && fraction < 1)
                {
                    value += fraction; // "4 1/2"
                    next++;
                }
            }
            else if (TryFractionWords(i, out value, out next))
            {
                return true; // "half", "a quarter", "three quarter"
            }
            else if (!TryWordInteger(i, out value, out next))
            {
                return false;
            }
            else if (value is > 0 and < 4 && TryFractionDenominator(next, value, out var words, out var afterWords))
            {
                value = words; // "one half", "three quarters"
                next = afterWords;
                return true;
            }

            if (value == decimal.Truncate(value) && TryAndFraction(next, out var extra, out var afterFraction))
            {
                value += extra;
                next = afterFraction;
            }

            return true;
        }

        private static bool TryParseDigits(string text, out decimal value)
        {
            value = 0;
            var slash = text.IndexOf('/');
            if (slash > 0)
            {
                if (decimal.TryParse(text[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var top) &&
                    decimal.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bottom) && bottom != 0)
                {
                    value = top / bottom;
                    return true;
                }

                return false;
            }

            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        }

        // "half", "a half", "quarter", "a quarter", "three quarter(s)" standing alone.
        private bool TryFractionWords(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            var j = IsWord(i, "a") ? i + 1 : i;
            if (IsWord(j, "half"))
            {
                value = 0.5m;
            }
            else if (IsWord(j, "quarter"))
            {
                value = 0.25m;
            }
            else
            {
                return false;
            }

            next = j + 1; // only rewritten when a unit or a dimension follows ("a half" alone stays text)
            return true;
        }

        // After a small word number: "one half", "three quarters", "three fourths".
        private bool TryFractionDenominator(int i, decimal count, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (count == 1 && IsWord(i, "half"))
            {
                value = 0.5m;
            }
            else if (IsWord(i, "quarter", "quarters", "fourth", "fourths"))
            {
                value = count / 4;
            }
            else
            {
                return false;
            }

            next = i + 1;
            return true;
        }

        // "and a half", "and one half", "and a quarter", "and three quarters", "and 1/2", "and half".
        private bool TryAndFraction(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (!IsWord(i, "and"))
            {
                return false;
            }

            var j = i + 1;
            if (j < _tokens.Count && _tokens[j].Kind == TokenKind.Number && _tokens[j].Text.Contains('/') &&
                TryParseDigits(_tokens[j].Text, out var fraction) && fraction < 1)
            {
                value = fraction;
                next = j + 1;
                return true;
            }

            if (IsWord(j, "half"))
            {
                value = 0.5m;
                next = j + 1;
                return true;
            }

            if (IsWord(j, "a", "one") && IsWord(j + 1, "half"))
            {
                value = 0.5m;
                next = j + 2;
                return true;
            }

            if (IsWord(j, "a", "one") && IsWord(j + 1, "quarter"))
            {
                value = 0.25m;
                next = j + 2;
                return true;
            }

            if (IsWord(j, "three") && IsWord(j + 1, "quarter", "quarters", "fourths"))
            {
                value = 0.75m;
                next = j + 2;
                return true;
            }

            return false;
        }

        // "thirty", "forty-five", "forty five", "twelve hundred", "one thousand two hundred and fifty".
        private bool TryWordInteger(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (!TryGroup(i, out var group, out var j))
            {
                return false;
            }

            if (IsWord(j, "thousand"))
            {
                value = group * 1000;
                j++;
                var k = IsWord(j, "and") && IsNumberWord(j + 1) ? j + 1 : j;
                if (TryGroup(k, out var rest, out var afterRest))
                {
                    value += rest;
                    j = afterRest;
                }

                next = j;
                return true;
            }

            value = group;
            next = j;
            return true;
        }

        private bool TryGroup(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (!TryBelowHundred(i, out var small, out var j))
            {
                return false;
            }

            if (IsWord(j, "hundred"))
            {
                value = small * 100;
                j++;
                var k = IsWord(j, "and") && IsNumberWord(j + 1) ? j + 1 : j;
                if (TryBelowHundred(k, out var rest, out var afterRest))
                {
                    value += rest;
                    j = afterRest;
                }

                next = j;
                return true;
            }

            value = small;
            next = j;
            return true;
        }

        private bool TryBelowHundred(int i, out decimal value, out int next)
        {
            value = 0;
            next = i;
            if (i >= _tokens.Count || _tokens[i].Kind != TokenKind.Word)
            {
                return false;
            }

            var text = _tokens[i].Text;
            if (Tens.TryGetValue(text, out var tens))
            {
                value = tens;
                next = i + 1;
                var j = IsSymbol(next, "-") ? next + 1 : next;
                if (j < _tokens.Count && _tokens[j].Kind == TokenKind.Word && Units.TryGetValue(_tokens[j].Text, out var unit) && unit is > 0 and < 10)
                {
                    value += unit;
                    next = j + 1;
                }

                return true;
            }

            if (Units.TryGetValue(text, out var single))
            {
                value = single;
                next = i + 1;
                return true;
            }

            return false;
        }

        private bool IsNumberWord(int i) =>
            i < _tokens.Count && _tokens[i].Kind == TokenKind.Word && (Units.ContainsKey(_tokens[i].Text) || Tens.ContainsKey(_tokens[i].Text));

        private bool IsWord(int i, params string[] words) =>
            i >= 0 && i < _tokens.Count && _tokens[i].Kind == TokenKind.Word && words.Any(w => string.Equals(w, _tokens[i].Text, StringComparison.OrdinalIgnoreCase));

        private bool IsSymbol(int i, string symbol) =>
            i >= 0 && i < _tokens.Count && _tokens[i].Kind == TokenKind.Symbol && _tokens[i].Text == symbol;

        private bool IsOther(int i, string text) =>
            i >= 0 && i < _tokens.Count && _tokens[i].Kind == TokenKind.Other && _tokens[i].Text == text;
    }

    private static string Format(Measure measure) => measure.Unit switch
    {
        MeasureUnit.Feet => FormatFeet(measure.Value),
        MeasureUnit.Inch => FormatInches(measure.Value) + "\"",
        MeasureUnit.FeetInch when measure.Value == decimal.Truncate(measure.Value) =>
            FormatNumber(measure.Value) + "'-" + FormatInches(measure.Inches) + "\"",
        MeasureUnit.FeetInch => FormatFeet(measure.Value + (measure.Inches / 12)),
        _ => FormatNumber(measure.Value),
    };

    // 30 -> 30'; 2.5 -> 2'-6"; 2.3 -> 2.3' (not a whole sixteenth of an inch).
    private static string FormatFeet(decimal feet)
    {
        var whole = decimal.Truncate(feet);
        if (whole == feet)
        {
            return FormatNumber(feet) + "'";
        }

        var inches = (feet - whole) * 12;
        return IsSixteenths(inches) ? FormatNumber(whole) + "'-" + FormatInches(inches) + "\"" : FormatNumber(feet) + "'";
    }

    // 9 -> 9; 4.5 -> 4 1/2; 0.75 -> 3/4; 4.3 -> 4.3.
    private static string FormatInches(decimal inches)
    {
        if (!IsSixteenths(inches))
        {
            return FormatNumber(inches);
        }

        var whole = decimal.Truncate(inches);
        var sixteenths = (int)((inches - whole) * 16);
        if (sixteenths == 0)
        {
            return FormatNumber(whole);
        }

        var denominator = 16;
        while (sixteenths % 2 == 0)
        {
            sixteenths /= 2;
            denominator /= 2;
        }

        var fraction = sixteenths.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);
        return whole == 0 ? fraction : FormatNumber(whole) + " " + fraction;
    }

    private static bool IsSixteenths(decimal value) => value * 16 == decimal.Truncate(value * 16);

    private static string FormatNumber(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
