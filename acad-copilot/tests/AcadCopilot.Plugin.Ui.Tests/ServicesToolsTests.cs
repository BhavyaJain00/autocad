using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Tools;

namespace AcadCopilot.Plugin.Ui.Tests;

// The services / presentation tools are registered with the plug-in (no AutoCAD type is touched here; the tool bodies
// need AutoCAD and are compile-verified only).
public sealed class ServicesToolsTests
{
    [Theory]
    [InlineData("insert_symbol_library")]
    [InlineData("place_services_legend")]
    [InlineData("fill_presentation_regions")]
    [InlineData("add_presentation_backdrop")]
    [InlineData("export_view_png")]
    public void The_services_and_presentation_tools_are_registered_as_write_tools(string name)
    {
        var registry = ToolRegistry.CreateDefault();
        Assert.True(registry.TryGet(name, out var spec), name + " is not registered.");
        Assert.Equal(ToolRisk.Write, spec.Definition.Risk);
        Assert.Empty(spec.Definition.Lint());
        Assert.NotNull(spec.Handler);
    }
}
