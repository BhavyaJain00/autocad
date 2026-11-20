using System.Text.Json;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Panel;

/// <summary>Persisted panel settings (no secrets: the CLIs keep their own logins).</summary>
public sealed record CopilotSettings
{
    /// <summary>Selected provider.</summary>
    public ProviderId Provider { get; init; } = ProviderId.Claude;

    /// <summary>Selected model per provider, keyed by lower-case provider id ("claude"); missing/null = CLI default.</summary>
    public IReadOnlyDictionary<string, string?> Models { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Drafting speed chosen with the panel's Watch / Fast switch: true = watch mode (a pause after every typed input so
    /// the user can follow each command; the default), false = fast. The panel applies it to
    /// <see cref="Drafting.DraftingPace.Watch"/> when it starts and saves it with <see cref="CopilotSettingsStore.Update"/>.
    /// </summary>
    public bool DraftingWatch { get; init; } = true;

    /// <summary>
    /// AI switching: when the chat's provider hits a usage/rate limit, its sign-in expires or it fails again after a
    /// retry, the same job continues with the next signed-in provider of <see cref="ProviderOrder"/> (default on).
    /// </summary>
    public bool AutoSwitch { get; init; } = true;

    /// <summary>Order in which providers take over a job (default Claude, ChatGPT, Gemini); missing providers follow.</summary>
    public IReadOnlyList<ProviderId> ProviderOrder { get; init; } = DefaultProviderOrder;

    /// <summary>Ask "Shall I draw this?" once before drawing a new plan (default on).</summary>
    public bool AskBeforeDrawing { get; init; } = true;

    /// <summary>Run a second-opinion review automatically after a drawing is finished (default off).</summary>
    public bool AutoReview { get; init; }

    /// <summary>Tutor mode: the AI explains every AutoCAD command it uses in one short line (default off).</summary>
    public bool TutorMode { get; init; }

    /// <summary>
    /// Web research: the AI may use its CLI's read-only web tools (search, read a page) to find ideas on the internet
    /// (default off: no web access at all).
    /// </summary>
    public bool WebResearch { get; init; }

    /// <summary>The default <see cref="ProviderOrder"/>.</summary>
    public static IReadOnlyList<ProviderId> DefaultProviderOrder { get; } = [ProviderId.Claude, ProviderId.Codex, ProviderId.Gemini];

    /// <summary>The feature switches of these settings (not stored twice: the flat values above are).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CopilotFeatures Features => new()
    {
        AutoSwitch = AutoSwitch,
        ProviderOrder = CopilotFeatures.NormalizeOrder(ProviderOrder),
        AskBeforeDrawing = AskBeforeDrawing,
        AutoReview = AutoReview,
        TutorMode = TutorMode,
        WebResearch = WebResearch,
    };

    /// <summary>These settings with <paramref name="features"/> applied.</summary>
    public CopilotSettings With(CopilotFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return this with
        {
            AutoSwitch = features.AutoSwitch,
            ProviderOrder = CopilotFeatures.NormalizeOrder(features.ProviderOrder),
            AskBeforeDrawing = features.AskBeforeDrawing,
            AutoReview = features.AutoReview,
            TutorMode = features.TutorMode,
            WebResearch = features.WebResearch,
        };
    }

    /// <summary>Settings key of a provider.</summary>
    public static string Key(ProviderId provider) => provider.ToString().ToLowerInvariant();
}

/// <summary>Loads/saves <see cref="CopilotSettings"/> as JSON. Corrupt or missing files fall back to defaults.</summary>
public sealed class CopilotSettingsStore
{
    private readonly Lock _gate = new();

    /// <summary>Creates a store for <paramref name="path"/> (defaults to <see cref="AppPaths.SettingsFile"/>).</summary>
    public CopilotSettingsStore(string? path = null)
    {
        Path = path ?? AppPaths.SettingsFile;
    }

    /// <summary>Settings file path.</summary>
    public string Path { get; }

    /// <summary>Reads the settings (defaults when missing or unreadable).</summary>
    public CopilotSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<CopilotSettings>(File.ReadAllText(Path), CopilotJson.Options) ?? new CopilotSettings()
                    : new CopilotSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                CopilotLog.Warn($"Settings file '{Path}' unreadable; using defaults", ex);
                return new CopilotSettings();
            }
        }
    }

    /// <summary>Writes the settings atomically (errors are logged, not thrown).</summary>
    public void Save(CopilotSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, CopilotJson.Indented));
                File.Move(temp, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopilotLog.Warn($"Could not save settings to '{Path}'", ex);
            }
        }
    }

    /// <summary>
    /// Read-modify-write: reads the file (defaults when missing or unreadable), applies <paramref name="change"/> and
    /// saves the result unless <paramref name="change"/> returned the same instance. For a value that one writer owns
    /// while another keeps a whole <see cref="CopilotSettings"/> in memory (the panel's <see cref="CopilotSettings.DraftingWatch"/>
    /// next to the backend's provider and models): only the changed value is written, on top of what is in the file.
    /// </summary>
    /// <returns>The settings now in effect.</returns>
    public CopilotSettings Update(Func<CopilotSettings, CopilotSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var current = Load();
            var updated = change(current) ?? current;
            if (!ReferenceEquals(updated, current))
            {
                Save(updated);
            }

            return updated;
        }
    }
}
