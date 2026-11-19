using System.IO;
using AcadCopilot.Core;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// The panel side of the learning feature: activity rows of the learning tools with their result message, "Learn my
/// style", the knowledge folder link and the product in the header. Renders artifacts\ui\14-learning-*.png.
/// </summary>
public sealed class LearningPanelTests : IDisposable
{
    private const double Width = 430;
    private const double Height = 760;

    private readonly string _temp;

    public LearningPanelTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-learning-ui-").FullName;
    }

    public void Dispose()
    {
        DraftingPace.Watch = true;
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Theory]
    [InlineData(KnowledgeTools.RecallKnowledge, "Checking what I've learned", "Checked what I've learned")]
    [InlineData(KnowledgeTools.RememberRule, "Remembering your rule", "Remembered your rule")]
    [InlineData(KnowledgeTools.SaveRecipe, "Saving a recipe", "Saved a recipe")]
    [InlineData(KnowledgeTools.SaveLesson, "Saving a lesson", "Saved a lesson")]
    [InlineData(KnowledgeTools.Forget, "Forgetting an item", "Forgot an item")]
    [InlineData(KnowledgeTools.LearnStyleFromDrawing, "Learning your style from this drawing", "Learned your style from this drawing")]
    public void Learning_tools_have_readable_titles(string tool, string running, string done)
    {
        Assert.Equal(running, ToolActivityText.Title(tool, ActivityState.Running));
        Assert.Equal(done, ToolActivityText.Title(tool, ActivityState.Succeeded));
        Assert.True(ToolActivityText.IsLearning(tool));
        Assert.False(ToolActivityText.IsLearning("get_drawing_info"));
    }

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Learning_rows_show_what_was_learned(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var (pane, _) = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.AddRange(LearningScript());
        vm.ComposerText = "Remember: my outer walls are always 9\" and partitions 4 1/2\". Then learn my style from this drawing.";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(g => g.Rows.Count == 4), "learning rows");

        // learn_style_from_drawing runs in the plug-in: its full result message replaces the CLI's short summary.
        vm.OnToolCallStarting(KnowledgeTools.LearnStyleFromDrawing, null);
        vm.OnToolCallFinished(KnowledgeTools.LearnStyleFromDrawing, null, new ToolResult
        {
            Success = true,
            Message = "Learned the style of GF-plan-30x40.dwg: walls 9\" (A-WALL, color 7), text 4 1/2\" Arial, dimensions ARCH 1:48.",
        });
        await Sta.Settle();

        var rows = vm.Items.OfType<ActivityGroupItem>().Single().Rows;
        Assert.Equal(["Checked what I've learned", "Remembered your rule", "Saved a recipe", "Learned your style from this drawing"], rows.Select(r => r.Title));
        Assert.All(rows, r => Assert.True(r.ShowResultLine));
        Assert.Equal("Remembered: outer walls 9\", partitions 4 1/2\".", rows[1].ResultSummary);
        Assert.StartsWith("Learned the style of GF-plan-30x40.dwg", rows[3].ResultSummary, StringComparison.Ordinal);
        Save(pane, "14-learning-rows", theme);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Learn_my_style_sends_the_prompt_and_the_folder_opens_only_when_clicked() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        backend.Acad = new AcadSessionInfo(4242, "2027", "Drawing2.dwg") { Product = "AutoCAD 2027" };
        var (pane, opened) = await CreatePaneAsync(backend, PanelTheme.Light);
        var vm = pane.ViewModel;
        Assert.Equal("AutoCAD 2027 · Drawing2.dwg", vm.DocumentText);
        Assert.Contains(ChatViewModel.LearnStylePrompt, vm.ExamplePrompts);

        Assert.True(vm.HasKnowledgeDirectory);
        Assert.Equal(AppPaths.Knowledge, vm.KnowledgeDirectory);
        Assert.Empty(opened);
        vm.OpenKnowledgeFolderCommand.Execute(null);
        Assert.Equal([AppPaths.Knowledge], opened);
        Assert.True(Directory.Exists(AppPaths.Knowledge));

        vm.IsSettingsOpen = true;
        vm.LearnStyleCommand.Execute(null);
        await Sta.WaitUntil(() => !vm.IsBusy && backend.Sent.Count == 1, "sent");
        Assert.Equal(["Learn my style from this drawing"], backend.Sent);
        Assert.False(vm.IsSettingsOpen);
        vm.Dispose();
    });

    [Fact]
    public void The_header_falls_back_to_the_version_without_a_product() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        backend.Acad = new AcadSessionInfo(4242, "2027", null);
        var (pane, _) = await CreatePaneAsync(backend, PanelTheme.Light);
        Assert.Equal("AutoCAD 2027 · No drawing open", pane.ViewModel.DocumentText);
        pane.ViewModel.Dispose();
    });

    [Fact]
    public void Settings_with_the_learning_section() => Sta.Run(async () =>
    {
        var (pane, _) = await CreatePaneAsync(FakeBackend.Create(), PanelTheme.Dark);
        pane.ViewModel.IsSettingsOpen = true;
        await Sta.Settle();
        Save(pane, "14-learning-settings", PanelTheme.Dark, Width, 1000);
        pane.ViewModel.Dispose();
    });

    internal static IEnumerable<AgentEvent> LearningScript() =>
    [
        new SessionStarted("session-learn"),
        new ToolCallStarted("k1", KnowledgeTools.RecallKnowledge, "topic: walls", "acad"),
        new ToolCallFinished("k1", true, "2 rules, 1 recipe (G+1 staircase), your style: A-WALL layers, ARCH dimensions."),
        new ToolCallStarted("k2", KnowledgeTools.RememberRule, "rule: Outer walls 9\", partitions 4 1/2\"", "acad"),
        new ToolCallFinished("k2", true, "Remembered: outer walls 9\", partitions 4 1/2\"."),
        new ToolCallStarted("k3", KnowledgeTools.SaveRecipe, "name: Dog-legged staircase, steps: 6 items", "acad"),
        new ToolCallFinished("k3", true, "Saved the recipe \"Dog-legged staircase\" (6 steps)."),
        new ToolCallStarted("k4", KnowledgeTools.LearnStyleFromDrawing, "", "acad"),
        new ToolCallFinished("k4", true, "Learned the drawing style."),
        new TextDone("I'll use 9\" outer walls and 4 1/2\" partitions from now on, and your layer and dimension style."),
    ];

    private async Task<(CopilotPane Pane, List<string> Opened)> CreatePaneAsync(FakeBackend backend, PanelTheme theme)
    {
        var opened = new List<string>();
        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            Voice = new Voice.VoiceInputService(new FakeSpeechFactory(), new ManualClock(), _ => { }),
            OpenFolder = opened.Add,
        });
        await pane.ViewModel.RefreshStatusesAsync();
        return (pane, opened);
    }

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width = Width, double height = Height)
    {
        var path = Snapshot.Render(pane, width, height, $"{name}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }
}
