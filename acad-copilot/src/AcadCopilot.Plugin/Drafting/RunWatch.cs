using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>Kind of input AutoCAD is asking for.</summary>
internal enum PromptKind
{
    /// <summary>A point (getpoint).</summary>
    Point,

    /// <summary>An angle.</summary>
    Angle,

    /// <summary>A distance.</summary>
    Distance,

    /// <summary>A corner (window or rectangle).</summary>
    Corner,

    /// <summary>A string (names, text).</summary>
    String,

    /// <summary>An option keyword.</summary>
    Keyword,

    /// <summary>An integer.</summary>
    Integer,

    /// <summary>A real number.</summary>
    Double,

    /// <summary>One object.</summary>
    Entity,

    /// <summary>One nested object.</summary>
    NestedEntity,

    /// <summary>A selection set.</summary>
    Selection,
}

/// <summary>One prompt AutoCAD started (from the Editor.PromptingForXxx events).</summary>
/// <param name="Kind">What is asked for.</param>
/// <param name="Message">The prompt text as the options carry it.</param>
/// <param name="AllowSpaces">For string prompts: spaces are part of the answer.</param>
/// <param name="Sequence">Value of <see cref="RunWatch.Sequence"/> when it started.</param>
internal sealed record PromptEvent(PromptKind Kind, string Message, bool AllowSpaces, int Sequence);

/// <summary>Objects appended, modified and erased while one step ran.</summary>
internal sealed class StepChanges
{
    /// <summary>Objects appended to the drawing (new objects), in order.</summary>
    public List<ObjectId> Appended { get; } = [];

    /// <summary>Objects modified.</summary>
    public HashSet<ObjectId> Modified { get; } = [];

    /// <summary>Objects erased (or un-appended by an undo inside a command).</summary>
    public List<ObjectId> Erased { get; } = [];
}

/// <summary>
/// Everything the drafting engine observes while a run_commands call types into one drawing: prompt starts
/// (Editor.PromptingForXxx), quiescence, command start/end/cancel/failure/unknown, AutoLISP starts, modal dialogs
/// (Application.EnterModal/LeaveModal), and the objects appended/modified/erased per step. It also vetoes, through
/// DocumentCollection.DocumentLockModeChanged, any command AutoCAD is about to run that the security policy forbids
/// (<see cref="CommandPolicy.ClassifyRunningCommand"/>) or that needs approval the user has not given in this call: the
/// backstop if typed input ever reached the Command prompt. Create and dispose on AutoCAD's main thread; every handler
/// runs there too and never throws.
/// </summary>
internal sealed class RunWatch : IDisposable
{
    private readonly Document _document;
    private readonly Editor _editor;
    private readonly Database _database;
    private readonly DocumentCollection _documents;
    private readonly CommandAliases _aliases;
    private readonly IReadOnlySet<string> _approved;
    private readonly Dictionary<int, StepChanges> _steps = [];
    private readonly HashSet<ObjectId> _appendedInRun = [];
    private readonly List<string> _lispStarts = [];
    private bool _disposed;
    private bool _keyWatch;
    private bool _expectingCancel;
    private long _ownEscapeEnded;
    private int _commandsAtOwnEscapeEnd;
    private long _userEscapePressed;

    /// <summary>Subscribes to the events of <paramref name="document"/>. Main thread.</summary>
    /// <param name="document">The drawing the call types into.</param>
    /// <param name="aliases">Command aliases used to judge running commands.</param>
    /// <param name="approved">Canonical names of the approval commands the user approved for this call.</param>
    public RunWatch(Document document, CommandAliases aliases, IReadOnlySet<string> approved)
    {
        _document = document;
        _editor = document.Editor;
        _database = document.Database;
        _documents = AcadApp.DocumentManager;
        _aliases = aliases;
        _approved = approved;
        try
        {
            Subscribe();
        }
        catch
        {
            // Never leave half the handlers attached: detach what was attached, then report the failure.
            Dispose();
            throw;
        }
    }

