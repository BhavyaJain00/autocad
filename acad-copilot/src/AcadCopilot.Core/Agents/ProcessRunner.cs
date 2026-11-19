using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>How to start a child process (never with a window).</summary>
public sealed record ProcessLaunch
{
    /// <summary>Executable path (use <see cref="CommandLocator"/> to resolve npm shims like "claude").</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one per element (quoted automatically).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory; null = current.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment changes: a value sets the variable, null removes it. Everything else is inherited.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Text written to stdin right after start (UTF-8).</summary>
    public string? StandardInput { get; init; }

    /// <summary>
    /// When false (default) stdin is closed right after <see cref="StandardInput"/> is written, so CLIs that read
    /// piped stdin never wait for it. When true, use <see cref="RunningProcess.WriteLineAsync"/> and
    /// <see cref="RunningProcess.CloseStandardInput"/> (e.g. Claude Code --input-format stream-json).
    /// </summary>
    public bool KeepStandardInputOpen { get; init; }
}

/// <summary>Which stream a line came from.</summary>
public enum OutputStream
{
    /// <summary>Standard output.</summary>
    StdOut,

    /// <summary>Standard error.</summary>
    StdErr,
}

/// <summary>One line of child-process output.</summary>
/// <param name="Stream">Source stream.</param>
/// <param name="Text">Line text without the newline.</param>
public readonly record struct OutputLine(OutputStream Stream, string Text);

/// <summary>Captured result of <see cref="ProcessRunner.RunToEndAsync"/>.</summary>
/// <param name="ExitCode">Exit code (-1 when killed on timeout).</param>
/// <param name="StandardOutput">All stdout text.</param>
/// <param name="StandardError">All stderr text.</param>
/// <param name="TimedOut">True when the process was killed because it ran too long.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Starts child processes for the CLI runners: no window (CreateNoWindow), stdout/stderr streamed line by line
/// as UTF-8, the whole process tree killed on cancel/dispose, and every child placed in a Windows job object that
/// kills it automatically if AutoCAD exits or crashes.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Starts the process. Dispose the result to make sure the tree is gone.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    public static RunningProcess Start(ProcessLaunch launch)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
            WorkingDirectory = launch.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        return new RunningProcess(process, launch);
    }

    /// <summary>Runs a short command to completion and captures its output (for --version, status checks).</summary>
    public static async Task<ProcessResult> RunToEndAsync(ProcessLaunch launch, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await using var running = Start(launch);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await foreach (var line in running.ReadLinesAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                (line.Stream == OutputStream.StdOut ? stdout : stderr).AppendLine(line.Text);
            }

            var exitCode = await running.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            running.Kill();
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
    }

    internal static Encoding Utf8NoBom { get; } = new UTF8Encoding(false);
}

/// <summary>A started child process. Single consumer of <see cref="ReadLinesAsync"/>.</summary>
public sealed class RunningProcess : IAsyncDisposable
{
    private const int StdErrTailLines = 200;
    private readonly Process _process;
    private readonly Channel<OutputLine> _lines = Channel.CreateUnbounded<OutputLine>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> _stdErrTail = new();
    private readonly Lock _tailGate = new();
    private readonly ChildProcessJob? _job;
    private int _openStreams = 2;
    private int _disposed;

    internal RunningProcess(Process process, ProcessLaunch launch)
    {
        _process = process;
        _process.OutputDataReceived += (_, e) => OnData(OutputStream.StdOut, e.Data);
        _process.ErrorDataReceived += (_, e) => OnData(OutputStream.StdErr, e.Data);
        if (!_process.Start())
        {
            throw new InvalidOperationException($"Could not start '{launch.FileName}'.");
        }

        Id = _process.Id;
        StartedAt = DateTimeOffset.UtcNow;
        _job = ChildProcessJob.TryCreateFor(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        if (launch.StandardInput is not null)
        {
            try
            {
                _process.StandardInput.Write(launch.StandardInput);
                _process.StandardInput.Flush();
            }
            catch (IOException ex)
            {
                CopilotLog.Warn($"Could not write stdin of process {Id} (it probably exited)", ex);
            }
        }

        if (!launch.KeepStandardInputOpen)
        {
            CloseStandardInput();
        }

        CopilotLog.Info($"Started process {Id}: {launch.FileName} ({launch.Arguments.Count} args)");
    }

    /// <summary>OS process id.</summary>
    public int Id { get; }

    /// <summary>When the process started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>True once the process has exited.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>The last stderr lines (useful for error messages such as "not logged in").</summary>
    public string StandardErrorTail
    {
        get
        {
            lock (_tailGate)
            {
                return string.Join(Environment.NewLine, _stdErrTail);
            }
        }
    }

    /// <summary>
    /// Yields stdout and stderr lines as they arrive until both streams close. If
    /// <paramref name="cancellationToken"/> fires, the process tree is killed and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public async IAsyncEnumerable<OutputLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            bool more;
            try
            {
                more = await _lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill explicitly: a token registration could be unregistered before it runs when the channel
                // resumes this iterator inline during Cancel().
                Kill();
                throw;
            }

            if (!more)
            {
                yield break;
            }

            while (_lines.Reader.TryRead(out var line))
            {
                yield return line;
            }
        }
    }

    /// <summary>Waits for exit and returns the exit code. Cancelling kills the tree.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }

        return _process.ExitCode;
    }

    /// <summary>Writes one line to stdin (only when started with KeepStandardInputOpen).</summary>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes stdin (signals EOF).</summary>
    public void CloseStandardInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Process already gone.
        }
        catch (InvalidOperationException)
        {
            // Not redirected / already closed.
        }
    }

    /// <summary>Kills the process and all its children (no-op when already exited).</summary>
    public void Kill()
    {
        // The job holds the whole tree, including children spawned just before the kill.
        if (_job?.Terminate() == true)
        {
            CopilotLog.Info($"Terminated job of process {Id}.");
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                CopilotLog.Info($"Killed process tree {Id}.");
            }
        }
        catch (InvalidOperationException)
        {
            // Exited meanwhile.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CopilotLog.Warn($"Could not kill process {Id}", ex);
        }
    }

    /// <summary>Kills the tree if still running and releases the process handle.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Kill();
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CopilotLog.Warn($"Process {Id} did not exit within 5 s after kill.");
        }
        catch (InvalidOperationException)
        {
            // Never started.
        }

        _lines.Writer.TryComplete();
        _job?.Dispose();
        _process.Dispose();
    }

    private void OnData(OutputStream stream, string? data)
    {
        if (data is null)
        {
            if (Interlocked.Decrement(ref _openStreams) == 0)
            {
                _lines.Writer.TryComplete();
            }

            return;
        }

        if (stream == OutputStream.StdErr)
        {
            lock (_tailGate)
            {
                _stdErrTail.Enqueue(data);
                while (_stdErrTail.Count > StdErrTailLines)
                {
                    _stdErrTail.Dequeue();
                }
            }
        }

        _lines.Writer.TryWrite(new OutputLine(stream, data));
    }
}
