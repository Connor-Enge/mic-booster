using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicBooster.Audio;

/// <summary>
/// Enumerates capture and render endpoints, resolves a saved selection back to a live device,
/// and reports hot-plug changes.
/// </summary>
/// <remarks>
/// Every COM read is individually guarded. Endpoints on unknown hardware lie, throw, and vanish
/// mid-call, so a failure anywhere is treated as "this one device is unusable" rather than being
/// allowed to abort enumeration or reach the caller.
/// </remarks>
public sealed class DeviceManager : IDisposable
{
    /// <summary>
    /// One physical unplug makes Windows fire several callbacks (state changed, default changed,
    /// removed). Collapsing them into a single notification keeps the UI from rebuilding its
    /// device lists three times and stealing the user's combo-box selection mid-click.
    /// </summary>
    private const int ChangeDebounceMilliseconds = 250;

    private const int AudclntNotInitialized = unchecked((int)0x88890001);
    private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntDeviceInUse = unchecked((int)0x8889000A);

    private readonly object _sync = new();
    private readonly NotificationClient _notificationClient;
    private readonly System.Threading.Timer _debounceTimer;

    private MMDeviceEnumerator? _enumerator;
    private bool _callbackRegistered;
    private bool _disposed;

    /// <summary>
    /// Raised once per burst of endpoint changes, roughly <see cref="ChangeDebounceMilliseconds"/>
    /// after the last one. Fires on a thread-pool thread, so consumers must marshal to their own
    /// thread before touching UI state.
    /// </summary>
    public event EventHandler? DevicesChanged;

    public DeviceManager()
    {
        _notificationClient = new NotificationClient(this);
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // Warm the enumerator so hot-plug notifications are live before the first enumeration.
        EnsureEnumerator();
    }

    /// <summary>Active capture endpoints, in the order Windows reports them.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => Enumerate(DataFlow.Capture, Role.Communications);

    /// <summary>Active render endpoints, in the order Windows reports them.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
        => Enumerate(DataFlow.Render, Role.Multimedia);

    /// <summary>Snapshot of the default capture endpoint (communications role), or null.</summary>
    public AudioDeviceInfo? GetDefaultCapture() => GetDefaultInfo(DataFlow.Capture, Role.Communications);

    /// <summary>Snapshot of the default render endpoint (multimedia role), or null.</summary>
    public AudioDeviceInfo? GetDefaultRender() => GetDefaultInfo(DataFlow.Render, Role.Multimedia);

    /// <summary>
    /// Opens the endpoint with this exact ID, or null when it is missing or not active.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="MMDevice"/> is owned by the caller, who must dispose it.
    /// <see cref="DeviceManager"/> never keeps a live device handle.
    /// </remarks>
    public MMDevice? GetDeviceById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        MMDeviceEnumerator? enumerator = EnsureEnumerator();
        if (enumerator is null) return null;

        MMDevice? device;
        try
        {
            device = enumerator.GetDevice(id);
        }
        catch (Exception)
        {
            return null;
        }

        if (device is null) return null;
        if (IsActive(device)) return device;

