using System.Globalization;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using RibbonAutoHideMode = Autodesk.Internal.Windows.RibbonAutoHideMode;

namespace AcadCopilot.Plugin.Palette;

/// <summary>
/// <see cref="ILayoutSurface"/> inside AutoCAD 2027 (API names checked by reflection over AdWindows.dll / acmgd.dll):
/// the ribbon is <c>Autodesk.Windows.ComponentManager.Ribbon</c> and its minimise state is
/// <c>RibbonControl.AutoHideMode</c> (<c>Autodesk.Internal.Windows.RibbonAutoHideMode</c>: None, HidePanelBar = tab names,
/// HidePanel = panel titles, CollapsePanels = panel buttons), which is what the ribbon's own minimise button cycles and is
/// fully reversible (RIBBONCLOSE would remove the ribbon instead). NAVBARDISPLAY is saved in the registry; NAVVCUBEDISPLAY
/// is saved in the drawing, so it is set under a document lock and the drawing's DBMOD is put back to 0 afterwards when
/// it had no unsaved changes (ObjectARX <c>acdbSetDbmod</c>, exported by acdb26.dll). System-variable notices are muted
/// (SYSMON 0) while focus mode sets them. Main thread only; never throws.
/// </summary>
internal sealed class AcadLayoutSurface : ILayoutSurface
{
    private const string ViewCubeVariable = "NAVVCUBEDISPLAY";

    public int? RibbonMode
    {
        get
        {
            try
            {
                return ComponentManager.Ribbon is { CanRibbonAutoHide: true } ribbon ? (int)ribbon.AutoHideMode : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or COMException)
            {
                CopilotLog.Warn("Could not read the ribbon state", ex);
                return null;
            }
        }
    }

