using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Drafting;

/// <summary>
/// Feet-inch text helpers for drafting reports and inputs (all values are in inches).
/// <list type="bullet">
/// <item><see cref="Format"/>: architectural display text, the way AutoCAD shows LUNITS 4: <c>12'-6 1/2"</c>, <c>0'-9"</c>.</item>
/// <item><see cref="FormatForInput"/>: text AutoCAD accepts as typed input when LUNITS is 3 or 4, with no spaces
/// (a space acts like Enter at most prompts): <c>12'6-1/2"</c>, <c>9"</c>.</item>
/// <item><see cref="TryParse"/>: reads the usual ways people and LLMs write feet and inches.</item>
/// </list>
/// </summary>
public static class FeetInches
{
    /// <summary>Default fraction precision (1/16 inch).</summary>
    public const int DefaultDenominator = 16;

    /// <summary>
    /// Architectural display text: feet, a dash, inches and a reduced fraction rounded to 1/<paramref name="denominator"/>
    /// inch: 150.5 -&gt; <c>12'-6 1/2"</c>, 360 -&gt; <c>30'-0"</c>, 9 -&gt; <c>0'-9"</c>, -6 -&gt; <c>-0'-6"</c>.
    /// </summary>
    /// <param name="inches">Length in inches.</param>
    /// <param name="denominator">Fraction precision: 1, 2, 4, 8, 16, 32 or 64.</param>
    public static string Format(double inches, int denominator = DefaultDenominator)
    {
        var (negative, feet, whole, numerator, reduced) = Split(inches, denominator);
        var text = new StringBuilder();
        if (negative)
        {
            text.Append('-');
        }

        text.Append(feet.ToString(CultureInfo.InvariantCulture)).Append("'-").Append(whole.ToString(CultureInfo.InvariantCulture));
        if (numerator > 0)
        {
            text.Append(' ').Append(numerator.ToString(CultureInfo.InvariantCulture)).Append('/').Append(reduced.ToString(CultureInfo.InvariantCulture));
        }

        return text.Append('"').ToString();
    }

    /// <summary>
    /// Feet-inch text that can be typed into AutoCAD when LUNITS is 3 or 4, without spaces:
    /// 150.5 -&gt; <c>12'6-1/2"</c>, 360 -&gt; <c>30'</c>, 9 -&gt; <c>9"</c>, 4.5 -&gt; <c>4-1/2"</c>, 0.5 -&gt; <c>1/2"</c>.
    /// </summary>
    /// <param name="inches">Length in inches.</param>
    /// <param name="denominator">Fraction precision: 1, 2, 4, 8, 16, 32 or 64.</param>
    public static string FormatForInput(double inches, int denominator = DefaultDenominator)
    {
        var (negative, feet, whole, numerator, reduced) = Split(inches, denominator);
        var text = new StringBuilder();
        if (negative)
        {
            text.Append('-');
        }

        var hasInches = whole > 0 || numerator > 0;
        if (feet > 0)
        {
            text.Append(feet.ToString(CultureInfo.InvariantCulture)).Append('\'');
        }

        if (!hasInches)
        {
            return feet > 0 ? text.ToString() : "0\"";
        }

        if (whole > 0)
        {
            text.Append(whole.ToString(CultureInfo.InvariantCulture));
            if (numerator > 0)
            {
                text.Append('-');
            }
        }

        if (numerator > 0)
        {
            text.Append(numerator.ToString(CultureInfo.InvariantCulture)).Append('/').Append(reduced.ToString(CultureInfo.InvariantCulture));
        }

        return text.Append('"').ToString();
    }

    /// <summary>
    /// Reads a length written in feet and/or inches and returns it in inches. Accepted forms (case-insensitive,
    /// optional leading sign): <c>12'6"</c>, <c>12'-6"</c>, <c>12' 6"</c>, <c>12'-6 1/2"</c>, <c>12'6-1/2"</c>,
    /// <c>12'6</c>, <c>12'</c>, <c>12.5'</c>, <c>6"</c>, <c>6 1/2"</c>, <c>6-1/2"</c>, <c>1/2"</c>, <c>6.25in</c>,
    /// <c>12ft 6in</c>, and a bare number (read as inches). The typographic marks ′ and ″ are accepted too.
    /// </summary>
    /// <param name="text">The text to read.</param>
    /// <param name="inches">The length in inches when the text was understood.</param>
    /// <returns>True when <paramref name="text"/> is a feet-inch length.</returns>
    public static bool TryParse(string? text, out double inches)
    {
        inches = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var reader = new Reader(text.Trim().Replace('′', '\'').Replace('″', '"'));
        var negative = false;
        if (reader.Peek() is '-' or '+')
        {
            negative = reader.Next() == '-';
            reader.SkipSpaces();
        }

        if (!reader.TryReadNumber(out var first, out _))
        {
            return false;
        }

        reader.SkipSpaces();
        double feet = 0;
        double inchPart;
        if (reader.TryReadFeetMark())
        {
            feet = first;
            reader.SkipSpaces();
            if (reader.AtEnd)
            {
                inches = Sign(negative, feet * 12);
                return true;
            }

            if (reader.Peek() == '-')
            {
                reader.Next();
                reader.SkipSpaces();
            }

            if (!TryReadInches(ref reader, out inchPart))
            {
                return false;
            }
        }
        else
        {
            reader.Reset();
            if (reader.Peek() is '-' or '+')
            {
                reader.Next();
                reader.SkipSpaces();
            }

            if (!TryReadInches(ref reader, out inchPart))
            {
                return false;
            }

        }

        reader.SkipSpaces();
        if (!reader.AtEnd)
        {
            return false;
        }

        inches = Sign(negative, (feet * 12) + inchPart);
        return true;
    }

