using System.ComponentModel;
using System.Runtime.CompilerServices;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>Static facts about one official CLI (names, npm package, how its users sign in).</summary>
/// <param name="DisplayName">Provider name shown in the panel ("Claude", "ChatGPT", "Gemini").</param>
/// <param name="CliName">Product name of the CLI ("Claude Code", "Codex CLI", "Gemini CLI").</param>
/// <param name="Command">Command looked up with <see cref="CommandLocator"/> ("claude", "codex", "gemini").</param>
/// <param name="NpmPackage">npm package to install or update ("@anthropic-ai/claude-code").</param>
/// <param name="AccountKind">Account the user signs in with ("Claude", "ChatGPT", "Google").</param>
/// <param name="SignInSteps">What to do in the sign-in window; null = the generic text.</param>
internal sealed record CliProfile(string DisplayName, string CliName, string Command, string NpmPackage, string AccountKind, string? SignInSteps = null)
{
    /// <summary>Message shown when the CLI is not installed.</summary>
    public string NotInstalledMessage =>
        $"{CliName} is not installed (the '{Command}' command was not found). Install it with: npm install -g {NpmPackage} " +
        $"then sign in with your {AccountKind} account and reopen the panel.";

    /// <summary>Message shown when the CLI reports that it is not signed in.</summary>
    public string NotSignedInMessage(string? detail) =>
        $"{CliName} is not signed in{(string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")")}. " +
        (SignInSteps ?? $"Click \"Sign in\" to log in with your {AccountKind} account in the window that opens") + ", then send your message again.";
}

/// <summary>Test seams and tunables shared by the CLI runners. The public runners use the defaults.</summary>
internal sealed record CliRunnerOptions
{
    /// <summary>Resolves a command name to an executable (default <see cref="CommandLocator.Resolve"/>).</summary>
    public Func<string, ResolvedCommand?> Resolve { get; init; } = CommandLocator.Resolve;

    /// <summary>Working directory for status commands; null = %LOCALAPPDATA%\AcadCopilot\workspace.</summary>
    public string? StatusWorkingDirectory { get; init; }

    /// <summary>User profile folder used to find CLI config/credential files; null = the real profile.</summary>
    public string? UserHome { get; init; }

    /// <summary>Reads environment variables (tests replace it).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Timeout of each status command (--version, auth status).</summary>
    public TimeSpan StatusTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the CLI may keep running after it reported the end of the turn before it is killed.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The user's home folder.</summary>
    public string Home => string.IsNullOrWhiteSpace(UserHome) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : UserHome;

    /// <summary>Folder for status commands (created when needed).</summary>
    public string StatusDirectory => AppPaths.Ensure(StatusWorkingDirectory ?? AppPaths.Workspace);
}

