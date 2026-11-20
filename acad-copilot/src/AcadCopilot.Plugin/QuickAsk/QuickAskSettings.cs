using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.ViewModels;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>Quick Ask preferences (no secrets), kept in %LOCALAPPDATA%\AcadCopilot\quickask.json.</summary>
public sealed record QuickAskSettings
{
    /// <summary>Show the round AI button in the drawing area's top-right corner (default on).</summary>
    public bool ShowButton { get; init; } = true;

    /// <summary>The keyboard shortcut that opens the Quick Ask box ("Ctrl+Shift+Space").</summary>
    public string Shortcut { get; init; } = QuickAskShortcut.Default.ToString();
}

/// <summary>Loads / saves <see cref="QuickAskSettings"/> as JSON (atomic writes; errors are logged, never thrown).</summary>
public sealed class QuickAskSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Lock _gate = new();

    /// <summary>Creates a store for <paramref name="path"/> (default: quickask.json under <see cref="AppPaths.Root"/>).</summary>
    public QuickAskSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.Root, "quickask.json");
    }

    /// <summary>File path.</summary>
    public string Path { get; }

    /// <summary>Reads the settings; defaults when missing or unreadable.</summary>
    public QuickAskSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<QuickAskSettings>(File.ReadAllText(Path), JsonOptions) ?? new QuickAskSettings()
                    : new QuickAskSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                CopilotLog.Warn($"Quick Ask settings '{Path}' unreadable; using defaults", ex);
                return new QuickAskSettings();
            }
        }
    }

    /// <summary>Writes the settings.</summary>
    public void Save(QuickAskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
                CopilotLog.Warn($"Could not save Quick Ask settings to '{Path}'", ex);
            }
        }
    }
}

/// <summary>Modifier keys of a <see cref="QuickAskShortcut"/>.</summary>
[Flags]
public enum ShortcutModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Ctrl.</summary>
    Ctrl = 1,

    /// <summary>Shift.</summary>
    Shift = 2,

    /// <summary>Alt.</summary>
    Alt = 4,
}

/// <summary>
/// The Quick Ask keyboard shortcut: modifiers plus one key (Windows virtual-key code). Pure: parsing, display text and
/// matching a key press. Ctrl or Alt is required (a plain key or Shift+key would swallow typing), and the clipboard /
/// undo / file shortcuts AutoCAD users press all day are refused.
/// </summary>
/// <param name="Modifiers">Ctrl / Shift / Alt.</param>
/// <param name="VirtualKey">Windows virtual-key code of the key (0x20 = Space).</param>
/// <param name="KeyName">Display name of the key ("Space", "Q", "F2").</param>
public sealed record QuickAskShortcut(ShortcutModifiers Modifiers, int VirtualKey, string KeyName)
{
    /// <summary>Ctrl+Shift+Space.</summary>
    public static readonly QuickAskShortcut Default = new(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, 0x20, "Space");

    // Shortcuts people press all day in AutoCAD (and the panel's own Ctrl+Space for the mic): never taken over.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+S", "Ctrl+O", "Ctrl+N", "Ctrl+P", "Ctrl+Q",
        "Ctrl+Space", "Ctrl+Shift+S", "Ctrl+Shift+C", "Ctrl+Shift+V", "Alt+F4", "Alt+Space", "Ctrl+Alt+Delete",
    };

    /// <summary>"Ctrl+Shift+Space".</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ShortcutModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ShortcutModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ShortcutModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    /// <summary>True when a key press of <paramref name="virtualKey"/> with these modifier states is this shortcut.</summary>
    public bool Matches(int virtualKey, bool ctrl, bool shift, bool alt) =>
        virtualKey == VirtualKey
        && ctrl == Modifiers.HasFlag(ShortcutModifiers.Ctrl)
        && shift == Modifiers.HasFlag(ShortcutModifiers.Shift)
        && alt == Modifiers.HasFlag(ShortcutModifiers.Alt);

    /// <summary>
    /// Parses "Ctrl+Shift+Space", "ctrl + alt + q", "Ctrl-Shift-F2". Keys: Space, A-Z, 0-9, F1-F12. False with a reason
    /// the settings can show when it is not usable.
    /// </summary>
    public static bool TryParse(string? text, out QuickAskShortcut shortcut, out string? error)
    {
        shortcut = Default;
        error = null;
        var parts = (text ?? string.Empty)
            .Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0)
        {
            error = "Type a shortcut such as Ctrl+Shift+Space.";
            return false;
        }

        var modifiers = ShortcutModifiers.None;
        string? keyName = null;
        var key = 0;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL" or "CTL" or "STRG":
                    modifiers |= ShortcutModifiers.Ctrl;
                    continue;
                case "SHIFT":
                    modifiers |= ShortcutModifiers.Shift;
                    continue;
                case "ALT":
                    modifiers |= ShortcutModifiers.Alt;
                    continue;
            }

            if (keyName is not null)
            {
                error = $"Use one key with Ctrl / Shift / Alt (\"{text?.Trim()}\" has more than one).";
                return false;
            }

            if (!TryKey(part, out key, out keyName))
            {
                error = $"\"{part}\" is not a key the shortcut can use (Space, A-Z, 0-9 or F1-F12).";
                return false;
            }
        }

        if (keyName is null)
        {
            error = "Add a key after the modifiers, for example Ctrl+Shift+Space.";
            return false;
        }

        if (!modifiers.HasFlag(ShortcutModifiers.Ctrl) && !modifiers.HasFlag(ShortcutModifiers.Alt))
        {
            error = "The shortcut needs Ctrl or Alt, so it never gets in the way of typing.";
            return false;
        }

        var candidate = new QuickAskShortcut(modifiers, key, keyName);
        if (Reserved.Contains(candidate.ToString()))
        {
            error = $"{candidate} is already used by AutoCAD or Windows. Choose another one (default Ctrl+Shift+Space).";
            return false;
        }

        shortcut = candidate;
        return true;
    }

    private static bool TryKey(string part, out int key, out string name)
    {
        var upper = part.ToUpperInvariant();
        key = 0;
        name = upper;
        if (upper is "SPACE" or "SPACEBAR")
        {
            key = 0x20;
            name = "Space";
            return true;
        }

        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            key = upper[0];
            return true;
        }

        if (upper.Length is 2 or 3 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 12)
        {
            key = 0x70 + number - 1;
            return true;
        }

        return false;
    }
}

