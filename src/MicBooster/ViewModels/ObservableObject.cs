using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MicBooster.ViewModels;

/// <summary>
/// Change-notification base for the view models. Hand-rolled rather than pulled from a
/// toolkit package so the app keeps its single NAudio dependency.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises a change
    /// notification, returning false when the value was already equal.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for the calling member, or a named property.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
