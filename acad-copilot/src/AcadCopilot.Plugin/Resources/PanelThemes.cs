using System.Windows;
using System.Windows.Media;

namespace AcadCopilot.Plugin.Resources;

/// <summary>The panel's color theme (follows AutoCAD's light / dark UI theme, system variable COLORTHEME).</summary>
public enum PanelTheme
{
    /// <summary>AutoCAD light theme (COLORTHEME = 1).</summary>
    Light,

    /// <summary>AutoCAD dark theme (COLORTHEME = 0, AutoCAD's default).</summary>
    Dark,
}

/// <summary>
/// Builds the color palette resource dictionary for a <see cref="PanelTheme"/>. Every style in CopilotPane.xaml uses
/// these keys through DynamicResource, so swapping the dictionary re-themes the whole panel. The tones follow AutoCAD
/// 2027's own palettes (Themes\DarkTheme.xbel / LightTheme.xbel in the AutoCAD folder): the blue-gray #3B4453 palette
/// body, #454F61 lists, #4E5A6E hover and #2E3440 borders in the dark theme; #F2F2F2 palettes, white lists, #D1EBFA hover
/// and the #0160BF accent in the light theme, so the docked palette reads as part of AutoCAD. "Accent" fills buttons;
/// "AccentText" is the accent for text and icons on panel and card backgrounds (lighter in the dark theme, for contrast).
/// "SegmentTrack" / "SegmentSelected" draw the two-way switches (provider tabs, Watch / Fast). "Recording" is the red of
/// the listening mic (white "RecordingForeground" on it, "RecordingSoft" behind the "Listening..." strip, "RecordingText"
/// for red text on panel backgrounds).
/// </summary>
public static class PanelThemes
{
    /// <summary>Creates the palette for <paramref name="theme"/>.</summary>
    public static ResourceDictionary Create(PanelTheme theme)
    {
        var palette = theme == PanelTheme.Dark ? Dark : Light;
        var dictionary = new ResourceDictionary();
        foreach (var (key, color) in palette)
        {
            dictionary[key + "Color"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[key] = brush;
        }

        dictionary["IsDarkTheme"] = theme == PanelTheme.Dark;
        return dictionary;
    }

    private static readonly (string Key, Color Color)[] Light =
    [
        ("PanelBackground", Rgb(0xF2, 0xF2, 0xF2)),
        ("HeaderBackground", Rgb(0xFA, 0xFA, 0xFA)),
        ("SurfaceBackground", Rgb(0xFF, 0xFF, 0xFF)),
        ("SurfaceAltBackground", Rgb(0xE6, 0xE7, 0xE9)),
        ("HoverBackground", Rgb(0xDD, 0xEE, 0xFA)),
        ("PressedBackground", Rgb(0xC3, 0xDC, 0xF4)),
        ("LineBrush", Rgb(0xD8, 0xD8, 0xD8)),
        ("StrongLineBrush", Rgb(0xAE, 0xAE, 0xAE)),
        ("TextPrimary", Rgb(0x19, 0x19, 0x19)),
        ("TextSecondary", Rgb(0x46, 0x4C, 0x54)),
        ("TextMuted", Rgb(0x6E, 0x75, 0x7E)),
        ("Accent", Rgb(0x01, 0x60, 0xBF)),
        ("AccentHover", Rgb(0x01, 0x50, 0x9F)),
        ("AccentForeground", Rgb(0xFF, 0xFF, 0xFF)),
        ("AccentText", Rgb(0x01, 0x60, 0xBF)),
        ("AccentSoft", Rgb(0xE4, 0xF0, 0xFC)),
        ("AccentSoftLine", Rgb(0x9C, 0xC6, 0xEE)),
        ("UserBubble", Rgb(0xDC, 0xEB, 0xFA)),
        ("UserBubbleText", Rgb(0x10, 0x2A, 0x46)),
        ("Success", Rgb(0x1C, 0x8A, 0x3C)),
        ("SuccessSoft", Rgb(0xE3, 0xF4, 0xE8)),
        ("Danger", Rgb(0xC4, 0x2B, 0x2B)),
        ("DangerSoft", Rgb(0xFC, 0xEB, 0xEB)),
        ("DangerLine", Rgb(0xED, 0xB5, 0xB5)),
        ("Warning", Rgb(0x9A, 0x5A, 0x00)),
        ("WarningSoft", Rgb(0xFF, 0xF3, 0xDE)),
        ("WarningLine", Rgb(0xED, 0xCB, 0x8E)),
        ("InputBackground", Rgb(0xFF, 0xFF, 0xFF)),
        ("CodeBackground", Rgb(0xF0, 0xF1, 0xF3)),
        ("ScrollThumb", Rgb(0xBA, 0xBA, 0xBA)),
        ("FocusBrush", Rgb(0x41, 0x95, 0xDB)),
        ("ShadowColorBrush", Rgb(0x10, 0x18, 0x28)),
        ("SegmentTrack", Rgb(0xE4, 0xE5, 0xE8)),
        ("SegmentSelected", Rgb(0xFF, 0xFF, 0xFF)),
        ("Recording", Rgb(0xD9, 0x30, 0x30)),
        ("RecordingForeground", Rgb(0xFF, 0xFF, 0xFF)),
        ("RecordingSoft", Rgb(0xFD, 0xEC, 0xEC)),
        ("RecordingText", Rgb(0xB4, 0x23, 0x23)),
    ];

    private static readonly (string Key, Color Color)[] Dark =
    [
        ("PanelBackground", Rgb(0x3B, 0x44, 0x53)),
        ("HeaderBackground", Rgb(0x35, 0x3D, 0x4B)),
        ("SurfaceBackground", Rgb(0x45, 0x4F, 0x61)),
        ("SurfaceAltBackground", Rgb(0x31, 0x38, 0x45)),
        ("HoverBackground", Rgb(0x4E, 0x5A, 0x6E)),
        ("PressedBackground", Rgb(0x58, 0x65, 0x7B)),
        ("LineBrush", Rgb(0x2E, 0x34, 0x40)),
        ("StrongLineBrush", Rgb(0x6E, 0x7A, 0x8E)),
        ("TextPrimary", Rgb(0xF2, 0xF4, 0xF7)),
        ("TextSecondary", Rgb(0xC5, 0xCD, 0xD8)),
        ("TextMuted", Rgb(0xA2, 0xAC, 0xBA)),
        ("Accent", Rgb(0x06, 0x96, 0xD7)),
        ("AccentHover", Rgb(0x1E, 0xA8, 0xE6)),
        ("AccentForeground", Rgb(0xFF, 0xFF, 0xFF)),
        ("AccentText", Rgb(0x7A, 0xB9, 0xFF)),
        ("AccentSoft", Rgb(0x3D, 0x57, 0x72)),
        ("AccentSoftLine", Rgb(0x4F, 0x86, 0xB3)),
        ("UserBubble", Rgb(0x41, 0x5E, 0x80)),
        ("UserBubbleText", Rgb(0xF3, 0xF7, 0xFC)),
        ("Success", Rgb(0x62, 0xC9, 0x80)),
        ("SuccessSoft", Rgb(0x36, 0x51, 0x45)),
        ("Danger", Rgb(0xF2, 0x84, 0x84)),
        ("DangerSoft", Rgb(0x52, 0x3C, 0x46)),
        ("DangerLine", Rgb(0x8C, 0x4E, 0x58)),
        ("Warning", Rgb(0xF2, 0xBA, 0x55)),
        ("WarningSoft", Rgb(0x50, 0x48, 0x3A)),
        ("WarningLine", Rgb(0x8E, 0x75, 0x44)),
        ("InputBackground", Rgb(0x32, 0x39, 0x46)),
        ("CodeBackground", Rgb(0x36, 0x3E, 0x4C)),
        ("ScrollThumb", Rgb(0x5E, 0x69, 0x7B)),
        ("FocusBrush", Rgb(0x38, 0xAB, 0xDF)),
        ("ShadowColorBrush", Rgb(0x00, 0x00, 0x00)),
        ("SegmentTrack", Rgb(0x2A, 0x30, 0x3A)),
        ("SegmentSelected", Rgb(0x4E, 0x5A, 0x6E)),
        ("Recording", Rgb(0xE5, 0x48, 0x4D)),
        ("RecordingForeground", Rgb(0xFF, 0xFF, 0xFF)),
        ("RecordingSoft", Rgb(0x55, 0x3C, 0x46)),
        ("RecordingText", Rgb(0xFF, 0x9B, 0x9B)),
    ];

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
