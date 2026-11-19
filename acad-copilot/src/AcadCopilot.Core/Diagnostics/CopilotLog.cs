using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger. Does nothing until <see cref="Initialize"/> is called, so library
/// code can log freely and tests stay silent. The file is trimmed when it grows past ~2 MB.
/// Never throws: logging must not break AutoCAD.
/// </summary>
public static class CopilotLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _alsoStdErr;

    /// <summary>Path of the active log file, or null when logging is off.</summary>
    public static string? FilePath => _path;

    /// <summary>Starts logging to <paramref name="filePath"/>; optionally mirrors lines to stderr (the MCP bridge does).</summary>
    public static void Initialize(string filePath, bool alsoStdErr = false)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxBytes)
                {
                    File.Move(filePath, filePath + ".old", overwrite: true);
                }
            }
            catch (IOException)
            {
                // Logging stays best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging stays best-effort.
            }

            _path = filePath;
            _alsoStdErr = alsoStdErr;
        }
    }

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    /// <summary>Writes an error line with the exception.</summary>
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var path = _path;
        if (path is null && !_alsoStdErr)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);
        if (ex is not null)
        {
            line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (level == "ERROR")
            {
                line.AppendLine().Append(ex);
            }
        }

        var text = line.AppendLine().ToString();
        lock (Gate)
        {
            try
            {
                if (path is not null)
                {
                    // FileShare.ReadWrite: several bridge processes may append to the same file.
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                }

                if (_alsoStdErr)
                {
                    Console.Error.Write(text);
                }
            }
            catch (IOException)
            {
                // Best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort.
            }
        }
    }
}
