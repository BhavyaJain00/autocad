using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Everything a tool handler needs for one call: main-thread access to the active drawing (through
/// <see cref="AcadDispatcher"/>), dynamic approval in the panel, unit helpers (drawing units and feet-inches),
/// handle &lt;-&gt; ObjectId conversion and a logger. Created by the pipe handler for each tools.call.
/// <para>
/// Threading: handlers start on a thread-pool thread. Touch AutoCAD only inside <see cref="InvokeAsync{T}(Func{T}, CancellationToken)"/>,
/// <see cref="ReadAsync"/> or <see cref="WriteAsync"/>; the members marked "main thread" throw elsewhere.
/// </para>
/// </summary>
public sealed class ToolContext
{
    private readonly RevisionTracker _revisions;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? _requestApproval;

    /// <summary>Creates the context (the pipe handler does this; tests may too).</summary>
    /// <param name="dispatcher">AutoCAD main-thread dispatcher.</param>
    /// <param name="revisions">Drawing revision counter.</param>
    /// <param name="tool">Definition of the tool being called.</param>
    /// <param name="callId">Correlation id of the call (from the bridge), for logs.</param>
    /// <param name="requestApproval">Asks the panel for approval; null when no panel is attached (approvals are then refused).</param>
    public ToolContext(
        AcadDispatcher dispatcher,
        RevisionTracker revisions,
        ToolDefinition tool,
        string? callId,
        Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? requestApproval)
    {
        Dispatcher = dispatcher;
        _revisions = revisions;
        Tool = tool;
        CallId = string.IsNullOrWhiteSpace(callId) ? "-" : callId;
        _requestApproval = requestApproval;
    }

    /// <summary>The tool being called.</summary>
    public ToolDefinition Tool { get; }

    /// <summary>Correlation id of this call ("-" when the caller gave none).</summary>
    public string CallId { get; }

    /// <summary>AutoCAD main-thread dispatcher.</summary>
    public AcadDispatcher Dispatcher { get; }

    /// <summary>Current drawing revision (any thread).</summary>
    public long Revision => _revisions.Current;

    /// <summary>The active drawing. Main thread only.</summary>
    /// <exception cref="ToolException">No drawing is open.</exception>
    public Document Document
    {
        get
        {
            EnsureMainThread();
            return AcadDispatcher.RequireActiveDocument();
        }
    }

    /// <summary>Database of the active drawing. Main thread only.</summary>
    public Database Database => Document.Database;

    /// <summary>Editor (command line, views) of the active drawing. Main thread only.</summary>
    public Editor Editor => Document.Editor;

    /// <summary>Runs <paramref name="func"/> on AutoCAD's main thread (application context).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken) => Dispatcher.InvokeAsync(func, cancellationToken);

    /// <summary>Runs the asynchronous <paramref name="func"/> on AutoCAD's main thread; its awaits resume there.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func, CancellationToken cancellationToken) => Dispatcher.InvokeAsync(func, cancellationToken);

    /// <summary>Reads the active drawing on the main thread in an open/close transaction (no lock).</summary>
    public Task<T> ReadAsync<T>(Func<Document, Transaction, T> read, CancellationToken cancellationToken) =>
        Dispatcher.ReadAsync(read, cancellationToken);

    /// <summary>Changes the active drawing on the main thread: document lock + one committed transaction.</summary>
    public Task<T> WriteAsync<T>(Func<Document, Transaction, T> write, CancellationToken cancellationToken) =>
        Dispatcher.WriteAsync(write, cancellationToken);

    /// <summary>
    /// Asks the user in the panel to approve something this call is about to do (for example run_commands before a SAVE
    /// or PURGE). True = approved (also when the user chose "Always" earlier in this chat for the same tool and
    /// <paramref name="title"/>, or set the panel to auto-approve). False = rejected, cancelled, or no panel is attached.
    /// Any thread; never call it while blocking the main thread.
    /// </summary>
    /// <param name="title">Short title on the approval card ("Save the drawing (QSAVE)"); also the "Always" key.</param>
    /// <param name="details">What will happen, in plain words.</param>
    /// <param name="cancellationToken">Cancels the prompt (counts as rejected).</param>
    public async Task<bool> ApproveAsync(string title, string details, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (_requestApproval is null)
        {
            Log($"Approval '{title}' refused: no AI Copilot panel is attached.");
            return false;
        }

        try
        {
            var decision = await _requestApproval(
                new ApprovalRequest
                {
                    ToolName = Tool.Name + ":" + title,
                    ToolTitle = title,
                    Summary = details,
                    Risk = ToolRisk.Destructive,
                },
                cancellationToken).ConfigureAwait(false);
            Log($"Approval '{title}': {decision}.");
            return decision is ApprovalDecision.Approve or ApprovalDecision.AlwaysApprove;
        }
        catch (UiUnavailableException ex)
        {
            Log($"Approval '{title}' refused: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            Log($"Approval '{title}' cancelled.");
            return false;
        }
    }

    /// <summary>
    /// Unit settings of <paramref name="database"/> (LUNITS, INSUNITS, LUPREC) as the shared
    /// <see cref="DrawingUnits"/> (AcadCopilot.Core.Drafting: feet-inches text, inches per unit, report JSON). Main thread only.
    /// </summary>
    public static DrawingUnits GetUnits(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new DrawingUnits { LUnits = database.Lunits, InsUnits = (int)database.Insunits, LuPrec = database.Luprec };
    }

    /// <summary>Unit settings of the active drawing. Main thread only.</summary>
    public DrawingUnits GetUnits() => GetUnits(Database);

    /// <summary>Rounds a drawing-unit value for results (6 decimals, no -0).</summary>
    public static double Round(double value) => DrawingUnits.Round(value);

    /// <summary>A length for results: {"du": drawing units, "ftIn": "12'-4 1/2\""} (ftIn omitted when the scale is unknown).</summary>
    public static JsonObject Length(double value, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);
        return units.Length(value);
    }

    /// <summary>A point for results: {"x", "y"[, "z" when not 0], "ftIn": "(12'-0\", 30'-0\")"} (x/y/z in drawing units).</summary>
    public static JsonObject Point(Point3d point, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var result = new JsonObject { ["x"] = Round(point.X), ["y"] = Round(point.Y) };
        if (Math.Abs(point.Z) > 1e-9)
        {
            result["z"] = Round(point.Z);
        }

        if (units.ToFeetInches(point.X) is { } x && units.ToFeetInches(point.Y) is { } y)
        {
            result["ftIn"] = $"({x}, {y})";
        }

        return result;
    }

    /// <summary>Handle of <paramref name="id"/> as upper-case hex text ("2A3F"), the form used in every result.</summary>
    public static string HandleOf(ObjectId id) => id.Handle.Value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Handle value of <paramref name="id"/> (for ToolResult.CreatedIds / ModifiedIds / DeletedIds).</summary>
    public static long HandleValueOf(ObjectId id) => id.Handle.Value;

    /// <summary>Parses a hex handle ("2A3F", "0x2a3f", "#2A3F").</summary>
    public static bool TryParseHandle(string? text, out Handle handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().TrimStart('#');
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (!long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return false;
        }

        handle = new Handle(value);
        return true;
    }

    /// <summary>Finds the object with hex <paramref name="handle"/> in <paramref name="database"/>. Main thread only.</summary>
    public static bool TryGetObjectId(Database database, string? handle, out ObjectId id)
    {
        ArgumentNullException.ThrowIfNull(database);
        id = ObjectId.Null;
        if (!TryParseHandle(handle, out var parsed))
        {
            return false;
        }

        return database.TryGetObjectId(parsed, out id) && !id.IsNull && !id.IsErased;
    }

    /// <summary>The object with hex <paramref name="handle"/>; throws a <see cref="ToolException"/> the AI can act on. Main thread only.</summary>
    public static ObjectId GetObjectId(Database database, string? handle)
    {
        if (!TryParseHandle(handle, out _))
        {
            throw new ToolException($"'{handle}' is not an entity handle. Handles are hex text like \"2A3F\" (from list_entities or run_commands results).");
        }

        return TryGetObjectId(database, handle, out var id)
            ? id
            : throw new ToolException($"No object with handle {handle} exists in this drawing (it may have been erased or undone). Use list_entities to find current handles.");
    }

    /// <summary>Writes an info line to addin.log, tagged with the tool name and call id.</summary>
    public void Log(string message) => CopilotLog.Info($"[{Tool.Name} {CallId}] {message}");

    /// <summary>Writes a warning line to addin.log, tagged with the tool name and call id.</summary>
    public void Warn(string message, Exception? exception = null) => CopilotLog.Warn($"[{Tool.Name} {CallId}] {message}", exception);

    private void EnsureMainThread()
    {
        if (!Dispatcher.IsMainThread)
        {
            throw new InvalidOperationException(
                "AutoCAD objects can only be used on AutoCAD's main thread: wrap the code in ToolContext.InvokeAsync / ReadAsync / WriteAsync.");
        }
    }
}
