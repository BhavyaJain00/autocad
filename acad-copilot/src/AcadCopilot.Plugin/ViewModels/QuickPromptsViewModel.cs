using System.Collections.ObjectModel;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>One entry of the ⚡ quick-prompt menu (built in or saved by the user).</summary>
public sealed class QuickPromptItem : ObservableObject
{
    private string _title;
    private string _text;

    /// <summary>Creates the entry.</summary>
    public QuickPromptItem(string id, string title, string text, bool isBuiltIn)
    {
        Id = id;
        _title = title;
        _text = text;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>Stable id ("builtin-..." for built-ins).</summary>
    public string Id { get; }

    /// <summary>Menu text.</summary>
    public string Title
    {
        get => _title;
        internal set => Set(ref _title, value);
    }

    /// <summary>The message it puts in the message box.</summary>
    public string Text
    {
        get => _text;
        internal set
        {
            if (Set(ref _text, value))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    /// <summary>True for the prompts that ship with the panel (they cannot be edited or deleted).</summary>
    public bool IsBuiltIn { get; }

    /// <summary>True for the user's own prompts (edit and delete buttons).</summary>
    public bool IsUserPrompt => !IsBuiltIn;

    /// <summary>The full text, for the tool tip.</summary>
    public string ToolTip => Text;
}

/// <summary>
/// The composer's ⚡ quick prompts, shared by every chat tab of the panel: built-in prompts plus the user's own (saved in
/// panel.json, <see cref="PanelSettings.QuickPrompts"/>), with an inline editor to save the current message as a prompt
/// or edit / delete one. UI thread only.
/// </summary>
public sealed class QuickPromptsViewModel : ObservableObject
{
    /// <summary>Most prompts a user can save.</summary>
    public const int MaxSaved = 40;

    /// <summary>Longest menu title.</summary>
    public const int MaxTitleLength = 40;

    /// <summary>Longest prompt text.</summary>
    public const int MaxTextLength = 4000;

    private readonly PanelSettingsStore _store;
    private QuickPromptItem? _editing;
    private bool _isEditorOpen;
    private string _editTitle = string.Empty;
    private string _editText = string.Empty;
    private string? _editError;

    /// <summary>Loads the user's prompts from <paramref name="store"/>.</summary>
    public QuickPromptsViewModel(PanelSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        foreach (var saved in store.Load().QuickPrompts)
        {
            if (!string.IsNullOrWhiteSpace(saved.Text))
            {
                Saved.Add(new QuickPromptItem(saved.Id, Clean(saved.Title, saved.Text), saved.Text, isBuiltIn: false));
            }
        }

        Saved.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSaved));
        CommitEditCommand = new RelayCommand(() => CommitEdit());
        CancelEditCommand = new RelayCommand(CancelEdit);
        EditCommand = new RelayCommand(p => BeginEdit(p as QuickPromptItem), p => p is QuickPromptItem { IsUserPrompt: true });
        DeleteCommand = new RelayCommand(p => Delete(p as QuickPromptItem), p => p is QuickPromptItem { IsUserPrompt: true });
    }

    /// <summary>The prompts that ship with the panel.</summary>
    public IReadOnlyList<QuickPromptItem> BuiltIns { get; } = AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase)
        ?
        [
            new("builtin-3d-robot", "3D Sci-Fi Robot Character",
                "Model a 3D sci-fi robot character in Blender 5.3 using BPY, Bevel and Subdivision Surface modifiers, metallic and glowing emission Principled BSDF materials, and studio lighting.",
                isBuiltIn: true),
            new("builtin-3d-island", "3D Low-Poly Island Scene",
                "Create a 3D low-poly floating island environment in Blender 5.3 with terrain, pine trees, rocks, water plane, sun light, and camera, then capture the 3D viewport.",
                isBuiltIn: true),
            new("builtin-3d-studio", "3D Product Studio & Lighting",
                "Set up a 3D product render studio in Blender 5.3: curved studio backdrop, 3-point area lighting (Key, Fill, Rim), glass and brushed-metal Principled BSDF shaders, and depth-of-field camera.",
                isBuiltIn: true),
            new("builtin-3d-modifiers", "Bevel, Subdivide & Shade Smooth",
                "Apply Bevel and Subdivision Surface modifiers with smooth shading and a clean Principled BSDF material to the selected 3D mesh in Blender 5.3.",
                isBuiltIn: true),
            new("builtin-3d-turntable", "360° Turntable Camera Animation",
                "Create a 360-degree turntable camera animation (frames 1 to 120) orbiting the scene center in Blender 5.3.",
                isBuiltIn: true),
            new("builtin-review", "Review this 3D Blender scene", ChatViewModel.ReviewPrompt, isBuiltIn: true),
            new("builtin-export-png", "Render & Export 3D Viewport PNG",
                "Frame the 3D scene nicely and export a high-resolution PNG render to my exports folder with export_view_png.",
                isBuiltIn: true),
        ]
        :
        [
            new("builtin-plan-30x40", "30x40 G+1 standard plan",
                "Draw a standard G+1 house for a 30' x 40' east-facing plot. Ground floor: car parking, living, kitchen, 1 bedroom, 1 toilet and the staircase. First floor: 2 bedrooms with attached toilets, a family lounge and a balcony. Show me the room schedule first and wait for \"Draw it\".",
                isBuiltIn: true),
            new("builtin-dimensions", "Add dimensions",
                "Add dimensions to this plan: overall sizes, every room and the wall openings, on layer A-ANNO-DIMS with the ARCH-48 style.",
                isBuiltIn: true),
            // Exactly this short text: the session recognises it and runs a second-opinion review by another AI.
            new("builtin-review", "Review this plan", ChatViewModel.ReviewPrompt, isBuiltIn: true),
            new("builtin-learn-style", "Learn my style from this drawing", ChatViewModel.LearnStylePrompt, isBuiltIn: true),
            new("builtin-plot-pdf", "Plot to PDF",
                "Plot the sheets to one PDF with plot_to_pdf (create the sheets with create_sheets first if there are none).",
                isBuiltIn: true),
            new("builtin-electrical-draft", "Electrical layout (draft)",
                "Draw a first-draft electrical layout of this floor plan for my electrician: switchboards, lights, fans, sockets, AC and geyser points, DB, with the legend and the draft note.",
                isBuiltIn: true),
            new("builtin-colour-elevation", "Colour elevation + PNG",
                "Make a colour presentation version of this elevation (soft palette, sky, ground and shadows) and export it as a PNG I can share.",
                isBuiltIn: true),
            new("builtin-full-set", "Full drawing set from this plan",
                "Make the full drawing set from this approved ground floor plan: plan it with plan_full_set, then draft the other floors, front, rear and side elevations, the section through the stair, the site plan, furniture layouts, the door/window schedule and the area statement stage by stage, then arrange_drawings and create_sheets (A3). Ask me before the PDF.",
                isBuiltIn: true),
        ];

