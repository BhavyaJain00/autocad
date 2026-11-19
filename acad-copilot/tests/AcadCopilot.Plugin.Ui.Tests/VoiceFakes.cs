using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>A clock the tests move by hand (silence time-outs).</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = TimeSpan.TicksPerHour;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>A recognizer stand-in: never touches audio. The test speaks through <see cref="Sink"/>.</summary>
internal sealed class FakeSpeechEngine(ISpeechSink sink, string languageTag = "en-IN", string name = "Windows dictation", string? notice = null) : ISpeechEngine
{
    public ISpeechSink Sink { get; } = sink;

    public string Name { get; } = name;

    public string LanguageTag { get; } = languageTag;

    public string? Notice { get; } = notice;

    /// <summary>Phrase delivered while stopping (the recognizer finishing the last words).</summary>
    public string? FinalPhraseOnStop { get; set; }

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public Task StopAsync()
    {
        Stops++;
        if (FinalPhraseOnStop is { } final)
        {
            Sink.OnPhrase(final);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Factory stand-in: starts <see cref="FakeSpeechEngine"/>s, fails with <see cref="Failure"/>, or waits for <see cref="Gate"/>.</summary>
internal sealed class FakeSpeechFactory : ISpeechEngineFactory
{
    public List<VoiceRequest> Requests { get; } = [];

    public List<FakeSpeechEngine> Engines { get; } = [];

    public FakeSpeechEngine? Last => Engines.Count > 0 ? Engines[^1] : null;

    /// <summary>Thrown by the next starts when set.</summary>
    public VoiceProblem? Failure { get; set; }

    /// <summary>When set, a start waits for it (the "Starting the microphone..." state).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public string LanguageTag { get; set; } = "en-IN";

    public string EngineName { get; set; } = "Windows dictation";

    public string? Notice { get; set; }

    public async Task<ISpeechEngine> StartAsync(VoiceRequest request, ISpeechSink sink, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (Failure is { } problem)
        {
            throw new VoiceException(problem);
        }

        var engine = new FakeSpeechEngine(sink, request.LanguageTag ?? LanguageTag, EngineName, Notice);
        Engines.Add(engine);
        return engine;
    }
}
