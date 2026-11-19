using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Agents;

/// <summary>Kind of a file the user attached to a message.</summary>
internal enum AttachmentKind
{
    /// <summary>png/jpg/gif/webp/bmp.</summary>
    Image,

    /// <summary>PDF document.</summary>
    Pdf,

    /// <summary>Plain text (txt, md, csv, json, xml, ...).</summary>
    Text,

    /// <summary>Anything else.</summary>
    Other,

    /// <summary>The file does not exist.</summary>
    Missing,
}

/// <summary>An attached file with its kind.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Kind">Kind.</param>
internal sealed record Attachment(string Path, AttachmentKind Kind);

/// <summary>
/// Shared text helpers of the CLI runners: JSON field access, short summaries for "CAD activity" rows,
/// failure classification ("not signed in", rate limits), TOML quoting for Codex -c overrides, attachments.
/// </summary>
internal static partial class CliText
{
    private const int MaxInlineTextBytes = 100_000;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static readonly string[] TextExtensions = [".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".ini", ".log", ".yaml", ".yml", ".html", ".htm"];

    private static readonly string[] SignInMarkers =
    [
        "not logged in", "please run /login", "run /login", "authentication_failed", "invalid api key", "api key not valid",
        "oauth token has expired", "token has expired", "please log in", "log in again", "login again", "not signed in",
        "sign in again", "unauthenticated", "please set an auth method", "fatalauthenticationerror",
        "must specify the gemini_api_key", "codex login", "access token could not be refreshed", "refresh token",
        "oauth_org_not_allowed", "missing bearer",
    ];

    private static readonly string[] RateLimitMarkers =
    [
        "rate limit", "rate_limit", "ratelimit", "usage limit", "usage_limit", "hit your limit", "limit reached", "limit_reached",
        "resource_exhausted", "quota", "too many requests", "out of extra usage", "credit balance is too low",
        "exhausted your", "exceeded your current", "limit exceeded", "limits reset",
    ];

    // Limits that only end when a plan's window resets (as opposed to a short burst of 429s a CLI retries itself):
    // Claude Code "You've hit your limit · resets 3pm" / "Claude AI usage limit reached|<epoch>" / "5-hour limit reached",
    // Codex "You've hit your usage limit. Upgrade to Pro ... or try again in 2 hours" (usage_limit_reached),
    // Gemini "You have exhausted your capacity on this model" / "Quota exceeded for quota metric ... per day".
    private static readonly string[] HardLimitMarkers =
    [
        "usage limit", "usage_limit", "hit your limit", "limit reached", "limit_reached", "out of extra usage",
        "credit balance is too low", "exhausted your", "quota exceeded", "exceeded your current quota", "per day", "daily limit",
        "weekly limit", "resets",
    ];

    private static readonly string[] OptionMarkers = ["unknown option", "unexpected argument", "unknown argument", "unrecognized option"];

    private static readonly string[] NoiseMarkers =
    [
        "warning: windows 10 detected", "true color", "ripgrep is not available", "could not create path aliases",
        "loaded cached credentials", "data collection is", "yolo mode", "is deprecated", "migrate to policy engine",
    ];

    private static readonly string[] TechnicalTails = [", url:", ", cf-ray:", ", request id:"];

    /// <summary>The terminal event for Stop.</summary>
    public static TurnFailed Stopped { get; } = new("Stopped.", AgentFailureKind.Cancelled);

    /// <summary>Parses a JSON-lines record; false for blank or non-JSON lines and non-objects.</summary>
    public static bool TryParseObject(string line, out JsonObject value)
    {
        value = null!;
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(line) is JsonObject parsed)
            {
                value = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON (a log line printed on stdout).
        }

        return false;
    }

