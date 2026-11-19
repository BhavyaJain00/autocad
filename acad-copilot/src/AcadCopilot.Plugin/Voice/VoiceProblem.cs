namespace AcadCopilot.Plugin.Voice;

/// <summary>Why voice input could not start or stopped.</summary>
public enum VoiceProblemKind
{
    /// <summary>No microphone (or none enabled) on this PC.</summary>
    NoMicrophone,

    /// <summary>Windows privacy settings block the microphone for desktop apps.</summary>
    MicrophoneBlocked,

    /// <summary>Windows "Online speech recognition" is off and no offline recognizer is installed for the language.</summary>
    OnlineSpeechOff,

    /// <summary>The speech language is not installed in Windows.</summary>
    LanguageMissing,

    /// <summary>The online speech service could not be reached.</summary>
    NoInternet,

    /// <summary>Nothing was said (information, not an error).</summary>
    NothingHeard,

    /// <summary>Windows speech recognition is not available on this PC.</summary>
    Unavailable,

    /// <summary>Anything else.</summary>
    Failed,
}

/// <summary>A voice input problem as the panel explains it: what happened, what to do, and where in Windows Settings.</summary>
/// <param name="Kind">The kind of problem.</param>
/// <param name="Title">Short headline ("Microphone access is blocked").</param>
/// <param name="Detail">What to do, with the Windows Settings path.</param>
/// <param name="SettingsUri">ms-settings: page that fixes it, or null.</param>
public sealed record VoiceProblem(VoiceProblemKind Kind, string Title, string Detail, string? SettingsUri = null)
{
    /// <summary>False for information (nothing heard).</summary>
    public bool IsError => Kind != VoiceProblemKind.NothingHeard;
}

/// <summary>Thrown by speech engines and factories with the problem to show.</summary>
public sealed class VoiceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VoiceException(VoiceProblem problem, Exception? inner = null)
        : base(problem.Title + ": " + problem.Detail, inner)
    {
        Problem = problem;
    }

    /// <summary>The problem to show.</summary>
    public VoiceProblem Problem { get; }
}

/// <summary>The panel's voice problem texts and the mapping of Windows speech errors to them (pure).</summary>
public static class VoiceProblems
{
    /// <summary>SPERR_SPEECH_PRIVACY_POLICY_NOT_ACCEPTED: Windows "Online speech recognition" is off.</summary>
    public const int PrivacyPolicyNotAccepted = unchecked((int)0x80045509);

    /// <summary>SPERR_NOT_FOUND: SAPI found no audio input device ("Cannot find the requested data item").</summary>
    public const int SapiNotFound = unchecked((int)0x8004503A);

    /// <summary>E_ACCESSDENIED.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>E_NOTFOUND (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)).</summary>
    public const int NotFound = unchecked((int)0x80070490);

    /// <summary>MF_E_NO_CAPTURE_DEVICES_AVAILABLE.</summary>
    public const int NoCaptureDevices = unchecked((int)0xC00D3EA2);

    /// <summary>REGDB_E_CLASSNOTREG.</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>The microphone privacy page.</summary>
    public const string MicrophoneSettings = "ms-settings:privacy-microphone";

    /// <summary>The speech privacy page (Online speech recognition).</summary>
    public const string SpeechPrivacySettings = "ms-settings:privacy-speech";

    /// <summary>Language settings (speech packs).</summary>
    public const string LanguageSettings = "ms-settings:regionlanguage";

    /// <summary>Sound settings (input device).</summary>
    public const string SoundSettings = "ms-settings:sound";

    /// <summary>No microphone found.</summary>
    public static VoiceProblem NoMicrophone() => new(
        VoiceProblemKind.NoMicrophone,
        "No microphone found",
        "Connect a microphone or headset and check Windows Settings > System > Sound > Input, then click the mic again.",
        SoundSettings);

    /// <summary>Microphone blocked by Windows privacy settings.</summary>
    public static VoiceProblem MicrophoneBlocked() => new(
        VoiceProblemKind.MicrophoneBlocked,
        "Microphone access is blocked",
        "Turn on Windows Settings > Privacy > Microphone > \"Allow access to the microphone on this device\" and \"Allow desktop apps to access your microphone\", then click the mic again.",
        MicrophoneSettings);

    /// <summary>Online speech recognition off and no offline recognizer for <paramref name="languageName"/>.</summary>
    public static VoiceProblem OnlineSpeechOff(string languageName) => new(
        VoiceProblemKind.OnlineSpeechOff,
        "Online speech recognition is off",
        $"Windows dictation needs Windows Settings > Privacy > Speech > \"Online speech recognition\" turned on (no offline recognizer for {languageName} is installed). It uses your Windows account, no key.",
        SpeechPrivacySettings);

    /// <summary>The speech language <paramref name="languageName"/> is not installed.</summary>
    public static VoiceProblem LanguageMissing(string languageName) => new(
        VoiceProblemKind.LanguageMissing,
        $"{languageName} speech is not installed",
        $"Add it in Windows Settings > Time & Language > Language: choose {languageName} > Options > Speech > Download, or pick another voice language in AI Copilot settings.",
        LanguageSettings);

    /// <summary>The online service could not be reached.</summary>
    public static VoiceProblem NoInternet() => new(
        VoiceProblemKind.NoInternet,
        "Windows speech service not reachable",
        "Windows dictation needs an internet connection. Check the connection and click the mic again.");

    /// <summary>Nothing was said before the time-out.</summary>
    public static VoiceProblem NothingHeard() => new(
        VoiceProblemKind.NothingHeard,
        "Didn't hear anything",
        "Click the mic (or press Ctrl+Space) and speak. Check that the right microphone is the Windows default input.",
        SoundSettings);

    /// <summary>Speech recognition is not available.</summary>
    public static VoiceProblem Unavailable(string reason) => new(
        VoiceProblemKind.Unavailable,
        "Voice input is not available",
        reason);

    /// <summary>Any other failure.</summary>
    public static VoiceProblem Failed(string message) => new(
        VoiceProblemKind.Failed,
        "Voice input stopped",
        string.IsNullOrWhiteSpace(message) ? "Windows speech recognition reported an error. Click the mic to try again." : message.Trim());

    /// <summary>Maps an exception from Windows speech (WinRT or SAPI) to a problem.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="languageName">Display name of the language (for the online-speech text).</param>
    public static VoiceProblem FromException(Exception exception, string languageName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is VoiceException voice)
        {
            return voice.Problem;
        }

        if (exception is UnauthorizedAccessException)
        {
            return MicrophoneBlocked();
        }

        return FromHResult(exception.HResult, languageName) ?? Failed(exception.Message);
    }

    /// <summary>Maps a Windows speech HRESULT to a problem; null when it is not a known one.</summary>
    public static VoiceProblem? FromHResult(int hresult, string languageName) => hresult switch
    {
        PrivacyPolicyNotAccepted => OnlineSpeechOff(languageName),
        AccessDenied => MicrophoneBlocked(),
        SapiNotFound or NotFound or NoCaptureDevices => NoMicrophone(),
        ClassNotRegistered => Unavailable("Windows speech recognition is not installed on this PC."),
        unchecked((int)0x80072EE7) or unchecked((int)0x80072EFD) or unchecked((int)0x80072EE2) or unchecked((int)0x80072F78) => NoInternet(),
        _ => null,
    };
}
