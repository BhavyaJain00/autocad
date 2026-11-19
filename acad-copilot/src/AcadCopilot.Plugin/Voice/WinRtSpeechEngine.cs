using System.Globalization;
using System.Runtime.CompilerServices;
using AcadCopilot.Core.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.SpeechRecognition;
using WinLanguage = Windows.Globalization.Language;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// Windows' built-in dictation (Windows.Media.SpeechRecognition, the recognizer behind Win+H): continuous dictation
/// with live hypotheses, in any language whose Windows speech pack is installed (en-IN, en-US, hi-IN ...). Needs
/// Windows Settings > Privacy > Speech > "Online speech recognition" on (Windows' own service, the user's Windows
/// account, no key) and microphone access for desktop apps. Works in an unpackaged desktop process such as AutoCAD.
/// Every call runs off the UI thread; events arrive on worker threads and go straight to the sink.
/// </summary>
internal sealed class WinRtSpeechEngine : ISpeechEngine
{
    /// <summary>Backup stop when nothing is heard (the panel stops after about 2 s of silence itself).</summary>
    private static readonly TimeSpan AutoStopSilence = TimeSpan.FromSeconds(15);

    private readonly SpeechRecognizer _recognizer;
    private readonly ISpeechSink _sink;
    private volatile bool _stopping;
    private int _disposed;

    private WinRtSpeechEngine(SpeechRecognizer recognizer, ISpeechSink sink, string languageTag)
    {
        _recognizer = recognizer;
        _sink = sink;
        LanguageTag = languageTag;
        _recognizer.HypothesisGenerated += OnHypothesis;
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResult;
        _recognizer.ContinuousRecognitionSession.Completed += OnCompleted;
    }

    /// <inheritdoc />
    public string Name => "Windows dictation";

    /// <inheritdoc />
    public string LanguageTag { get; }

    /// <inheritdoc />
    public string? Notice => null;

    /// <summary>
    /// Starts dictation. Kept out of line so that a missing Windows Runtime projection surfaces as an exception of
    /// this call (caught by the factory), not of its caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<ISpeechEngine> StartAsync(VoiceRequest request, ISpeechSink sink, CancellationToken cancellationToken)
    {
        var microphones = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask(cancellationToken).ConfigureAwait(false);
        if (!microphones.Any(m => m.IsEnabled))
        {
            throw new VoiceException(VoiceProblems.NoMicrophone());
        }

        var supported = SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList();
        var tag = VoiceLanguages.Pick(request.LanguageTag, supported, PreferredLanguages());
        var languageName = VoiceLanguages.DisplayName(tag ?? request.LanguageTag);
        if (tag is null)
        {
            throw new VoiceException(VoiceProblems.LanguageMissing(languageName));
        }

        var recognizer = new SpeechRecognizer(new WinLanguage(tag));
        try
        {
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await recognizer.CompileConstraintsAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new VoiceException(Map(compiled.Status, languageName) ?? VoiceProblems.Failed("Windows dictation could not start (" + compiled.Status + ")."));
            }

            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = AutoStopSilence;
            var engine = new WinRtSpeechEngine(recognizer, sink, tag);
            try
            {
                await recognizer.ContinuousRecognitionSession.StartAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                engine.Unhook();
                throw;
            }

            CopilotLog.Info($"Voice input: Windows dictation started ({tag})");
            return engine;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recognizer.Dispose();
            throw new VoiceException(VoiceProblems.FromException(ex, languageName), ex);
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        _stopping = true;
        try
        {
            await _recognizer.ContinuousRecognitionSession.StopAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException || ex.HResult == unchecked((int)0x8000000E))
        {
            // The session had already ended (time-out or error).
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping = true;
        Unhook();
        try
        {
            _recognizer.Dispose();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: closing Windows dictation failed", ex);
        }
    }

    private static IEnumerable<string?> PreferredLanguages()
    {
        string? system = null;
        try
        {
            system = SpeechRecognizer.SystemSpeechLanguage?.LanguageTag;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: the Windows speech language is unknown", ex);
        }

        yield return system;
        yield return CultureInfo.CurrentUICulture.Name;
        IReadOnlyList<string> user = [];
        try
        {
            user = Windows.System.UserProfile.GlobalizationPreferences.Languages;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: the Windows language list is unknown", ex);
        }

        foreach (var tag in user)
        {
            yield return tag;
        }
    }

    // Recognition outcomes that are problems; null for normal ends (silence, stop).
    private static VoiceProblem? Map(SpeechRecognitionResultStatus status, string languageName) => status switch
    {
        SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.TimeoutExceeded or SpeechRecognitionResultStatus.UserCanceled
            or SpeechRecognitionResultStatus.PauseLimitExceeded => null,
        SpeechRecognitionResultStatus.MicrophoneUnavailable => VoiceProblems.NoMicrophone(),
        SpeechRecognitionResultStatus.NetworkFailure => VoiceProblems.NoInternet(),
        SpeechRecognitionResultStatus.TopicLanguageNotSupported or SpeechRecognitionResultStatus.GrammarLanguageMismatch => VoiceProblems.LanguageMissing(languageName),
        SpeechRecognitionResultStatus.AudioQualityFailure => VoiceProblems.Failed("The microphone sound was too poor to recognize (too quiet or too noisy). Check the Windows input device and try again."),
        _ => VoiceProblems.Failed("Windows dictation stopped (" + status + ")."),
    };

    private void OnHypothesis(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _sink.OnHypothesis(args.Hypothesis.Text);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: hypothesis failed", ex);
        }
    }

    private void OnResult(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        try
        {
            var result = args.Result;
            if (result.Status == SpeechRecognitionResultStatus.Success && result.Confidence != SpeechRecognitionConfidence.Rejected &&
                !string.IsNullOrWhiteSpace(result.Text))
            {
                _sink.OnPhrase(result.Text);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: result failed", ex);
        }
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        try
        {
            if (!_stopping)
            {
                _sink.OnStopped(Map(args.Status, VoiceLanguages.DisplayName(LanguageTag)));
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: completion failed", ex);
        }
    }

    private void Unhook()
    {
        try
        {
            _recognizer.HypothesisGenerated -= OnHypothesis;
            _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResult;
            _recognizer.ContinuousRecognitionSession.Completed -= OnCompleted;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: unhooking Windows dictation failed", ex);
        }
    }
}
