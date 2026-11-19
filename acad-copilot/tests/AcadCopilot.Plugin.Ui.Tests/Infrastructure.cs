using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadCopilot.Core;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;

// The drafting pace (DraftingPace.Watch) and AppPaths' home folder are process-wide: run the tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>Keeps every test away from the real %LOCALAPPDATA%\AcadCopilot folders (settings.json, panel.json).</summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string> Home = new(() =>
    {
        var home = Path.Combine(Path.GetTempPath(), "acadcopilot-ui-tests-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, home);
        return home;
    });

    /// <summary>Points AppPaths at a temporary folder for this test process (idempotent).</summary>
    public static string Isolate() => Home.Value;
}

/// <summary>Small generated pictures (nothing on disk).</summary>
internal static class TestImages
{
    /// <summary>A PNG that looks like a plan on AutoCAD's dark drawing area: dashed plot line, double walls, a dimension line.</summary>
    public static byte[] DrawingPng(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x21, 0x28, 0x30)), null, new Rect(0, 0, width, height));
            var plot = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0)), 1) { DashStyle = DashStyles.Dash };
            var wall = new Pen(Brushes.White, 1.5);
            var dims = new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0xC8, 0x5A)), 1);
            context.DrawRectangle(null, plot, new Rect(width * 0.1, height * 0.08, width * 0.8, height * 0.8));
            context.DrawRectangle(null, wall, new Rect(width * 0.2, height * 0.2, width * 0.6, height * 0.6));
            context.DrawRectangle(null, wall, new Rect((width * 0.2) + 5, (height * 0.2) + 5, (width * 0.6) - 10, (height * 0.6) - 10));
            context.DrawLine(wall, new Point(width * 0.5, (height * 0.2) + 5), new Point(width * 0.5, (height * 0.8) - 5));
            context.DrawLine(wall, new Point((width * 0.5) + 3, (height * 0.2) + 5), new Point((width * 0.5) + 3, (height * 0.8) - 5));
            context.DrawLine(dims, new Point(width * 0.2, height * 0.92), new Point(width * 0.8, height * 0.92));
            context.DrawLine(dims, new Point(width * 0.2, height * 0.88), new Point(width * 0.2, height * 0.96));
            context.DrawLine(dims, new Point(width * 0.8, height * 0.88), new Point(width * 0.8, height * 0.96));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>Runs test bodies on a dedicated STA thread with a WPF dispatcher (no window is ever created).</summary>
internal static class Sta
{
    public static void Run(Func<Task> body, int timeoutSeconds = 120)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                task = Task.FromException(ex);
            }

            task.ContinueWith(
                t =>
                {
                    if (t.Exception is not null)
                    {
                        error = t.Exception.InnerException ?? t.Exception;
                    }

                    frame.Continue = false;
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            throw new TimeoutException("The STA test body did not finish.");
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>Processes every queued dispatcher operation down to ApplicationIdle priority.</summary>
    public static void DoEvents() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Lets async continuations run, then drains the dispatcher.</summary>
    public static async Task Settle(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            await Task.Delay(15);
            DoEvents();
        }
    }

    /// <summary>Pumps until <paramref name="condition"/> is true (or fails after 10 s).</summary>
    public static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
            DoEvents();
        }
    }
}

/// <summary>Renders elements off-screen to PNG files in artifacts\ui.</summary>
internal static class Snapshot
{
    public static string ArtifactsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcadCopilot.sln")))
            {
                directory = directory.Parent;
            }

            // Built with --artifacts-path outside the repository: use the repository these sources are in.
            var root = directory?.FullName ?? RepositoryOfSources() ?? Path.GetTempPath();
            return Directory.CreateDirectory(Path.Combine(root, "artifacts", "ui")).FullName;
        }
    }

    private static string? RepositoryOfSources([CallerFilePath] string sourceFile = "")
    {
        var directory = string.IsNullOrEmpty(sourceFile) ? null : new FileInfo(sourceFile).Directory;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcadCopilot.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    public static string Render(FrameworkElement element, double width, double height, string name, double scale = 1.5)
    {
        element.Width = width;
        element.Height = height;
        for (var i = 0; i < 4; i++)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            Sta.DoEvents();
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(ArtifactsDirectory, name + ".png");
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return path;
    }
}

