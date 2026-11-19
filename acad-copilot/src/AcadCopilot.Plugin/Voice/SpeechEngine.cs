using System.Globalization;

namespace AcadCopilot.Plugin.Voice;

/// <summary>Receives what a running <see cref="ISpeechEngine"/> hears. Called on any thread.</summary>
public interface ISpeechSink
{
    /// <summary>The words of the phrase being spoken so far (replaces the previous hypothesis).</summary>
    void OnHypothesis(string text);

    /// <summary>A finished phrase.</summary>
    void OnPhrase(string text);

    /// <summary>The engine stopped by itself (time-out, device lost, service error); <paramref name="problem"/> null = normal end.</summary>
    void OnStopped(VoiceProblem? problem);
}

/// <summary>A started speech recognizer listening to the microphone.</summary>
public interface ISpeechEngine : IDisposable
{
    /// <summary>Which recognizer: "Windows dictation" or "Windows offline speech".</summary>
    string Name { get; }

    /// <summary>The language it listens for ("en-IN").</summary>
    string LanguageTag { get; }

    /// <summary>A hint to show while listening (for example why the offline recognizer is used), or null.</summary>
    string? Notice { get; }

    /// <summary>Stops listening; phrases still being recognized are delivered to the sink before the task ends.</summary>
    Task StopAsync();
}

/// <summary>Opens the microphone and starts a recognizer (off the UI thread).</summary>
public interface ISpeechEngineFactory
{
    /// <summary>Starts listening for <paramref name="request"/>, reporting to <paramref name="sink"/>.</summary>
    /// <exception cref="VoiceException">The problem to show (no microphone, blocked, language missing ...).</exception>
    Task<ISpeechEngine> StartAsync(VoiceRequest request, ISpeechSink sink, CancellationToken cancellationToken);
}

/// <summary>What to listen for.</summary>
/// <param name="LanguageTag">BCP-47 tag ("en-IN", "hi-IN"); null = the Windows speech / display language.</param>
public sealed record VoiceRequest(string? LanguageTag);

/// <summary>A voice language choice in the settings.</summary>
/// <param name="Tag">BCP-47 tag; null = automatic (Windows language).</param>
/// <param name="DisplayName">Text in the drop-down.</param>
public sealed record VoiceLanguageChoice(string? Tag, string DisplayName);

/// <summary>Voice languages and the choice of the recognizer language (pure).</summary>
public static class VoiceLanguages
{
    /// <summary>The choices offered in the settings (automatic first).</summary>
    public static IReadOnlyList<VoiceLanguageChoice> Choices { get; } =
    [
        new(null, "Windows language (automatic)"),
        new("en-IN", "English (India)"),
        new("en-US", "English (US)"),
        new("en-GB", "English (UK)"),
        new("hi-IN", "Hindi (India)"),
    ];

    /// <summary>The choice for <paramref name="tag"/> (automatic when unknown).</summary>
    public static VoiceLanguageChoice Find(string? tag) =>
        Choices.FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase)) ?? Choices[0];

    /// <summary>"English (India)" for "en-IN" (the tag itself when Windows does not know it).</summary>
    public static string DisplayName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "the Windows language";
        }

        if (Choices.FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return known.DisplayName;
        }

        try
        {
            return CultureInfo.GetCultureInfo(tag).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>
    /// Picks the recognizer language. An explicit <paramref name="requested"/> tag must be supported (exact tag), else
    /// null. Automatic (null) takes the first supported of <paramref name="preferred"/> (the Windows speech language, the
    /// display language), then English (India), English (US), English (UK), then any English, then the first supported.
    /// </summary>
    /// <param name="requested">The tag chosen in the settings, or null for automatic.</param>
    /// <param name="supported">Tags the recognizer supports.</param>
    /// <param name="preferred">Windows' own languages, most preferred first.</param>
    public static string? Pick(string? requested, IReadOnlyCollection<string> supported, IEnumerable<string?> preferred)
    {
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(preferred);
        string? Match(string? tag) => tag is null ? null : supported.FirstOrDefault(s => string.Equals(s, tag, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Match(requested);
        }

        foreach (var tag in preferred.Concat(["en-IN", "en-US", "en-GB"]))
        {
            if (Match(tag) is { } found)
            {
                return found;
            }
        }

        return supported.FirstOrDefault(s => s.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? supported.FirstOrDefault();
    }
}

/// <summary>Joins dictated text to what is already in the message box (pure).</summary>
public static class VoiceText
{
    /// <summary>
    /// <paramref name="existing"/> followed by <paramref name="spoken"/>, with one space between them unless the existing
    /// text is empty or already ends with white space.
    /// </summary>
    public static string Append(string? existing, string? spoken)
    {
        var words = (spoken ?? string.Empty).Trim();
        var before = existing ?? string.Empty;
        if (words.Length == 0)
        {
            return before;
        }

        if (before.Trim().Length == 0)
        {
            return words;
        }

        return char.IsWhiteSpace(before[^1]) ? before + words : before + " " + words;
    }

    /// <summary>The finished phrases and the phrase being spoken, as one normalized text.</summary>
    public static string Spoken(IEnumerable<string> phrases, string? hypothesis)
    {
        var parts = phrases.Append(hypothesis ?? string.Empty).Select(p => p.Trim()).Where(p => p.Length > 0);
        return SpokenTextNormalizer.Normalize(string.Join(" ", parts));
    }
}
