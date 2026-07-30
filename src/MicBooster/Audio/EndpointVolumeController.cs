using NAudio.CoreAudioApi;

namespace MicBooster.Audio;

/// <summary>
/// Wraps the Windows endpoint volume of a capture device — the "Microphone" slider in Sound
/// Settings. It is the one control that needs no routing or virtual cable and that every
/// application sees at once, so it has to keep working on hardware that reports nonsense.
/// </summary>
/// <remarks>
/// <para>Every COM read and write is wrapped. An endpoint can be invalidated at any moment
/// (unplugged, disabled, driver restart) and a <c>COMException</c> escaping into the UI or a
/// timer callback would take the app down.</para>
/// <para>Capability is established by trying, not by trusting
/// <see cref="AudioEndpointVolume.HardwareSupport"/>: real devices report a
/// <see cref="EEndpointHardwareSupport"/> of 0 while the scalar works perfectly.</para>
/// <para><see cref="ExternalLevelChanged"/> and <see cref="ExternalMuteChanged"/> are raised from
/// the WASAPI notification callback, which runs on a COM-owned thread. Consumers must marshal to
/// their own thread (<c>Dispatcher.BeginInvoke</c> or similar) before touching UI state.</para>
/// </remarks>
public sealed class EndpointVolumeController : IDisposable
{
    /// <summary>
    /// Scalar difference below which an incoming notification is treated as the echo of our own
    /// write. The Windows mixer moves in 1% steps, so this is comfortably finer than any real move.
    /// </summary>
    private const float EchoEpsilon = 0.0015f;

    /// <summary>A dB range narrower than this is not a usable scale, whatever the driver claims.</summary>
    private const float MinimumUsefulDbSpan = 1f;

    private readonly object _sync = new();

    // Held for lifetime, not for use: MMDevice has a finalizer that disposes the
    // AudioEndpointVolume it hands out, so letting it become unreachable would tear our
    // notification registration down underneath us.
    private MMDevice? _device;
    private AudioEndpointVolume? _volume;
    private AudioMeterInformation? _meter;
    private AudioEndpointVolumeNotificationDelegate? _handler;

    private bool _supportsVolume;
    private bool _supportsMute;
    private string? _deviceName;
    private bool _disposed;

    // Last values we observed, so a failed COM read can still answer with something plausible
    // instead of collapsing the UI meter or slider to zero.
    private float _lastKnownLevel;
    private bool _lastKnownMute;

    // Echo suppression. Set immediately before we write, cleared by the matching notification.
    // NaN / -1 mean "no write outstanding"; both are 32-bit and safe to read from the COM thread.
    private volatile float _lastWrittenLevel = float.NaN;
    private volatile int _lastWrittenMute = -1;

    /// <summary>
    /// Raised when the level was changed by something other than us — the Windows mixer, a
    /// keyboard key, another application. Carries the new level as a percentage (0..100).
    /// Fires on a COM thread; marshal before use.
    /// </summary>
    public event EventHandler<float>? ExternalLevelChanged;

    /// <summary>
    /// Raised when the mute state was changed outside this app. Fires on a COM thread;
    /// marshal before use.
    /// </summary>
    public event EventHandler<bool>? ExternalMuteChanged;

    /// <summary>True once <see cref="Attach"/> has succeeded and before <see cref="Detach"/>.</summary>
    public bool IsAttached
    {
        get { lock (_sync) return _volume is not null; }
    }

    /// <summary>True when the endpoint accepted a level write during the attach probe.</summary>
    public bool SupportsVolume
    {
        get { lock (_sync) return _supportsVolume; }
    }

    /// <summary>
    /// True when the endpoint has a working mute. Downgraded to false if a later write fails,
    /// since some drivers accept the probe and then refuse the real thing.
    /// </summary>
    public bool SupportsMute
    {
        get { lock (_sync) return _supportsMute; }
    }

    /// <summary>Friendly name captured at attach time, because reading it later can throw.</summary>
    public string? DeviceName
    {
        get { lock (_sync) return _deviceName; }
    }