/// <summary>Runner stand-in: only the metadata and status matter for the panel.</summary>
internal sealed class FakeRunner : IAgentRunner
{
    public FakeRunner(ProviderId id, string displayName, IReadOnlyList<ModelChoice> models, ProviderStatus status)
    {
        ProviderId = id;
        DisplayName = displayName;
        Models = models;
        Status = status;
    }

    public ProviderId ProviderId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<ModelChoice> Models { get; }

    public ProviderStatus Status { get; set; }

    public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public CommandLine GetSignInCommand() => new("fake", ["login"]);

    public IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The panel talks to the backend, not to runners.");
}

/// <summary>Scriptable <see cref="ICopilotBackend"/>: each SendAsync yields <see cref="Script"/>, then optionally waits.</summary>
internal sealed class FakeBackend : ICopilotBackend
{
    private readonly Dictionary<ProviderId, string?> _models = [];
    private ProviderId _selected;
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeBackend(params FakeRunner[] runners)
    {
        Runners = runners;
        _selected = runners[0].ProviderId;
    }

    public static FakeBackend Create(SignInState claude = SignInState.SignedIn, SignInState codex = SignInState.SignedOut, SignInState gemini = SignInState.Unknown, bool codexInstalled = true) => new(
        new FakeRunner(ProviderId.Claude, "Claude", [new(null, "Default"), new("opus", "Opus"), new("sonnet", "Sonnet")],
            new ProviderStatus { Provider = ProviderId.Claude, Installed = true, Version = "2.1.312", SignIn = claude, Account = claude == SignInState.SignedIn ? "Claude Max" : null }),
        new FakeRunner(ProviderId.Codex, "ChatGPT", [new(null, "Default"), new("gpt-5-codex", "GPT-5 Codex")],
            new ProviderStatus { Provider = ProviderId.Codex, Installed = codexInstalled, Version = codexInstalled ? "0.154.0" : null, SignIn = codexInstalled ? codex : SignInState.Unknown }),
        new FakeRunner(ProviderId.Gemini, "Gemini", [new(null, "Default"), new("gemini-2.5-pro", "Gemini 2.5 Pro")],
            new ProviderStatus { Provider = ProviderId.Gemini, Installed = true, Version = "0.60.0", SignIn = gemini }));

    public IReadOnlyList<IAgentRunner> Runners { get; }

    public ProviderId SelectedProvider
    {
        get => _selected;
        set
        {
            _selected = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsBusy { get; private set; }

    public AcadSessionInfo Acad { get; set; } = new(4242, "2027", "GF-plan-30x40.dwg");

    public event EventHandler? StateChanged;

    public event EventHandler? ChatReset;

    public List<AgentEvent> Script { get; } = [];

    /// <summary>When true, SendAsync waits after the script until <see cref="Release"/> or Stop.</summary>
    public bool HoldOpen { get; set; }

    public List<string> Sent { get; } = [];

    public List<IReadOnlyList<string>> SentAttachments { get; } = [];

    public List<ProviderId> SignIns { get; } = [];

    public int NewChats { get; private set; }

    public ICopilotUi? Ui { get; private set; }

    public string? GetModel(ProviderId provider) => _models.TryGetValue(provider, out var model) ? model : null;

    public void SetModel(ProviderId provider, string? modelId)
    {
        _models[provider] = modelId;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) =>
        Runners.First(r => r.ProviderId == provider).GetStatusAsync(cancellationToken);

    public void Release() => _release.TrySetResult();

    public async IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Sent.Add(text);
        SentAttachments.Add(attachments);
        IsBusy = true;
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            foreach (var item in Script)
            {
                await Task.Yield();
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new TurnFailed("Stopped.", AgentFailureKind.Cancelled);
                    yield break;
                }

                yield return item;
                if (item is TurnCompleted or TurnFailed)
                {
                    yield break;
                }
            }

            if (HoldOpen)
            {
                var stopped = false;
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                }

                if (stopped)
                {
                    yield return new TurnFailed("Stopped.", AgentFailureKind.Cancelled);
                    yield break;
                }
            }

            yield return new TurnCompleted("end_turn");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NewChat()
    {
        NewChats++;
        ChatReset?.Invoke(this, EventArgs.Empty);
    }

    public bool SignIn(ProviderId provider)
    {
        SignIns.Add(provider);
        return true;
    }

    public void AttachUi(ICopilotUi ui) => Ui = ui;
}