    public string? ActiveDrawingId
    {
        get
        {
            try
            {
                return AcadApp.DocumentManager.MdiActiveDocument is { } document ? DrawingDirectory.IdOf(document) : null;
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }

    public int? ActiveViewCube => AcadApp.DocumentManager.MdiActiveDocument is null ? null : GetSystemVariable(ViewCubeVariable);

    public bool TrySetRibbonMode(int mode)
    {
        try
        {
            if (ComponentManager.Ribbon is not { } ribbon || !Enum.IsDefined(typeof(RibbonAutoHideMode), mode))
            {
                return false;
            }

            ribbon.AutoHideMode = (RibbonAutoHideMode)mode;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or COMException)
        {
            CopilotLog.Warn($"Could not set the ribbon state to {mode}", ex);
            return false;
        }
    }

    public int? GetSystemVariable(string name)
    {
        try
        {
            return Convert.ToInt32(AcadApp.GetSystemVariable(name), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidCastException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool TrySetSystemVariable(string name, int value) => Muted(() => SetVariable(name, value));

    public bool TrySetActiveViewCube(int value)
    {
        if (AcadApp.DocumentManager.MdiActiveDocument is not { } document)
        {
            return false;
        }

        try
        {
            var clean = GetSystemVariable("DBMOD") == 0;
            using (AcadDispatcher.LockDocument(document))
            {
                if (!Muted(() => SetVariable(ViewCubeVariable, value)))
                {
                    return false;
                }

                if (clean && GetSystemVariable("DBMOD") != 0)
                {
                    KeepUnmodified(document.Database);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is AcadBusyException or Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn($"Could not set {ViewCubeVariable} to {value}", ex);
            return false;
        }
    }

    private static bool SetVariable(string name, int value)
    {
        try
        {
            AcadApp.SetSystemVariable(name, value);
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException)
        {
            CopilotLog.Warn($"Focus mode: could not set {name} to {value}", ex);
            return false;
        }
    }

    // SYSMON 0 while focus mode sets variables: no "**** System Variable Changed ****" notice in the command line.
    private bool Muted(Func<bool> body)
    {
        var sysmon = GetSystemVariable("SYSMON");
        var muted = sysmon is { } on && on != 0 && SetVariable("SYSMON", 0);
        try
        {
            return body();
        }
        finally
        {
            if (muted)
            {
                SetVariable("SYSMON", sysmon!.Value);
            }
        }
    }

    // Focus mode's ViewCube change is not an edit of the user's drawing: no "Save changes?" for it.
    private static void KeepUnmodified(Database database)
    {
        try
        {
            _ = AcdbSetDbmod(database.UnmanagedObject, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            CopilotLog.Warn("Could not keep the drawing unmodified after hiding the ViewCube", ex);
        }
    }

    [DllImport("acdb26.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?acdbSetDbmod@@YAHPEAVAcDbDatabase@@H@Z")]
    private static extern int AcdbSetDbmod(IntPtr database, int value);
}

/// <summary>
/// Connects <see cref="FocusLayoutController"/> to AutoCAD's events: a drawing that becomes active gets its ViewCube
/// hidden while focus mode is on (on the next idle, when the switch is complete), a drawing being saved keeps the user's
/// ViewCube setting in its file, a closed drawing is forgotten. Main thread only; never throws into AutoCAD.
/// </summary>
internal sealed class AcadFocusLayout : IDisposable
{
    private readonly HashSet<IntPtr> _databases = [];
    private bool _idleQueued;
    private bool _disposed;

    public AcadFocusLayout(FocusLayoutStore? store = null)
    {
        Controller = new FocusLayoutController(new AcadLayoutSurface(), store);
        try
        {
            var documents = AcadApp.DocumentManager;
            documents.DocumentBecameCurrent += OnDocumentBecameCurrent;
            documents.DocumentCreated += OnDocumentCreated;
            documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            foreach (Document document in documents)
            {
                Watch(document);
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Focus mode cannot follow the open drawings", ex);
        }
    }

    public FocusLayoutController Controller { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Controller.SetFocusMode(false);
            var documents = AcadApp.DocumentManager;
            documents.DocumentBecameCurrent -= OnDocumentBecameCurrent;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document document in documents)
            {
                Unwatch(document.Database);
            }

            if (_idleQueued)
            {
                AcadApp.Idle -= OnIdle;
                _idleQueued = false;
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Focus mode: releasing the drawing events failed", ex);
        }
    }

    private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        if (e.Document is { } document)
        {
            Watch(document);
        }

        QueueActivation();
    }

    private void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e) => QueueActivation();

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document is { } document)
            {
                Controller.OnDrawingClosed(DrawingDirectory.IdOf(document));
                Unwatch(document.Database);
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Focus mode: could not forget a closing drawing", ex);
        }
    }

    // The ViewCube of a drawing that just became active is set on the next idle (inside the switch it is not reliable).
    private void QueueActivation()
    {
        if (_idleQueued || _disposed)
        {
            return;
        }

        _idleQueued = true;
        AcadApp.Idle += OnIdle;
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        AcadApp.Idle -= OnIdle;
        _idleQueued = false;
        try
        {
            Controller.OnDrawingActivated();
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Focus mode: could not update the active drawing", ex);
        }
    }

    private void Watch(Document document)
    {
        var database = document.Database;
        if (database is null || !_databases.Add(database.UnmanagedObject))
        {
            return;
        }

        database.BeginSave += OnBeginSave;
        database.SaveComplete += OnSaveEnded;
        database.AbortSave += OnSaveEnded;
    }

    private void Unwatch(Database? database)
    {
        if (database is null || !_databases.Remove(database.UnmanagedObject))
        {
            return;
        }

        database.BeginSave -= OnBeginSave;
        database.SaveComplete -= OnSaveEnded;
        database.AbortSave -= OnSaveEnded;
    }

    private void OnBeginSave(object? sender, DatabaseIOEventArgs e) => ForDrawingOf(sender, Controller.OnDrawingSaving);

    private void OnSaveEnded(object? sender, EventArgs e) => ForDrawingOf(sender, Controller.OnDrawingSaved);

    private static void ForDrawingOf(object? sender, Action<string> action)
    {
        try
        {
            if (sender is Database database && AcadApp.DocumentManager.GetDocument(database) is { } document)
            {
                action(DrawingDirectory.IdOf(document));
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Focus mode: could not handle a drawing save", ex);
        }
    }
}
