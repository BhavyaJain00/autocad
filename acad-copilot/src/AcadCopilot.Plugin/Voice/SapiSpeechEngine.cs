using System.Globalization;
using System.Runtime.CompilerServices;
using System.Speech.Recognition;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// Offline fallback: the desktop "Windows Speech Recognition" engine (SAPI 5 through System.Speech) with its free
/// dictation grammar. Works without internet and without the "Online speech recognition" switch, but is less accurate
/// and only knows the languages whose desktop recognizer is installed (English (US) on most PCs). Runs on its own
/// worker threads; events go straight to the sink.
/// </summary>
internal sealed class SapiSpeechEngine : ISpeechEngine
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly SpeechRecognitionEngine _engine;
    private readonly ISpeechSink _sink;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private int _disposed;

    private SapiSpeechEngine(SpeechRecognitionEngine engine, ISpeechSink sink, string languageTag, string? notice)
    {
        _engine = engine;
        _sink = sink;
        LanguageTag = languageTag;
        Notice = notice;
        _engine.SpeechHypothesized += OnHypothesized;
        _engine.SpeechRecognized += OnRecognized;
        _engine.RecognizeCompleted += OnCompleted;
    }

    /// <inheritdoc />
    public string Name => "Windows offline speech";

    /// <inheritdoc />
    public string LanguageTag { get; }

    /// <inheritdoc />
    public string? Notice { get; }

    /// <summary>Opens the default microphone and starts dictation (blocking for a moment: call off the UI thread).</summary>
    /// <param name="request">The language wanted.</param>
    /// <param name="sink">Where the words go.</param>
    /// <param name="notice">The hint shown while listening (why the offline recognizer is used), or null.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ISpeechEngine Start(VoiceRequest request, ISpeechSink sink, string? notice)
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var tag = VoiceLanguages.Pick(
            request.LanguageTag,
            [.. recognizers.Select(r => r.Culture.Name)],
            [CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name]);
        var languageName = VoiceLanguages.DisplayName(tag ?? request.LanguageTag);
        if (tag is null)
        {
            throw new VoiceException(VoiceProblems.LanguageMissing(languageName));
        }

        var info = recognizers.First(r => string.Equals(r.Culture.Name, tag, StringComparison.OrdinalIgnoreCase));
        var engine = new SpeechRecognitionEngine(info);
        try
        {
            engine.SetInputToDefaultAudioDevice();
            engine.LoadGrammar(new DictationGrammar { Name = "dictation" });
            var started = new SapiSpeechEngine(engine, sink, tag, notice);
            engine.RecognizeAsync(RecognizeMode.Multiple);
            CopilotLog.Info($"Voice input: offline Windows Speech Recognition started ({tag})");
            return started;
        }
        catch (Exception ex)
        {
            engine.Dispose();
            throw new VoiceException(VoiceProblems.FromException(ex, languageName), ex);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        _stopping = true;
        try
        {
            // Finishes the phrase being spoken (SpeechRecognized) before RecognizeCompleted.
            _engine.RecognizeAsyncStop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Not running any more.
        }

        try
        {
            await _completed.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            CopilotLog.Warn("Voice input: Windows Speech Recognition did not stop in time", ex);
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
        _engine.SpeechHypothesized -= OnHypothesized;
        _engine.SpeechRecognized -= OnRecognized;
        _engine.RecognizeCompleted -= OnCompleted;
        try
        {
            _engine.RecognizeAsyncCancel();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Not running.
        }

        try
        {
            _engine.Dispose();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: closing Windows Speech Recognition failed", ex);
        }
    }

    private void OnHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (!_stopping && !string.IsNullOrWhiteSpace(e.Result?.Text))
        {
            _sink.OnHypothesis(e.Result.Text);
        }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Result?.Text))
        {
            _sink.OnPhrase(e.Result.Text);
        }
    }

    private void OnCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        _completed.TrySetResult();
        if (_stopping)
        {
            return;
        }

        var problem = e.Error is { } error ? VoiceProblems.FromException(error, VoiceLanguages.DisplayName(LanguageTag)) : null;
        _sink.OnStopped(problem);
    }
}