    private void Subscribe()
    {
        _editor.PromptingForPoint += OnPoint;
        _editor.PromptingForAngle += OnAngle;
        _editor.PromptingForDistance += OnDistance;
        _editor.PromptingForCorner += OnCorner;
        _editor.PromptingForString += OnString;
        _editor.PromptingForKeyword += OnKeyword;
        _editor.PromptingForInteger += OnInteger;
        _editor.PromptingForDouble += OnDouble;
        _editor.PromptingForEntity += OnEntity;
        _editor.PromptingForNestedEntity += OnNestedEntity;
        _editor.PromptingForSelection += OnSelection;
        _editor.EnteringQuiescentState += OnEnteringQuiescent;
        _editor.LeavingQuiescentState += OnLeavingQuiescent;
        _document.CommandWillStart += OnCommandWillStart;
        _document.CommandEnded += OnCommandEnded;
        _document.CommandCancelled += OnCommandCancelled;
        _document.CommandFailed += OnCommandFailed;
        _document.UnknownCommand += OnUnknownCommand;
        _document.LispWillStart += OnLispWillStart;
        _documents.DocumentLockModeChanged += OnLockModeChanged;
        AcadApp.EnterModal += OnEnterModal;
        AcadApp.LeaveModal += OnLeaveModal;
        _database.ObjectAppended += OnAppended;
        _database.ObjectModified += OnModified;
        _database.ObjectErased += OnErased;
        _database.ObjectUnappended += OnUnappended;
        _database.ObjectReappended += OnReappended;
        try
        {
            SubscribeKeys();
            _keyWatch = true;
        }
        catch (Exception ex)
        {
            // Older or changed API: the key state at the moment of the cancel is the remaining evidence.
            CopilotLog.Warn("Drafting: Application.PreTranslateMessage is not available; the user's Esc is recognised from the key state only", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SubscribeKeys() => AcadApp.PreTranslateMessage += OnPreTranslateMessage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UnsubscribeKeys() => AcadApp.PreTranslateMessage -= OnPreTranslateMessage;

    /// <summary>Increases on every prompt start, command start/end/cancel/failure, quiescence change and modal change.</summary>
    public int Sequence { get; private set; }

    /// <summary>The last prompt AutoCAD started (null before the first one).</summary>
    public PromptEvent? LastPrompt { get; private set; }

    /// <summary>The step whose changes are being recorded (1-based; 0 = before the first step).</summary>
    public int CurrentStep { get; set; }

    /// <summary>Open modal dialogs according to EnterModal/LeaveModal.</summary>
    public int ModalDepth { get; private set; }

    /// <summary>True while the copilot itself pressed Esc (a cancel then is not the user's).</summary>
    public bool ExpectingCancel
    {
        get => _expectingCancel;
        set
        {
            if (_expectingCancel && !value)
            {
                // A late cancel notification of the copilot's own Esc may still arrive just after this.
                _ownEscapeEnded = Stopwatch.GetTimestamp();
                _commandsAtOwnEscapeEnd = CommandsStarted;
            }

            _expectingCancel = value;
        }
    }

    /// <summary>
    /// The command the user cancelled with Esc while the call ran, or null. Set only with evidence of a real Esc key
    /// press by the user (a key-down seen in AutoCAD's message loop shortly before, or the key held down at the moment of
    /// the cancel); the copilot's own Esc (typed or posted) never counts.
    /// </summary>
    public string? UserCancelledCommand { get; private set; }

    /// <summary>
    /// A command that AutoCAD cancelled while neither the copilot nor (as far as the key evidence shows) the user pressed
    /// Esc, or null. The engine stops the call and says so, without blaming the user.
    /// </summary>
    public string? UnexplainedCancel { get; private set; }

    /// <summary>The unknown command AutoCAD reported since the last reset, or null.</summary>
    public string? UnknownCommand { get; set; }

    /// <summary>Number of commands that started.</summary>
    public int CommandsStarted { get; private set; }

    /// <summary>Name of the last command that started.</summary>
    public string? LastStartedCommand { get; private set; }

    /// <summary>True while AutoCAD is idle at the Command prompt.</summary>
    public bool Quiescent { get; private set; }

    /// <summary>What the guard vetoed ("SHELL: runs another program..."), or null.</summary>
    public string? Vetoed { get; private set; }

    /// <summary>First lines of AutoLISP evaluations that started during the call.</summary>
    public IReadOnlyList<string> LispStarts => _lispStarts;

    /// <summary>Every object appended during the call.</summary>
    public IReadOnlyCollection<ObjectId> AppendedInRun => _appendedInRun;

    /// <summary>The changes recorded for <paramref name="step"/> (empty when none).</summary>
    public StepChanges Changes(int step) => _steps.TryGetValue(step, out var changes) ? changes : new StepChanges();

    /// <summary>Unsubscribes everything. Main thread.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Try(() =>
        {
            _editor.PromptingForPoint -= OnPoint;
            _editor.PromptingForAngle -= OnAngle;
            _editor.PromptingForDistance -= OnDistance;
            _editor.PromptingForCorner -= OnCorner;
            _editor.PromptingForString -= OnString;
            _editor.PromptingForKeyword -= OnKeyword;
            _editor.PromptingForInteger -= OnInteger;
            _editor.PromptingForDouble -= OnDouble;
            _editor.PromptingForEntity -= OnEntity;
            _editor.PromptingForNestedEntity -= OnNestedEntity;
            _editor.PromptingForSelection -= OnSelection;
            _editor.EnteringQuiescentState -= OnEnteringQuiescent;
            _editor.LeavingQuiescentState -= OnLeavingQuiescent;
        });
        Try(() =>
        {
            _document.CommandWillStart -= OnCommandWillStart;
            _document.CommandEnded -= OnCommandEnded;
            _document.CommandCancelled -= OnCommandCancelled;
            _document.CommandFailed -= OnCommandFailed;
            _document.UnknownCommand -= OnUnknownCommand;
            _document.LispWillStart -= OnLispWillStart;
        });
        Try(() => _documents.DocumentLockModeChanged -= OnLockModeChanged);
        if (_keyWatch)
        {
            Try(UnsubscribeKeys);
        }

        Try(() =>
        {
            AcadApp.EnterModal -= OnEnterModal;
            AcadApp.LeaveModal -= OnLeaveModal;
        });
        Try(() =>
        {
            _database.ObjectAppended -= OnAppended;
            _database.ObjectModified -= OnModified;
            _database.ObjectErased -= OnErased;
            _database.ObjectUnappended -= OnUnappended;
            _database.ObjectReappended -= OnReappended;
        });
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Drafting: detaching an AutoCAD event handler failed", ex);
        }
    }

