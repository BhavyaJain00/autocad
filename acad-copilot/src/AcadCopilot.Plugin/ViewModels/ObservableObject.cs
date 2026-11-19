using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for the panel view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and raises <see cref="PropertyChanged"/> when the value changed.</summary>
    /// <returns>True when the value changed.</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="propertyName"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties.</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>Simple <see cref="ICommand"/> backed by delegates; call <see cref="NotifyCanExecuteChanged"/> when its state changes.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Creates a command without a parameter.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <summary>Creates a command that receives the command parameter.</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    /// <remarks>
    /// Commands run on AutoCAD's main (WPF) thread: an exception escaping here would reach AutoCAD's dispatcher and could
    /// end the AutoCAD session, so it is logged instead.
    /// </remarks>
    public void Execute(object? parameter)
    {
        try
        {
            if (CanExecute(parameter))
            {
                _execute(parameter);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Error("A panel command failed", ex);
        }
    }

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
