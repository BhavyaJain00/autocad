using System.IO;
using System.Globalization;
using AcadCopilot.Core;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Ipc;
using AcadCopilot.Plugin.Palette;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// No [assembly: ExtensionApplication] / [assembly: CommandClass] on purpose: assembly-level attributes whose types live in
// AutoCAD's DLLs break every tool that reads this assembly's attributes outside AutoCAD (the UI tests' xUnit runner).
// AutoCAD finds the IExtensionApplication implementation and the [CommandMethod]s by scanning the public types instead.
namespace AcadCopilot.Plugin.Acad;

/// <summary>
/// The plug-in entry point (IExtensionApplication), loaded by AutoCAD at start-up from the bundle.
/// <para>
/// Initialize (main thread, AutoCAD still starting): log, main-thread dispatcher, drawing revision tracker, tool
/// registry, panel backend (CLI runners + bridge launch), pipe server and discovery file (keyed by the acad.exe pid, random
/// token). The palette and the ribbon tab are created on the first Application.Idle, when AutoCAD's main window, ribbon
/// and first drawing exist (creating palettes during Initialize at start-up is too early).
/// Terminate: stops the pipe, deletes the discovery file, releases event handlers. Nothing ever escapes into AutoCAD:
/// failures are logged (addin.log) and reported in one short command-line line.
/// </para>
/// </summary>
public sealed class CopilotApp : IExtensionApplication
{
    private static CopilotApp? _current;
    private readonly Lock _gate = new();
    private AcadDispatcher? _dispatcher;
    private RevisionTracker? _revisions;
    private ToolRegistry? _tools;
    private CopilotBackend? _backend;
    private PipeServer? _pipe;
    private PaletteHost? _palette;
    private RibbonTabHost? _ribbon;
    private Learning.ActionRecorder? _recorder;
    private string? _pipeName;
    private string? _token;
    private string? _instanceFile;
    private string? _startupError;
    private bool _idleHooked;
    private bool _documentEventsHooked;

    /// <summary>The running instance (null before Initialize or when start-up failed early).</summary>
    public static CopilotApp? Current => Volatile.Read(ref _current);

    /// <summary>Main-thread dispatcher (null when start-up failed).</summary>
    public AcadDispatcher? Dispatcher => _dispatcher;

    /// <summary>The tool registry (null when start-up failed).</summary>
    public ToolRegistry? Tools => _tools;

    /// <summary>The panel backend (null when start-up failed).</summary>
    public CopilotBackend? Backend => _backend;

    /// <summary>The palette host (null until the first idle).</summary>
    public PaletteHost? Palette => _palette;

    /// <summary>Drawing revision counter (null when start-up failed).</summary>
    public RevisionTracker? Revisions => _revisions;

    /// <summary>Full pipe path (\\.\pipe\AcadCopilot-&lt;pid&gt;), once the pipe server runs.</summary>
    public string? PipePath => _pipeName is null ? null : PipeNames.FullPath(_pipeName);

    /// <summary>Discovery file path, once written.</summary>
    public string? InstanceFile => _instanceFile;

    /// <summary>Number of bridge processes connected and authenticated.</summary>
    public int ConnectedClients => _pipe?.AuthenticatedClientCount ?? 0;

    /// <summary>Why start-up failed, if it did.</summary>
    public string? StartupError => _startupError;

    /// <inheritdoc />
    public void Initialize()
    {
        Volatile.Write(ref _current, this);
        try
        {
            CopilotLog.Initialize(Path.Combine(AppPaths.Logs, "addin.log"));
            CopilotLog.Info($"AI Copilot {AcadHost.PluginVersion} starting in {AcadHost.ProductName} ({AcadHost.Release}), pid {AcadHost.ProcessId}, .NET {Environment.Version}.");
            StartServices();
        }
        catch (System.Exception ex)
        {
            _startupError = ex.Message;
            CopilotLog.Error("AI Copilot failed to start", ex);
            AcadHost.WriteLine($"could not start ({ex.Message}). Details: {CopilotLog.FilePath ?? AppPaths.Logs}");
        }

        try
        {
            // UI is created on the first idle even when the pipe failed, so the user still sees the panel and the error.
            AcadApp.Idle += OnFirstIdle;
            _idleHooked = true;
            AcadApp.QuitWillStart += OnQuitWillStart;
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("AI Copilot could not hook AutoCAD's idle event", ex);
        }
    }

    /// <inheritdoc />
    public void Terminate()
    {
        try
        {
            CopilotLog.Info("AI Copilot stopping.");
            Shutdown();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("AI Copilot shutdown failed", ex);
        }
    }

