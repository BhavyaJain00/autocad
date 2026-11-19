using AcadCopilot.Core.Agents;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>Sign-in state of a provider as shown by the status dot.</summary>
public enum ProviderState
{
    /// <summary>Status check running.</summary>
    Checking,

    /// <summary>Installed and signed in (or sign-in could not be confirmed but the CLI is usable).</summary>
    Ready,

    /// <summary>Installed but signed out.</summary>
    SignedOut,

    /// <summary>The CLI was not found.</summary>
    NotInstalled,
}

/// <summary>One provider (Claude / ChatGPT / Gemini) in the picker.</summary>
public sealed class ProviderViewModel : ObservableObject
{
    private ProviderStatus? _status;
    private bool _isChecking;
    private bool _isSelected;

    /// <summary>Creates the provider entry.</summary>
    public ProviderViewModel(IAgentRunner runner)
    {
        Runner = runner;
    }

    /// <summary>The runner.</summary>
    public IAgentRunner Runner { get; }

    /// <summary>Provider id.</summary>
    public ProviderId Id => Runner.ProviderId;

    /// <summary>"Claude", "ChatGPT", "Gemini".</summary>
    public string DisplayName => Runner.DisplayName;

    /// <summary>The official CLI behind the provider.</summary>
    public string CliName => Id switch
    {
        ProviderId.Claude => "Claude Code",
        ProviderId.Codex => "Codex CLI",
        ProviderId.Gemini => "Gemini CLI",
        _ => DisplayName,
    };

    /// <summary>Which subscription/account signs in.</summary>
    public string AccountKind => Id switch
    {
        ProviderId.Claude => "Claude Pro or Max account",
        ProviderId.Codex => "ChatGPT account",
        ProviderId.Gemini => "Google account",
        _ => "account",
    };

    /// <summary>npm package to install when missing.</summary>
    public string InstallCommand => Id switch
    {
        ProviderId.Claude => "npm install -g @anthropic-ai/claude-code",
        ProviderId.Codex => "npm install -g @openai/codex",
        ProviderId.Gemini => "npm install -g @google/gemini-cli",
        _ => string.Empty,
    };

    /// <summary>Last status, null before the first check.</summary>
    public ProviderStatus? Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                RaiseStateProperties();
            }
        }
    }

    /// <summary>True while checking.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (Set(ref _isChecking, value))
            {
                RaiseStateProperties();
            }
        }
    }

    /// <summary>True when this provider is selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Dot state.</summary>
    public ProviderState State
    {
        get
        {
            if (Status is null)
            {
                return ProviderState.Checking;
            }

            if (!Status.Installed)
            {
                return ProviderState.NotInstalled;
            }

            return Status.SignIn == SignInState.SignedOut ? ProviderState.SignedOut : ProviderState.Ready;
        }
    }

    /// <summary>Green dot.</summary>
    public bool IsReady => State == ProviderState.Ready;

    /// <summary>Amber dot.</summary>
    public bool IsSignedOut => State == ProviderState.SignedOut;

    /// <summary>Grey dot.</summary>
    public bool IsNotInstalled => State == ProviderState.NotInstalled;

    /// <summary>Hollow dot.</summary>
    public bool IsUnknown => State == ProviderState.Checking;

    /// <summary>True when the status is known and the CLI can run a turn.</summary>
    public bool IsUsable => Status?.IsUsable == true;

    /// <summary>True when a "Sign in" button makes sense.</summary>
    public bool CanSignIn => Status is { Installed: true, SignIn: not SignInState.SignedIn };

    /// <summary>True when the CLI is signed in for sure.</summary>
    public bool IsSignedIn => Status?.SignIn == SignInState.SignedIn;

    /// <summary>Short status text.</summary>
    public string StatusText => State switch
    {
        ProviderState.Checking => IsChecking ? "Checking..." : "Not checked yet",
        ProviderState.NotInstalled => $"{CliName} not installed",
        ProviderState.SignedOut => "Not signed in",
        _ when Status?.SignIn == SignInState.SignedIn =>
            string.IsNullOrWhiteSpace(Status.Account) ? "Signed in" : $"Signed in · {Status.Account}",
        _ => "Ready",
    };

    /// <summary>Longer detail (version / reason).</summary>
    public string DetailText
    {
        get
        {
            if (Status is null)
            {
                return CliName;
            }

            if (!Status.Installed)
            {
                return string.IsNullOrWhiteSpace(Status.Detail) ? $"Install with: {InstallCommand}" : Status.Detail!;
            }

            var version = string.IsNullOrWhiteSpace(Status.Version) ? CliName : $"{CliName} {Status.Version}";
            return string.IsNullOrWhiteSpace(Status.Detail) ? version : $"{version} · {Status.Detail}";
        }
    }

    /// <summary>Tooltip of the picker button.</summary>
    public string ToolTip => $"{DisplayName} via {CliName}: {StatusText}";

    private void RaiseStateProperties() => OnPropertiesChanged(
        nameof(State), nameof(IsReady), nameof(IsSignedOut), nameof(IsNotInstalled), nameof(IsUnknown),
        nameof(IsUsable), nameof(CanSignIn), nameof(IsSignedIn), nameof(StatusText), nameof(DetailText), nameof(ToolTip));
}
