using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.McpBridge.Tests;

public sealed class BridgeOptionsTests
{
    [Fact]
    public void Parses_all_options()
    {
        var options = BridgeOptions.Parse(["--pid", "1234", "--panel", "--instances-dir", @"C:\x", "--connect-timeout", "1.5", "--log", "none"]);
        Assert.Equal(1234, options.Pid);
        Assert.True(options.Panel);
        Assert.Equal(@"C:\x", options.InstancesDirectory);
        Assert.Equal(TimeSpan.FromSeconds(1.5), options.ConnectTimeout);
        Assert.Equal("none", options.LogFile);
    }

    [Fact]
    public void Defaults_and_errors()
    {
        var options = BridgeOptions.Parse([]);
        Assert.Null(options.Pid);
        Assert.False(options.Panel);
        Assert.True(BridgeOptions.Parse(["--help"]).ShowHelp);
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--pid"]));
        Assert.Throws<FormatException>(() => BridgeOptions.Parse(["--pid", "abc"]));
        Assert.Null(options.SessionId);
    }

    [Fact]
    public void Session_names_the_panel_chat_tab()
    {
        Assert.Equal("tab-1a2b3c4d", BridgeOptions.Parse(["--pid", "1", "--panel", "--session", "tab-1a2b3c4d"]).SessionId);
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--session"]));
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--session", "two words"]));
        Assert.Throws<ArgumentException>(() => BridgeOptions.Parse(["--session", new string('x', 65)]));
        Assert.Contains("--session <id>", BridgeOptions.Usage, StringComparison.Ordinal);
    }
}