    private void Prompt(PromptKind kind, string? message, bool allowSpaces = false)
    {
        Sequence++;
        LastPrompt = new PromptEvent(kind, CommandLineText.Clean(message), allowSpaces, Sequence);
    }

    private void OnPoint(object? sender, PromptPointOptionsEventArgs e) => Prompt(PromptKind.Point, SafeMessage(() => e.Options?.Message));

    private void OnAngle(object? sender, PromptAngleOptionsEventArgs e) => Prompt(PromptKind.Angle, SafeMessage(() => e.Options?.Message));

    private void OnDistance(object? sender, PromptDistanceOptionsEventArgs e) => Prompt(PromptKind.Distance, SafeMessage(() => e.Options?.Message));

    private void OnCorner(object? sender, PromptPointOptionsEventArgs e) => Prompt(PromptKind.Corner, SafeMessage(() => e.Options?.Message));

    private void OnString(object? sender, PromptStringOptionsEventArgs e)
    {
        var allowSpaces = false;
        try
        {
            allowSpaces = e.Options?.AllowSpaces ?? false;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            // Unknown: treat as no spaces.
        }

        Prompt(PromptKind.String, SafeMessage(() => e.Options?.Message), allowSpaces);
    }

    private void OnKeyword(object? sender, PromptKeywordOptionsEventArgs e) => Prompt(PromptKind.Keyword, SafeMessage(() => e.Options?.Message));

    private void OnInteger(object? sender, PromptIntegerOptionsEventArgs e) => Prompt(PromptKind.Integer, SafeMessage(() => e.Options?.Message));

    private void OnDouble(object? sender, PromptDoubleOptionsEventArgs e) => Prompt(PromptKind.Double, SafeMessage(() => e.Options?.Message));

    private void OnEntity(object? sender, PromptEntityOptionsEventArgs e) => Prompt(PromptKind.Entity, SafeMessage(() => e.Options?.Message));

    private void OnNestedEntity(object? sender, PromptNestedEntityOptionsEventArgs e) => Prompt(PromptKind.NestedEntity, SafeMessage(() => e.Options?.Message));

    private void OnSelection(object? sender, PromptSelectionOptionsEventArgs e) => Prompt(PromptKind.Selection, SafeMessage(() => e.Options?.MessageForAdding) ?? "Select objects:");