    /// <summary>
    /// The Windows capture level as a percentage (0..100), backed by
    /// <see cref="AudioEndpointVolume.MasterVolumeLevelScalar"/>. This is the source of truth for
    /// the level; the dB figure is only ever decoration.
    /// </summary>
    public float LevelPercent
    {
        get
        {
            lock (_sync)
            {
                var volume = _volume;
                if (volume is null) return _lastKnownLevel * 100f;

                try
                {
                    float scalar = volume.MasterVolumeLevelScalar;
                    if (float.IsFinite(scalar)) _lastKnownLevel = Math.Clamp(scalar, 0f, 1f);
                }
                catch (Exception)
                {
                    // Endpoint went away mid-read; the cached value is the best answer available.
                }

                return _lastKnownLevel * 100f;
            }
        }
        set
        {
            float scalar = Math.Clamp(value, 0f, 100f) / 100f;

            lock (_sync)
            {
                var volume = _volume;
                if (volume is null || !_supportsVolume) return;

                // Armed before the write: some drivers deliver the notification synchronously
                // from inside the setter, so arming afterwards would miss the echo.
                _lastWrittenLevel = scalar;
                try
                {
                    volume.MasterVolumeLevelScalar = scalar;
                    _lastKnownLevel = scalar;
                }
                catch (Exception)
                {
                    _lastWrittenLevel = float.NaN;
                    _supportsVolume = false;
                }
            }
        }
    }

