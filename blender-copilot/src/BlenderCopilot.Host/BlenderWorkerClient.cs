using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace BlenderCopilot.Host;

/// <summary>
/// TCP JSON-line client that talks to the Python <c>BlenderWorkerServer</c> running on Blender 5.3's main thread.
/// </summary>
public sealed class BlenderWorkerClient
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly int _port;

    public BlenderWorkerClient(int port)
    {
        _port = port;
    }

    public int Port => _port;

    public async Task<JsonObject> SendRawAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync("127.0.0.1", _port, connectCts.Token).ConfigureAwait(false);
        await using var stream = tcp.GetStream();
        using var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 16384, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);

        var payload = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject(),
        };
        await writer.WriteLineAsync(payload.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new IOException("Blender 5.3 closed the worker connection without answering.");
        }

        return JsonNode.Parse(line) as JsonObject
            ?? throw new IOException("Blender 5.3 returned an invalid JSON object.");
    }

    public async Task<ToolResult> CallToolAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        JsonObject raw;
        try
        {
            raw = await SendRawAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CopilotLog.Warn($"Blender 5.3 call '{method}' failed", ex);
            return ToolResult.Fail($"Could not reach Blender 5.3 during '{method}': {ex.Message}");
        }

        var success = raw["success"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        var message = raw["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : (success ? "OK" : "Failed");
        var revision = raw["revision"] is JsonValue rv && rv.TryGetValue<long>(out var rev) ? rev : 1L;
        var data = raw["data"]?.DeepClone() as JsonObject;

        var created = ReadStringArray(raw["createdIds"]);
        var modified = ReadStringArray(raw["modifiedIds"]);
        var deleted = ReadStringArray(raw["deletedIds"]);
        var warnings = ReadStringArray(raw["warnings"]);
        var errors = ReadStringArray(raw["errors"]);

        var images = new List<ToolImage>();
        if (raw["images"] is JsonArray imgArr)
        {
            foreach (var item in imgArr.OfType<JsonObject>())
            {
                var mime = item["mimeType"]?.GetValue<string>() ?? "image/png";
                var b64 = item["base64Data"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    images.Add(new ToolImage { MimeType = mime, Base64Data = b64 });
                }
            }
        }

        return new ToolResult
        {
            Success = success,
            Message = message,
            Data = data,
            CreatedIds = created,
            ModifiedIds = modified,
            DeletedIds = deleted,
            Warnings = warnings,
            Errors = errors,
            Images = images,
            Revision = revision,
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return [];
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }

        return list;
    }
}