    /// <summary>String property, or null.</summary>
    public static string? Str(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Integer property, or 0.</summary>
    public static long Long(JsonNode? node, string name)
    {
        if (node is JsonObject obj && obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
            long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return 0;
    }

    /// <summary>Boolean property, or null when missing / not a boolean.</summary>
    public static bool? Bool(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValueKind() == JsonValueKind.True
            : null;

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters (with an ellipsis).</summary>
    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var singleLine = text.Trim();
        return singleLine.Length <= max ? singleLine : singleLine[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Short human summary of tool arguments for a "CAD activity" row, e.g. "level: Level 1, walls: 4 items".
    /// </summary>
    public static string SummarizeArguments(JsonNode? arguments)
    {
        if (arguments is not JsonObject obj || obj.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (parts.Count == 3)
            {
                parts.Add("…");
                break;
            }

            var shown = value switch
            {
                null => "null",
                JsonArray array => array.Count == 1 ? "1 item" : $"{array.Count} items",
                JsonObject => "{…}",
                JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => Truncate(scalar.GetValue<string>(), 32),
                _ => Truncate(value.ToJsonString(), 32),
            };
            parts.Add($"{key}: {shown}");
        }

        return Truncate(string.Join(", ", parts), 100);
    }

    /// <summary>
    /// Interprets a tool result text. The AutoCAD bridge returns a ToolResult as JSON ({success, message, errors});
    /// other results are plain text. The call failed when the CLI says so or when the JSON says success=false.
    /// </summary>
    /// <param name="callId">Id of the call (from the matching <see cref="ToolCallStarted"/>).</param>
    /// <param name="text">Result text (may be null).</param>
    /// <param name="cliReportedError">True when the CLI marked the call as failed.</param>
    /// <param name="cliErrorText">Error text reported by the CLI, if any.</param>
    public static ToolCallFinished SummarizeResult(string callId, string? text, bool cliReportedError, string? cliErrorText)
    {
        string? message = null;
        bool? jsonSuccess = null;
        var errors = new List<string>();
        if (text is not null && TryParseObject(text, out var obj))
        {
            jsonSuccess = Bool(obj, "success");
            message = Str(obj, "message") ?? Str(obj, "error");
            if (obj["errors"] is JsonArray errorArray)
            {
                errors.AddRange(errorArray.Select(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null).OfType<string>());
            }
        }

        var firstLine = FirstLine(text);
        var success = !cliReportedError && jsonSuccess != false;
        if (success)
        {
            return new ToolCallFinished(callId, true, Truncate(message ?? firstLine ?? "Done", 160));
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : message ?? cliErrorText ?? firstLine ?? "The tool failed.";
        var summary = Truncate(message ?? errors.FirstOrDefault() ?? cliErrorText ?? firstLine ?? "Failed", 160);
        return new ToolCallFinished(callId, false, summary, Truncate(error, 600));
    }

    /// <summary>Concatenates the text items of an MCP content array (or returns a plain string).</summary>
    public static string? ContentText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>();
            case JsonArray array:
                var texts = array
                    .Select(item => item is JsonObject o && Str(o, "type") is "text" or null ? Str(o, "text") : null)
                    .OfType<string>()
                    .ToList();
                return texts.Count == 0 ? null : string.Join("\n", texts);
            default:
                return null;
        }
    }

    /// <summary>Failure category of an error text.</summary>
    public static AgentFailureKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AgentFailureKind.Other;
        }

        var lower = text.ToLowerInvariant();
        if (SignInMarkers.Any(lower.Contains) || (Http401().IsMatch(lower) && (lower.Contains("unauthor", StringComparison.Ordinal) || lower.Contains("auth", StringComparison.Ordinal))))
        {
            return AgentFailureKind.NotSignedIn;
        }

        return RateLimitMarkers.Any(lower.Contains) || Http429().IsMatch(lower) ? AgentFailureKind.RateLimited : AgentFailureKind.Other;
    }

    /// <summary>
    /// True when <paramref name="text"/> reports a plan or quota limit that lasts until it resets (hours), not a short burst
    /// of rate limiting that the CLI retries by itself. Such a turn should fail at once (and the panel may switch AI).
    /// </summary>
    public static bool IsHardLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Classify(text) != AgentFailureKind.RateLimited)
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        return HardLimitMarkers.Any(lower.Contains);
    }

    /// <summary>A user-facing <see cref="TurnFailed"/> for an error text (category detected unless given).</summary>
    public static TurnFailed Failure(CliProfile profile, string detail, AgentFailureKind? kind = null)
    {
        var readable = Truncate(CleanDetail(detail), 400);
        var lower = detail.ToLowerInvariant();
        if (kind is null && OptionMarkers.Any(lower.Contains))
        {
            return new TurnFailed(
                $"{profile.CliName} rejected an option the panel uses ({readable}). Update it with: npm install -g {profile.NpmPackage}@latest",
                AgentFailureKind.Other);
        }

        var actual = kind ?? Classify(detail);
        return actual switch
        {
            AgentFailureKind.NotSignedIn => new TurnFailed(profile.NotSignedInMessage(readable), actual),
            AgentFailureKind.RateLimited => new TurnFailed(
                $"{profile.DisplayName} reports a usage or rate limit: {readable}. Wait until it resets, or switch to another provider.", actual),
            AgentFailureKind.NotInstalled => new TurnFailed(profile.NotInstalledMessage, actual),
            AgentFailureKind.Cancelled => Stopped,
            _ => new TurnFailed($"{profile.CliName}: {readable}", actual),
        };
    }

    /// <summary>
    /// Makes a CLI error readable: the human sentence of nested API JSON, without retry prefixes
    /// ("Reconnecting... 2/5 (") and without trailing URLs / request ids.
    /// </summary>
    public static string CleanDetail(string text)
    {
        var cleaned = ExtractReadableError(text);
        var retry = RetryPrefix().Match(cleaned);
        if (retry.Success)
        {
            cleaned = cleaned[retry.Length..];
        }

        foreach (var tail in TechnicalTails)
        {
            var index = cleaned.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                cleaned = cleaned[..index];
            }
        }

        cleaned = cleaned.Trim();
        while (cleaned.EndsWith(')') && cleaned.Count(c => c == ')') > cleaned.Count(c => c == '('))
        {
            cleaned = cleaned[..^1].TrimEnd();
        }

        return cleaned.Length == 0 ? text.Trim() : cleaned;
    }

    /// <summary>
    /// Pulls the human sentence out of nested API error JSON ("[API Error: {... \"message\": \"API key not valid\" ...}]").
    /// Returns the text unchanged when there is nothing to extract.
    /// </summary>
    public static string ExtractReadableError(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("\"message\"", StringComparison.Ordinal) && !text.Contains("\\\"message\\\"", StringComparison.Ordinal))
        {
            return text.Trim();
        }

        var flat = text.Replace("\\n", " ", StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal);
        foreach (Match match in MessageField().Matches(flat))
        {
            var candidate = match.Groups["m"].Value.Trim();
            if (candidate.Length > 0 && candidate[0] != '{' && candidate[0] != '[')
            {
                return candidate;
            }
        }

        return text.Trim();
    }

    /// <summary>The last non-noise lines of stderr output (max 3 lines).</summary>
    public static string LastMeaningfulLines(string stdErr)
    {
        var lines = stdErr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !NoiseMarkers.Any(n => l.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Where(l => !l.StartsWith("at ", StringComparison.Ordinal))
            .ToList();
        return string.Join(" ", lines.TakeLast(3));
    }

    /// <summary>First non-empty line of a text, or null.</summary>
    public static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>TOML basic string ("..."), escaping quotes, backslashes and control characters.</summary>
    public static string TomlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>TOML array of strings.</summary>
    public static string TomlArray(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(TomlString)) + "]";

    /// <summary>TOML inline table of strings: { KEY = "value", "odd key" = "v" }.</summary>
    public static string TomlInlineTable(IEnumerable<KeyValuePair<string, string>> values) =>
        "{" + string.Join(", ", values.Select(p => $"{TomlKey(p.Key)} = {TomlString(p.Value)}")) + "}";

    /// <summary>A TOML key: bare when possible, quoted otherwise.</summary>
    public static string TomlKey(string key) => BareKey().IsMatch(key) ? key : TomlString(key);

    /// <summary>Classifies attachments by extension and existence.</summary>
    public static IReadOnlyList<Attachment> ClassifyAttachments(IEnumerable<string> paths) =>
        [.. paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Select(ClassifyAttachment)];

    /// <summary>Reads a small text attachment for inlining; null when too big or unreadable.</summary>
    public static string? ReadInlineText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxInlineTextBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Distinct parent folders of existing attachments.</summary>
    public static IReadOnlyList<string> AttachmentFolders(IEnumerable<Attachment> attachments) =>
        [.. attachments
            .Where(a => a.Kind != AttachmentKind.Missing)
            .Select(a => Path.GetDirectoryName(a.Path))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Appends "Attached files" notes to the user's prompt.</summary>
    /// <param name="prompt">User text.</param>
    /// <param name="readable">Files the model can open itself (listed with <paramref name="howToRead"/>).</param>
    /// <param name="howToRead">Hint such as "use the Read tool to view them".</param>
    /// <param name="inlined">Files whose content is pasted into the message.</param>
    /// <param name="unavailable">Files the provider cannot open.</param>
    public static string ComposePrompt(
        string prompt,
        IReadOnlyCollection<Attachment> readable,
        string howToRead,
        IReadOnlyCollection<(string Path, string Content)> inlined,
        IReadOnlyCollection<Attachment> unavailable)
    {
        if (readable.Count == 0 && inlined.Count == 0 && unavailable.Count == 0)
        {
            return prompt;
        }

        var builder = new StringBuilder(prompt.TrimEnd());
        if (readable.Count > 0)
        {
            builder.Append("\n\nAttached files (").Append(howToRead).Append("):");
            foreach (var file in readable)
            {
                builder.Append("\n- ").Append(file.Path);
            }
        }

        foreach (var (path, content) in inlined)
        {
            builder.Append("\n\nAttached file ").Append(path).Append(":\n```\n").Append(content.TrimEnd()).Append("\n```");
        }

        if (unavailable.Count > 0)
        {
            builder.Append("\n\nThe user also attached files that cannot be opened here: ");
            builder.Append(string.Join(", ", unavailable.Select(a => a.Kind == AttachmentKind.Missing ? a.Path + " (not found)" : a.Path)));
            builder.Append(". Tell the user if you need their content.");
        }

        return builder.ToString();
    }

    private static Attachment ClassifyAttachment(string path)
    {
        if (!File.Exists(path))
        {
            return new Attachment(path, AttachmentKind.Missing);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var kind = ImageExtensions.Contains(extension) ? AttachmentKind.Image
            : extension == ".pdf" ? AttachmentKind.Pdf
            : TextExtensions.Contains(extension) ? AttachmentKind.Text
            : AttachmentKind.Other;
        return new Attachment(path, kind);
    }

    [GeneratedRegex(@"\b401\b", RegexOptions.CultureInvariant)]
    private static partial Regex Http401();

    [GeneratedRegex(@"(status|error|code|http)\W{0,3}429\b|\b429\W{0,3}(too many|rate|resource)", RegexOptions.CultureInvariant)]
    private static partial Regex Http429();

    [GeneratedRegex("\"message\"\\s*:\\s*\"(?<m>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex MessageField();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BareKey();

    [GeneratedRegex(@"^Reconnecting\.\.\.\s*\d+/\d+\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RetryPrefix();
}
