using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Learning;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.Learning;
using AcadCopilot.Plugin.Palette;
using AcadCopilot.Plugin.QuickAsk;
using AcadCopilot.Plugin.ViewModels;

namespace BlenderCopilot.Host;

/// <summary>
/// Implements <see cref="IDrawingInteraction"/> for Blender 5.3: live selection chip from Blender's selected objects
/// and 3D Cursor point picking for the 📍 Point button.
/// </summary>
public sealed class BlenderDrawingInteraction : IDrawingInteraction
{
    private readonly BlenderWorkerClient _worker;
    private readonly Dispatcher _dispatcher;
    private SelectionSnapshot? _selection;
    private PickInProgress? _activePick;
    private PickedPoint? _cursorPoint;
    private CancellationTokenSource? _pickCts;

    public BlenderDrawingInteraction(BlenderWorkerClient worker, Dispatcher dispatcher)
    {
        _worker = worker;
        _dispatcher = dispatcher;
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? PickStateChanged;

    public SelectionSnapshot? CurrentSelection => _selection;

    public PickInProgress? ActivePick => _activePick;

    public PickedPoint? LastCursorPoint => _cursorPoint;

    public void UpdateFromPing(JsonObject? selectionJson, JsonObject? cursorJson)
    {
        if (cursorJson is not null)
        {
            var x = cursorJson["x"]?.GetValue<double>() ?? 0;
            var y = cursorJson["y"]?.GetValue<double>() ?? 0;
            var z = cursorJson["z"]?.GetValue<double>() ?? 0;
            var xt = cursorJson["xText"]?.GetValue<string>();
            var yt = cursorJson["yText"]?.GetValue<string>();
            _cursorPoint = new PickedPoint(x, y, z, xt, yt);
        }

        if (selectionJson is null)
        {
            return;
        }

        var count = selectionJson["count"]?.GetValue<int>() ?? 0;
        var title = selectionJson["drawingTitle"]?.GetValue<string>() ?? "Untitled.blend";
        var items = new List<SelectedObject>();
        if (selectionJson["items"] is JsonArray arr)
        {
            foreach (var obj in arr.OfType<JsonObject>())
            {
                items.Add(new SelectedObject(
                    obj["handle"]?.GetValue<string>() ?? "0",
                    obj["type"]?.GetValue<string>() ?? "MESH",
                    obj["layer"]?.GetValue<string>() ?? "0",
                    obj["detail"]?.GetValue<string>() ?? string.Empty));
            }
        }

        var next = new SelectionSnapshot
        {
            Count = count,
            DrawingTitle = title,
            Items = items,
        };
        if (_selection?.Key != next.Key)
        {
            _selection = next;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<PickResult> PickPointsAsync(PickRequest request, CancellationToken cancellationToken)
    {
        var norm = request.Normalized();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pickCts = cts;
        _activePick = new PickInProgress(norm.Prompt + " (using Blender 3D Cursor)", norm.Source, norm.MaxPoints, 0);
        PickStateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var resp = await _worker.SendRawAsync("pick_point", null, cts.Token).ConfigureAwait(true);
            var pts = new List<PickedPoint>();
            if (resp["data"]?["points"] is JsonArray arr)
            {
                foreach (var p in arr.OfType<JsonObject>())
                {
                    pts.Add(new PickedPoint(
                        p["x"]?.GetValue<double>() ?? 0,
                        p["y"]?.GetValue<double>() ?? 0,
                        p["z"]?.GetValue<double>() ?? 0,
                        p["xText"]?.GetValue<string>(),
                        p["yText"]?.GetValue<string>()));
                }
            }

            if (pts.Count == 0 && _cursorPoint is not null)
            {
                pts.Add(_cursorPoint);
            }

            return new PickResult
            {
                Status = pts.Count > 0 ? PickStatus.Picked : PickStatus.Cancelled,
                Points = pts,
                Message = pts.Count > 0 ? "Picked 3D Cursor location from Blender 5.3." : "Cancelled.",
            };
        }
        catch (OperationCanceledException)
        {
            return new PickResult { Status = PickStatus.Cancelled, Message = "Cancelled." };
        }
        catch (Exception ex)
        {
            return PickResult.Fail("Could not read 3D Cursor from Blender 5.3: " + ex.Message);
        }
        finally
        {
            _activePick = null;
            _pickCts = null;
            PickStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CancelPick()
    {
        try
        {
            _pickCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Implements <see cref="IUserActionRecorder"/> ("Learn from me") for Blender 5.3 Desktop.
/// </summary>
public sealed class BlenderActionRecorder : IUserActionRecorder
{
    private readonly BlenderWorkerClient _worker;
    private readonly KnowledgeStore _knowledge;
    private readonly Stopwatch _watch = new();
    private RecorderState _state = RecorderState.Off;
    private int _actionCount;

    public BlenderActionRecorder(BlenderWorkerClient worker, KnowledgeStore knowledge)
    {
        _worker = worker;
        _knowledge = knowledge;
    }

    public event EventHandler? Changed;

    public RecorderState State => _state;

    public int ActionCount => _actionCount;

    public TimeSpan Elapsed => _watch.Elapsed;

    public void UpdateFromPing(JsonObject? recorderJson)
    {
        if (recorderJson is null || _state == RecorderState.Off)
        {
            return;
        }

        var count = recorderJson["actionCount"]?.GetValue<int>() ?? _actionCount;
        if (count != _actionCount)
        {
            _actionCount = count;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? Start()
    {
        _state = RecorderState.Recording;
        _actionCount = 0;
        _watch.Restart();
        _ = Task.Run(() => _worker.SendRawAsync("recorder_control", new JsonObject { ["action"] = "start" }, CancellationToken.None));
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public void Pause()
    {
        if (_state == RecorderState.Recording)
        {
            _state = RecorderState.Paused;
            _watch.Stop();
            _ = Task.Run(() => _worker.SendRawAsync("recorder_control", new JsonObject { ["action"] = "pause" }, CancellationToken.None));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Resume()
    {
        if (_state == RecorderState.Paused)
        {
            _state = RecorderState.Recording;
            _watch.Start();
            _ = Task.Run(() => _worker.SendRawAsync("recorder_control", new JsonObject { ["action"] = "resume" }, CancellationToken.None));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<RecordingOutcome> StopAndSaveAsync(CancellationToken cancellationToken = default)
    {
        _watch.Stop();
        _state = RecorderState.Off;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            var resp = await _worker.SendRawAsync("recorder_control", new JsonObject { ["action"] = "stop" }, cancellationToken).ConfigureAwait(true);
            var actions = resp["data"]?["actions"] as JsonArray ?? [];
            var count = Math.Max(_actionCount, actions.Count);
            _actionCount = 0;

            var recordingsDir = Path.Combine(_knowledge.Directory, "recordings");
            Directory.CreateDirectory(recordingsDir);
            var fileName = $"{DateTime.Now:yyyy-MM-dd_HHmm}_BlenderScene.md";
            var filePath = Path.Combine(recordingsDir, fileName);

            var md = new StringBuilder();
            md.AppendLine("# Blender 5.3 User Recording");
            md.AppendLine();
            md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Recorded actions: {count}"));
            md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Duration: {_watch.Elapsed:mm\\:ss}"));
            md.AppendLine();
            md.AppendLine("## Steps");
            if (actions.Count == 0)
            {
                md.AppendLine("- User modeled in Blender 5.3 3D Viewport.");
            }
            else
            {
                foreach (var act in actions.OfType<JsonObject>())
                {
                    var cmd = act["command"]?.GetValue<string>() ?? "ACTION";
                    var summary = act["summary"]?.GetValue<string>() ?? string.Empty;
                    md.AppendLine($"- `{cmd}`: {summary}");
                }
            }

            File.WriteAllText(filePath, md.ToString(), Encoding.UTF8);
            return new RecordingOutcome(filePath, Math.Max(1, count), "Updated style.md from Blender 5.3 scene.", null);
        }
        catch (Exception ex)
        {
            return new RecordingOutcome(null, 0, null, "Could not save Blender recording: " + ex.Message);
        }
    }

    public void Discard()
    {
        _watch.Reset();
        _state = RecorderState.Off;
        _actionCount = 0;
        _ = Task.Run(() => _worker.SendRawAsync("recorder_control", new JsonObject { ["action"] = "discard" }, CancellationToken.None));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Focus mode toggle for the docked Blender 5.3 AI Copilot panel.
/// </summary>
public sealed class BlenderFocusLayoutHost : IFocusLayoutHost
{
    private bool _isFocusMode = true;

    public event EventHandler? Changed;

    public bool IsFocusMode => _isFocusMode;

    public string? SetFocusMode(bool on)
    {
        if (_isFocusMode != on)
        {
            _isFocusMode = on;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }
}

/// <summary>
/// Implements <see cref="IQuickAskHost"/> for the floating Quick Ask AI button and popup box over Blender 5.3's 3D Viewport.
/// </summary>
public sealed class BlenderQuickAskHost : IQuickAskHost
{
    private readonly BlenderWorkerClient _worker;
    private readonly BlenderDrawingInteraction _interaction;
    private readonly Action<ChatViewModel> _showPanel;
    private readonly Func<IntPtr> _blenderHwnd;

    public BlenderQuickAskHost(
        BlenderWorkerClient worker,
        BlenderDrawingInteraction interaction,
        Action<ChatViewModel> showPanel,
        Func<IntPtr> blenderHwnd)
    {
        _worker = worker;
        _interaction = interaction;
        _showPanel = showPanel;
        _blenderHwnd = blenderHwnd;
    }

    public CopilotPanelViewModel? Shell { get; set; }

    public string? ActiveDrawingId { get; set; } = "blend-1";

    public string? ActiveDrawingTitle { get; set; } = "Untitled.blend";

    public PickedPoint? CursorPoint => _interaction.LastCursorPoint;

    public SelectionSnapshot? Selection => _interaction.CurrentSelection;

    public Task<PickResult> PickPointAsync(CancellationToken cancellationToken) =>
        _interaction.PickPointsAsync(new PickRequest { Prompt = "Click or place 3D Cursor in Blender 5.3", MaxPoints = 1 }, cancellationToken);

    public void ShowChat(ChatViewModel chat) => _showPanel(chat);

    public async Task<QuickAskUndoResult> UndoAsync(ChatViewModel chat, int calls, CancellationToken cancellationToken)
    {
        var res = await _worker.CallToolAsync("undo", new JsonObject { ["count"] = Math.Max(1, calls) }, cancellationToken).ConfigureAwait(true);
        return new QuickAskUndoResult(res.Success, res.Message ?? "Undone.");
    }

    public void ReturnFocusToDrawing()
    {
        var hwnd = _blenderHwnd();
        if (hwnd != IntPtr.Zero)
        {
            _ = SetForegroundWindow(hwnd);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
