using System.IO;
using Microsoft.Win32;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Voice;

/// <summary>
/// The Windows privacy switches voice input depends on, read from the registry without opening the microphone:
/// <list type="bullet">
/// <item>Settings > Privacy > Microphone: "Allow access to the microphone on this device" (HKLM ...\ConsentStore\microphone),
/// "Allow apps to access your microphone" (HKCU ...\ConsentStore\microphone) and "Allow desktop apps to access your
/// microphone" (HKCU ...\ConsentStore\microphone\NonPackaged), each "Allow" or "Deny";</item>
/// <item>Settings > Privacy > Speech: "Online speech recognition" (HKCU Software\Microsoft\Speech_OneCore\Settings\
/// OnlineSpeechPrivacy, HasAccepted 1 / 0), or the group policy AllowInputPersonalization = 0.</item>
/// </list>
/// A missing value means unknown (the engines then find out when they start).
/// </summary>
/// <param name="DeviceMicrophone">Device-wide microphone access ("Allow", "Deny" or null).</param>
/// <param name="AppsMicrophone">Apps' microphone access ("Allow", "Deny" or null).</param>
/// <param name="DesktopAppsMicrophone">Desktop apps' microphone access ("Allow", "Deny" or null).</param>
/// <param name="OnlineSpeechAccepted">HasAccepted of online speech recognition (1, 0 or null).</param>
/// <param name="PolicyAllowsOnlineSpeech">The AllowInputPersonalization policy (1, 0 or null).</param>
public sealed record VoicePrivacy(
    string? DeviceMicrophone,
    string? AppsMicrophone,
    string? DesktopAppsMicrophone,
    int? OnlineSpeechAccepted,
    int? PolicyAllowsOnlineSpeech = null)
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>True when Windows blocks the microphone for desktop apps such as AutoCAD.</summary>
    public bool MicrophoneBlocked =>
        IsDeny(DeviceMicrophone) || IsDeny(DesktopAppsMicrophone);

    /// <summary>True / false when "Online speech recognition" is known to be on / off; null when unknown.</summary>
    public bool? OnlineSpeechAllowed =>
        PolicyAllowsOnlineSpeech == 0 ? false
        : OnlineSpeechAccepted switch
        {
            1 => true,
            0 => false,
            _ => null,
        };

    /// <summary>Reads the current user's settings (never throws).</summary>
    public static VoicePrivacy Read()
    {
        try
        {
            return new VoicePrivacy(
                ReadString(Registry.LocalMachine, ConsentStore, "Value"),
                ReadString(Registry.CurrentUser, ConsentStore, "Value"),
                ReadString(Registry.CurrentUser, ConsentStore + @"\NonPackaged", "Value"),
                ReadInt(Registry.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted"),
                ReadInt(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            CopilotLog.Warn("Could not read the Windows privacy settings for voice input", ex);
            return new VoicePrivacy(null, null, null, null);
        }
    }

    private static bool IsDeny(string? value) => string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : null;
    }
}
