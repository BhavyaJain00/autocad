using System.Diagnostics;
using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Streams_stdout_and_stderr_lines_and_exit_code()
    {
        var launch = new ProcessLaunch
        {
            FileName = "cmd.exe",
            Arguments = ["/d", "/c", "echo hello& echo oops 1>&2& exit /b 3"],
        };
        var result = await ProcessRunner.RunToEndAsync(launch, Timeout);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
        Assert.Equal("oops", result.StandardError.Trim());
    }

    [Fact]
    public async Task Writes_stdin_then_closes_it()
    {
        var launch = new ProcessLaunch { FileName = "sort.exe", StandardInput = "b\r\nA\r\n" };
        var lines = new List<OutputLine>();
        await using var process = ProcessRunner.Start(launch);
        await foreach (var line in process.ReadLinesAsync().WithCancellation(CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(0, await process.WaitForExitAsync());
        Assert.Equal(["A", "b"], lines.Where(l => l.Stream == OutputStream.StdOut).Select(l => l.Text));
    }

    [Fact]
    public async Task Cancel_kills_the_whole_tree()
    {
        var launch = new ProcessLaunch { FileName = "cmd.exe", Arguments = ["/d", "/c", "ping -n 60 127.0.0.1 >nul"] };
        await using var process = ProcessRunner.Start(launch);
        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in process.ReadLinesAsync(cts.Token))
            {
                Assert.NotNull(unused.Text);
            }
        });

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(wait.Token);
        Assert.True(process.HasExited);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Run_to_end_reports_timeout()
    {
        var launch = new ProcessLaunch { FileName = "cmd.exe", Arguments = ["/d", "/c", "ping -n 60 127.0.0.1 >nul"] };
        var result = await ProcessRunner.RunToEndAsync(launch, TimeSpan.FromMilliseconds(500));
        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Environment_changes_are_applied()
    {
        var launch = new ProcessLaunch
        {
            FileName = "cmd.exe",
            Arguments = ["/d", "/c", "echo %RCP_TEST_VALUE%"],
            Environment = new Dictionary<string, string?> { ["RCP_TEST_VALUE"] = "set-by-test" },
        };
        var result = await ProcessRunner.RunToEndAsync(launch, Timeout);
        Assert.Equal("set-by-test", result.StandardOutput.Trim());
    }
}

public sealed class CommandLocatorTests
{
    [Fact]
    public void Unwraps_native_exe_shim()
    {
        using var dir = new TestDirectory();
        var target = dir.File(@"node_modules\@vendor\tool\bin\tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, string.Empty);
        var shim = dir.File("tool.cmd");
        File.WriteAllText(shim, "@ECHO off\r\nGOTO start\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%dp0%\\node_modules\\@vendor\\tool\\bin\\tool.exe\"   %*\r\n");

        var resolved = CommandLocator.FromFile(shim);
        Assert.Equal(target, resolved.FileName, ignoreCase: true);
        Assert.Empty(resolved.PrefixArguments);
        Assert.Equal(shim, resolved.SourcePath);
    }

    [Fact]
    public void Unwraps_node_script_shim_using_local_node()
    {
        using var dir = new TestDirectory();
        var script = dir.File(@"node_modules\@vendor\cli\bin\cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, string.Empty);
        File.WriteAllText(dir.File("node.exe"), string.Empty);
        var shim = dir.File("cli.cmd");
        File.WriteAllText(shim,
            "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n)\r\n" +
            "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@vendor\\cli\\bin\\cli.js\" %*\r\n");

        var resolved = CommandLocator.FromFile(shim);
        Assert.Equal(dir.File("node.exe"), resolved.FileName, ignoreCase: true);
        Assert.Equal(script, Assert.Single(resolved.PrefixArguments), ignoreCase: true);

        var launch = resolved.ToLaunch(["--version"], dir.Path);
        Assert.Equal([script, "--version"], launch.Arguments);
        Assert.Equal(dir.Path, launch.WorkingDirectory);
    }

    [Fact]
    public void Unknown_batch_file_falls_back_to_cmd()
    {
        using var dir = new TestDirectory();
        var bat = dir.File("x.cmd");
        File.WriteAllText(bat, "@echo hi\r\n");
        var resolved = CommandLocator.FromFile(bat);
        Assert.Equal("cmd.exe", resolved.FileName);
        Assert.Equal(["/d", "/c", bat], resolved.PrefixArguments);
    }

    [Fact]
    public void Resolves_commands_on_path()
    {
        var cmd = CommandLocator.Resolve("cmd");
        Assert.NotNull(cmd);
        Assert.EndsWith("cmd.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(CommandLocator.Resolve("definitely-not-a-real-command-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Sign_in_terminal_command_line_is_quoted_for_cmd()
    {
        var resolved = new ResolvedCommand(@"C:\Program Files\nodejs\node.exe", [@"C:\npm\codex.js"], @"C:\npm\codex.cmd");
        var arguments = TerminalLauncher.BuildCmdArguments(resolved, ["login", "two words"], "Sign in to ChatGPT");
        Assert.Equal("/s /k \"title Sign in to ChatGPT && \"C:\\Program Files\\nodejs\\node.exe\" C:\\npm\\codex.js login \"two words\"\"", arguments);
    }

    [Theory]
    [InlineData("mcp__acad__create_wall", "acad", "create_wall")]
    [InlineData("acad__get_model_info", "acad", "get_model_info")]
    [InlineData("Bash", null, "Bash")]
    [InlineData("mcp__other__thing", null, "mcp__other__thing")]
    public void Tool_names_are_normalised(string raw, string? server, string name)
    {
        var (actualServer, actualName) = ToolNameHelper.Split(raw);
        Assert.Equal(server, actualServer);
        Assert.Equal(name, actualName);
    }
}