/// <summary>Short, read-only CLI commands for status checks (--version, auth status).</summary>
internal static partial class CliCommands
{
    /// <summary>Runs a short command in the status folder; null when it could not be started.</summary>
    public static async Task<ProcessResult?> RunAsync(
        ResolvedCommand command,
        IReadOnlyList<string> arguments,
        CliRunnerOptions options,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var launch = command.ToLaunch(arguments, options.StatusDirectory) with { Environment = environment };
        try
        {
            return await Task.Run(() => ProcessRunner.RunToEndAsync(launch, options.StatusTimeout, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            CopilotLog.Warn($"Could not run {command.SourcePath} {string.Join(' ', arguments)}", ex);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            CopilotLog.Warn($"Could not run {command.SourcePath} {string.Join(' ', arguments)}", ex);
            return null;
        }
    }

    /// <summary>Extracts "1.2.3" (optionally with a pre-release suffix) from version output.</summary>
    public static string? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>The file that identifies the CLI (its .exe or .js entry point, not node.exe).</summary>
    public static string ExecutableOf(ResolvedCommand command) =>
        command.PrefixArguments.Count > 0 ? command.PrefixArguments[^1] : command.FileName;

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VersionPattern();
}

/// <summary>One CLI process start: the launch, the text piped to stdin, and temp files to delete afterwards.</summary>
/// <param name="Launch">Process launch (KeepStandardInputOpen is forced on by the engine).</param>
/// <param name="StandardInput">Text written to stdin (the user's prompt); stdin is closed afterwards.</param>
/// <param name="TempPaths">Files or folders created for this turn only.</param>
internal sealed record CliInvocation(ProcessLaunch Launch, string StandardInput, IReadOnlyList<string> TempPaths);

/// <summary>
/// Turns one CLI's JSON-lines output into <see cref="AgentEvent"/>s. Stateful: one instance per process run.
/// Terminal events (<see cref="TurnCompleted"/> / <see cref="TurnFailed"/>) may be returned from
/// <see cref="ParseLine"/>; the engine keeps the first one and ends the turn with it.
/// </summary>
internal abstract class CliStreamParser
{
    private const int StdErrTailLines = 60;
    private readonly Queue<string> _stdErr = new();
    private readonly string _callTag = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Creates the parser.</summary>
    protected CliStreamParser(CliProfile profile, string serverName)
    {
        Profile = profile;
        ServerName = serverName;
    }

    /// <summary>The CLI.</summary>
    public CliProfile Profile { get; }

    /// <summary>MCP server name of the AutoCAD bridge ("acad").</summary>
    public string ServerName { get; }

    /// <summary>True when the CLI said the session to resume does not exist (the engine then starts a new one).</summary>
    public virtual bool IsMissingSession => false;

    /// <summary>The last stderr lines.</summary>
    public string StandardErrorTail => string.Join("\n", _stdErr);

    /// <summary>Parses one stdout line (non-JSON lines are ignored).</summary>
    public abstract IEnumerable<AgentEvent> ParseLine(string line);

    /// <summary>Records one stderr line.</summary>
    public virtual void ObserveStdErr(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _stdErr.Enqueue(line);
        while (_stdErr.Count > StdErrTailLines)
        {
            _stdErr.Dequeue();
        }
    }

    /// <summary>Events still buffered when the output ended without a terminal event (e.g. pending text).</summary>
    public virtual IEnumerable<AgentEvent> Flush() => [];

    /// <summary>The terminal event when the process ended without reporting one.</summary>
    public virtual AgentEvent CreateFallbackTerminal(int exitCode)
    {
        var detail = CliText.LastMeaningfulLines(StandardErrorTail);
        if (exitCode == 0 && detail.Length == 0)
        {
            return new TurnCompleted("exit");
        }

        return CliText.Failure(Profile, detail.Length > 0 ? detail : $"{Profile.CliName} exited with code {exitCode}.");
    }

    /// <summary>Makes a CLI tool-call id unique across turns (some CLIs restart their ids at item_0 every run).</summary>
    protected string CallId(string raw) => $"{_callTag}:{raw}";
}

/// <summary>
/// Runs one turn of a CLI: starts the process without a window, pipes the prompt to stdin, streams stdout through a
/// <see cref="CliStreamParser"/>, ends with exactly one terminal event, kills the process tree on Stop, and starts a
/// fresh conversation once when the CLI no longer knows the session to resume.
/// </summary>
internal static class CliTurnEngine
{
    /// <summary>Runs the turn.</summary>
    /// <param name="profile">CLI facts.</param>
    /// <param name="resumeSessionId">Session to continue, or null.</param>
    /// <param name="build">Builds the process start for a session id (null = new conversation).</param>
    /// <param name="createParser">Creates the stream parser for one run.</param>
    /// <param name="options">Tunables.</param>
    /// <param name="cancellationToken">Stop.</param>
    public static async IAsyncEnumerable<AgentEvent> RunAsync(
        CliProfile profile,
        string? resumeSessionId,
        Func<string?, CliInvocation> build,
        Func<CliStreamParser> createParser,
        CliRunnerOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resume = string.IsNullOrWhiteSpace(resumeSessionId) ? null : resumeSessionId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield return CliText.Stopped;
                yield break;
            }

            CliInvocation? invocation = null;
            AgentEvent? setupFailure = null;
            try
            {
                invocation = build(resume);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                CopilotLog.Error($"Could not prepare the {profile.CliName} turn", ex);
                setupFailure = new TurnFailed($"Could not prepare the {profile.CliName} turn: {ex.Message}", AgentFailureKind.Other);
            }

            if (setupFailure is not null || invocation is null)
            {
                yield return setupFailure ?? new TurnFailed($"Could not prepare the {profile.CliName} turn.", AgentFailureKind.Other);
                yield break;
            }

            var parser = createParser();
            var producedContent = false;
            AgentEvent? terminal = null;
            var pendingSessions = new List<AgentEvent>();
            await foreach (var agentEvent in RunProcessAsync(profile, invocation, parser, options, cancellationToken).ConfigureAwait(false))
            {
                if (agentEvent is TurnCompleted or TurnFailed)
                {
                    terminal ??= agentEvent;
                    continue;
                }

                // Session ids are held back until the CLI shows real progress, so a failed resume (which may echo the
                // unknown id) never replaces the conversation the panel will resume next.
                if (agentEvent is SessionStarted && !producedContent)
                {
                    pendingSessions.Add(agentEvent);
                    continue;
                }

                if (!producedContent)
                {
                    producedContent = true;
                    foreach (var session in pendingSessions)
                    {
                        yield return session;
                    }

                    pendingSessions.Clear();
                }

                yield return agentEvent;
            }

            terminal ??= new TurnFailed($"{profile.CliName} ended without a result.", AgentFailureKind.Other);
            if (resume is not null && terminal is TurnFailed { Kind: not AgentFailureKind.Cancelled } && parser.IsMissingSession && !producedContent)
            {
                CopilotLog.Info($"{profile.CliName} no longer has session {resume}; starting a new conversation.");
                resume = null;
                continue;
            }

            if (!parser.IsMissingSession)
            {
                foreach (var session in pendingSessions)
                {
                    yield return session;
                }
            }

            yield return terminal;
            yield break;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> RunProcessAsync(
        CliProfile profile,
        CliInvocation invocation,
        CliStreamParser parser,
        CliRunnerOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var launch = invocation.Launch with { KeepStandardInputOpen = true, StandardInput = null };
        RunningProcess? process = null;
        AgentEvent? startFailure = null;
        try
        {
            // Off the caller's thread: the panel may consume this stream on AutoCAD's UI thread.
            process = await Task.Run(() => ProcessRunner.Start(launch), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            startFailure = CliText.Stopped;
        }
        catch (Win32Exception ex)
        {
            CopilotLog.Error($"Could not start {profile.CliName} ({launch.FileName})", ex);
            startFailure = new TurnFailed($"Could not start {profile.CliName}: {ex.Message}. {profile.NotInstalledMessage}", AgentFailureKind.NotInstalled);
        }
        catch (InvalidOperationException ex)
        {
            CopilotLog.Error($"Could not start {profile.CliName} ({launch.FileName})", ex);
            startFailure = new TurnFailed($"Could not start {profile.CliName}: {ex.Message}", AgentFailureKind.Other);
        }

        if (startFailure is not null || process is null)
        {
            DeleteTempPaths(invocation.TempPaths);
            yield return startFailure ?? new TurnFailed($"Could not start {profile.CliName}.", AgentFailureKind.Other);
            yield break;
        }

        var writer = WriteInputAsync(process, invocation.StandardInput);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            AgentEvent? terminal = null;
            var stopped = false;
            var lines = process.ReadLinesAsync(readCts.Token).GetAsyncEnumerator(readCts.Token);
            try
            {
                while (true)
                {
                    OutputLine line;
                    try
                    {
                        if (!await lines.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        line = lines.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        // Either Stop (the tree was killed) or the exit grace after the final event expired.
                        stopped = cancellationToken.IsCancellationRequested;
                        break;
                    }

                    if (line.Stream == OutputStream.StdErr)
                    {
                        parser.ObserveStdErr(line.Text);
                        continue;
                    }

                    if (terminal is not null)
                    {
                        continue;
                    }

                    List<AgentEvent> parsed;
                    try
                    {
                        parsed = [.. parser.ParseLine(line.Text)];
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
                    {
                        CopilotLog.Warn($"{profile.CliName}: could not interpret an output line", ex);
                        continue;
                    }

                    foreach (var agentEvent in parsed)
                    {
                        if (terminal is not null)
                        {
                            break;
                        }

                        if (agentEvent is TurnCompleted or TurnFailed)
                        {
                            terminal = agentEvent;
                            continue;
                        }

                        yield return agentEvent;
                    }

                    if (terminal is TurnFailed)
                    {
                        // Fail fast (e.g. not signed in): the CLI may keep retrying for a long time.
                        break;
                    }

                    if (terminal is TurnCompleted)
                    {
                        // Let the CLI finish writing its session, but never wait forever.
                        readCts.CancelAfter(options.ExitGrace);
                    }
                }
            }
            finally
            {
                await lines.DisposeAsync().ConfigureAwait(false);
            }

            if (stopped && terminal is null)
            {
                yield return CliText.Stopped;
                yield break;
            }

            if (terminal is null)
            {
                foreach (var pending in parser.Flush())
                {
                    yield return pending;
                }

                var exitCode = await WaitForExitCodeAsync(process, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return CliText.Stopped;
                    yield break;
                }

                terminal = parser.CreateFallbackTerminal(exitCode);
            }

            yield return terminal;
        }
        finally
        {
            process.Kill();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                CopilotLog.Warn($"{profile.CliName}: writing the prompt failed", ex);
            }

            await process.DisposeAsync().ConfigureAwait(false);
            DeleteTempPaths(invocation.TempPaths);
        }
    }

    private static Task WriteInputAsync(RunningProcess process, string text) => Task.Run(async () =>
    {
        try
        {
            await process.WriteLineAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            CopilotLog.Warn($"Could not write the prompt to process {process.Id} (it probably exited)", ex);
        }
        finally
        {
            process.CloseStandardInput();
        }
    });

    private static async Task<int> WaitForExitCodeAsync(RunningProcess process, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>Deletes per-turn temp files/folders (best effort).</summary>
    internal static void DeleteTempPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                CopilotLog.Warn($"Could not delete {path}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                CopilotLog.Warn($"Could not delete {path}", ex);
            }
        }
    }
}
