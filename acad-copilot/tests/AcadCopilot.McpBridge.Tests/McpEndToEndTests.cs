using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadCopilot.McpBridge.Tests;

/// <summary>MCP-level tests with the official SDK client: in-process server, and the real AcadCopilotMcp.exe.</summary>
public sealed class McpEndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static CallToolRequestParams Call(string name, string argumentsJson)
    {
        var arguments = new Dictionary<string, JsonElement>();
        foreach (var property in JsonDocument.Parse(argumentsJson).RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return new CallToolRequestParams { Name = name, Arguments = arguments };
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>Checks an MCP capture_view result: JSON text without base64 first, then the PNG as image content.</summary>
    private static void AssertImageResult(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.Content.Count);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.DoesNotContain(ImageToolHandler.TinyPngBase64, text, StringComparison.Ordinal);
        var json = JsonNode.Parse(text)!;
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Equal(480.0, json["data"]!["viewHeight"]!.GetValue<double>());
        Assert.Equal("image/png", json["images"]![0]!["mimeType"]!.GetValue<string>());

        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(ImageToolHandler.TinyPng, image.DecodedData.ToArray());
    }

    /// <summary>Runs <paramref name="body"/> against an in-process bridge server (official SDK on both ends) wired to <paramref name="acad"/>.</summary>
    private static async Task WithInProcessClientAsync(FakeAcad acad, Func<McpClient, Task> body)
    {
        await using var link = new AcadLink(acad.Options(panel: true));
        var service = new BridgeToolService(link, panel: true);

        // client -> server and server -> client byte streams.
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        await using var server = BridgeServer.Create(new StreamServerTransport(serverIn, toClient, BridgeServer.ServerName), service, link);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);

        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer, clientIn), cancellationToken: new CancellationTokenSource(Timeout).Token))
        {
            await body(client);
        }

        await stop.CancelAsync();
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // RunAsync ends by cancellation.
        }
    }

    [Fact]
    public async Task In_process_server_lists_and_calls_tools_through_the_sdk()
    {
        await using var acad = FakeAcad.Start();
        await WithInProcessClientAsync(acad, async client =>
        {
            Assert.Equal("acad", client.ServerInfo?.Name);
            Assert.Contains("run_commands", client.ServerInstructions, StringComparison.Ordinal);
            var tools = await client.ListToolsAsync();
            Assert.Equal(["get_model_info", "create_wall", "acad_status", "ask_user", "update_plan", "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "forget", "list_recordings", "read_recording"], tools.Select(t => t.Name));
            var wall = tools.Single(t => t.Name == "create_wall").ProtocolTool;
            Assert.False(wall.Annotations!.ReadOnlyHint);
            Assert.Equal("object", wall.InputSchema.GetProperty("type").GetString());

            var result = await client.CallToolAsync(Call("create_wall", """{"level":"Level 1"}"""));
            Assert.NotEqual(true, result.IsError);
            Assert.Equal(1001, JsonNode.Parse(Text(result))!["createdIds"]![0]!.GetValue<long>());

            var failed = await client.CallToolAsync(Call("make_coffee", "{}"));
            Assert.True(failed.IsError);
        });
    }

    [Fact]
    public async Task In_process_image_result_arrives_as_mcp_image_content()
    {
        await using var acad = FakeAcad.StartWithImages();
        await WithInProcessClientAsync(acad, async client =>
        {
            var capture = (await client.ListToolsAsync()).Single(t => t.Name == "capture_view").ProtocolTool;
            Assert.True(capture.Annotations!.ReadOnlyHint);

            AssertImageResult(await client.CallToolAsync(Call("capture_view", "{}")));

            // A failed call keeps isError and still delivers its picture.
            var failed = await client.CallToolAsync(Call("capture_view", """{"mode":"error"}"""));
            Assert.True(failed.IsError);
            Assert.IsType<ImageContentBlock>(failed.Content[1]);

            // Tools without images keep a single text block.
            var wall = await client.CallToolAsync(Call("create_wall", "{}"));
            Assert.IsType<TextContentBlock>(Assert.Single(wall.Content));
        });
    }

    [Fact]
    public async Task Built_exe_serves_mcp_over_stdio_without_a_window()
    {
        await using var acad = FakeAcad.StartWithImages();
        var exe = FindBridgeExe();
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--panel",
                     "--session", "tab-e2e00001",
                     "--instances-dir", acad.InstancesDirectory,
                     "--log", acad.Directory.File("bridge.log"),
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ACADCOPILOT_HOME"] = acad.Directory.Path;
        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var transport = new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
            await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
            {
                Assert.Equal("acad", client.ServerInfo?.Name);
                var names = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(t => t.Name).ToList();
                Assert.Contains("get_model_info", names);
                Assert.Contains("ask_user", names);

                var info = await client.CallToolAsync(Call("get_model_info", "{}"), timeout.Token);
                Assert.NotEqual(true, info.IsError);
                Assert.Equal("House.dwg", JsonNode.Parse(Text(info))!["data"]!["document"]!["title"]!.GetValue<string>());

                // The picture travels fake add-in -> pipe -> AcadCopilotMcp.exe -> stdio -> MCP image content.
                AssertImageResult(await client.CallToolAsync(Call("capture_view", "{}"), timeout.Token));

                var answer = await client.CallToolAsync(
                    Call("ask_user", """{"questions":[{"question":"Roof type?","options":[{"label":"Gable"},{"label":"Hip"}]}]}"""),
                    timeout.Token);
                Assert.Contains("Gable", Text(answer), StringComparison.Ordinal);

                var status = JsonNode.Parse(Text(await client.CallToolAsync(Call("acad_status", "{}"), timeout.Token)))!;
                Assert.True(status["connected"]!.GetValue<bool>());

                // Learning tools are answered by the bridge itself, into ACADCOPILOT_HOME's knowledge folder.
                var rule = await client.CallToolAsync(Call("remember_rule", """{"text":"Toilets at least 5'x7' clear","category":"planning"}"""), timeout.Token);
                Assert.NotEqual(true, rule.IsError);
                Assert.Contains("Toilets at least 5'x7' clear", File.ReadAllText(Path.Combine(acad.Directory.Path, "knowledge", "rules.md")), StringComparison.Ordinal);
                var recall = await client.CallToolAsync(Call("recall_knowledge", """{"query":"bathroom"}"""), timeout.Token);
                Assert.Equal("Toilets at least 5'x7' clear", JsonNode.Parse(Text(recall))!["data"]!["items"]![0]!["text"]!.GetValue<string>());

                // "Learn from me" recordings are read by the bridge too, from ACADCOPILOT_HOME's knowledge\recordings.
                var recordings = Directory.CreateDirectory(Path.Combine(acad.Directory.Path, "knowledge", "recordings")).FullName;
                File.WriteAllText(Path.Combine(recordings, "2026-11-19_1412_House.md"), "# Recording 2026-11-19 14:12 - House.dwg\n\n1. **OFFSET** on layer A-WALL\n");
                var read = await client.CallToolAsync(Call("read_recording", """{"name":"2026-11-19_1412_House"}"""), timeout.Token);
                Assert.NotEqual(true, read.IsError);
                Assert.Contains("**OFFSET** on layer A-WALL", JsonNode.Parse(Text(read))!["data"]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
            }

            // A CLI stops its MCP server by closing stdin: the bridge must exit cleanly.
            process.StandardInput.Close();
            using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(exitWait.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.True(acad.Handler.Calls.Count >= 1);

            // Every request of a panel tab's bridge names the tab (routing of cards, plan, approvals and the drawing).
            Assert.All(acad.Handler.Calls, call => Assert.Equal("tab-e2e00001", call.SessionId));
            Assert.Equal("tab-e2e00001", Assert.Single(acad.Handler.QuestionSessions));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await stderr;
        }
    }

    [Fact]
    public async Task Built_exe_prints_help_and_rejects_bad_arguments()
    {
        var exe = FindBridgeExe();
        var (code, output) = await RunAsync(exe, "--help");
        Assert.Equal(0, code);
        Assert.Contains("--panel", output, StringComparison.Ordinal);
        var (badCode, _) = await RunAsync(exe, "--nope");
        Assert.Equal(2, badCode);
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string argument)
    {
        var startInfo = new ProcessStartInfo(exe, argument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(wait.Token);
        return (process.ExitCode, await output + await error);
    }

    private static string FindBridgeExe()
    {
        // Built with --artifacts-path: bin\AcadCopilot.McpBridge.Tests\<config> next to bin\AcadCopilot.McpBridge\<config>.
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (testOutput.Parent?.Name == "AcadCopilot.McpBridge.Tests" && testOutput.Parent.Parent is { } binRoot)
        {
            var sibling = Path.Combine(binRoot.FullName, "AcadCopilot.McpBridge", testOutput.Name, "AcadCopilotMcp.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcadCopilot.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var exe = Path.Combine(directory.FullName, "src", "AcadCopilot.McpBridge", "bin", configuration, "net10.0", "AcadCopilotMcp.exe");
        Assert.True(File.Exists(exe), $"Bridge not built: {exe}");
        return exe;
    }
}
