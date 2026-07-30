using System.ComponentModel;
using System.Globalization;
using MicBooster.Models;
using MicBooster.Services;
using MicBooster.ViewModels;

namespace MicBooster;

/// <summary>
/// The single window. It owns the view model, the global hotkey, and the tray icon, and
/// nothing else: everything audio-related lives behind <see cref="MainViewModel"/>.
/// </summary>
/// <remarks>
/// Window geometry is not restored between runs. The view model owns the settings file and
/// does not expose size, and a wrong-sized window is a far smaller problem than reaching
/// around it.
/// </remarks>
public partial class MainWindow : System.Windows.Window
{
    private const string MuteHotkeyId = "mute";

    private readonly MainViewModel _viewModel = new();
    private readonly HotkeyManager _hotkeys = new();

    private TrayIcon? _tray;
    private bool _hotkeysAttached;
    private bool _trayHintShown;
    private bool _cleanedUp;

    /// <summary>Builds the window and binds it to a fresh view model.</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // The second copy of the app is already shutting down; it must not open devices.
        if (App.IsDuplicateInstance)
        {
            Close();
            return;
        }

        _viewModel.Initialize();

        _tray = CreateTrayIcon();

        // Safe here: the window handle exists by the time Loaded is raised.
        _hotkeys.Attach(this);
        _hotkeysAttached = true;
        RegisterMuteHotkey();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _hotkeysAttached = false;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // Tray first: a leftover notification icon outlives the process and looks broken.
        _tray?.Dispose();
        _tray = null;

        _hotkeys.UnregisterAll();
        _hotkeys.Dispose();

        _viewModel.Shutdown();
        _viewModel.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != System.Windows.WindowState.Minimized || !_viewModel.MinimizeToTray)
        {
            return;
        }

        Hide();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("Mic Booster", "Still running. Double-click this icon to bring it back.");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An empty name means "everything changed", which a view model may use after loading
        // settings or a preset.
        var all = string.IsNullOrEmpty(e.PropertyName);

        if (all || e.PropertyName == nameof(MainViewModel.Muted))
        {
            _tray?.SetMuted(_viewModel.Muted);
        }

        if (all || e.PropertyName == nameof(MainViewModel.IsRunning))
        {
            _tray?.SetRunning(_viewModel.IsRunning);
        }

        if ((all || e.PropertyName == nameof(MainViewModel.MuteHotkey)) && _hotkeysAttached)
        {
            RegisterMuteHotkey();
        }
    }

    private TrayIcon CreateTrayIcon()
    {
        var tray = new TrayIcon();
        tray.ShowRequested += RestoreFromTray;
        tray.ExitRequested += Close;
        tray.MuteToggleRequested += () => Execute(_viewModel.ToggleMuteCommand);
        tray.EngineToggleRequested += () => Execute(_viewModel.ToggleEngineCommand);
        tray.SetMuted(_viewModel.Muted);
        tray.SetRunning(_viewModel.IsRunning);
        return tray;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Re-registers the mute hotkey and reports the outcome next to the hotkey box. A
    /// shortcut another app already owns is a normal thing to hit, not an error dialog.
    /// </summary>
    private void RegisterMuteHotkey()
    {
        var gesture = _viewModel.MuteHotkey ?? string.Empty;

        if (_hotkeys.Register(MuteHotkeyId, gesture, () => Execute(_viewModel.ToggleMuteCommand)))
        {
            HotkeyStatusText.Text = string.IsNullOrWhiteSpace(gesture)
                ? "No global hotkey set."
                : gesture.Trim() + " mutes the microphone from any app.";
            return;
        }

        HotkeyStatusText.Text = string.IsNullOrEmpty(_hotkeys.LastError)
            ? gesture + " could not be registered; another app is probably using it."
            : _hotkeys.LastError;
    }

    private static void Execute(System.Windows.Input.ICommand? command)
    {
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}

/// <summary>
/// Magnitude of a numeric binding, for the reduction meters that only ever draw a positive
/// amount while the values feeding them can be signed.
/// </summary>
public sealed class AbsoluteValueConverter : System.Windows.Data.IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is null ? 0d : Math.Abs(System.Convert.ToDouble(value, culture));
        }
        catch (Exception)
        {
            // A binding is never worth an exception; an idle meter is the honest fallback.
            return 0d;
        }
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Negates a boolean, for controls that are enabled only when a setting is off.</summary>
public sealed class InverseBooleanConverter : System.Windows.Data.IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : true;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : false;
}

/// <summary>
/// Turns the routing and channel enums into wording that means something to someone who has
/// never seen an audio routing dialog.
/// </summary>
public sealed class EnumLabelConverter : System.Windows.Data.IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RoutingMode.None => "Nothing (process and measure only)",
        RoutingMode.Output => "The output device below",
        ChannelMode.MixAll => "Mix all channels",
        ChannelMode.Left => "Channel 1 only (left)",
        ChannelMode.Right => "Channel 2 only (right)",
        ChannelMode.Specific => "A specific channel",
        _ => value?.ToString() ?? string.Empty
    };

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
