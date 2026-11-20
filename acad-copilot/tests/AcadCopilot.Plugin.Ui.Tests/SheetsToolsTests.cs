using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Tools;

namespace AcadCopilot.Plugin.Ui.Tests;

// The drawing-set tools are registered with the plug-in with the right risks (no AutoCAD type is touched here; the tool
// bodies need AutoCAD and are compile-verified only).
public sealed class SheetsToolsTests
{
    [Theory]
    [InlineData("plan_full_set", ToolRisk.Read)]
    [InlineData("arrange_drawings", ToolRisk.Write)]
    [InlineData("create_sheets", ToolRisk.Write)]
    [InlineData("plot_to_pdf", ToolRisk.Destructive)]
    public void The_drawing_set_tools_are_registered(string name, ToolRisk risk)
    {
        var registry = ToolRegistry.CreateDefault();
        Assert.True(registry.TryGet(name, out var spec), name + " is not registered.");
        Assert.Equal(risk, spec.Definition.Risk);
        Assert.Empty(spec.Definition.Lint());
        Assert.NotNull(spec.Handler);
    }
}
