namespace AcadCopilot.Core;

/// <summary>
/// Well-known per-user folders of AI Copilot for AutoCAD, all under %LOCALAPPDATA%\AcadCopilot.
/// Set the environment variable <see cref="HomeEnvironmentVariable"/> to relocate everything
/// (tests do this so they never touch the real folders).
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable that overrides <see cref="Root"/>.</summary>
    public const string HomeEnvironmentVariable = "ACADCOPILOT_HOME";

    /// <summary>Root folder: %LOCALAPPDATA%\AcadCopilot (or the override).</summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcadCopilot");
        }
    }

    /// <summary>Discovery files of running AutoCAD instances: instances\&lt;pid&gt;.json.</summary>
    public static string Instances => Path.Combine(Root, "instances");

    /// <summary>Log files (addin.log, bridge.log).</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// Private working folder for the CLI agents (their cwd). Runners may create per-provider
    /// sub-folders here (for example workspace\gemini\.gemini\settings.json).
    /// </summary>
    public static string Workspace => Path.Combine(Root, "workspace");

    /// <summary>
    /// The local knowledge base the AI reads every turn and writes to (rules.md, style.md, lessons.md, recipes\), plain
    /// Markdown the user can open and edit. See AcadCopilot.Core.Learning.KnowledgeStore.
    /// </summary>
    public static string Knowledge => Path.Combine(Root, "knowledge");

    /// <summary>Panel settings (selected provider, models).</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Creates <paramref name="path"/> if needed and returns it.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
