using System.Diagnostics;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// Voice input shared by the chat tabs of one panel: the recognizer factory, the clock for the silence time-out, the
/// Windows Settings opener, and which tab is listening (one microphone, one listening tab at a time).
/// </summary>
public sealed class VoiceInputService
{
    private readonly Action<string> _openSettings;

    /// <summary>Creates the service.</summary>
    /// <param name="factory">Starts recognizers (default: Windows dictation with the offline fallback).</param>
    /// <param name="time">Clock for the silence time-out (default: the system clock).</param>
    /// <param name="openSettings">Opens an ms-settings: page (default: the Windows shell).</param>
    public VoiceInputService(ISpeechEngineFactory? factory = null, TimeProvider? time = null, Action<string>? openSettings = null)
    {
        Factory = factory ?? new WindowsSpeechEngineFactory();
        Time = time ?? TimeProvider.System;
        _openSettings = openSettings ?? OpenWithShell;
    }

    /// <summary>Starts recognizers.</summary>
    public ISpeechEngineFactory Factory { get; }

    /// <summary>Clock for the silence time-out.</summary>
    public TimeProvider Time { get; }

    /// <summary>The voice input that is listening now (any tab), or null.</summary>
    internal object? Active { get; set; }

    /// <summary>Opens <paramref name="uri"/> (an ms-settings: page). Errors are logged.</summary>
    public void OpenSettings(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _openSettings(uri);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn($"Could not open Windows Settings ({uri})", ex);
        }
    }

    private static void OpenWithShell(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
}
