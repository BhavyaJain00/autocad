using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// How the panel answers approval prompts: the risky commands of run_commands (SAVE, QSAVE, SAVEAS, WBLOCK, EXPORT, PLOT,
/// PUBLISH, PURGE, LAYDEL, AUDIT, ERASE ALL) and any tool marked destructive. Blocked commands never run either way.
/// </summary>
public enum ApprovalMode
{
    /// <summary>Show an approval card before every risky step (default).</summary>
    AskDestructive,

    /// <summary>Approve risky steps automatically.</summary>
    AutoApprove,
}

/// <summary>Panel-only preferences (no secrets), stored next to the backend settings.</summary>
public sealed record PanelSettings
{
    /// <summary>Approval behaviour for risky steps.</summary>
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.AskDestructive;

    /// <summary>Send the message as soon as the speaker stops (voice input); off = the words wait in the message box.</summary>
    public bool VoiceAutoSend { get; init; }

    /// <summary>Voice input language (BCP-47, "en-IN"); null = the Windows speech / display language.</summary>
    public string? VoiceLanguage { get; init; }
}

/// <summary>Loads/saves <see cref="PanelSettings"/> as JSON (%LOCALAPPDATA%\AcadCopilot\panel.json by default).</summary>
public sealed class PanelSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _gate = new();

    /// <summary>Creates a store for <paramref name="path"/> (defaults to panel.json under <see cref="AppPaths.Root"/>).</summary>
    public PanelSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.Root, "panel.json");
    }

    /// <summary>File path.</summary>
    public string Path { get; }

    /// <summary>Reads the settings; defaults when missing or unreadable.</summary>
    public PanelSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(Path), JsonOptions) ?? new PanelSettings()
                    : new PanelSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                CopilotLog.Warn($"Panel settings '{Path}' unreadable; using defaults", ex);
                return new PanelSettings();
            }
        }
    }

    /// <summary>Writes the settings (errors are logged, not thrown).</summary>
    public void Save(PanelSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopilotLog.Warn($"Could not save panel settings to '{Path}'", ex);
            }
        }
    }
}