    /// <summary>Shows the palette when hidden, hides it when shown (command COPILOT, ribbon button). Main thread.</summary>
    public void TogglePalette()
    {
        var palette = EnsurePalette();
        if (palette is null)
        {
            AcadHost.WriteLine("the chat panel is not available: " + (_startupError ?? "see " + CopilotLog.FilePath));
            return;
        }

        palette.Toggle();
    }

    /// <summary>Status lines for COPILOTSTATUS. Main thread.</summary>
    public IReadOnlyList<string> DescribeStatus()
    {
        var lines = new List<string>
        {
            $"AI Copilot {AcadHost.PluginVersion} in {AcadHost.ProductName} ({AcadHost.Release}), process {AcadHost.ProcessId}.",
        };
        if (_startupError is not null)
        {
            lines.Add("Start-up error: " + _startupError);
        }

        lines.Add(_pipe is null
            ? "Pipe: not running."
            : $"Pipe: {PipePath} ({ConnectedClients} bridge connection(s)).");
        lines.Add("Instance file: " + (_instanceFile ?? "not written"));
        lines.Add(_tools is null
            ? "Tools: none."
            : $"Tools ({_tools.Count}): {string.Join(", ", _tools.Definitions.Select(d => d.Name))}.");
        var bridge = BridgeLocator.ExpectedPath;
        lines.Add($"Bridge: {bridge} ({(File.Exists(bridge) ? "found" : "MISSING")}).");
        lines.Add("Panel: " + (_palette is null ? "not created yet" : _palette.IsVisible ? "open" : "hidden") + ".");
        if (_backend is { } backend)
        {
            var sessions = backend.Sessions;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Chat tabs: {sessions.Count} ({backend.RunningSessionCount} working, at most {backend.MaxRunningSessions} at once)."));
            foreach (var session in sessions)
            {
                var turn = session.AcadTurn.Text is { } text ? ", " + text : string.Empty;
                lines.Add($"  {session.Id}: {session.Drawing?.DisplayTitle ?? "no drawing yet"}{(session.IsBusy ? ", working" : string.Empty)}{turn}.");
            }
        }
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"Drawing revision: {_revisions?.Current ?? 0}; AutoCAD dialog open: {(_dispatcher?.IsModal == true ? "yes" : "no")}."));
        lines.Add("Log: " + (CopilotLog.FilePath ?? "off"));
        lines.Add("Learned knowledge (user-editable): " + AppPaths.Knowledge);
        lines.Add(_recorder is { State: not AcadCopilot.Core.Learning.RecorderState.Off } recorder
            ? string.Create(CultureInfo.InvariantCulture, $"Learn from me: {recorder.State.ToString().ToLowerInvariant()} ({recorder.ActionCount} actions); recordings in {Path.Combine(AppPaths.Knowledge, "recordings")}.")
            : "Learn from me: off.");
        return lines;
    }

    private void StartServices()
    {
        var pid = AcadHost.ProcessId;
        _dispatcher = AcadDispatcher.CreateOnMainThread();
        _revisions = new RevisionTracker();
        _revisions.Attach(AcadApp.DocumentManager);
        _tools = ToolRegistry.CreateDefault();
        CopilotLog.Info($"{_tools.Count} AutoCAD tools registered: {string.Join(", ", _tools.Definitions.Select(d => d.Name))}.");

        IAgentRunner[] runners = [new ClaudeCodeRunner(), new CodexRunner(), new GeminiRunner()];
        var mainThread = _dispatcher;
        _backend = new CopilotBackend(
            runners,
            () => BridgeLocator.CreateLaunch(pid),
            CurrentSession(),
            currentDrawing: () => mainThread.IsMainThread ? DrawingDirectory.Active() : null);
        try
        {
            // The knowledge folder exists from the start, so the user can find and edit it.
            _backend.Knowledge.EnsureCreated();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Could not create the knowledge folder " + _backend.Knowledge.Directory, ex);
        }

        // "Learn from me": idle (no AutoCAD event attached) until the user clicks Start learning in the panel.
        _recorder = new Learning.ActionRecorder(_dispatcher, _backend.Knowledge);

        if (!BridgeLocator.Exists)
        {
            CopilotLog.Warn($"The MCP bridge is missing at {BridgeLocator.ExpectedPath}; the AI cannot reach AutoCAD until it is reinstalled.");
        }

        var dispatcher = _dispatcher;
        var handler = new PluginPipeHandler(
            _tools,
            _dispatcher,
            _revisions,
            _backend,
            (sessionId, ct) => dispatcher.InvokeAsync(
                () =>
                {
                    if (_palette?.IsVisible != true && EnsurePalette() is { } palette)
                    {
                        // The palette was hidden: show it on the tab that asks.
                        palette.Show();
                        palette.Pane?.ShowSession(sessionId);
                    }
                },
                ct));
        InstanceDiscovery.RemoveStale();
        _pipeName = PipeNames.ForProcess(pid);
        _token = PipeNames.NewToken();
        _pipe = new PipeServer(_pipeName, _token, handler);
        _pipe.Start();
        WriteDiscoveryFile();

        var documents = AcadApp.DocumentManager;
        documents.DocumentBecameCurrent += OnDocumentChanged;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        documents.DocumentDestroyed += OnDocumentDestroyed;
        _documentEventsHooked = true;
        CopilotLog.Info($"Pipe {PipePath} listening; discovery file {_instanceFile}.");
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        AcadApp.Idle -= OnFirstIdle;
        _idleHooked = false;
        try
        {
            EnsurePalette();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("The AI Copilot palette could not be created", ex);
            AcadHost.WriteLine("the chat panel could not be created: " + ex.Message);
        }

        try
        {
            _ribbon ??= new RibbonTabHost(TogglePalette);
            _ribbon.Attach();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("The AI Copilot ribbon tab could not be added", ex);
        }

        if (_startupError is null)
        {
            CopilotLog.Info("AI Copilot ready.");
        }
    }

    private PaletteHost? EnsurePalette()
    {
        if (_palette is not null)
        {
            return _palette;
        }

        if (_backend is null)
        {
            return null;
        }

        var palette = new PaletteHost(_backend, recorder: _recorder);
        palette.Create();
        _palette = palette;
        return palette;
    }

    private void OnDocumentChanged(object? sender, DocumentCollectionEventArgs e) => RefreshSession();

    private void OnDocumentDestroyed(object? sender, DocumentDestroyedEventArgs e) => RefreshSession();

    // Chats bound to a drawing that closes keep their conversation; their drawing actions fail with a clear message.
    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document is { } document)
            {
                _backend?.MarkDrawingClosed(DrawingDirectory.IdOf(document));
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Could not mark a closing drawing in the chats", ex);
        }
    }

    private void RefreshSession()
    {
        try
        {
            var session = CurrentSession();
            _backend?.UpdateAcad(session);
            _backend?.UpdateDrawings(DrawingDirectory.All());
            WriteDiscoveryFile();
            if (_pipe is { } pipe)
            {
                var payload = CopilotJson.ToNode(new DocumentChangedParams { DocumentTitle = session.DocumentTitle, Revision = _revisions?.Current ?? 0 });
                _ = pipe.NotifyAllAsync(RpcMethods.DocumentChanged, payload).ContinueWith(
                    t => CopilotLog.Warn("documentChanged notification failed", t.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Could not refresh the active drawing info", ex);
        }
    }

    private static AcadSessionInfo CurrentSession() =>
        new(AcadHost.ProcessId, AcadHost.VersionYear, AcadHost.ActiveDocumentTitle()) { Product = AcadHost.ProductName };

    private void WriteDiscoveryFile()
    {
        if (_pipeName is null || _token is null)
        {
            return;
        }

        lock (_gate)
        {
            _instanceFile = InstanceDiscovery.Write(new AcadInstanceInfo
            {
                Pid = AcadHost.ProcessId,
                Pipe = PipeNames.FullPath(_pipeName),
                Token = _token,
                AcadVersion = AcadHost.VersionYear,
                DocumentTitle = AcadHost.ActiveDocumentTitle(),
                StartedAt = _startedAt,
            });
        }
    }

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private void OnQuitWillStart(object? sender, EventArgs e)
    {
        try
        {
            // AutoCAD hides palettes while it closes: remember now whether the panel was open.
            _palette?.Freeze();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Could not save the palette state at quit", ex);
        }
    }

    private void Shutdown()
    {
        if (_idleHooked)
        {
            AcadApp.Idle -= OnFirstIdle;
            _idleHooked = false;
        }

        try
        {
            AcadApp.QuitWillStart -= OnQuitWillStart;
            if (_documentEventsHooked)
            {
                AcadApp.DocumentManager.DocumentBecameCurrent -= OnDocumentChanged;
                AcadApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                AcadApp.DocumentManager.DocumentDestroyed -= OnDocumentDestroyed;
                _documentEventsHooked = false;
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Could not detach the document handlers", ex);
        }

        _dispatcher?.Dispose();
        if (_pipe is { } pipe)
        {
            _pipe = null;
            try
            {
                if (!pipe.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)))
                {
                    CopilotLog.Warn("The pipe server did not stop within 3 s.");
                }
            }
            catch (System.Exception ex)
            {
                CopilotLog.Warn("Stopping the pipe server failed", ex);
            }
        }

        InstanceDiscovery.Delete(AcadHost.ProcessId);
        _instanceFile = null;

        try
        {
            _ribbon?.Dispose();
            _palette?.Dispose();
            _recorder?.Dispose();
            _revisions?.Dispose();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Warn("Releasing the panel failed", ex);
        }

        CopilotLog.Info("AI Copilot stopped.");
    }
}
