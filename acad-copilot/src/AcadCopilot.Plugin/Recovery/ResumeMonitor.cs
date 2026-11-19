using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Recovery;

/// <summary>An unfinished job offered for the drawing that just became active (or none).</summary>
public sealed class ResumeOfferEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    public ResumeOfferEventArgs(string? drawingId, ResumeInfo? offer)
    {
        DrawingId = drawingId;
        Offer = offer;
    }

    /// <summary>The <see cref="DrawingDirectory"/> id of the drawing the offer is about.</summary>
    public string? DrawingId { get; }

    /// <summary>The newest unfinished job of that drawing from an AutoCAD session that has ended; null = nothing to offer.</summary>
    public ResumeInfo? Offer { get; }
}

/// <summary>
/// Crash recovery at start-up: when a drawing is opened (and for the drawing open when the plug-in starts), looks for an
/// unfinished job journal of that drawing written by an AutoCAD session that has ended, and exposes it as
/// <see cref="CurrentOffer"/> / <see cref="OfferChanged"/> so the panel can show a "Resume previous job" card. Also
/// deletes journals and checkpoint copies older than 7 days once. Main thread; never throws into AutoCAD.
/// </summary>
public static class ResumeMonitor
{
    private static bool _started;

    /// <summary>Raised on AutoCAD's main thread when the offer changes (a drawing with an unfinished job became active, or it was dismissed).</summary>
    public static event EventHandler<ResumeOfferEventArgs>? OfferChanged;

    /// <summary>The current offer (null when there is none).</summary>
    public static ResumeInfo? CurrentOffer { get; private set; }

    /// <summary>The drawing id the current offer is about.</summary>
    public static string? CurrentDrawingId { get; private set; }

    /// <summary>
    /// Starts watching opened drawings and checks the active one (call once from the plug-in's start-up on the main
    /// thread; calling again does nothing).
    /// </summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            _started = true;
            RecoveryJournal.CleanOnce();
            var documents = AcadApp.DocumentManager;
            documents.DocumentCreated += OnDocumentCreated;
            if (documents.MdiActiveDocument is { } active)
            {
                Check(active);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Resume monitor: start failed", ex);
        }
    }

    /// <summary>Stops watching (plug-in shutdown).</summary>
    public static void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        try
        {
            AcadApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Resume monitor: stop failed", ex);
        }
    }

    /// <summary>
    /// Looks for an unfinished job of <paramref name="document"/>'s drawing left by an ended AutoCAD session and updates
    /// <see cref="CurrentOffer"/>. Returns the offer (null when none). Main thread.
    /// </summary>
    public static ResumeInfo? Check(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var offer = RecoveryJournal.FindUnfinished(document, otherSessionsOnly: true).FirstOrDefault();
        if (offer is not null)
        {
            CopilotLog.Info($"Recovery: unfinished job found for {offer.DrawingTitle}: {offer.Summarize()}");
            Publish(DrawingDirectory.IdOf(document), offer);
        }

        return offer;
    }

    /// <summary>
    /// The user dismissed the card (or resumed the job): the journal is never offered again. Returns false when it was
    /// not found. Any thread.
    /// </summary>
    public static bool Dismiss(string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        bool dismissed;
        try
        {
            dismissed = RecoveryJournal.Store.Dismiss(session);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Resume monitor: dismiss failed", ex);
            return false;
        }

        if (CurrentOffer is { } offer && string.Equals(offer.Session, session, StringComparison.OrdinalIgnoreCase))
        {
            Publish(null, null);
        }

        return dismissed;
    }

    /// <summary>
    /// The message the panel sends to the chat when the user clicks "Resume" on the card: it makes the AI read the
    /// journal and continue.
    /// </summary>
    public static string ResumePrompt(ResumeInfo offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var job = offer.PlanTitle is { Length: > 0 } title ? $"'{title}'" : "the drafting job";
        return $"Resume {job} on {offer.DrawingTitle ?? "this drawing"} where it stopped. Call get_resume_info first, check the drawing, " +
               "then continue with what remains.";
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document is { } document)
            {
                Check(document);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Resume monitor: checking an opened drawing failed", ex);
        }
    }

    private static void Publish(string? drawingId, ResumeInfo? offer)
    {
        CurrentOffer = offer;
        CurrentDrawingId = drawingId;
        try
        {
            OfferChanged?.Invoke(null, new ResumeOfferEventArgs(drawingId, offer));
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Resume monitor: a listener failed", ex);
        }
    }
}