    private static string? SafeMessage(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    private void OnEnteringQuiescent(object? sender, EventArgs e)
    {
        Quiescent = true;
        Sequence++;
    }

    private void OnLeavingQuiescent(object? sender, EventArgs e)
    {
        Quiescent = false;
        Sequence++;
    }

    private void OnCommandWillStart(object? sender, CommandEventArgs e)
    {
        CommandsStarted++;
        LastStartedCommand = SafeMessage(() => e.GlobalCommandName);
        Sequence++;
    }

    private void OnCommandEnded(object? sender, CommandEventArgs e) => Sequence++;

    private void OnCommandFailed(object? sender, CommandEventArgs e) => Sequence++;

    private void OnCommandCancelled(object? sender, CommandEventArgs e)
    {
        Sequence++;
        if (ExpectingCancel)
        {
            return;
        }

        var name = SafeMessage(() => e.GlobalCommandName) ?? "the command";
        if (_ownEscapeEnded != 0 && CommandsStarted == _commandsAtOwnEscapeEnd && Stopwatch.GetElapsedTime(_ownEscapeEnded) < OwnEscapeTail)
        {
            // The late notification of the copilot's own Esc (no command started since).
            return;
        }

        if (UserPressedEscape())
        {
            UserCancelledCommand ??= name;
        }
        else
        {
            UnexplainedCancel ??= name;
            CopilotLog.Warn($"Drafting: AutoCAD cancelled {name} without an Esc from the copilot or a key press from the user.");
        }
    }

    // A key press by the user: an Esc key-down that AutoCAD's message loop saw recently and the copilot did not post
    // itself, or the Esc key held down right now (the cancel runs while the key-down is processed).
    private bool UserPressedEscape()
    {
        if (_userEscapePressed != 0 && Stopwatch.GetElapsedTime(_userEscapePressed) < UserEscapeWindow)
        {
            return true;
        }

        try
        {
            return (GetKeyState(VkEscape) & 0x8000) != 0 || (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void OnPreTranslateMessage(object? sender, PreTranslateMessageEventArgs e)
    {
        try
        {
            var message = e.Message;
            if (message.message is WmKeyDown or WmSysKeyDown && (int)message.wParam == VkEscape)
            {
                var posted = DialogCloser.LastPressTimestamp;
                if (posted == 0 || Stopwatch.GetElapsedTime(posted) > OwnEscapeTail)
                {
                    _userEscapePressed = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Never disturb AutoCAD's message loop.
        }
    }

    private void OnUnknownCommand(object? sender, UnknownCommandEventArgs e)
    {
        UnknownCommand = SafeMessage(() => e.GlobalCommandName) ?? "?";
        Sequence++;
    }

    private void OnLispWillStart(object? sender, LispWillStartEventArgs e)
    {
        var line = SafeMessage(() => e.FirstLine) ?? string.Empty;
        if (_lispStarts.Count < 5)
        {
            _lispStarts.Add(line.Length > 120 ? line[..120] + "..." : line);
        }

        Sequence++;
    }

    private void OnEnterModal(object? sender, EventArgs e)
    {
        ModalDepth++;
        Sequence++;
    }

    private void OnLeaveModal(object? sender, EventArgs e)
    {
        ModalDepth = Math.Max(0, ModalDepth - 1);
        Sequence++;
    }

    private void OnLockModeChanged(object? sender, DocumentLockModeChangedEventArgs e)
    {
        try
        {
            if (e.Document is null || !e.Document.Equals(_document) || e.CurrentMode is DocumentLockMode.None or DocumentLockMode.NotLocked)
            {
                return;
            }

            var name = e.GlobalCommandName;
            var verdict = CommandPolicy.ClassifyRunningCommand(name, _aliases);
            if (verdict.IsAllowed)
            {
                return;
            }

            var canonical = CommandPolicy.Parse(CommandPolicy.NormalizeName(name?.TrimStart('#')), _aliases).Canonical;
            if (verdict.NeedsApproval && _approved.Contains(canonical))
            {
                return;
            }

            e.Veto();
            Vetoed ??= verdict.IsBlocked ? verdict.Describe() : $"{verdict.Subject}: needs the user's approval, which this call did not ask for";
            Sequence++;
            CopilotLog.Warn($"Drafting: vetoed {name} during run_commands ({verdict.Describe()}).");
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Drafting: the command guard failed", ex);
        }
    }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkEscape = 0x1B;
    private static readonly TimeSpan OwnEscapeTail = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan UserEscapeWindow = TimeSpan.FromSeconds(3);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private StepChanges Current()
    {
        if (!_steps.TryGetValue(CurrentStep, out var changes))
        {
            changes = new StepChanges();
            _steps[CurrentStep] = changes;
        }

        return changes;
    }

    private void OnAppended(object? sender, ObjectEventArgs e)
    {
        if (TryId(e.DBObject) is { } id)
        {
            Current().Appended.Add(id);
            _appendedInRun.Add(id);
        }
    }

    private void OnModified(object? sender, ObjectEventArgs e)
    {
        if (TryId(e.DBObject) is { } id)
        {
            Current().Modified.Add(id);
        }
    }

    private void OnErased(object? sender, ObjectErasedEventArgs e)
    {
        if (TryId(e.DBObject) is { } id)
        {
            if (e.Erased)
            {
                Current().Erased.Add(id);
            }
            else
            {
                Current().Modified.Add(id);
            }
        }
    }

    private void OnUnappended(object? sender, ObjectEventArgs e)
    {
        if (TryId(e.DBObject) is { } id)
        {
            Current().Erased.Add(id);
        }
    }

    private void OnReappended(object? sender, ObjectEventArgs e)
    {
        if (TryId(e.DBObject) is { } id)
        {
            Current().Appended.Add(id);
            _appendedInRun.Add(id);
        }
    }

    private static ObjectId? TryId(DBObject? dbObject)
    {
        try
        {
            return dbObject is null ? null : dbObject.ObjectId;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
