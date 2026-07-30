using System.Windows.Input;

namespace MicBooster.ViewModels;

/// <summary>
/// Parameterless <see cref="ICommand"/> over a delegate pair. The view model decides when
/// availability changed and calls <see cref="RaiseCanExecuteChanged"/>, rather than relying on
/// <see cref="CommandManager"/>, which re-queries on every input event and would poll device
/// state far more often than it changes.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command. <paramref name="canExecute"/> null means always available.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        if (_canExecute is null) return true;
        try
        {
            return _canExecute();
        }
        catch
        {
            // WPF evaluates this during binding and layout; letting an exception out there
            // tears down the visual tree. A disabled control is the safe answer.
            return false;
        }
    }

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();

    /// <summary>Tells bound controls to re-query <see cref="CanExecute"/>. Call on the UI thread.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
