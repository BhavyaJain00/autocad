using System.IO;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Acad;

/// <summary>
/// Thrown when a chat's tool call reaches AutoCAD while another drawing than the chat's own is active (the user or
/// another chat switched drawings in between). The pipe handler then retries the call in the AutoCAD turn queue, which
/// activates the chat's drawing first.
/// </summary>
public sealed class DrawingNotActiveException : ToolException
{
    /// <summary>Creates the exception.</summary>
    public DrawingNotActiveException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Ids for open drawings, so each chat tab can stay bound to "its" drawing: the id is kept in the document's
/// <see cref="Document.UserData"/> (it lives and dies with the document), the title is the file name. Also switches
/// AutoCAD to a chat's drawing (<see cref="DocumentCollection.MdiActiveDocument"/>) when its turn comes. Main thread only.
/// </summary>
public static class DrawingDirectory
{
    private const string IdKey = "AcadCopilot.DrawingId";
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(3);
    private static int _nextId;

    /// <summary>The id of <paramref name="document"/> (assigned on first use).</summary>
    public static string IdOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var data = document.UserData;
        if (data[IdKey] is string id)
        {
            return id;
        }

        id = "dwg-" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        data[IdKey] = id;
        return id;
    }

    /// <summary><paramref name="document"/> as a <see cref="DrawingRef"/>.</summary>
    public static DrawingRef Describe(Document document) => new(IdOf(document), Title(document));

    /// <summary>The active drawing, or null when none is open.</summary>
    public static DrawingRef? Active()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        return document is null ? null : Describe(document);
    }

    /// <summary>Every open drawing.</summary>
    public static IReadOnlyList<DrawingRef> All()
    {
        var list = new List<DrawingRef>();
        foreach (Document document in AcadApp.DocumentManager)
        {
            list.Add(Describe(document));
        }

        return list;
    }

    /// <summary>The open document with <paramref name="id"/>, or null when it was closed.</summary>
    public static Document? Find(string id)
    {
        foreach (Document document in AcadApp.DocumentManager)
        {
            if (document.UserData[IdKey] is string own && string.Equals(own, id, StringComparison.Ordinal))
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>True when the drawing <paramref name="id"/> is the active one.</summary>
    public static bool IsActive(string id) =>
        AcadApp.DocumentManager.MdiActiveDocument is { } active && string.Equals(IdOf(active), id, StringComparison.Ordinal);

    /// <summary>
    /// Makes <paramref name="drawing"/> the active drawing (AutoCAD has one command line, which types into the active
    /// drawing). Returns a sentence for the tool result when it switched, null when it was active already. Run inside an
    /// asynchronous main-thread item (it waits for AutoCAD to finish the switch).
    /// </summary>
    /// <exception cref="ToolException">The drawing was closed, or AutoCAD does not allow switching drawings now.</exception>
    /// <exception cref="AcadBusyException">AutoCAD did not switch in time.</exception>
    public static async Task<string?> ActivateAsync(AcadDispatcher dispatcher, DrawingRef drawing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(drawing);
        var document = Find(drawing.Id)
            ?? throw new ToolException(
                $"The drawing '{drawing.Title}' that this chat works on was closed, so nothing was done. Tell the user; they can reopen it and start a new chat tab on it.");
        var documents = AcadApp.DocumentManager;
        if (documents.MdiActiveDocument is { } active && string.Equals(IdOf(active), drawing.Id, StringComparison.Ordinal))
        {
            return null;
        }

        if (!documents.DocumentActivationEnabled)
        {
            throw new ToolException(
                $"AutoCAD does not allow switching drawings right now, so the copilot could not go to '{drawing.Title}' (this chat's drawing). Ask the user to switch to it, then try again.");
        }

        var previous = documents.MdiActiveDocument is { } before ? Title(before) : null;
        documents.MdiActiveDocument = document;
        var deadline = DateTime.UtcNow + ActivationTimeout;
        while (!IsActive(drawing.Id))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AcadBusyException(
                    $"AutoCAD did not switch to '{drawing.Title}' (this chat's drawing) in time. Ask the user to switch to it, then try again.");
            }

            try
            {
                await dispatcher.NextIdleAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                // Check again.
            }
        }

        CopilotLog.Info($"Switched AutoCAD to '{drawing.Title}' for a chat (was '{previous ?? "none"}').");
        return previous is null
            ? $"AutoCAD switched to '{drawing.Title}', this chat's drawing."
            : $"AutoCAD switched from '{previous}' to '{drawing.Title}', this chat's drawing.";
    }

    private static string Title(Document document)
    {
        try
        {
            return Path.GetFileName(document.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return document.Name;
        }
    }
}