        SafeDispose(device);
        return null;
    }

    /// <summary>
    /// Finds the capture device to record from: exact ID, then a case-insensitive name match,
    /// then the system default, then null.
    /// </summary>
    /// <remarks>
    /// The name fallback is the point of storing both ID and name in settings. Windows mints a
    /// new endpoint ID when a USB device is reinstalled or moved to another port, and without the
    /// fallback the app would silently switch to whatever the default happens to be.
    /// The returned <see cref="MMDevice"/> is owned by the caller, who must dispose it.
    /// </remarks>
    public MMDevice? ResolveCapture(string? id, string? nameFallback)
        => Resolve(DataFlow.Capture, Role.Communications, id, nameFallback);

    /// <summary>
    /// Finds the render device to play into, using the same ID → name → default chain as
    /// <see cref="ResolveCapture"/>. The returned <see cref="MMDevice"/> is owned by the caller,
    /// who must dispose it.
    /// </summary>
    public MMDevice? ResolveRender(string? id, string? nameFallback)
        => Resolve(DataFlow.Render, Role.Multimedia, id, nameFallback);

    /// <summary>
    /// True when the exception means the endpoint is gone or unusable, so callers can tear the
    /// stream down and re-resolve instead of reporting a hard fault.
    /// </summary>
    public static bool IsDeviceInvalidated(Exception ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (IsDeviceInvalidated(inner)) return true;
                }
            }

            // COMException.ErrorCode and Exception.HResult usually agree, but a wrapper
            // exception can carry the real code in only one of them.
            if (current is COMException com && IsInvalidatedHResult(com.ErrorCode)) return true;
            if (IsInvalidatedHResult(current.HResult)) return true;

            current = current.InnerException;
        }

        return false;
    }

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        bool registered;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            enumerator = _enumerator;
            _enumerator = null;
            registered = _callbackRegistered;
            _callbackRegistered = false;
        }

        try { _debounceTimer.Dispose(); } catch (Exception) { }

        if (enumerator is not null)
        {
            if (registered)
            {
                try { enumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
                catch (Exception) { }
            }

            SafeDispose(enumerator);
        }

        DevicesChanged = null;
    }

    private static bool IsInvalidatedHResult(int hresult)
        => hresult == AudclntDeviceInvalidated
        || hresult == AudclntNotInitialized
        || hresult == AudclntDeviceInUse;

    /// <summary>
    /// Returns the shared enumerator, creating it on first need. Creation fails while the Windows
    /// audio service is stopped or restarting, so a null result is transient and retried later
    /// rather than being cached as a permanent failure.
    /// </summary>
    private MMDeviceEnumerator? EnsureEnumerator()
    {
        lock (_sync)
        {
            if (_disposed) return null;
            if (_enumerator is not null) return _enumerator;

            try
            {
                _enumerator = new MMDeviceEnumerator();
            }
            catch (Exception)
            {
                return null;
            }

            try
            {
                _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
                _callbackRegistered = true;
            }
            catch (Exception)
            {
                // Losing hot-plug notifications costs the user a manual refresh; it is not fatal.
                _callbackRegistered = false;
            }

            return _enumerator;
        }
    }

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow, Role role)
    {
        MMDeviceEnumerator? enumerator = EnsureEnumerator();
        if (enumerator is null) return Array.Empty<AudioDeviceInfo>();

        string? defaultId = TryGetDefaultId(enumerator, flow, role);

        MMDeviceCollection collection;
        int count;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            count = collection.Count;
        }
        catch (Exception)
        {
            return Array.Empty<AudioDeviceInfo>();
        }

        var result = new List<AudioDeviceInfo>(count);
        for (int i = 0; i < count; i++)
        {
            MMDevice? device = null;
            try
            {
                device = collection[i];
                AudioDeviceInfo? info = Describe(device, defaultId);
                if (info is not null) result.Add(info);
            }
            catch (Exception)
            {
                // One hostile endpoint must not hide the rest of the user's hardware.
            }
            finally
            {
                if (device is not null) SafeDispose(device);
            }
        }

        return result;
    }

    private AudioDeviceInfo? GetDefaultInfo(DataFlow flow, Role role)
    {
        MMDeviceEnumerator? enumerator = EnsureEnumerator();
        if (enumerator is null) return null;

        MMDevice? device = null;
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;

            device = enumerator.GetDefaultAudioEndpoint(flow, role);
            if (device is null) return null;

            string? id = TryReadId(device);
            return Describe(device, id);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (device is not null) SafeDispose(device);
        }
    }

    private MMDevice? Resolve(DataFlow flow, Role role, string? id, string? nameFallback)
    {
        MMDeviceEnumerator? enumerator = EnsureEnumerator();
        if (enumerator is null) return null;

        MMDevice? byId = GetDeviceById(id);
        if (byId is not null)
        {
            if (MatchesFlow(byId, flow)) return byId;

            // A saved ID that now points at the other direction (settings edited by hand, or a
            // device that changed identity) would blow up far deeper in, so drop it here.
            SafeDispose(byId);
        }

        MMDevice? byName = FindByName(enumerator, flow, nameFallback);
        if (byName is not null) return byName;

        return GetDefaultDevice(enumerator, flow, role);
    }

    private static MMDevice? FindByName(MMDeviceEnumerator enumerator, DataFlow flow, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string wanted = name.Trim();

        MMDeviceCollection collection;
        int count;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            count = collection.Count;
        }
        catch (Exception)
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            MMDevice? device = null;
            bool matched = false;
            try
            {
                device = collection[i];
                string? friendly = device.FriendlyName;
                matched = friendly is not null
                          && string.Equals(friendly.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                matched = false;
            }

            if (matched) return device;
            if (device is not null) SafeDispose(device);
        }

        return null;
    }

    private static MMDevice? GetDefaultDevice(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        MMDevice? device;
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            device = enumerator.GetDefaultAudioEndpoint(flow, role);
        }
        catch (Exception)
        {
            return null;
        }

        if (device is null) return null;
        if (IsActive(device)) return device;

        SafeDispose(device);
        return null;
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        MMDevice? device = null;
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;

            device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device is null ? null : TryReadId(device);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (device is not null) SafeDispose(device);
        }
    }

    /// <summary>
    /// Reads everything worth knowing about a device into a detached snapshot. Returns null only
    /// when the ID cannot be read, since a device we can never look up again is useless.
    /// </summary>
    private static AudioDeviceInfo? Describe(MMDevice device, string? defaultId)
    {
        string? id = TryReadId(device);
        if (string.IsNullOrEmpty(id)) return null;

        string name = string.Empty;
        try
        {
            name = device.FriendlyName ?? string.Empty;
        }
        catch (Exception)
        {
            // Fall through to the description or the raw ID below.
        }

        string? description = null;
        try
        {
            description = device.DeviceFriendlyName;
            if (string.IsNullOrWhiteSpace(description)) description = null;
        }
        catch (Exception)
        {
        }

        if (string.IsNullOrWhiteSpace(name)) name = description ?? id;

        bool active = IsActive(device);

        int sampleRate = 0;
        int channels = 0;
        try
        {
            // Documented to throw AUDCLNT_E_DEVICE_INVALIDATED on invalidated endpoints, and
            // observed to throw on some endpoints that still report themselves as active.
            NAudio.Wave.WaveFormat? format = device.AudioClient.MixFormat;
            if (format is not null)
            {
                int rate = format.SampleRate;
                int channelCount = format.Channels;

                // Bogus values are worse than no values: the UI would print "0 kHz / 0 ch"
                // as if it were fact.
                if (rate is > 0 and <= 768_000 && channelCount is > 0 and <= 64)
                {
                    sampleRate = rate;
                    channels = channelCount;
                }
            }
        }
        catch (Exception)
        {
        }

        return new AudioDeviceInfo
        {
            Id = id,
            FriendlyName = name,
            Description = description,
            IsDefault = defaultId is not null && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
            IsActive = active,
            SampleRate = sampleRate,
            Channels = channels
        };
    }

    private static string? TryReadId(MMDevice device)
    {
        try { return device.ID; }
        catch (Exception) { return null; }
    }

    private static bool IsActive(MMDevice device)
    {
        try { return device.State == DeviceState.Active; }
        catch (Exception) { return false; }
    }

    private static bool MatchesFlow(MMDevice device, DataFlow flow)
    {
        try { return device.DataFlow == flow; }
        catch (Exception)
        {
            // An unreadable direction on a device whose ID matched exactly: trust the ID.
            return true;
        }
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch (Exception) { }
    }

    private void ScheduleDevicesChanged()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                _debounceTimer.Change(ChangeDebounceMilliseconds, Timeout.Infinite);
            }
        }
        catch (Exception)
        {
            // Called from a COM callback; an exception escaping here would cross back into
            // the audio service.
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // This runs on a pool thread, where an unhandled exception kills the process.
        }
    }

    /// <summary>
    /// Forwards endpoint notifications to the manager. Kept private so the callbacks are not
    /// part of <see cref="DeviceManager"/>'s public surface, and implemented explicitly so they
    /// cannot be called by anything but COM.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceManager _owner;

        internal NotificationClient(DeviceManager owner) => _owner = owner;

        void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
            => _owner.ScheduleDevicesChanged();

        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
            => _owner.ScheduleDevicesChanged();

        void IMMNotificationClient.OnDeviceRemoved(string deviceId)
            => _owner.ScheduleDevicesChanged();

        void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            => _owner.ScheduleDevicesChanged();

        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Far too chatty to act on: volume and meter property changes alone would keep the
            // debounce timer permanently armed.
        }
    }
}