    /// <summary>The user's own prompts, in menu order.</summary>
    public ObservableCollection<QuickPromptItem> Saved { get; } = [];

    /// <summary>True when the user has saved prompts.</summary>
    public bool HasSaved => Saved.Count > 0;

    /// <summary>True while the inline editor (save / edit) is shown in the menu.</summary>
    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        private set
        {
            if (Set(ref _isEditorOpen, value))
            {
                OnPropertiesChanged(nameof(EditorHeader), nameof(IsMenuListVisible));
            }
        }
    }

    /// <summary>True when the list (not the editor) is shown.</summary>
    public bool IsMenuListVisible => !IsEditorOpen;

    /// <summary>"Save as quick prompt" or "Edit quick prompt".</summary>
    public string EditorHeader => _editing is null ? "Save as quick prompt" : "Edit quick prompt";

    /// <summary>Title being edited.</summary>
    public string EditTitle
    {
        get => _editTitle;
        set => Set(ref _editTitle, value ?? string.Empty);
    }

    /// <summary>Text being edited.</summary>
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value ?? string.Empty);
    }

    /// <summary>Why the edit cannot be saved, or null.</summary>
    public string? EditError
    {
        get => _editError;
        private set
        {
            if (Set(ref _editError, value))
            {
                OnPropertyChanged(nameof(HasEditError));
            }
        }
    }

    /// <summary>True when <see cref="EditError"/> is shown.</summary>
    public bool HasEditError => EditError is not null;

    /// <summary>Saves the editor's prompt.</summary>
    public RelayCommand CommitEditCommand { get; }

    /// <summary>Closes the editor without saving.</summary>
    public RelayCommand CancelEditCommand { get; }

    /// <summary>Opens the editor for the prompt passed as parameter.</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>Deletes the prompt passed as parameter.</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>Opens the editor for a new prompt with <paramref name="text"/> (the current message).</summary>
    public void BeginSave(string text)
    {
        _editing = null;
        EditText = (text ?? string.Empty).Trim();
        EditTitle = ChatViewModel.TitleFrom(EditText);
        EditError = Saved.Count >= MaxSaved ? $"At most {MaxSaved} quick prompts: delete one first." : null;
        IsEditorOpen = true;
        OnPropertyChanged(nameof(EditorHeader));
    }

    /// <summary>Opens the editor for <paramref name="item"/> (a user prompt).</summary>
    public void BeginEdit(QuickPromptItem? item)
    {
        if (item is not { IsUserPrompt: true })
        {
            return;
        }

        _editing = item;
        EditTitle = item.Title;
        EditText = item.Text;
        EditError = null;
        IsEditorOpen = true;
        OnPropertyChanged(nameof(EditorHeader));
    }

    /// <summary>Saves the editor's title and text (a new prompt or the one edited). False (with <see cref="EditError"/>) when invalid.</summary>
    public bool CommitEdit()
    {
        var text = EditText.Trim();
        if (text.Length == 0)
        {
            EditError = "Type the message the prompt should put in the message box.";
            return false;
        }

        if (text.Length > MaxTextLength)
        {
            EditError = $"At most {MaxTextLength} characters.";
            return false;
        }

        var title = Clean(EditTitle, text);
        if (_editing is { } item)
        {
            item.Title = title;
            item.Text = text;
        }
        else
        {
            if (Saved.Count >= MaxSaved)
            {
                EditError = $"At most {MaxSaved} quick prompts: delete one first.";
                return false;
            }

            Saved.Add(new QuickPromptItem(Guid.NewGuid().ToString("N"), title, text, isBuiltIn: false));
        }

        Persist();
        CancelEdit();
        return true;
    }

    /// <summary>Closes the editor.</summary>
    public void CancelEdit()
    {
        _editing = null;
        EditError = null;
        IsEditorOpen = false;
    }

    /// <summary>Deletes a user prompt.</summary>
    public void Delete(QuickPromptItem? item)
    {
        if (item is { IsUserPrompt: true } && Saved.Remove(item))
        {
            if (ReferenceEquals(_editing, item))
            {
                CancelEdit();
            }

            Persist();
        }
    }

    /// <summary>Moves a user prompt to <paramref name="index"/> (menu order).</summary>
    public void Move(QuickPromptItem item, int index)
    {
        var from = Saved.IndexOf(item);
        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Saved.Count - 1);
        if (index != from)
        {
            Saved.Move(from, index);
            Persist();
        }
    }

    private static string Clean(string? title, string text)
    {
        var line = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
        {
            line = ChatViewModel.TitleFrom(text);
        }

        return line.Length > MaxTitleLength ? line[..MaxTitleLength].TrimEnd() + "…" : line;
    }

    // Read-modify-write, so the other panel settings in panel.json stay as they are.
    private void Persist() =>
        _store.Save(_store.Load() with { QuickPrompts = [.. Saved.Select(s => new SavedQuickPrompt(s.Id, s.Title, s.Text))] });
}
