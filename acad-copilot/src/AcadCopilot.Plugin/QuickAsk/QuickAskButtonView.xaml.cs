using System.Windows.Controls;
using AcadCopilot.Plugin.Resources;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>
/// The floating AI button plus the status bubble of the last Quick Ask (DataContext: <see cref="QuickAskController"/>).
/// Nothing in it takes keyboard focus. Rendered off-screen in the UI tests.
/// </summary>
public partial class QuickAskButtonView : UserControl
{
    /// <summary>Creates the view in <paramref name="theme"/>.</summary>
    public QuickAskButtonView(PanelTheme theme)
    {
        InitializeComponent();
        ApplyTheme(theme);
        Bubble.MouseEnter += (_, _) => SetPointerOver(true);
        Bubble.MouseLeave += (_, _) => SetPointerOver(false);
    }

    /// <summary>The theme applied.</summary>
    public PanelTheme Theme { get; private set; }

    /// <summary>Re-colors the view (AutoCAD's light / dark theme).</summary>
    public void ApplyTheme(PanelTheme theme)
    {
        var dictionaries = Resources.MergedDictionaries;
        var palette = PanelThemes.Create(theme);
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = palette;
        }
        else
        {
            dictionaries.Add(palette);
        }

        Theme = theme;
    }

    private void SetPointerOver(bool over)
    {
        if (DataContext is QuickAskController controller)
        {
            controller.Status.IsPointerOver = over;
        }
    }
}
