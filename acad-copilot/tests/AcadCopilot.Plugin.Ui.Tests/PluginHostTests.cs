using System.IO;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Palette;
using AcadCopilot.Plugin.Tools;

namespace AcadCopilot.Plugin.Ui.Tests;

// Plug-in host pieces that run without AutoCAD (no AutoCAD type is touched here).
public sealed class ToolRegistryTests
{
    private static ToolSpec Spec(string name, ToolRisk risk = ToolRisk.Read) => new(
        new ToolDefinition { Name = name, Title = "Tool " + name, Description = "Does " + name + ".", Risk = risk, InputSchema = Schema.Object() },
        (_, _, _) => Task.FromResult(ToolResult.Ok()));

    [Fact]
    public void Default_registry_has_valid_unique_tools_including_get_drawing_info()
    {
        var registry = ToolRegistry.CreateDefault();

        Assert.True(registry.TryGet("get_drawing_info", out var info));
        Assert.Equal(ToolRisk.Read, info.Definition.Risk);
        Assert.All(registry.Definitions, d => Assert.Empty(d.Lint()));
        Assert.Equal(registry.Count, registry.Definitions.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(registry.Definitions, d => BridgeTools.ReservedNames.Contains(d.Name));
        Assert.Empty(JsonSchemaValidator.Validate(info.Definition.InputSchema, new System.Text.Json.Nodes.JsonObject()));
    }

    [Fact]
    public void Duplicate_reserved_and_invalid_tools_are_rejected()
    {
        var duplicate = Assert.Throws<ArgumentException>(() => new ToolRegistry([Spec("draw_line"), Spec("draw_line")]));
        Assert.Contains("defined twice", duplicate.Message, StringComparison.Ordinal);
        var reserved = Assert.Throws<ArgumentException>(() => new ToolRegistry([Spec(BridgeTools.AskUser)]));
        Assert.Contains("reserved", reserved.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ToolRegistry([Spec("Bad Name")]));

        var registry = new ToolRegistry([Spec("draw_line", ToolRisk.Write), Spec("look")]);
        Assert.Equal(new[] { "draw_line", "look" }, registry.Definitions.Select(d => d.Name));
        Assert.False(registry.TryGet("missing", out _));
    }
}

public sealed class BridgeLocatorTests
{
    [Fact]
    public void Launch_points_at_the_bridge_with_the_pid_and_panel_flag()
    {
        var file = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"), BridgeLocator.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        var previous = Environment.GetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable, file);
            var launch = BridgeLocator.CreateLaunch(4321);

            Assert.Equal(file, launch.Command);
            Assert.Equal(new[] { "--pid", "4321", "--panel" }, launch.Arguments);
            Assert.Equal("acad", launch.ServerName);
            Assert.True(launch.Environment.TryGetValue("DOTNET_ROOT", out var root));
            Assert.True(Directory.Exists(Path.Combine(root, "host", "fxr")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable, previous);
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void A_missing_bridge_is_reported_with_its_path()
    {
        var previous = Environment.GetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable, null);
            Assert.Equal(Path.Combine(BridgeLocator.PluginDirectory, "bridge", BridgeLocator.ExecutableName), BridgeLocator.ExpectedPath);

            var missing = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"), BridgeLocator.ExecutableName);
            Environment.SetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable, missing);
            Assert.False(BridgeLocator.Exists);
            var error = Assert.Throws<FileNotFoundException>(() => BridgeLocator.CreateLaunch(1));
            Assert.Contains(missing, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BridgeLocator.OverrideEnvironmentVariable, previous);
        }
    }
}

public sealed class PaletteStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void First_start_uses_defaults_then_the_saved_state_comes_back()
    {
        var store = new PaletteStateStore(Path.Combine(_directory, "palette.json"));
        Assert.True(store.IsFirstRun);
        var initial = store.Load();
        Assert.True(initial.Visible);
        Assert.Equal(PaletteHost.DefaultWidth, initial.Width);

        store.Save(initial with { Visible = false, Width = 512, Height = 640 });

        Assert.False(store.IsFirstRun);
        Assert.Equal(new PaletteState { Visible = false, Width = 512, Height = 640 }, store.Load());
    }

    [Fact]
    public void A_broken_state_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "palette.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(new PaletteState(), new PaletteStateStore(path).Load());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }
}