public sealed class BridgeToolServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Lists_acad_tools_plus_bridge_tools()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options());
        var panel = new BridgeToolService(link, panel: true);
        var names = (await panel.ListToolsAsync(None)).Select(t => t.Name).ToList();
        Assert.Equal(["get_model_info", "create_wall", "acad_status", "ask_user", "update_plan", "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "forget", "list_recordings", "read_recording"], names);

        var external = new BridgeToolService(link, panel: false);
        Assert.Equal(["get_model_info", "create_wall", "acad_status", "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "forget", "list_recordings", "read_recording"], (await external.ListToolsAsync(None)).Select(t => t.Name));
        Assert.True((await external.CallToolAsync("ask_user", [], None)).IsError);
    }

    [Fact]
    public async Task Forwards_tool_calls_to_the_addin()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true);

        var ok = await service.CallToolAsync("create_wall", new JsonObject { ["level"] = "Level 1" }, None);
        Assert.False(ok.IsError);
        var json = JsonNode.Parse(ok.Text)!;
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Equal(1001, json["createdIds"]![0]!.GetValue<long>());
        Assert.True(acad.Handler.Calls.TryDequeue(out var call));
        Assert.Equal("Level 1", call.Arguments!["level"]!.GetValue<string>());
        Assert.StartsWith("bridge-", call.CallId, StringComparison.Ordinal);

        var unknown = await service.CallToolAsync("make_coffee", null, None);
        Assert.True(unknown.IsError);
        Assert.Contains("Unknown tool", unknown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_bridge_sends_its_tab_with_every_call()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options() with { SessionId = "tab-0000beef" });
        var service = new BridgeToolService(link, panel: true);

        Assert.False((await service.CallToolAsync("create_wall", new JsonObject(), None)).IsError);
        var ask = JsonNode.Parse("""{"questions":[{"question":"Floors?","options":[{"label":"G+1"},{"label":"G+2"}]}]}""")!.AsObject();
        Assert.False((await service.CallToolAsync("ask_user", ask, None)).IsError);
        var plan = JsonNode.Parse("""{"tasks":[{"title":"Walls","status":"pending"}]}""")!.AsObject();
        Assert.False((await service.CallToolAsync("update_plan", plan, None)).IsError);
        var status = JsonNode.Parse((await service.CallToolAsync("acad_status", null, None)).Text)!;

        Assert.Equal("tab-0000beef", Assert.Single(acad.Handler.Calls).SessionId);
        Assert.Equal("tab-0000beef", Assert.Single(acad.Handler.QuestionSessions));
        Assert.Equal("tab-0000beef", Assert.Single(acad.Handler.PlanSessions));
        Assert.Equal("tab-0000beef", status["chatSession"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_user_and_update_plan_go_to_the_panel()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true);

        var ask = JsonNode.Parse("""{"questions":[{"question":"How many bedrooms?","options":[{"label":"3 bedrooms","recommended":true},{"label":"4 bedrooms"}]}]}""")!.AsObject();
        var answered = await service.CallToolAsync("ask_user", ask, None);
        Assert.False(answered.IsError);
        var response = CopilotJson.Deserialize<QuestionResponse>(answered.Text);
        Assert.Equal("3 bedrooms", response.Answers[0].Selected[0]);
        Assert.True(acad.Handler.Questions.TryDequeue(out var forwarded));
        Assert.True(forwarded.Questions[0].Options[0].Recommended);

        var invalid = await service.CallToolAsync("ask_user", new JsonObject { ["questions"] = new JsonArray() }, None);
        Assert.True(invalid.IsError);
        Assert.Contains("Invalid arguments", invalid.Text, StringComparison.Ordinal);

        var plan = JsonNode.Parse("""{"tasks":[{"title":"Walls","status":"completed"},{"title":"Roof","status":"in_progress"}]}""")!.AsObject();
        var planned = await service.CallToolAsync("update_plan", plan, None);
        Assert.Equal("Plan updated: 1/2 completed.", planned.Text);
        Assert.True(acad.Handler.Plans.TryDequeue(out var sent));
        Assert.Equal(PlanTaskStatus.InProgress, sent.Tasks[1].Status);

        acad.Handler.UiAvailable = false;
        var unavailable = await service.CallToolAsync("ask_user", ask, None);
        Assert.True(unavailable.IsError);
        Assert.Contains("No panel", unavailable.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_acad_every_acad_tool_returns_a_clear_error()
    {
        using var dir = new TestDirectory();
        var options = new BridgeOptions { InstancesDirectory = dir.Path, LogFile = "none", ConnectTimeout = TimeSpan.FromSeconds(1) };
        await using var link = new AcadLink(options);
        var service = new BridgeToolService(link, panel: true);

        Assert.Equal(["acad_status", "ask_user", "update_plan", "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "forget", "list_recordings", "read_recording"], (await service.ListToolsAsync(None)).Select(t => t.Name));
        var call = await service.CallToolAsync("get_model_info", null, None);
        Assert.True(call.IsError);
        Assert.Contains("No running AutoCAD 2027", call.Text, StringComparison.Ordinal);
        Assert.True((await service.CallToolAsync("ask_user", JsonNode.Parse("""{"questions":[{"question":"Q?","options":[{"label":"a"},{"label":"b"}]}]}""")!.AsObject(), None)).IsError);

        var status = JsonNode.Parse((await service.CallToolAsync("acad_status", null, None)).Text)!;
        Assert.False(status["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Status_reports_the_connection()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: false);
        var status = JsonNode.Parse((await service.CallToolAsync("acad_status", null, None)).Text)!;
        Assert.True(status["connected"]!.GetValue<bool>());
        Assert.Equal(Environment.ProcessId, status["pid"]!.GetValue<int>());
        Assert.Equal("House.dwg", status["documentTitle"]!.GetValue<string>());
        Assert.Equal(2, status["acadToolCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reconnects_after_acad_restarts()
    {
        await using var acad = FakeAcad.Start();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: false);
        var changes = 0;
        link.ToolsChanged += _ => Interlocked.Increment(ref changes);
        Assert.False((await service.CallToolAsync("get_model_info", null, None)).IsError);

        await acad.StopAsync();
        await WaitUntilAsync(() => link.Current is null);
        var down = await service.CallToolAsync("get_model_info", null, None);
        Assert.True(down.IsError);

        acad.Restart();
        var up = await service.CallToolAsync("get_model_info", null, None);
        Assert.False(up.IsError);
        Assert.True(Volatile.Read(ref changes) >= 3);
    }

    [Fact]
    public async Task Image_results_carry_the_png_beside_json_text_without_base64()
    {
        await using var acad = FakeAcad.StartWithImages();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: true);
        Assert.Contains("capture_view", (await service.ListToolsAsync(None)).Select(t => t.Name));

        var result = await service.CallToolAsync("capture_view", null, None);
        Assert.False(result.IsError);
        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(ImageToolHandler.TinyPng, image.GetBytes());

        Assert.DoesNotContain(ImageToolHandler.TinyPngBase64, result.Text, StringComparison.Ordinal);
        var json = JsonNode.Parse(result.Text)!;
        Assert.True(json["success"]!.GetValue<bool>());
        Assert.Equal(12, json["revision"]!.GetValue<long>());
        Assert.Equal(480.0, json["data"]!["viewHeight"]!.GetValue<double>());
        var described = Assert.Single(json["images"]!.AsArray())!;
        Assert.Equal("image/png", described["mimeType"]!.GetValue<string>());
        Assert.Equal(2, described["width"]!.GetValue<int>());
        Assert.Equal(3, described["height"]!.GetValue<int>());
        Assert.Equal(ImageToolHandler.TinyPng.Length, described["bytes"]!.GetValue<int>());

        // Ordinary tools of the same fake still come back as before (no images, unchanged JSON).
        var wall = await service.CallToolAsync("create_wall", new JsonObject { ["level"] = "Level 1" }, None);
        Assert.Empty(wall.Images);
        Assert.DoesNotContain("\"images\"", wall.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_an_llm_cannot_take_are_dropped_and_named_in_the_warnings()
    {
        await using var acad = FakeAcad.StartWithImages();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: false);

        var result = await service.CallToolAsync("capture_view", new JsonObject { ["mode"] = "bad" }, None);
        Assert.False(result.IsError);
        Assert.Equal(ImageToolHandler.TinyPng, Assert.Single(result.Images).GetBytes());
        var json = JsonNode.Parse(result.Text)!;
        Assert.Single(json["images"]!.AsArray());
        var warnings = json["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Equal("Fake warning.", warnings[0]);
        Assert.Contains(warnings, w => w.StartsWith("Image 2 could not be sent: unsupported media type 'image/bmp'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Image 3 could not be sent: the image data is not valid base64", StringComparison.Ordinal));
        Assert.DoesNotContain("not base64!", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_results_keep_the_error_flag_and_their_image()
    {
        await using var acad = FakeAcad.StartWithImages();
        await using var link = new AcadLink(acad.Options());
        var service = new BridgeToolService(link, panel: false);

        var result = await service.CallToolAsync("capture_view", new JsonObject { ["mode"] = "error" }, None);
        Assert.True(result.IsError);
        Assert.Single(result.Images);
        var json = JsonNode.Parse(result.Text)!;
        Assert.False(json["success"]!.GetValue<bool>());
        Assert.Equal("The view could not be read completely.", json["errors"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Oversized_images_are_not_forwarded()
    {
        // 'A' * n is valid base64 for n multiple of 4 (zero bytes): just over the limit once decoded.
        var length = ((BridgeToolService.MaxImageBytes / 3) + 2) * 4;
        var huge = new ToolImage { MimeType = ToolImage.PngMimeType, Base64Data = new string('A', length) };
        Assert.True(huge.ByteLength > BridgeToolService.MaxImageBytes);
        var small = ToolImage.FromPng(ImageToolHandler.TinyPng);

        var converted = BridgeToolService.ToCallResult(ToolResult.Ok("Two pictures.") with { Images = [huge, small] });
        Assert.False(converted.IsError);
        Assert.Same(small, Assert.Single(converted.Images));
        Assert.Contains("Image 1 could not be sent: it is too large", converted.Text, StringComparison.Ordinal);
        Assert.True(converted.Text.Length < 2_000, "The text part must never carry image data.");

        var plain = ToolResult.Ok("No pictures.") with { Revision = 4 };
        var forwarded = BridgeToolService.ToCallResult(plain);
        Assert.Equal(plain.ToJson(), forwarded.Text);
        Assert.Empty(forwarded.Images);
        Assert.True(BridgeToolService.ToCallResult(ToolResult.Fail("Nope")).IsError);
    }

    [Fact]
    public void Mcp_result_puts_text_first_then_one_image_block_per_image()
    {
        var png = ToolImage.FromPng(ImageToolHandler.TinyPng);
        var mcp = BridgeServer.ToMcpResult(new BridgeCallResult("""{"success":false}""", IsError: true) { Images = [png, png] });
        Assert.True(mcp.IsError);
        Assert.Equal(3, mcp.Content.Count);
        Assert.Equal("""{"success":false}""", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(mcp.Content[0]).Text);
        foreach (var block in mcp.Content.Skip(1))
        {
            var image = Assert.IsType<ModelContextProtocol.Protocol.ImageContentBlock>(block);
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(ImageToolHandler.TinyPng, image.DecodedData.ToArray());
        }

        var plain = BridgeServer.ToMcpResult(new BridgeCallResult("done", IsError: false));
        Assert.Equal("done", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(plain.Content)).Text);
        Assert.False(plain.IsError);
    }

    [Fact]
    public async Task Learning_tools_work_without_AutoCAD()
    {
        using var dir = new TestDirectory();
        var options = new BridgeOptions { InstancesDirectory = dir.File("instances"), LogFile = "none", ConnectTimeout = TimeSpan.FromSeconds(1) };
        await using var link = new AcadLink(options);
        var store = new AcadCopilot.Core.Learning.KnowledgeStore(dir.File("knowledge"));
        var service = new BridgeToolService(link, panel: false, store);

        var saved = await service.CallToolAsync("remember_rule", JsonNode.Parse("""{"text":"Dimensions outside the walls","category":"dimensions"}""")!.AsObject(), None);
        Assert.False(saved.IsError, saved.Text);
        Assert.Contains("Dimensions outside the walls", File.ReadAllText(store.RulesPath), StringComparison.Ordinal);

        var lesson = await service.CallToolAsync("save_lesson", JsonNode.Parse("""{"mistake":"SYSMON notices looked like errors","fix":"They are notices; carry on"}""")!.AsObject(), None);
        Assert.False(lesson.IsError, lesson.Text);

        var recall = await service.CallToolAsync("recall_knowledge", JsonNode.Parse("""{"query":"dims"}""")!.AsObject(), None);
        Assert.False(recall.IsError);
        Assert.Contains("Dimensions outside the walls", recall.Text, StringComparison.Ordinal);

        var invalid = await service.CallToolAsync("save_recipe", JsonNode.Parse("""{"name":"x"}""")!.AsObject(), None);
        Assert.True(invalid.IsError);
        Assert.Contains("Invalid arguments", invalid.Text, StringComparison.Ordinal);

        var forget = await service.CallToolAsync("forget", JsonNode.Parse("""{"kind":"rule","text":"outside the walls"}""")!.AsObject(), None);
        Assert.False(forget.IsError, forget.Text);
        Assert.DoesNotContain("Dimensions outside the walls", File.ReadAllText(store.RulesPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_tools_list_and_read_the_users_recordings_without_AutoCAD()
    {
        using var dir = new TestDirectory();
        var options = new BridgeOptions { InstancesDirectory = dir.File("instances"), LogFile = "none", ConnectTimeout = TimeSpan.FromSeconds(1) };
        await using var link = new AcadLink(options);
        var store = new AcadCopilot.Core.Learning.KnowledgeStore(dir.File("knowledge"));
        var service = new BridgeToolService(link, panel: true, store);

        var none = await service.CallToolAsync("list_recordings", null, None);
        Assert.False(none.IsError, none.Text);
        Assert.Contains("No recordings yet", none.Text, StringComparison.Ordinal);
        Assert.True((await service.CallToolAsync("read_recording", null, None)).IsError);

        var library = new AcadCopilot.Core.Learning.RecordingLibrary(store.Directory);
        var body = "# Recording 2026-11-19 14:12 - House.dwg\n\n## Summary\n\n- 2 actions (2 commands, 0 layer changes); objects: 2 created, 0 modified, 0 erased\n\n## Step 1 - On A-DOOR: LINE, ARC (14:12)\n\n" +
                   string.Concat(Enumerable.Range(1, 400).Select(i => $"{i}. **LINE** on layer A-DOOR (14:12:00)\n   - `Specify first point:` `3'1.5,10'4.5`\n"));
        var path = library.Save(new DateTimeOffset(2026, 11, 19, 14, 12, 0, TimeSpan.Zero), "House.dwg", body);

        var listed = JsonNode.Parse((await service.CallToolAsync("list_recordings", null, None)).Text)!;
        Assert.Equal(Path.GetFileName(path), listed["data"]!["recordings"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Recording 2026-11-19 14:12 - House.dwg", listed["data"]!["recordings"]![0]!["title"]!.GetValue<string>());

        // Long recordings come in parts that add up to the file.
        var first = JsonNode.Parse((await service.CallToolAsync("read_recording", JsonNode.Parse("""{"name":"latest","maxCharacters":10000}""")!.AsObject(), None)).Text)!["data"]!;
        Assert.True(first["more"]!.GetValue<bool>());
        var next = first["nextOffset"]!.GetValue<int>();
        var second = JsonNode.Parse((await service.CallToolAsync("read_recording", new JsonObject { ["name"] = Path.GetFileName(path), ["offset"] = next }, None)).Text)!["data"]!;
        Assert.False(second["more"]!.GetValue<bool>());
        Assert.Equal(body, first["text"]!.GetValue<string>() + second["text"]!.GetValue<string>());

        // Only files inside knowledge\recordings can be read.
        File.WriteAllText(store.RulesPath, "- secret");
        var outside = await service.CallToolAsync("read_recording", JsonNode.Parse("""{"name":"..\\rules.md"}""")!.AsObject(), None);
        Assert.True(outside.IsError);
        Assert.DoesNotContain("secret", outside.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_describe_the_drafting_tool_set()
    {
        foreach (var tool in new[] { "get_drawing_info", "run_commands", "capture_view", "list_entities", "get_entity", "list_layers", "get_sysvars", "undo", "cancel_command", "acad_status", "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "learn_style_from_drawing" })
        {
            Assert.Contains(tool, BridgeServer.Instructions, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("get_model_info", BridgeServer.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Architecture", BridgeServer.Instructions, StringComparison.Ordinal);
        Assert.True(BridgeServer.Instructions.Length < 2_500);
    }

    [Fact]
    public async Task List_changed_is_only_needed_when_availability_changed()
    {
        using var dir = new TestDirectory();
        await using var link = new AcadLink(new BridgeOptions { InstancesDirectory = dir.Path, LogFile = "none" });
        var service = new BridgeToolService(link, panel: false);
        Assert.Null(service.LastListIncludedAcad);
        Assert.False(service.IsListStale(ToolsChangeReason.Connected));

        await service.ListToolsAsync(None);
        Assert.False(service.LastListIncludedAcad);
        Assert.True(service.IsListStale(ToolsChangeReason.Connected));
        Assert.False(service.IsListStale(ToolsChangeReason.Disconnected));
        Assert.True(service.IsListStale(ToolsChangeReason.AddinNotification));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(50);
        }
    }
}