    /// <summary>
    /// The level in decibels, or null when the device's reported range cannot be trusted.
    /// One real microphone reported a range of 1.5E-05 .. 0.0015 dB, which is meaningless and must
    /// never be shown to the user.
    /// </summary>
    public float? LevelDb
    {
        get
        {
            lock (_sync)
            {
                var volume = _volume;
                if (volume is null) return null;

                try
                {
                    var range = volume.VolumeRange;
                    float min = range.MinDecibels;
                    float max = range.MaxDecibels;

                    if (!float.IsFinite(min) || !float.IsFinite(max)) return null;
                    if (max - min <= MinimumUsefulDbSpan) return null;

                    float db = volume.MasterVolumeLevel;
                    if (!float.IsFinite(db)) return null;

                    // A level well outside the driver's own stated range means the two readings
                    // disagree, so neither is worth displaying.
                    if (db < min - 1f || db > max + 1f) return null;

                    return Math.Clamp(db, min, max);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// The endpoint mute. Not every capture endpoint implements it; a failed write clears
    /// <see cref="SupportsMute"/> rather than throwing.
    /// </summary>
    public bool Mute
    {
        get
        {
            lock (_sync)
            {
                var volume = _volume;
                if (volume is null) return _lastKnownMute;

                try
                {
                    _lastKnownMute = volume.Mute;
                }
                catch (Exception)
                {
                    _supportsMute = false;
                }

                return _lastKnownMute;
            }
        }
        set
        {
            lock (_sync)
            {
                var volume = _volume;
                if (volume is null) return;

                _lastWrittenMute = value ? 1 : 0;
                try
                {
                    volume.Mute = value;
                    _lastKnownMute = value;
                }
                catch (Exception)
                {
                    _lastWrittenMute = -1;
                    _supportsMute = false;
                }
            }
        }
    }

    /// <summary>
    /// Live input peak as a percentage (0..100), read straight from the endpoint meter.
    /// Works whether or not the DSP engine is running, and returns 0 rather than throwing.
    /// </summary>
    public float PeakPercent
    {
        get
        {
            AudioMeterInformation? meter;
            lock (_sync) meter = _meter;
            if (meter is null) return 0f;

            try
            {
                float peak = meter.MasterPeakValue;
                if (!float.IsFinite(peak)) return 0f;
                return Math.Clamp(peak, 0f, 1f) * 100f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }

    /// <summary>
    /// Binds to <paramref name="device"/>, hooks its volume notification and probes what the
    /// endpoint actually supports.
    /// </summary>
    /// <param name="device">
    /// The endpoint to control, or null to simply detach. The controller does not take ownership:
    /// the device stays owned by whoever resolved it, so it is safe to hand the same
    /// <see cref="MMDevice"/> to other controllers.
    /// </param>
    /// <returns>
    /// True when the endpoint's level can be read. False leaves the controller detached, which is
    /// the correct outcome for a device that has just been invalidated.
    /// </returns>
    public bool Attach(MMDevice? device)
    {
        Detach();
        if (device is null) return false;

        lock (_sync)
        {
            if (_disposed) return false;

            AudioEndpointVolume volume;
            float scalar;
            try
            {
                volume = device.AudioEndpointVolume;
                scalar = volume.MasterVolumeLevelScalar;
            }
            catch (Exception)
            {
                // No endpoint volume at all, or the device is already gone. Nothing to control.
                return false;
            }

            if (!float.IsFinite(scalar)) return false;

            _device = device;
            _volume = volume;
            _lastKnownLevel = Math.Clamp(scalar, 0f, 1f);

            // Probe by writing the value it already has: a genuine no-op that still proves the
            // setter is wired up, which the HardwareSupport flags do not reliably tell us.
            _lastWrittenLevel = _lastKnownLevel;
            try
            {
                volume.MasterVolumeLevelScalar = _lastKnownLevel;
                _supportsVolume = true;
            }
            catch (Exception)
            {
                _lastWrittenLevel = float.NaN;
                _supportsVolume = false;
            }

            try
            {
                _lastKnownMute = volume.Mute;
                _lastWrittenMute = _lastKnownMute ? 1 : 0;
                volume.Mute = _lastKnownMute;
                _supportsMute = true;
            }
            catch (Exception)
            {
                _lastWrittenMute = -1;
                _lastKnownMute = false;
                _supportsMute = false;
            }

            try
            {
                _meter = device.AudioMeterInformation;
                _ = _meter.MasterPeakValue;
            }
            catch (Exception)
            {
                _meter = null;
            }

            try
            {
                _deviceName = device.FriendlyName;
            }
            catch (Exception)
            {
                _deviceName = null;
            }

            try
            {
                _handler = OnVolumeNotification;
                volume.OnVolumeNotification += _handler;
            }
            catch (Exception)
            {
                // Losing the notification only costs us mixer sync, not control.
                _handler = null;
            }

            return true;
        }
    }

    /// <summary>
    /// Unhooks the notification and drops the endpoint. Safe to call when already detached.
    /// </summary>
    public void Detach()
    {
        MMDevice? device;
        AudioEndpointVolume? volume;
        AudioEndpointVolumeNotificationDelegate? handler;

        lock (_sync)
        {
            device = _device;
            volume = _volume;
            handler = _handler;

            _device = null;
            _volume = null;
            _meter = null;
            _handler = null;
            _supportsVolume = false;
            _supportsMute = false;
            _deviceName = null;
            _lastWrittenLevel = float.NaN;
            _lastWrittenMute = -1;
        }

        if (volume is not null && handler is not null)
        {
            try
            {
                volume.OnVolumeNotification -= handler;
            }
            catch (Exception)
            {
                // Already torn down by the driver. The delegate dies with the device either way.
            }
        }

        // Keeps the endpoint reachable until the unhook has happened, so its finalizer cannot
        // dispose the volume object out from under the line above.
        GC.KeepAlive(device);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Detach();
        ExternalLevelChanged = null;
        ExternalMuteChanged = null;
    }

    /// <summary>
    /// The WASAPI volume callback. Runs on a COM thread and must never throw back into COM,
    /// so the whole body is guarded.
    /// </summary>
    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        try
        {
            float scalar = data.MasterVolume;
            bool muted = data.Muted;
            bool raiseLevel = false;
            bool raiseMute = false;

            lock (_sync)
            {
                if (_disposed || _volume is null) return;

                if (float.IsFinite(scalar))
                {
                    scalar = Math.Clamp(scalar, 0f, 1f);

                    float echo = _lastWrittenLevel;
                    if (!float.IsNaN(echo) && MathF.Abs(scalar - echo) <= EchoEpsilon)
                    {
                        // Our own write coming back. Consume the arming so a later mixer move to
                        // the same value is still reported.
                        _lastWrittenLevel = float.NaN;
                    }
                    else if (MathF.Abs(scalar - _lastKnownLevel) > EchoEpsilon)
                    {
                        raiseLevel = true;
                    }

                    _lastKnownLevel = scalar;
                }

                int echoMute = _lastWrittenMute;
                if (echoMute >= 0 && (echoMute != 0) == muted)
                {
                    _lastWrittenMute = -1;
                }
                else if (muted != _lastKnownMute)
                {
                    raiseMute = true;
                }

                _lastKnownMute = muted;
            }

            // Raised outside the lock: a handler that marshals synchronously would otherwise
            // hold the lock across a dispatcher wait.
            if (raiseLevel) ExternalLevelChanged?.Invoke(this, scalar * 100f);
            if (raiseMute) ExternalMuteChanged?.Invoke(this, muted);
        }
        catch (Exception)
        {
            // Includes anything a subscriber threw. Nothing useful can be done on a COM callback.
        }
    }
}