    /// <summary>
    /// True when <paramref name="input"/> contains feet-inch notation that AutoCAD only understands with LUNITS 3 or 4:
    /// a digit followed by a foot or inch mark (<c>@30'&lt;0</c>, <c>12'6",0</c>, <c>4-1/2"</c>).
    /// </summary>
    public static bool ContainsFeetInchNotation(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        for (var i = 1; i < input.Length; i++)
        {
            if (input[i] is '\'' or '"' or '′' or '″' && char.IsDigit(input[i - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static double Sign(bool negative, double value) => negative ? -value : value;

    // Inches part: "6", "6.25", "6 1/2", "6-1/2", "1/2", followed by an optional inch mark.
    private static bool TryReadInches(ref Reader reader, out double inches)
    {
        inches = 0;
        if (!reader.TryReadNumber(out var number, out var isInteger))
        {
            return false;
        }

        if (reader.Peek() == '/' && isInteger)
        {
            reader.Next();
            if (!reader.TryReadInteger(out var denominator) || denominator == 0)
            {
                return false;
            }

            inches = number / denominator;
            reader.SkipSpaces();
            reader.TryReadInchMark();
            return true;
        }

        inches = number;
        var mark = reader.Position;
        if (isInteger && (reader.Peek() == '-' || reader.Peek() == ' '))
        {
            reader.Next();
            reader.SkipSpaces();
            if (reader.TryReadInteger(out var fractionNumerator) && reader.Peek() == '/')
            {
                reader.Next();
                if (!reader.TryReadInteger(out var fractionDenominator) || fractionDenominator == 0)
                {
                    return false;
                }

                inches += (double)fractionNumerator / fractionDenominator;
            }
            else
            {
                reader.Position = mark;
            }
        }

        reader.SkipSpaces();
        reader.TryReadInchMark();
        return true;
    }

    private static (bool Negative, long Feet, long Whole, long Numerator, long Denominator) Split(double inches, int denominator)
    {
        if (denominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "Use 1, 2, 4, 8, 16, 32 or 64.");
        }

        if (double.IsNaN(inches) || double.IsInfinity(inches))
        {
            throw new ArgumentOutOfRangeException(nameof(inches), inches, "The length must be a finite number.");
        }

        var units = (long)Math.Round(Math.Abs(inches) * denominator, MidpointRounding.AwayFromZero);
        var negative = inches < 0 && units > 0;
        var perFoot = 12L * denominator;
        var feet = units / perFoot;
        var rest = units % perFoot;
        var whole = rest / denominator;
        var numerator = rest % denominator;
        long reduced = denominator;
        while (numerator > 0 && numerator % 2 == 0 && reduced % 2 == 0)
        {
            numerator /= 2;
            reduced /= 2;
        }

        return (negative, feet, whole, numerator, reduced);
    }

    private ref struct Reader
    {
        private readonly string _text;

        public Reader(string text)
        {
            _text = text;
            Position = 0;
        }

        public int Position { get; set; }

        public readonly bool AtEnd => Position >= _text.Length;

        public readonly char Peek() => AtEnd ? '\0' : _text[Position];

        public char Next() => _text[Position++];

        public void Reset() => Position = 0;

        public void SkipSpaces()
        {
            while (!AtEnd && _text[Position] == ' ')
            {
                Position++;
            }
        }

        public bool TryReadInteger(out long value)
        {
            var start = Position;
            while (!AtEnd && char.IsAsciiDigit(_text[Position]))
            {
                Position++;
            }

            if (Position == start || Position - start > 12)
            {
                Position = start;
                value = 0;
                return false;
            }

            value = long.Parse(_text.AsSpan(start, Position - start), NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        public bool TryReadNumber(out double value, out bool isInteger)
        {
            var start = Position;
            while (!AtEnd && char.IsAsciiDigit(_text[Position]))
            {
                Position++;
            }

            isInteger = true;
            if (!AtEnd && _text[Position] == '.')
            {
                isInteger = false;
                Position++;
                while (!AtEnd && char.IsAsciiDigit(_text[Position]))
                {
                    Position++;
                }
            }

            var span = _text.AsSpan(start, Position - start);
            if (span.Length == 0 || span is "." || span.Length > 20
                || !double.TryParse(span, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            {
                Position = start;
                value = 0;
                return false;
            }

            return true;
        }

        public bool TryReadFeetMark()
        {
            if (Peek() == '\'')
            {
                // Two apostrophes ('') are an inch mark, not a foot mark.
                if (Position + 1 < _text.Length && _text[Position + 1] == '\'')
                {
                    return false;
                }

                Position++;
                return true;
            }

            return TryReadWord("feet") || TryReadWord("foot") || TryReadWord("ft.") || TryReadWord("ft");
        }

        public bool TryReadInchMark()
        {
            if (Peek() == '"')
            {
                Position++;
                return true;
            }

            if (Peek() == '\'' && Position + 1 < _text.Length && _text[Position + 1] == '\'')
            {
                Position += 2;
                return true;
            }

            return TryReadWord("inches") || TryReadWord("inch") || TryReadWord("in.") || TryReadWord("in");
        }

        private bool TryReadWord(string word)
        {
            if (Position + word.Length <= _text.Length
                && string.Compare(_text, Position, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                Position += word.Length;
                return true;
            }

            return false;
        }
    }
}
