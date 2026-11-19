using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AcadCopilot.Core.Tools;

/// <summary>
/// An image attached to a <see cref="ToolResult"/>, for example the PNG picture of the drawing view that capture_view
/// returns. Wire shape (camelCase JSON): { mimeType, base64Data, width?, height? }.
/// The MCP bridge hands it to the LLM as MCP image content. Text channels (the JSON text the LLM reads, logs, the
/// panel) only see <see cref="Describe"/>, never the data.
/// </summary>
public sealed record ToolImage
{
    /// <summary>Media type of PNG images.</summary>
    public const string PngMimeType = "image/png";

    /// <summary>Media type of JPEG images.</summary>
    public const string JpegMimeType = "image/jpeg";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Image media types that Claude Code, Codex and Gemini CLI accept in MCP image content.</summary>
    public static IReadOnlySet<string> SupportedMimeTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PngMimeType, JpegMimeType, "image/gif", "image/webp" };

    /// <summary>Media type, for example "image/png".</summary>
    public required string MimeType { get; init; }

    /// <summary>The image file bytes, base64 encoded (standard alphabet, padded, no line breaks).</summary>
    public required string Base64Data { get; init; }

    /// <summary>Width in pixels, when known.</summary>
    public int? Width { get; init; }

    /// <summary>Height in pixels, when known.</summary>
    public int? Height { get; init; }

    /// <summary>Size of the decoded image in bytes, computed from <see cref="Base64Data"/> (not serialized).</summary>
    [JsonIgnore]
    public int ByteLength
    {
        get
        {
            var data = Base64Data ?? string.Empty;
            var padding = data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith('=') ? 1 : 0;
            return Math.Max(0, (data.Length / 4 * 3) - padding);
        }
    }

    /// <summary>Creates an image from its file bytes.</summary>
    /// <param name="data">The encoded image file (PNG, JPEG, ...).</param>
    /// <param name="mimeType">Its media type.</param>
    /// <param name="width">Width in pixels, when known.</param>
    /// <param name="height">Height in pixels, when known.</param>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty or <paramref name="mimeType"/> is blank.</exception>
    public static ToolImage FromBytes(ReadOnlySpan<byte> data, string mimeType, int? width = null, int? height = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("The image is empty.", nameof(data));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        return new ToolImage { MimeType = mimeType, Base64Data = Convert.ToBase64String(data), Width = width, Height = height };
    }

    /// <summary>Creates a PNG image; width and height are read from the PNG header.</summary>
    /// <param name="png">A complete PNG file.</param>
    /// <exception cref="ArgumentException"><paramref name="png"/> is not a PNG file.</exception>
    public static ToolImage FromPng(ReadOnlySpan<byte> png)
    {
        if (!TryReadPngSize(png, out var width, out var height))
        {
            throw new ArgumentException("The data is not a PNG file.", nameof(png));
        }

        return FromBytes(png, PngMimeType, width, height);
    }

    /// <summary>Reads the pixel size from a PNG file header (signature + IHDR chunk). False when it is not a PNG.</summary>
    public static bool TryReadPngSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 8-byte signature, then the IHDR chunk: 4-byte length (13), "IHDR", 4-byte width, 4-byte height (big-endian).
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>Decodes <see cref="Base64Data"/>.</summary>
    /// <exception cref="FormatException">The data is not valid base64.</exception>
    public byte[] GetBytes() => Convert.FromBase64String(Base64Data);

    /// <summary>
    /// Checks that the image can be sent to an LLM: a supported image media type and non-empty, valid base64 data.
    /// Returns null when it can, otherwise a short reason ("unsupported media type 'image/bmp'").
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(MimeType) || !SupportedMimeTypes.Contains(MimeType))
        {
            return $"unsupported media type '{MimeType}' (use {string.Join(", ", SupportedMimeTypes)})";
        }

        if (string.IsNullOrEmpty(Base64Data))
        {
            return "the image data is empty";
        }

        return Base64.IsValid(Base64Data.AsSpan()) ? null : "the image data is not valid base64";
    }

    /// <summary>Text description used instead of the data on text channels: { mimeType, width?, height?, bytes }.</summary>
    public JsonObject Describe()
    {
        var description = new JsonObject { ["mimeType"] = MimeType };
        if (Width is { } width)
        {
            description["width"] = width;
        }

        if (Height is { } height)
        {
            description["height"] = height;
        }

        description["bytes"] = ByteLength;
        return description;
    }
}
