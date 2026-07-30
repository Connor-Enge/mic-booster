using System.Text;
using System.Threading;

namespace MicBooster;

/// <summary>
/// Application entry point. Owns the single-instance guard and the last-resort
/// exception handlers.
/// </summary>
public partial class App : System.Windows.Application
{
    // Session-scoped: two people signed into the same PC should each get their own copy.
    private const string InstanceMutexName = @"Local\MicBooster.SingleInstance.9F2C4A1E";

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private volatile bool _reportingFault;

    /// <summary>
    /// True when this process lost the single-instance race and is on its way out.
    /// <see cref="MainWindow"/> checks it so the window never starts touching audio devices.
    /// </summary>
    internal static bool IsDuplicateInstance { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Hooked first, so a failure during the rest of startup still gets explained.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        if (!TryClaimSingleInstance())
        {
            IsDuplicateInstance = true;
            System.Windows.MessageBox.Show(
                "Mic Booster is already running. Look for its icon in the notification area.",
                "Mic Booster",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    /// <inheritdoc />
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            try
            {
                if (_ownsInstanceMutex)
                {
                    _instanceMutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
                // Released on a different thread than it was taken; the handle close below
                // frees it either way.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Two instances would fight over the same capture endpoint, so only the first one runs.
    /// </summary>
    private bool TryClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            _ownsInstanceMutex = createdNew;
            return createdNew;
        }
        catch (Exception)
        {
            // A locked-down machine can refuse the named object entirely. Running is a better
            // failure mode than refusing to start.
            _instanceMutex = null;
            _ownsInstanceMutex = false;
            return true;
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // The engine handles its own device faults, so anything arriving here is UI-level and
        // survivable. An audio app that silently disappears is the worst possible outcome.
        ReportFault("problem", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // The runtime is already unwinding; all that is left is to say why.
        ReportFault("fatal error", e.ExceptionObject as Exception);
    }

    private void ReportFault(string caption, Exception? exception)
    {
        if (_reportingFault)
        {
            return;
        }

        _reportingFault = true;
        try
        {
            System.Windows.MessageBox.Show(
                Describe(exception),
                "Mic Booster - " + caption,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // If even the message box fails there is nothing further this process can do.
        }
        finally
        {
            _reportingFault = false;
        }
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null)
        {
            return "Something went wrong and no detail was reported. "
                 + "If audio has stopped, press Stop and then Start again.";
        }

        var text = new StringBuilder();
        text.Append(exception.GetType().Name).Append(": ").AppendLine(exception.Message);

        var inner = exception.InnerException;
        for (var depth = 0; inner is not null && depth < 3; depth++)
        {
            text.Append("-> ").AppendLine(inner.Message);
            inner = inner.InnerException;
        }

        text.AppendLine();
        text.Append("Mic Booster is still running. If audio has stopped, press Stop and then Start again.");
        return text.ToString();
    }
}
