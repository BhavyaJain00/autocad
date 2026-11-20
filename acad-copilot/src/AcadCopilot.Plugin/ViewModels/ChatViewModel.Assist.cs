using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Recovery;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// The panel side of agent B's assistant features (<see cref="ICopilotAssist"/>: AI switching, "Ask before drawing",
/// second-opinion review, tutor mode) and of agent A's "Resume previous job" (<see cref="ResumeInfo"/>).
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>The message that asks for a second-opinion review (the session recognises it, <see cref="ReviewRequests"/>).</summary>
    public const string ReviewPrompt = "Review this plan";

    private RelayCommand? _secondOpinionCommand;

    private ICopilotAssist? Assist => _backend as ICopilotAssist;

    /// <summary>True when the backend offers the assistant switches (Settings shows the "AI BEHAVIOUR" section).</summary>
    public bool HasFeatures => Assist is not null;

    /// <summary>Continue with the next signed-in AI when one hits its limit or fails.</summary>
    public bool AutoSwitchProvider
    {
        get => Assist?.Features.AutoSwitch ?? false;
        set => UpdateFeatures(f => f.AutoSwitch == value ? f : f with { AutoSwitch = value });
    }

    /// <summary>Ask "Shall I draw this?" once before drawing a new plan.</summary>
    public bool AskBeforeDrawing
    {
        get => Assist?.Features.AskBeforeDrawing ?? false;
        set => UpdateFeatures(f => f.AskBeforeDrawing == value ? f : f with { AskBeforeDrawing = value });
    }

    /// <summary>A second AI reviews each finished drawing.</summary>
    public bool AutoReview
    {
        get => Assist?.Features.AutoReview ?? false;
        set => UpdateFeatures(f => f.AutoReview == value ? f : f with { AutoReview = value });
    }

    /// <summary>The AI explains each AutoCAD command it uses in one short line.</summary>
    public bool TutorMode
    {
        get => Assist?.Features.TutorMode ?? false;
        set => UpdateFeatures(f => f.TutorMode == value ? f : f with { TutorMode = value });
    }

    /// <summary>"Takes over in this order: Claude, ChatGPT, Gemini".</summary>
    public string ProviderOrderText
    {
        get
        {
            if (Assist is not { } assist)
            {
                return string.Empty;
            }

            var names = CopilotFeatures.NormalizeOrder(assist.Features.ProviderOrder)
                .Select(id => Providers.FirstOrDefault(p => p.Id == id)?.DisplayName ?? id.ToString());
            return "Takes over in this order: " + string.Join(", ", names) + " (only AIs you are signed in to).";
        }
    }

    /// <summary>Asks another AI for a second opinion on the drawing (queued while this chat works).</summary>
    public RelayCommand SecondOpinionCommand => _secondOpinionCommand ??= new RelayCommand(() =>
    {
        IsQuickMenuOpen = false;
        SendOrQueue(ReviewPrompt, ReviewPrompt);
    });

    /// <summary>Re-reads the switches (another tab changed them; they are shared).</summary>
    public void RefreshFeatures() =>
        OnPropertiesChanged(nameof(AutoSwitchProvider), nameof(AskBeforeDrawing), nameof(AutoReview), nameof(TutorMode), nameof(ProviderOrderText));

    /// <summary>
    /// Shows the "Resume previous job" card for <paramref name="info"/> (unless one for the same journal is already shown).
    /// Resume calls <paramref name="resume"/> (inside AutoCAD: <c>PaletteHost</c> sends <c>ResumeMonitor.ResumePrompt</c> in
    /// a tab bound to that drawing; it returns false when it could not, and the card stays open), or, when null, sends
    /// <see cref="ResumeCardItem.ResumePrompt"/> in this tab. After a resume and on Dismiss, <paramref name="dismissed"/>
    /// is called so the journal is not offered again.
    /// </summary>
    public void ShowResumeOffer(ResumeInfo info, Action<ResumeInfo>? dismissed = null, Func<ResumeInfo, bool>? resume = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        RunOnUi(() =>
        {
            if (Items.OfType<ResumeCardItem>().Any(c => c.IsOpen && c.Info.Session == info.Session))
            {
                return;
            }

            CloseAssistant();
            CloseActivityGroup();
            Items.Add(new ResumeCardItem(
                info,
                card =>
                {
                    if (resume is not null)
                    {
                        bool sent;
                        try
                        {
                            sent = resume(info);
                        }
                        catch (Exception ex)
                        {
                            CopilotLog.Warn("Could not resume the unfinished job", ex);
                            ShowNotice("Could not resume the previous job: " + ex.Message, isError: true);
                            return;
                        }

                        if (!sent)
                        {
                            return;
                        }
                    }
                    else
                    {
                        SendOrQueue("Resume previous job" + (info.PlanTitle is { Length: > 0 } plan ? $": {plan}" : string.Empty), card.ResumePrompt);
                    }

                    card.Outcome = "Resuming";
                    Forget(dismissed, info);
                },
                card =>
                {
                    card.Outcome = "Dismissed";
                    Forget(dismissed, info);
                }));
        });
    }

    private static void Forget(Action<ResumeInfo>? dismissed, ResumeInfo info)
    {
        try
        {
            dismissed?.Invoke(info);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not dismiss the unfinished job", ex);
        }
    }

    /// <summary>
    /// Sends <paramref name="sentText"/> (shown as <paramref name="displayText"/>) now, or puts it in the queue while this
    /// chat is working or cannot start a turn. True when it was sent at once.
    /// </summary>
    public bool SendOrQueue(string displayText, string sentText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayText);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentText);
        if (IsBusy || _session is { CanStartTurn: false } || Queue.Count > 0)
        {
            Queue.Add(new QueuedRequest(displayText, sentText, []));
            RenumberQueue();
            IsQueueExpanded = true;
            if (!IsBusy && !IsQueuePaused)
            {
                ScheduleNextQueued();
            }

            return false;
        }

        _ = RunTurnAsync(displayText, sentText, [], null, fromQueue: false);
        return true;
    }

    private void InitAssist()
    {
        if (Assist is { } assist)
        {
            try
            {
                // This panel draws the switch notice and the review card itself.
                assist.EmitNoticesAsText = false;
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Could not turn off the text form of switch / review notices", ex);
            }
        }
    }

    private void UpdateFeatures(Func<CopilotFeatures, CopilotFeatures> change)
    {
        if (Assist is not { } assist)
        {
            return;
        }

        var current = assist.Features;
        var updated = change(current);
        if (ReferenceEquals(updated, current))
        {
            return;
        }

        try
        {
            assist.Features = updated;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not change the AI behaviour settings", ex);
            SetNotice("Could not save the setting: " + ex.Message, isError: true);
        }

        RefreshFeatures();
    }

    // Agent B's notice events: rendered as a switch line and a review card.
    private bool ApplyAssistEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case ProviderSwitched switched:
                CloseAssistant();
                CloseActivityGroup();
                Items.Add(new SwitchNoticeItem(switched.Message, switched.Detail));
                return true;
            case ReviewCompleted review:
                CloseAssistant();
                CloseActivityGroup();
                Items.Add(new ReviewCardItem(review.Report, FixReview));
                return true;
            default:
                return false;
        }
    }

    private void FixReview(ReviewCardItem card)
    {
        if (card.Report.FixPrompt is not { Length: > 0 } prompt)
        {
            return;
        }

        card.IsFixRequested = true;
        SendOrQueue("Fix these", prompt);
    }
}
