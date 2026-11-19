using System.Windows.Controls;
using System.Windows.Input;
using AcadCopilot.Core.Diagnostics;
using Autodesk.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using SystemVariableChangedEventArgs = Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs;

namespace AcadCopilot.Plugin.Palette;

/// <summary>
/// Adds the ribbon tab "AI Copilot" with one large button that shows / hides the chat palette. The ribbon does not exist
/// yet when the plug-in loads at start-up, so the tab is added once <see cref="ComponentManager.Ribbon"/> is initialized,
/// and added again after a workspace switch (WSCURRENT) rebuilds the ribbon. Main thread only; never throws.
/// </summary>
public sealed class RibbonTabHost : IDisposable
{
    /// <summary>Id of the ribbon tab.</summary>
    public const string TabId = "ACADCOPILOT_TAB";

    private readonly Action _toggle;
    private bool _waitingForRibbon;
    private bool _waitingForIdle;
    private bool _attached;

    /// <summary>Creates the host; <paramref name="toggle"/> runs when the button is clicked (main thread).</summary>
    public RibbonTabHost(Action toggle)
    {
        _toggle = toggle;
    }

    /// <summary>Adds the tab now, or as soon as the ribbon exists; re-adds it after workspace switches.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        AcadApp.SystemVariableChanged += OnSystemVariableChanged;
        if (ComponentManager.Ribbon is null)
        {
            _waitingForRibbon = true;
            ComponentManager.ItemInitialized += OnItemInitialized;
        }
        else
        {
            EnsureTab();
        }
    }

    /// <summary>Adds the tab if the ribbon exists and does not have it yet. Returns true when the tab is present.</summary>
    public bool EnsureTab()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon is null)
            {
                return false;
            }

            if (ribbon.FindTab(TabId) is not null)
            {
                return true;
            }

            var button = new RibbonButton
            {
                Id = "ACADCOPILOT_TOGGLE",
                Text = "AI Copilot",
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                LargeImage = RibbonIcons.Create(32),
                Image = RibbonIcons.Create(16),
                ToolTip = "Show or hide the AI Copilot chat panel (command COPILOT).",
                CommandHandler = new RibbonCommand(_toggle),
            };
            var source = new RibbonPanelSource { Id = "ACADCOPILOT_PANEL", Title = "AI Copilot" };
            source.Items.Add(button);
            var tab = new RibbonTab { Id = TabId, Title = "AI Copilot" };
            tab.Panels.Add(new RibbonPanel { Source = source });
            ribbon.Tabs.Add(tab);
            CopilotLog.Info("Ribbon tab 'AI Copilot' added.");
            return true;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not add the AI Copilot ribbon tab", ex);
            return false;
        }
    }

    /// <summary>Stops listening for ribbon and workspace changes (the tab itself goes away with AutoCAD).</summary>
    public void Dispose()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        try
        {
            AcadApp.SystemVariableChanged -= OnSystemVariableChanged;
            if (_waitingForRibbon)
            {
                ComponentManager.ItemInitialized -= OnItemInitialized;
                _waitingForRibbon = false;
            }

            if (_waitingForIdle)
            {
                AcadApp.Idle -= OnIdleAfterWorkspaceSwitch;
                _waitingForIdle = false;
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Ribbon host clean-up failed", ex);
        }
    }

    private void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon is null)
        {
            return;
        }

        ComponentManager.ItemInitialized -= OnItemInitialized;
        _waitingForRibbon = false;
        EnsureTab();
    }

    private void OnSystemVariableChanged(object? sender, SystemVariableChangedEventArgs e)
    {
        if (!_waitingForIdle && string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
        {
            // The ribbon is rebuilt after the variable changes; add the tab back once AutoCAD is idle again.
            _waitingForIdle = true;
            AcadApp.Idle += OnIdleAfterWorkspaceSwitch;
        }
    }

    private void OnIdleAfterWorkspaceSwitch(object? sender, EventArgs e)
    {
        AcadApp.Idle -= OnIdleAfterWorkspaceSwitch;
        _waitingForIdle = false;
        EnsureTab();
    }

    /// <summary>ICommand behind the ribbon button.</summary>
    private sealed class RibbonCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CopilotLog.Error("The AI Copilot ribbon button failed", ex);
            }
        }
    }
}
