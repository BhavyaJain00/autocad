using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadRuntime = Autodesk.AutoCAD.Runtime;

namespace AcadCopilot.Plugin.Acad;

/// <summary>
/// AutoCAD could not run a request: a modal dialog is open, the main thread did not pick the request up in time, or
/// AutoCAD is closing. The pipe handler returns it to the bridge as JSON-RPC error <see cref="RpcErrorCodes.AcadBusy"/>.
/// </summary>
public sealed class AcadBusyException : Exception
{
    /// <summary>Creates the exception with a message the AI can act on.</summary>
    public AcadBusyException(string message)
        : base(message)
    {
    }

    /// <summary>The JSON-RPC error code (<see cref="RpcErrorCodes.AcadBusy"/>).</summary>
    public int Code => RpcErrorCodes.AcadBusy;
}

/// <summary>
/// Runs code on AutoCAD's main thread, in the application context, for callers on any thread (the pipe server answers
/// the MCP bridge on thread-pool threads; the AutoCAD API is single-threaded).
/// <para>
/// <b>Mechanism (AutoCAD 2027, .NET 10).</b> Work items are posted to the WPF <see cref="Dispatcher"/> of AutoCAD's
/// main thread, captured in <see cref="CreateOnMainThread"/> (IExtensionApplication.Initialize runs on the main thread).
/// AutoCAD's main window, ribbon and palettes are hosted on that thread, and every Win32 message loop that runs there
/// (the idle main loop, and the nested loops while a command waits for input) dispatches the dispatcher's posted
/// message. An item therefore starts like a palette button click: from the message loop, in the application context,
/// within milliseconds, without the user moving the mouse. While an item runs, <see cref="SynchronizationContext.Current"/>
/// is a <see cref="DispatcherSynchronizationContext"/> of the same dispatcher, so every <c>await</c> inside an
/// asynchronous item resumes on the main thread, also while a command is active and waiting for input (which is what
/// typing command input with Document.SendStringToExecute and then awaiting the next prompt needs).
/// </para>
/// <para>
/// <b>Why not the alternatives.</b> <c>DocumentCollection.ExecuteInApplicationContext</c> is called from a command (on
/// the main thread) to leave the document context, so it does not solve the hop from a background thread.
/// <c>Application.Idle</c> is raised by AutoCAD's idle processing after messages have been handled: a queue drained from
/// Idle needs a wake-up message when AutoCAD is completely idle, and AutoCAD does not document that Idle is raised while
/// a command waits for input, which typing command input step by step depends on. AutoCAD's own
/// <c>Autodesk.AutoCAD.Runtime.SynchronizationContext</c> is installed on the main thread but its scheduling is not
/// documented. The main thread's WPF dispatcher is documented .NET behaviour and is what AutoCAD's WPF palettes and the
/// ribbon run on. <see cref="NextIdleAsync"/> still exposes Idle as an "AutoCAD has caught up" signal.
/// </para>
/// <para>
/// <b>Busy.</b> An item does not start while a modal dialog is open in AutoCAD (AutoCAD's main window is disabled while
/// any modal dialog or message box is shown); it waits for the dialog to close. An item that has not started within
/// <see cref="StartTimeout"/> fails with <see cref="AcadBusyException"/>. Code already on the main thread runs inline.
/// Once started, synchronous items cannot be interrupted; asynchronous items should honour their cancellation token.
/// </para>
/// </summary>
public sealed class AcadDispatcher : IDisposable
{
    private static readonly TimeSpan ModalRecheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherSynchronizationContext _syncContext;
    private readonly int _mainThreadId;
    private readonly List<WorkItem> _waitingForDialog = [];
    private DispatcherTimer? _dialogTimer;
    private IntPtr _mainWindow;
    private int _modalEvents;
    private volatile bool _closed;
    private bool _contextWarningLogged;