/// <summary>
/// The Quick Ask settings shared by the floating button, the shortcut and the panel's Settings section (one instance per
/// AutoCAD session, <see cref="Shared"/>). Changes are saved at once. UI thread only.
/// </summary>
public sealed class QuickAskOptions : ObservableObject
{
    private static readonly Lazy<QuickAskOptions> SharedInstance = new(() => new QuickAskOptions(new QuickAskSettingsStore()));

    private readonly QuickAskSettingsStore _store;
    private QuickAskSettings _settings;
    private QuickAskShortcut _shortcut;
    private string _shortcutText;
    private string? _shortcutError;

    /// <summary>Creates the options over <paramref name="store"/>.</summary>
    public QuickAskOptions(QuickAskSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _settings = store.Load();
        _shortcut = QuickAskShortcut.TryParse(_settings.Shortcut, out var parsed, out _) ? parsed : QuickAskShortcut.Default;
        _shortcutText = _shortcut.ToString();
    }

    /// <summary>The session's options (quickask.json in %LOCALAPPDATA%\AcadCopilot).</summary>
    public static QuickAskOptions Shared => SharedInstance.Value;

    /// <summary>Show the floating AI button in the drawing area (persisted, default on).</summary>
    public bool ShowButton
    {
        get => _settings.ShowButton;
        set
        {
            if (_settings.ShowButton != value)
            {
                Save(_settings with { ShowButton = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The shortcut in use.</summary>
    public QuickAskShortcut Shortcut
    {
        get => _shortcut;
        private set
        {
            if (Set(ref _shortcut, value))
            {
                OnPropertyChanged(nameof(ShortcutHint));
            }
        }
    }

    /// <summary>
    /// The shortcut as typed in the settings. A valid one is used and saved at once; an invalid one keeps the previous
    /// shortcut and shows <see cref="ShortcutError"/>.
    /// </summary>
    public string ShortcutText
    {
        get => _shortcutText;
        set
        {
            value ??= string.Empty;
            if (!Set(ref _shortcutText, value))
            {
                return;
            }

            if (QuickAskShortcut.TryParse(value, out var parsed, out var error))
            {
                ShortcutError = null;
                Shortcut = parsed;
                if (!string.Equals(_settings.Shortcut, parsed.ToString(), StringComparison.Ordinal))
                {
                    Save(_settings with { Shortcut = parsed.ToString() });
                }
            }
            else
            {
                ShortcutError = error;
            }
        }
    }

    /// <summary>Why the typed shortcut cannot be used, or null.</summary>
    public string? ShortcutError
    {
        get => _shortcutError;
        private set
        {
            if (Set(ref _shortcutError, value))
            {
                OnPropertyChanged(nameof(HasShortcutError));
            }
        }
    }

    /// <summary>True when <see cref="ShortcutError"/> is shown.</summary>
    public bool HasShortcutError => ShortcutError is not null;

    /// <summary>"Ctrl+Shift+Space, or type AI on the command line".</summary>
    public string ShortcutHint => $"{Shortcut}, or type AI on the command line";

    /// <summary>Puts the shortcut back to Ctrl+Shift+Space.</summary>
    public void ResetShortcut() => ShortcutText = QuickAskShortcut.Default.ToString();

    private void Save(QuickAskSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }
}
