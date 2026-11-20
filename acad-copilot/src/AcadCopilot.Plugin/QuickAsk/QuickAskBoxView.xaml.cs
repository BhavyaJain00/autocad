using System.Windows.Controls;
using System.Windows.Input;
using AcadCopilot.Plugin.Resources;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>
/// The Quick Ask box (DataContext: <see cref="QuickAskBoxViewModel"/>). Enter sends, Esc stops the mic or closes,
/// Ctrl+Space starts / stops the mic (as in the panel). Rendered off-screen in the UI tests.
/// </summary>
public partial class QuickAskBoxView : UserControl
{
    /// <summary>Creates the view in <paramref name="theme"/>.</summary>
    public QuickAskBoxView(PanelTheme theme)
    {
        InitializeComponent();
        ApplyTheme(theme);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The theme applied.</summary>
    public PanelTheme Theme { get; private set; }

    /// <summary>The request's text box.</summary>
    public TextBox InputBox => Input;

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

    /// <summary>Puts the keyboard into the text box, caret at the end.</summary>
    public void FocusInput()
    {
        Input.Focus();
        Keyboard.Focus(Input);
        Input.CaretIndex = Input.Text.Length;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not QuickAskBoxViewModel box)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Enter or Key.Return when (Keyboard.Modifiers & ModifierKeys.Shift) == 0:
                e.Handled = true;
                if (box.Voice is { IsActive: true } listening)
                {
                    // Enter while listening: take the words heard so far, then send.
                    _ = SendAfterVoiceAsync(box, listening);
                }
                else if (box.SendCommand.CanExecute(null))
                {
                    box.SendCommand.Execute(null);
                }

                break;
            case Key.Escape:
                e.Handled = true;
                if (box.Voice is { IsActive: true } voice)
                {
                    _ = voice.StopAsync(allowAutoSend: false);
                }
                else
                {
                    box.CloseCommand.Execute(null);
                }

                break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.Control && box.Voice is { } mic:
                e.Handled = true;
                mic.ToggleCommand.Execute(null);
                break;
        }
    }

    private static async Task SendAfterVoiceAsync(QuickAskBoxViewModel box, ViewModels.VoiceInputViewModel voice)
    {
        await voice.StopAsync(allowAutoSend: false);
        if (box.SendCommand.CanExecute(null))
        {
            box.SendCommand.Execute(null);
        }
    }
}