    private AcadDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _syncContext = new DispatcherSynchronizationContext(dispatcher, DispatcherPriority.Normal);
        _mainThreadId = dispatcher.Thread.ManagedThreadId;
    }

    /// <summary>Default time an item may wait before it starts (see <see cref="StartTimeout"/>).</summary>
    public static TimeSpan DefaultStartTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an item may wait for AutoCAD's main thread before it fails as busy. Default 30 s.</summary>
    public TimeSpan StartTimeout { get; set; } = DefaultStartTimeout;

    /// <summary>True when the caller is on AutoCAD's main thread.</summary>
    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>
    /// True while a modal dialog or message box is open in AutoCAD: Windows disables the owner (AutoCAD's main window)
    /// for every modal MFC, WPF or Win32 dialog. Any thread.
    /// </summary>
    public bool IsModal
    {
        get
        {
            var window = _mainWindow;
            return window != IntPtr.Zero && !IsWindowEnabled(window);
        }
    }

    /// <summary>Balance of AutoCAD's EnterModal / LeaveModal notifications (diagnostics; 0 when no modal state is open).</summary>
    public int ModalNotificationDepth => Volatile.Read(ref _modalEvents);

    /// <summary>True after <see cref="Dispose"/> (AutoCAD is closing): new items fail as busy.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Creates the dispatcher for the calling thread, which must be AutoCAD's main thread (call it from
    /// IExtensionApplication.Initialize or a command).
    /// </summary>
    public static AcadDispatcher CreateOnMainThread()
    {
        var instance = new AcadDispatcher(Dispatcher.CurrentDispatcher);
        instance.RefreshMainWindow();
        AcadApp.EnterModal += instance.OnEnterModal;
        AcadApp.LeaveModal += instance.OnLeaveModal;
        return instance;
    }

    /// <summary>Runs <paramref name="func"/> on AutoCAD's main thread and returns its result.</summary>
    /// <exception cref="AcadBusyException">AutoCAD did not start it within <see cref="StartTimeout"/>, or is closing.</exception>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeCoreAsync(() => Task.FromResult(func()), cancellationToken);
    }

    /// <summary>
    /// Runs the asynchronous <paramref name="func"/> on AutoCAD's main thread; every await inside it resumes on the main
    /// thread in the application context. The returned task completes when the task of <paramref name="func"/> does.
    /// </summary>
    /// <exception cref="AcadBusyException">AutoCAD did not start it within <see cref="StartTimeout"/>, or is closing.</exception>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeCoreAsync(func, cancellationToken);
    }

    /// <summary>Runs <paramref name="action"/> on AutoCAD's main thread.</summary>
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeCoreAsync(
            () =>
            {
                action();
                return Task.FromResult(true);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads the active drawing on the main thread inside an open/close transaction (no document lock: reading needs
    /// none and must also work while a command is active).
    /// </summary>
    /// <exception cref="ToolException">No drawing is open.</exception>
    public Task<T> ReadAsync<T>(Func<Document, Transaction, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        return InvokeAsync(
            () =>
            {
                var document = RequireActiveDocument();
                using var transaction = document.Database.TransactionManager.StartOpenCloseTransaction();
                var result = read(document, transaction);
                transaction.Commit();
                return result;
            },
            cancellationToken);
    }

    /// <summary>
    /// Changes the active drawing on the main thread: locks the document (required in the application context) and runs
    /// <paramref name="write"/> in one transaction, committed when it returns and aborted when it throws.
    /// </summary>
    /// <exception cref="ToolException">No drawing is open, or the drawing is read-only.</exception>
    /// <exception cref="AcadBusyException">The document could not be locked (an active command holds it).</exception>
    public Task<T> WriteAsync<T>(Func<Document, Transaction, T> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return InvokeAsync(
            () =>
            {
                var document = RequireActiveDocument();
                if (document.IsReadOnly)
                {
                    throw new ToolException($"The drawing '{Path.GetFileName(document.Name)}' is open read-only; it cannot be changed.");
                }

                using var documentLock = LockDocument(document);
                using var transaction = document.TransactionManager.StartTransaction();
                var result = write(document, transaction);
                transaction.Commit();
                return result;
            },
            cancellationToken);
    }

    /// <summary>
    /// Locks <paramref name="document"/> for writing from the application context. Main thread only. Dispose the result.
    /// </summary>
    /// <exception cref="AcadBusyException">AutoCAD refused the lock.</exception>
    public static DocumentLock LockDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            return document.LockDocument();
        }
        catch (AcadRuntime.Exception ex) when (ex.ErrorStatus == AcadRuntime.ErrorStatus.LockViolation)
        {
            throw new AcadBusyException(
                "AutoCAD is busy: the drawing is locked by the active command. Cancel the command (cancel_command) and try again.");
        }
    }

    /// <summary>
    /// The drawing the current tool call must act on (a chat tab's drawing, <see cref="DrawingDirectory"/> id), or null
    /// for "whatever drawing is active". Set by the pipe handler around a tool handler; it flows (ExecutionContext) into
    /// the main-thread items the handler posts, where <see cref="RequireActiveDocument"/> checks it.
    /// </summary>
    public static string? ExpectedDrawingId
    {
        get => ExpectedDrawing.Value;
        set => ExpectedDrawing.Value = value;
    }

    private static readonly AsyncLocal<string?> ExpectedDrawing = new();

    /// <summary>
    /// The active drawing. Main thread only. When the running tool call belongs to a chat bound to another drawing
    /// (<see cref="ExpectedDrawingId"/>), throws <see cref="DrawingNotActiveException"/> instead of acting on the wrong one.
    /// </summary>
    /// <exception cref="ToolException">No drawing is open.</exception>
    /// <exception cref="DrawingNotActiveException">The chat's drawing is not the active one.</exception>
    public static Document RequireActiveDocument()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument
            ?? throw new ToolException("No drawing is open in AutoCAD. Ask the user to open or create a drawing (for example with the NEW command), then try again.");
        EnsureExpectedDrawing(document);
        return document;
    }

    private static void EnsureExpectedDrawing(Document? active = null)
    {
        if (ExpectedDrawing.Value is not { } expected)
        {
            return;
        }

        active ??= AcadApp.DocumentManager.MdiActiveDocument;
        if (active is not null && !string.Equals(DrawingDirectory.IdOf(active), expected, StringComparison.Ordinal))
        {
            throw new DrawingNotActiveException(
                $"Another drawing than this chat's drawing is active in AutoCAD ('{Path.GetFileName(active.Name)}'); nothing was done. Call the tool again.");
        }
    }

    /// <summary>
    /// Completes on AutoCAD's next <c>Application.Idle</c> notification (raised by AutoCAD's main message loop when no
    /// messages are pending). Call it on the main thread (inside an item); the await resumes on the main thread.
    /// </summary>
    /// <exception cref="TimeoutException">No idle notification arrived within <paramref name="timeout"/>.</exception>
    public Task NextIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException("NextIdleAsync must be called on AutoCAD's main thread.");
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        DispatcherTimer? timer = null;
        CancellationTokenRegistration registration = default;

        void Finish(Action complete)
        {
            AcadApp.Idle -= handler;
            timer?.Stop();
            registration.Dispose();
            complete();
        }

        handler = (_, _) => Finish(() => completion.TrySetResult());
        timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = timeout };
        timer.Tick += (_, _) => Finish(() => completion.TrySetException(new TimeoutException($"AutoCAD did not become idle within {timeout.TotalSeconds:0.#} s.")));
        AcadApp.Idle += handler;
        timer.Start();
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() => _dispatcher.InvokeAsync(() => Finish(() => completion.TrySetCanceled(cancellationToken))));
        }

        return completion.Task;
    }

    /// <summary>Stops accepting work (AutoCAD is closing). Items that have not started fail as busy.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            AcadApp.EnterModal -= OnEnterModal;
            AcadApp.LeaveModal -= OnLeaveModal;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not detach the modal-state handlers", ex);
        }

        if (IsMainThread)
        {
            _dialogTimer?.Stop();
            foreach (var item in _waitingForDialog)
            {
                item.TryFail(new AcadBusyException("AutoCAD is closing."));
            }

            _waitingForDialog.Clear();
        }
    }

    private Task<T> InvokeCoreAsync<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (_closed || _dispatcher.HasShutdownStarted)
        {
            return Task.FromException<T>(new AcadBusyException("AutoCAD is closing; the request was not run."));
        }

        // A chat's tool call must not act on another drawing than its own (ExpectedDrawingId flows in with the item).
        var item = new WorkItem<T>(
            () =>
            {
                EnsureExpectedDrawing();
                return body();
            },
            cancellationToken);
        if (IsMainThread)
        {
            // Already on the main thread (a command, an event handler or panel code): run inline; queueing would deadlock
            // a caller that waits.
            Start(item);
            return item.Task;
        }

        item.Arm(StartTimeout, BusyMessage);
        Post(item);
        return item.Task;
    }

    private void Post(WorkItem item)
    {
        try
        {
            // Normal priority = a posted Win32 message: picked up by any message loop on the main thread, even while the
            // user keeps moving the mouse (Background-priority work is postponed while input is pending).
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => TryStart(item)));
        }
        catch (Exception ex)
        {
            item.TryFail(new AcadBusyException("AutoCAD's main thread is not accepting requests (" + ex.Message + ")."));
        }
    }

    private void TryStart(WorkItem item)
    {
        if (item.IsAbandoned)
        {
            return;
        }

        if (_closed)
        {
            item.TryFail(new AcadBusyException("AutoCAD is closing; the request was not run."));
            return;
        }

        RefreshMainWindow();
        if (IsModal)
        {
            _waitingForDialog.Add(item);
            EnsureDialogTimer();
            return;
        }

        Start(item);
    }

    private void Start(WorkItem item)
    {
        if (!item.TryBegin())
        {
            return;
        }

        WarnOnceIfNotApplicationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_syncContext);
        try
        {
            item.Run();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void EnsureDialogTimer()
    {
        if (_dialogTimer is null)
        {
            _dialogTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ModalRecheckInterval };
            _dialogTimer.Tick += (_, _) => RecheckWaitingItems();
        }

        _dialogTimer.Start();
    }

    private void RecheckWaitingItems()
    {
        _waitingForDialog.RemoveAll(i => i.IsAbandoned);
        if (_waitingForDialog.Count == 0)
        {
            _dialogTimer?.Stop();
            return;
        }

        if (IsModal)
        {
            return;
        }

        _dialogTimer?.Stop();
        var ready = _waitingForDialog.ToArray();
        _waitingForDialog.Clear();
        foreach (var item in ready)
        {
            Post(item);
        }
    }

    private void OnEnterModal(object? sender, EventArgs e) => Interlocked.Increment(ref _modalEvents);

    private void OnLeaveModal(object? sender, EventArgs e)
    {
        if (Interlocked.Decrement(ref _modalEvents) < 0)
        {
            Interlocked.Exchange(ref _modalEvents, 0);
        }

        if (IsMainThread && _waitingForDialog.Count > 0 && !_closed)
        {
            try
            {
                // Re-check once the dialog's own message loop has unwound.
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RecheckWaitingItems));
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Could not schedule the re-check after a dialog closed", ex);
            }
        }
    }

    private void RefreshMainWindow()
    {
        if (_mainWindow != IntPtr.Zero || !IsMainThread)
        {
            return;
        }

        try
        {
            _mainWindow = AcadApp.MainWindow?.Handle ?? IntPtr.Zero;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("AutoCAD's main window handle is not available yet", ex);
        }
    }

    private void WarnOnceIfNotApplicationContext()
    {
        if (_contextWarningLogged)
        {
            return;
        }

        try
        {
            if (!AcadApp.DocumentManager.IsApplicationContext)
            {
                _contextWarningLogged = true;
                CopilotLog.Warn("A copilot request started outside AutoCAD's application context (called from inside a command).");
            }
        }
        catch (Exception ex)
        {
            _contextWarningLogged = true;
            CopilotLog.Warn("Could not read DocumentCollection.IsApplicationContext", ex);
        }
    }

    private string BusyMessage() =>
        IsModal
            ? "AutoCAD is busy: a dialog box is open in AutoCAD. Ask the user to close it, then try again."
            : $"AutoCAD did not respond within {StartTimeout.TotalSeconds:0} s (it may be running a long operation). Try again in a moment; if it keeps happening, ask the user to check AutoCAD.";

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private abstract class WorkItem
    {
        private const int Pending = 0;
        private const int Started = 1;
        private const int Abandoned = 2;
        private int _state;

        public bool IsAbandoned => Volatile.Read(ref _state) == Abandoned;

        public bool TryBegin() => Interlocked.CompareExchange(ref _state, Started, Pending) == Pending;

        public bool TryFail(Exception error)
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Pending)
            {
                return false;
            }

            SetException(error);
            return true;
        }

        public bool TryCancel(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Pending)
            {
                return false;
            }

            SetCanceled(token);
            return true;
        }

        public abstract void Run();

        protected abstract void SetException(Exception error);

        protected abstract void SetCanceled(CancellationToken token);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<Task<T>> _body;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private Timer? _timer;

        public WorkItem(Func<Task<T>> body, CancellationToken cancellationToken)
        {
            _body = body;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public void Arm(TimeSpan startTimeout, Func<string> busyMessage)
        {
            if (_cancellationToken.CanBeCanceled)
            {
                _registration = _cancellationToken.Register(() => TryCancel(_cancellationToken));
            }

            _timer = new Timer(_ => TryFail(new AcadBusyException(busyMessage())), null, startTimeout, Timeout.InfiniteTimeSpan);
        }

        public override void Run()
        {
            _timer?.Dispose();
            _registration.Dispose();
            Task<T> task;
            try
            {
                task = _body();
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
                return;
            }

            if (task.IsCompleted)
            {
                CompleteFrom(task);
            }
            else
            {
                task.ContinueWith(CompleteFrom, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        protected override void SetException(Exception error)
        {
            _timer?.Dispose();
            _registration.Dispose();
            _completion.TrySetException(error);
        }

        protected override void SetCanceled(CancellationToken token)
        {
            _timer?.Dispose();
            _completion.TrySetCanceled(token);
        }

        private void CompleteFrom(Task<T> task)
        {
            if (task.IsCanceled)
            {
                _completion.TrySetCanceled(_cancellationToken.IsCancellationRequested ? _cancellationToken : new CancellationToken(true));
            }
            else if (task.IsFaulted)
            {
                var error = task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException! : task.Exception;
                _completion.TrySetException(error);
            }
            else
            {
                _completion.TrySetResult(task.Result);
            }
        }
    }
}
