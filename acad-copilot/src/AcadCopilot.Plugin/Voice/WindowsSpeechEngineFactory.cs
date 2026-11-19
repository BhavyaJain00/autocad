using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// Chooses and starts the speech recognizer, all off the UI thread:
/// <list type="number">
/// <item>Windows privacy: microphone blocked for desktop apps -> explained, nothing is opened.</item>
/// <item>Windows dictation (<see cref="WinRtSpeechEngine"/>, best quality, live text) unless "Online speech recognition"
/// is known to be off.</item>
/// <item>Otherwise, or when dictation cannot start (online speech off, no internet, language not installed, Windows
/// Runtime not available): the offline Windows Speech Recognition engine (<see cref="SapiSpeechEngine"/>), with a hint
/// why. When neither works, the dictation problem is reported (it names the Windows setting to change).</item>
/// </list>
/// No microphone and a blocked microphone are reported at once (the other engine would fail the same way).
/// </summary>
public sealed class WindowsSpeechEngineFactory : ISpeechEngineFactory
{
    private readonly Func<VoicePrivacy> _privacy;
    private readonly Func<VoiceRequest, ISpeechSink, CancellationToken, Task<ISpeechEngine>> _online;
    private readonly Func<VoiceRequest, ISpeechSink, string?, ISpeechEngine> _offline;

    /// <summary>Creates the factory over Windows' own recognizers.</summary>
    public WindowsSpeechEngineFactory()
        : this(
            VoicePrivacy.Read,
            (request, sink, cancellationToken) => WinRtSpeechEngine.StartAsync(request, sink, cancellationToken),
            (request, sink, notice) => SapiSpeechEngine.Start(request, sink, notice))
    {
    }

    /// <summary>Creates the factory over stand-in recognizers (tests).</summary>
    internal WindowsSpeechEngineFactory(
        Func<VoicePrivacy> privacy,
        Func<VoiceRequest, ISpeechSink, CancellationToken, Task<ISpeechEngine>> online,
        Func<VoiceRequest, ISpeechSink, string?, ISpeechEngine> offline)
    {
        _privacy = privacy;
        _online = online;
        _offline = offline;
    }

    /// <inheritdoc />
    public Task<ISpeechEngine> StartAsync(VoiceRequest request, ISpeechSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        return Task.Run(() => StartCoreAsync(request, sink, cancellationToken), cancellationToken);
    }

    /// <summary>The hint shown while the offline recognizer listens because dictation could not be used.</summary>
    internal static string OfflineNotice(VoiceProblem why) => why.Kind switch
    {
        VoiceProblemKind.OnlineSpeechOff => "Offline recognizer. For better accuracy turn on Windows Settings > Privacy > Speech > Online speech recognition.",
        VoiceProblemKind.NoInternet => "Offline recognizer (no internet connection).",
        VoiceProblemKind.LanguageMissing => "Offline recognizer: " + why.Title + ".",
        _ => "Offline recognizer (Windows dictation is not available).",
    };

    private async Task<ISpeechEngine> StartCoreAsync(VoiceRequest request, ISpeechSink sink, CancellationToken cancellationToken)
    {
        var privacy = _privacy();
        if (privacy.MicrophoneBlocked)
        {
            throw new VoiceException(VoiceProblems.MicrophoneBlocked());
        }

        var languageName = VoiceLanguages.DisplayName(request.LanguageTag);
        VoiceProblem onlineProblem;
        if (privacy.OnlineSpeechAllowed == false)
        {
            onlineProblem = VoiceProblems.OnlineSpeechOff(languageName);
        }
        else
        {
            try
            {
                return await _online(request, sink, cancellationToken).ConfigureAwait(false);
            }
            catch (VoiceException ex) when (ex.Problem.Kind is not (VoiceProblemKind.NoMicrophone or VoiceProblemKind.MicrophoneBlocked))
            {
                onlineProblem = ex.Problem;
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or VoiceException))
            {
                // Typically the Windows Runtime projection could not load (FileNotFoundException, TypeLoadException).
                CopilotLog.Warn("Voice input: Windows dictation is not available", ex);
                onlineProblem = VoiceProblems.Unavailable("Windows dictation is not available on this PC (" + ex.Message + ").");
            }

            CopilotLog.Info($"Voice input: Windows dictation not used ({onlineProblem.Kind}); trying the offline recognizer");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _offline(request, sink, OfflineNotice(onlineProblem));
        }
        catch (VoiceException ex) when (ex.Problem.Kind is VoiceProblemKind.LanguageMissing or VoiceProblemKind.Unavailable)
        {
            throw new VoiceException(onlineProblem, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or VoiceException))
        {
            CopilotLog.Warn("Voice input: the offline recognizer is not available", ex);
            throw new VoiceException(onlineProblem, ex);
        }
    }
}
