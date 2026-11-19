using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests;

/// <summary>Images on tool results: wire format, backwards compatibility, text form without data, PNG helpers.</summary>
public sealed class ToolImageTests
{
    /// <summary>A real 2 x 3 px RGB PNG (79 bytes).</summary>
    internal const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAAA2iEnWAAAAFklEQVR42mP4//8/AwSAWIoaBv8ZGABZDAdz/0OXgAAAAABJRU5ErkJggg==";

    private static byte[] TinyPng => Convert.FromBase64String(TinyPngBase64);

    [Fact]
    public void Results_without_images_keep_their_old_json()
    {
        var result = ToolResult.Ok("Drew 4 lines.") with { CreatedIds = [10, 11, 12, 13], Revision = 3 };
        Assert.Empty(result.Images);
        Assert.Equal(
            """{"success":true,"message":"Drew 4 lines.","createdIds":[10,11,12,13],"modifiedIds":[],"deletedIds":[],"warnings":[],"errors":[],"revision":3}""",
            result.ToJson());
        Assert.Equal(result.ToJson(), result.ToTextJson());
    }

    [Fact]
    public void Old_json_and_null_images_read_as_no_images()
    {
        var old = CopilotJson.Deserialize<ToolResult>("""{"success":true,"message":"ok","createdIds":[5],"revision":2}""");
        Assert.True(old.Success);
        Assert.Equal([5L], old.CreatedIds);
        Assert.NotNull(old.Images);
        Assert.Empty(old.Images);

        var nulled = CopilotJson.Deserialize<ToolResult>("""{"success":false,"images":null}""");
        Assert.NotNull(nulled.Images);
        Assert.Empty(nulled.Images);

        var withNull = ToolResult.Ok() with { Images = null! };
        Assert.Empty(withNull.Images);
    }

    [Fact]
    public void Images_round_trip_in_camel_case()
    {
        var result = ToolResult.Ok("Captured the view.") with { Images = [ToolImage.FromPng(TinyPng)], Revision = 9 };
        var json = result.ToJson();
        Assert.Contains("\"images\":[{\"mimeType\":\"image/png\",\"base64Data\":\"" + TinyPngBase64 + "\",\"width\":2,\"height\":3}]", json, StringComparison.Ordinal);

        var back = CopilotJson.Deserialize<ToolResult>(json);
        var image = Assert.Single(back.Images);
        Assert.Equal(ToolImage.PngMimeType, image.MimeType);
        Assert.Equal(TinyPng, image.GetBytes());
        Assert.Equal(2, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(9, back.Revision);
    }

    [Fact]
    public void Text_json_describes_images_without_their_data()
    {
        var result = ToolResult.Ok("Captured the view.", new JsonObject { ["viewHeight"] = 480.0 }) with
        {
            Images = [ToolImage.FromPng(TinyPng)],
        };

        var text = result.ToTextJson();
        Assert.DoesNotContain(TinyPngBase64, text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64Data", text, StringComparison.Ordinal);
        var node = JsonNode.Parse(text)!;
        Assert.True(node["success"]!.GetValue<bool>());
        Assert.Equal(480.0, node["data"]!["viewHeight"]!.GetValue<double>());
        var described = Assert.Single(node["images"]!.AsArray())!;
        Assert.Equal("image/png", described["mimeType"]!.GetValue<string>());
        Assert.Equal(2, described["width"]!.GetValue<int>());
        Assert.Equal(3, described["height"]!.GetValue<int>());
        Assert.Equal(TinyPng.Length, described["bytes"]!.GetValue<int>());

        // The original result is unchanged: the pipe form still carries the data.
        Assert.Single(result.Images);
        Assert.Contains(TinyPngBase64, result.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Png_helpers_read_the_header_and_reject_other_data()
    {
        Assert.True(ToolImage.TryReadPngSize(TinyPng, out var width, out var height));
        Assert.Equal((2, 3), (width, height));
        Assert.False(ToolImage.TryReadPngSize("GIF89a-not-a-png-at-all!"u8, out _, out _));
        Assert.False(ToolImage.TryReadPngSize(TinyPng.AsSpan(0, 20), out _, out _));
        Assert.Throws<ArgumentException>(() => ToolImage.FromPng([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => ToolImage.FromBytes([], ToolImage.PngMimeType));
        Assert.Throws<ArgumentException>(() => ToolImage.FromBytes([1], " "));

        var jpeg = ToolImage.FromBytes([0xFF, 0xD8, 0xFF, 0xD9], ToolImage.JpegMimeType);
        Assert.Null(jpeg.Width);
        Assert.Equal(4, jpeg.ByteLength);
        Assert.DoesNotContain("width", jpeg.Describe().ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    public void Byte_length_matches_the_decoded_size(int size)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        Assert.Equal(size, ToolImage.FromBytes(bytes, ToolImage.PngMimeType).ByteLength);
    }

    [Fact]
    public void Validate_flags_images_an_llm_cannot_take()
    {
        Assert.Null(ToolImage.FromPng(TinyPng).Validate());
        Assert.Contains("unsupported media type", new ToolImage { MimeType = "image/bmp", Base64Data = TinyPngBase64 }.Validate(), StringComparison.Ordinal);
        Assert.Contains("not valid base64", new ToolImage { MimeType = "image/png", Base64Data = "***" }.Validate(), StringComparison.Ordinal);
        Assert.Contains("empty", new ToolImage { MimeType = "image/png", Base64Data = string.Empty }.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_cross_the_named_pipe()
    {
        var handler = new ImageHandler();
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        await using var server = new PipeServer(pipe, token, handler);
        server.Start();
        var instance = new AcadInstanceInfo { Pid = Environment.ProcessId, Pipe = PipeNames.FullPath(pipe), Token = token, AcadVersion = "2027" };
        await using var client = await PipeClient.ConnectAsync(instance, "test", TimeSpan.FromSeconds(10), CancellationToken.None);

        var result = await client.CallToolAsync("capture_view", null, "c1", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(TinyPng, Assert.Single(result.Images).GetBytes());
    }

    private sealed class ImageHandler : IPipeHandler
    {
        public AuthResult GetServerInfo() => new() { ServerName = "Fake", ServerVersion = "1", Pid = Environment.ProcessId };

        public Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult(new ToolsListResult());

        public Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok("Captured.") with { Images = [ToolImage.FromPng(TinyPng)] });

        public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
            throw new UiUnavailableException("No panel.");

        public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) =>
            throw new UiUnavailableException("No panel.");
    }
}
