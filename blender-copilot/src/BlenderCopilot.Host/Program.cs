using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadCopilot.Core;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.Views;

namespace BlenderCopilot.Host;

/// <summary>
/// Entry point of <c>BlenderCopilotHost.exe</c>: connects to the Blender 5.3 Python add-on worker,
/// starts the authenticated Named Pipe server and discovery file for <c>AcadCopilotMcp.exe</c>,
/// and opens the docked WPF AI Copilot panel + floating Quick Ask overlay on Blender 5.3 Desktop.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var blenderPid = Environment.ProcessId;
        var workerPort = 0;
        var blenderHwnd = IntPtr.Zero;
        var headless = false;
        var selfTest = false;
        string? artifactsDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--blender-pid" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid):
                    blenderPid = pid;
                    break;
                case "--worker-port" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port):
                    workerPort = port;
                    break;
                case "--blender-hwnd" when i + 1 < args.Length && long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwndVal):
                    blenderHwnd = new IntPtr(hwndVal);
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--artifacts-dir" when i + 1 < args.Length:
                    artifactsDir = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeEnvironmentVariable)))
        {
            var defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlenderCopilot");
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, defaultHome);
        }

        Directory.CreateDirectory(AppPaths.Root);
        CopilotLog.Initialize(Path.Combine(AppPaths.Logs, "blender-host.log"));
        CopilotLog.Info($"BlenderCopilotHost starting for Blender 5.3 pid {blenderPid}, worker port {workerPort}, hwnd {blenderHwnd}, home {AppPaths.Root}.");

        var worker = new BlenderWorkerClient(workerPort);
        var bridgeExe = Path.Combine(AppContext.BaseDirectory, "bridge", "AcadCopilotMcp.exe");
        var dotnetRoot = FindDotnetRoot();

        IAgentRunner[] runners = [new ClaudeCodeRunner(), new CodexRunner(), new GeminiRunner()];
        var sessionInfo = new AcadSessionInfo(blenderPid, "5.3", "Untitled.blend") { Product = "Blender 5.3" };
        CopilotBackend? backend = null;
        backend = new CopilotBackend(
            runners,
            () =>
            {
                var env = new Dictionary<string, string>
                {
                    [AppPaths.HomeEnvironmentVariable] = AppPaths.Root,
                };
                if (!string.IsNullOrWhiteSpace(dotnetRoot))
                {
                    env["DOTNET_ROOT"] = dotnetRoot;
                }

                return new McpServerLaunch
                {
                    ServerName = "acad",
                    Command = bridgeExe,
                    Arguments = ["--pid", blenderPid.ToString(CultureInfo.InvariantCulture), "--panel"],
                    Environment = env,
                };
            },
            sessionInfo,
            currentDrawing: () => new DrawingRef("blend-1", backend?.Acad.DocumentTitle ?? "Untitled.blend"));

        try
        {
            backend.Knowledge.EnsureCreated();
            BuiltInKnowledge.Seed(backend.Knowledge.Directory);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not seed knowledge folder", ex);
        }

        BlenderDockWindow? dockWindow = null;
        var handler = new BlenderPipeHandler(
            worker,
            backend,
            (sessionId, _) =>
            {
                if (dockWindow is not null)
                {
                    dockWindow.Dispatcher.InvokeAsync(() => dockWindow.RevealSession(sessionId));
                }

                return Task.CompletedTask;
            });

        InstanceDiscovery.RemoveStale();
        var pipeName = PipeNames.ForProcess(blenderPid);
        var token = PipeNames.NewToken();
        var pipeServer = new PipeServer(pipeName, token, handler);
        pipeServer.Start();

        var instanceInfo = new AcadInstanceInfo
        {
            Pid = blenderPid,
            Pipe = PipeNames.FullPath(pipeName),
            Token = token,
            AcadVersion = "Blender 5.3",
            DocumentTitle = "Untitled.blend",
            StartedAt = DateTimeOffset.UtcNow,
        };
        var discoveryFile = InstanceDiscovery.Write(instanceInfo);
        CopilotLog.Info($"Blender PipeServer listening on {PipeNames.FullPath(pipeName)}; discovery file {discoveryFile}.");

        try
        {
            if (selfTest)
            {
                return RunSelfTest(instanceInfo, backend, worker, artifactsDir);
            }

            if (headless)
            {
                Console.WriteLine($"READY_PIPE={PipeNames.FullPath(pipeName)}");
                Console.WriteLine($"DISCOVERY={discoveryFile}");
                Console.Out.Flush();
                while (Console.ReadLine() is not null)
                {
                }

                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            dockWindow = new BlenderDockWindow(blenderPid, blenderHwnd, worker, backend);
            app.MainWindow = dockWindow;
            dockWindow.Show();
            return app.Run();
        }
        finally
        {
            InstanceDiscovery.Delete(blenderPid);
            try
            {
                pipeServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    private static int RunSelfTest(AcadInstanceInfo instanceInfo, CopilotBackend backend, BlenderWorkerClient worker, string? artifactsDir)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = PanelTheme.Dark,
            CheckProviderStatusOnLoad = false,
            EnableVoice = false,
            Interaction = new BlenderDrawingInteraction(worker, dispatcher),
            Recorder = new BlenderActionRecorder(worker, backend.Knowledge),
            FocusLayout = new BlenderFocusLayoutHost(),
        });

        var result = Task.Run(async () =>
        {
            await using var client = await PipeClient.ConnectAsync(instanceInfo, "SelfTest", TimeSpan.FromSeconds(10), null, CancellationToken.None);
            var tools = await client.ListToolsAsync(CancellationToken.None);
            Console.WriteLine($"  [SelfTest] Pipe connected; {tools.Count} tools registered.");
            if (tools.Count < 20)
            {
                throw new InvalidOperationException($"Expected >= 20 tools, got {tools.Count}.");
            }

            var info = await client.CallToolAsync("get_drawing_info", new JsonObject(), "c1", CancellationToken.None);
            Console.WriteLine($"  [SelfTest] get_drawing_info: {info.Message}");
            if (!info.Success || info.Data?["product"]?.GetValue<string>() != "Blender 5.3")
            {
                throw new InvalidOperationException("get_drawing_info did not report product 'Blender 5.3'.");
            }

            var tpl = await client.CallToolAsync("apply_drawing_template", new JsonObject(), "c2", CancellationToken.None);
            Console.WriteLine($"  [SelfTest] apply_drawing_template: {tpl.Message}");
            if (!tpl.Success)
            {
                throw new InvalidOperationException("apply_drawing_template failed.");
            }

            var runArgs = new JsonObject
            {
                ["title"] = "3D House Ground Floor (30' x 40')",
                ["steps"] = new JsonArray
                {
                    new JsonObject { ["command"] = "SLAB_3D", ["inputs"] = new JsonArray("0,0", "30',40'", "6", "0", "GroundFloorSlab") },
                    new JsonObject { ["command"] = "WALL_3D", ["inputs"] = new JsonArray("2',3'", "28',3'", "9", "10'6", "SouthWall") },
                    new JsonObject { ["command"] = "WALL_3D", ["inputs"] = new JsonArray("28',3'", "28',37'", "9", "10'6", "EastWall") },
                    new JsonObject { ["command"] = "WALL_3D", ["inputs"] = new JsonArray("28',37'", "2',37'", "9", "10'6", "NorthWall") },
                    new JsonObject { ["command"] = "WALL_3D", ["inputs"] = new JsonArray("2',37'", "2',3'", "9", "10'6", "WestWall") },
                    new JsonObject { ["command"] = "WALL_3D", ["inputs"] = new JsonArray("15',3'", "15',37'", "4.5", "10'6", "CenterPartition") },
                    new JsonObject { ["command"] = "DOOR_3D", ["inputs"] = new JsonArray("8',3'", "3'6", "7'", "0") },
                    new JsonObject { ["command"] = "WINDOW_3D", ["inputs"] = new JsonArray("20',3'", "5'", "3'", "7'", "0") },
                    new JsonObject { ["command"] = "STAIR_3D", ["inputs"] = new JsonArray("3',24'", "7'6,11'", "10'6", "18") },
                    new JsonObject { ["command"] = "FURNITURE_3D", ["inputs"] = new JsonArray("sofa", "8',14'", "7',3'", "0") },
                    new JsonObject { ["command"] = "FURNITURE_3D", ["inputs"] = new JsonArray("bed", "21',26'", "6',6'6", "0") },
                    new JsonObject { ["command"] = "TEXT", ["inputs"] = new JsonArray("J", "MC", "8'6,15'", "10", "0", "LIVING 12'x18'", "") },
                    new JsonObject { ["command"] = "TEXT", ["inputs"] = new JsonArray("J", "MC", "21'6,25'", "10", "0", "BEDROOM 12'x14'", "") },
                    new JsonObject { ["command"] = "DIMLINEAR", ["inputs"] = new JsonArray("2',3'", "@26',0", "H", "@0,-4'") },
                },
            };
            var runRes = await client.CallToolAsync("run_commands", runArgs, "c3", CancellationToken.None);
            Console.WriteLine($"  [SelfTest] run_commands: {runRes.Message}");
            if (!runRes.Success || runRes.CreatedIds.Count < 10)
            {
                throw new InvalidOperationException("run_commands did not create expected 3D objects in Blender 5.3.");
            }

            var capRes = await client.CallToolAsync("capture_view", new JsonObject(), "c4", CancellationToken.None);
            Console.WriteLine($"  [SelfTest] capture_view: {capRes.Message} (images={capRes.Images.Count})");
            if (!capRes.Success || capRes.Images.Count == 0)
            {
                throw new InvalidOperationException("capture_view did not return a 3D viewport PNG.");
            }

            return (runArgs, runRes, capRes);
        }).GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(artifactsDir))
        {
            Directory.CreateDirectory(artifactsDir);
            // Populate the CopilotPane with a preview turn showing the 3D Blender scene and commands
            backend.UpdateAcad(new AcadSessionInfo(instanceInfo.Pid, "5.3", "3D-House-30x40.blend") { Product = "Blender 5.3" });
            var vm = pane.ViewModel;
            var turn = new AcadCopilot.Plugin.ViewModels.TurnInfo(1, vm.SelectedProvider, "Opus");
            vm.Items.Add(new AcadCopilot.Plugin.ViewModels.UserMessageItem("Build a 3D ground floor house on a 30' x 40' plot with walls, doors, windows, dog-legged staircase, furniture and dimensions in Blender 5.3", []));
            vm.Apply(new TextDone("Building the 3D ground floor slab, 9\" outer walls, 4.5\" partition walls, doors, windows, dog-legged staircase, 3D furniture, and annotations in Blender 5.3."), turn);
            vm.Apply(new ToolCallStarted("c1", "get_drawing_info", "", "acad"), turn);
            vm.Apply(new ToolCallFinished("c1", true, "3D-House-30x40.blend (Blender 5.3)"), turn);
            vm.Apply(new ToolCallStarted("c3", "run_commands", "{\"title\":\"3D House Ground Floor (30' x 40')\",\"steps\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14]}", "acad"), turn);
            vm.OnToolCallStarting("run_commands", result.runArgs);
            vm.OnToolCallFinished("run_commands", result.runArgs, result.runRes);
            vm.Apply(new ToolCallFinished("c3", true, result.runRes.Message ?? "Done"), turn);
            vm.Apply(new ToolCallStarted("c4", "capture_view", "", "acad"), turn);
            vm.OnToolCallStarting("capture_view", new JsonObject());
            vm.OnToolCallFinished("capture_view", new JsonObject(), result.capRes);
            vm.Apply(new ToolCallFinished("c4", true, result.capRes.Message ?? "Captured"), turn);
            vm.UpdatePlan(new AcadCopilot.Core.Interaction.PlanUpdate
            {
                Tasks =
                [
                    new() { Title = "Setup 3D collections & Principled BSDF materials", Status = AcadCopilot.Core.Interaction.PlanTaskStatus.Completed },
                    new() { Title = "3D floor slab & 9\" outer walls", Status = AcadCopilot.Core.Interaction.PlanTaskStatus.Completed },
                    new() { Title = "3D doors, windows & dog-legged staircase", Status = AcadCopilot.Core.Interaction.PlanTaskStatus.Completed },
                    new() { Title = "3D furniture, room text & dimensions", Status = AcadCopilot.Core.Interaction.PlanTaskStatus.Completed },
                ],
            });

            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            const double width = 460;
            const double height = 980;
            pane.Width = width;
            pane.Height = height;
            for (var i = 0; i < 4; i++)
            {
                pane.Measure(new Size(width, height));
                pane.Arrange(new Rect(0, 0, width, height));
                pane.UpdateLayout();
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var bmp = new RenderTargetBitmap((int)(width * 1.5), (int)(height * 1.5), 96 * 1.5, 96 * 1.5, PixelFormats.Pbgra32);
            bmp.Render(pane);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var outPng = Path.Combine(artifactsDir, "blender-copilot-panel-dark.png");
            using (var fs = File.Create(outPng))
            {
                encoder.Save(fs);
            }

            var viewportPng = Path.Combine(artifactsDir, "blender-3d-viewport-capture.png");
            File.WriteAllBytes(viewportPng, Convert.FromBase64String(result.capRes.Images[0].Base64Data));
            Console.WriteLine($"  [SelfTest] Rendered panel UI preview -> {outPng}");
            Console.WriteLine($"  [SelfTest] Saved Blender 5.3 3D viewport capture -> {viewportPng}");
        }

        pane.Shell.Dispose();
        return 0;
    }

    private static string? FindDotnetRoot()
    {
        var localDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
        if (Directory.Exists(localDotnet))
        {
            return localDotnet;
        }

        var progDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        return Directory.Exists(progDotnet) ? progDotnet : null;
    }
}
