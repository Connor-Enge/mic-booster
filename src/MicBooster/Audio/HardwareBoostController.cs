using NAudio.CoreAudioApi;
using NAudio.Wasapi.CoreAudioApi;

namespace MicBooster.Audio;

/// <summary>One selectable position of the hardware boost control.</summary>
/// <param name="Db">The gain in decibels this position applies.</param>
/// <param name="Label">Display text, e.g. "Off" or "+10 dB".</param>
public sealed record HardwareBoostStep(float Db, string Label)
{
    /// <summary>Returns <see cref="Label"/> so the record can be bound straight to a ComboBox.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Finds and drives the driver's "Microphone Boost" control, the analogue gain that sits ahead of
/// the converter. Using it is strictly better than digital gain when it exists, because it lifts
/// the signal before quantisation instead of amplifying the noise floor along with the voice.
/// </summary>
/// <remarks>
/// <para>Most USB microphones expose no such control, so <see cref="IsAvailable"/> being false is
/// the normal outcome rather than an error.</para>
/// <para>Finding the wrong control is far worse than finding none. The capture endpoint's own
/// topology carries no subunits, so the search has to cross the connector boundary into the
/// driver's graph — and on the other side the microphone path and the headphone monitor path share
/// their first few parts before forking. Both forks contain a 'Volume' subunit, and the monitor one
/// is the user's headphone level. So a part is only accepted when its name contains "boost", and
/// any branch that reaches a render-side part is abandoned outright.</para>
/// </remarks>
public sealed class HardwareBoostController : IDisposable
{
    /// <summary>The graph has cycles and unbounded fan-out; real boost controls sit 2-4 hops in.</summary>
    private const int MaxWalkDepth = 12;

    /// <summary>More positions than this is not a discrete control worth enumerating.</summary>
    private const int MaxSteps = 64;

    /// <summary>How many positions to synthesise when the driver's stepping is unusable.</summary>
    private const int SynthesisedStepCount = 7;

    /// <summary>
    /// Narrowest range worth exposing. One real driver reports a volume subunit spanning
    /// 1.5E-05 .. 0.0015 dB, which is a broken scale rather than a 0.0015 dB control, and offering
    /// steps from it would just give the user a slider that does nothing.
    /// </summary>
    private const float MinimumUsefulDbSpan = 1f;

    /// <summary>
    /// Names that mean we have wandered out of the capture path and into the render path. Hitting
    /// one of these ends the branch, so the monitor mix's volume can never be mistaken for boost.
    /// </summary>
    private static readonly string[] RenderPathMarkers =
    {
        "dac", "speaker", "headphone", "line out", "output"
    };

    private const string NotProbedStatus = "Select a microphone to check for a hardware boost control.";
    private const string NotAvailableStatus =
        "This microphone has no hardware boost control — use Boost in the processor instead.";

    private static readonly HardwareBoostStep[] NoSteps = Array.Empty<HardwareBoostStep>();

    private readonly object _sync = new();

    private AudioVolumeLevel? _control;

    // Held for lifetime, not for use: the control is reached through these two, and MMDevice has a
    // finalizer that releases what it handed out, so letting either become unreachable while we
    // still hold the control would pull the interface pointer out from under us.
    private MMDevice? _device;
    private Part? _part;

    private string? _controlName;
    private string _statusText = NotProbedStatus;
    private float _minDb;
    private float _maxDb;
    private float _stepDb;
    private float _currentDb;
    private IReadOnlyList<HardwareBoostStep> _steps = NoSteps;
    private bool _disposed;

    /// <summary>True when a usable boost control was found and can be read and written.</summary>
    public bool IsAvailable
    {
        get { lock (_sync) return _control is not null; }
    }

    /// <summary>The driver's own name for the control, e.g. "Microphone Boost". Null when none.</summary>
    public string? ControlName
    {
        get { lock (_sync) return _controlName; }
    }

    /// <summary>A sentence fit to show the user in either case, available or not.</summary>
    public string StatusText
    {
        get { lock (_sync) return _statusText; }
    }

    /// <summary>Lowest boost the control accepts, in dB. 0 when unavailable.</summary>
    public float MinDb
    {
        get { lock (_sync) return _minDb; }
    }

    /// <summary>Highest boost the control accepts, in dB. 0 when unavailable.</summary>
    public float MaxDb
    {
        get { lock (_sync) return _maxDb; }
    }

    /// <summary>The driver's reported increment in dB, or 0 when it did not report a usable one.</summary>
    public float StepDb
    {
        get { lock (_sync) return _stepDb; }
    }

    /// <summary>The discrete positions to offer the user. Empty when unavailable.</summary>
    public IReadOnlyList<HardwareBoostStep> Steps
    {
        get { lock (_sync) return _steps; }
    }

    /// <summary>
    /// The current boost in dB. Setting clamps to the control's range and snaps to the nearest
    /// entry in <see cref="Steps"/>, because drivers with a coarse stepping silently round anyway
    /// and the UI should show what actually happened.
    /// </summary>
    public float CurrentDb
    {
        get
        {
            lock (_sync)
            {
                var control = _control;
                if (control is null) return 0f;

                try
                {
                    float level = control.GetLevel(0);
                    if (float.IsFinite(level)) _currentDb = Math.Clamp(level, _minDb, _maxDb);
                }
                catch (Exception)
                {
                    // Device pulled, or the control vanished with a driver reset.
                }

                return _currentDb;
            }
        }
        set
        {
            lock (_sync)
            {
                var control = _control;
                if (control is null) return;

                float target = SnapLocked(value);
                try
                {
                    control.SetLevelUniform(target);
                    _currentDb = target;
                }
                catch (Exception)
                {
                    // Leave _currentDb alone so the next read reports what the hardware really has.
                }
            }
        }
    }

    /// <summary>
    /// Searches <paramref name="device"/> for a hardware boost control and binds to it.
    /// </summary>
    /// <param name="device">
    /// The capture endpoint to inspect, or null to just clear. The controller does not take
    /// ownership, so the same <see cref="MMDevice"/> may be shared with other controllers.
    /// </param>
    /// <returns>True when a control was found. False is the common, expected result.</returns>
    public bool Probe(MMDevice? device)
    {
        Release();
        if (device is null) return false;

        lock (_sync)
        {
            if (_disposed) return false;

            var found = FindBoostControl(device);
            if (found is null)
            {
                _statusText = NotAvailableStatus;
                return false;
            }

            var (part, control, name, min, max, step) = found.Value;

            _device = device;
            _part = part;
            _control = control;
            _controlName = name;
            _minDb = min;
            _maxDb = max;
            _stepDb = step;
            _steps = BuildSteps(min, max, step);

            _currentDb = min;
            try
            {
                float level = control.GetLevel(0);
                if (float.IsFinite(level)) _currentDb = Math.Clamp(level, min, max);
            }
            catch (Exception)
            {
                // Range is valid but the level read failed; the minimum is the safe assumption.
            }

            _statusText = $"{name}: up to {FormatDb(max)} in {_steps.Count} steps";
            return true;
        }
    }

    /// <summary>Drops the control and returns to the unprobed state.</summary>
    public void Release()
    {
        MMDevice? device;
        Part? part;

        lock (_sync)
        {
            device = _device;
            part = _part;
            _device = null;
            _part = null;
            _control = null;
            _controlName = null;
            _minDb = 0f;
            _maxDb = 0f;
            _stepDb = 0f;
            _currentDb = 0f;
            _steps = NoSteps;
            _statusText = NotProbedStatus;
        }

        // The control was reached through these two, so they must outlive the fields that
        // referenced it rather than being collected part-way through the clear.
        GC.KeepAlive(part);
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

        Release();
    }

    /// <summary>Nearest legal position to <paramref name="db"/>. Caller holds the lock.</summary>
    private float SnapLocked(float db)
    {
        if (!float.IsFinite(db)) return _currentDb;

        float clamped = Math.Clamp(db, _minDb, _maxDb);
        var steps = _steps;
        if (steps.Count == 0) return clamped;

        float best = steps[0].Db;
        float bestDelta = MathF.Abs(clamped - best);
        for (int i = 1; i < steps.Count; i++)
        {
            float delta = MathF.Abs(clamped - steps[i].Db);
            if (delta < bestDelta)
            {
                best = steps[i].Db;
                bestDelta = delta;
            }
        }

        return best;
    }

    /// <summary>
    /// Walks out of the endpoint into the driver's topology looking for a boost control.
    /// </summary>
    private static (Part Part, AudioVolumeLevel Control, string Name, float Min, float Max, float Step)?
        FindBoostControl(MMDevice device)
    {
        DeviceTopology topology;
        uint connectorCount;
        try
        {
            topology = device.DeviceTopology;
            connectorCount = topology.ConnectorCount;
        }
        catch (Exception)
        {
            // Non-active devices throw AUDCLNT_E_DEVICE_INVALIDATED here; nothing to search.
            return null;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (uint i = 0; i < connectorCount; i++)
        {
            Part? entry;
            try
            {
                var connector = topology.GetConnector(i);
                if (!connector.IsConnected) continue;

                // The endpoint's own side of the connector has no outgoing parts at all. The
                // driver's graph only becomes visible from the far side.
                entry = connector.ConnectedTo.Part;
            }
            catch (Exception)
            {
                continue;
            }

            if (entry is null) continue;

            var found = Search(entry, visited, 0);
            if (found is not null) return found;
        }

        return null;
    }

    private static (Part Part, AudioVolumeLevel Control, string Name, float Min, float Max, float Step)?
        Search(Part part, HashSet<string> visited, int depth)
    {
        if (depth > MaxWalkDepth) return null;

        string? globalId = TryGetGlobalId(part);
        if (globalId is not null && !visited.Add(globalId)) return null;

        string? name = TryGetName(part);

        // Abandon the branch the moment it turns into the render path, so the headphone monitor
        // volume is unreachable no matter what it is called.
        if (name is not null && ContainsAny(name, RenderPathMarkers)) return null;

        if (name is not null && name.Contains("boost", StringComparison.OrdinalIgnoreCase))
        {
            var control = TryGetVolumeLevel(part);
            if (control is not null && TryGetRange(control, out float min, out float max, out float step))
            {
                return (part, control, name, min, max, step);
            }
        }

        PartsList? outgoing;
        uint count;
        try
        {
            outgoing = part.PartsOutgoing;
            count = outgoing is null ? 0u : outgoing.Count;
        }
        catch (Exception)
        {
            return null;
        }

        if (outgoing is null) return null;

        for (uint i = 0; i < count; i++)
        {
            Part? next;
            try
            {
                next = outgoing[i];
            }
            catch (Exception)
            {
                continue;
            }

            if (next is null) continue;

            var found = Search(next, visited, depth + 1);
            if (found is not null) return found;
        }

        return null;
    }

    private static string? TryGetGlobalId(Part part)
    {
        try
        {
            string id = part.GlobalId;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetName(Part part)
    {
        try
        {
            string name = part.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            // Plenty of parts do not implement a name. Normal, not an error.
            return null;
        }
    }

    private static AudioVolumeLevel? TryGetVolumeLevel(Part part)
    {
        try
        {
            return part.AudioVolumeLevel;
        }
        catch (Exception)
        {
            // Parts without IAudioVolumeLevel throw E_NOINTERFACE here.
            return null;
        }
    }

    private static bool TryGetRange(AudioVolumeLevel control, out float min, out float max, out float step)
    {
        min = 0f;
        max = 0f;
        step = 0f;

        try
        {
            control.GetLevelRange(0, out float low, out float high, out float stepping);
            if (!float.IsFinite(low) || !float.IsFinite(high)) return false;
            if (high - low <= MinimumUsefulDbSpan) return false;
            if (!float.IsFinite(stepping)) stepping = 0f;

            min = low;
            max = high;
            step = stepping;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ContainsAny(string name, string[] markers)
    {
        for (int i = 0; i < markers.Length; i++)
        {
            if (name.Contains(markers[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Turns the reported range into the positions the UI offers. A real control reports something
    /// like 0..30 dB in 10 dB steps; when the stepping is missing or absurdly fine we fall back to
    /// a handful of evenly spaced positions so the user still gets a usable choice.
    /// </summary>
    private static IReadOnlyList<HardwareBoostStep> BuildSteps(float min, float max, float step)
    {
        float span = max - min;
        bool usableStep = step > 0f && span / step <= MaxSteps - 1;

        if (usableStep)
        {
            // The epsilon absorbs float error in span/step, which would otherwise drop the top
            // grid point on a range that divides exactly.
            int gridPoints = (int)MathF.Floor(span / step + 1e-4f);

            var stepped = new List<HardwareBoostStep>(gridPoints + 2);
            for (int i = 0; i <= gridPoints; i++)
            {
                float db = MathF.Min(min + i * step, max);
                stepped.Add(new HardwareBoostStep(db, FormatDb(db)));
            }

            // The grid does not always land on the maximum (e.g. 0..25 dB in 10 dB steps), and the
            // user must still be able to select full boost.
            float last = stepped[^1].Db;
            if (max - last > step * 0.01f) stepped.Add(new HardwareBoostStep(max, FormatDb(max)));

            return stepped;
        }

        var even = new List<HardwareBoostStep>(SynthesisedStepCount);
        for (int i = 0; i < SynthesisedStepCount; i++)
        {
            float db = min + span * i / (SynthesisedStepCount - 1);
            even.Add(new HardwareBoostStep(db, FormatDb(db)));
        }

        return even;
    }

    private static string FormatDb(float db)
    {
        // Anything within a twentieth of a dB of unity is the "off" position as far as a user
        // is concerned, and "+0 dB" reads as a bug.
        if (MathF.Abs(db) < 0.05f) return "Off";
        return db > 0f ? $"+{db:0.#} dB" : $"{db:0.#} dB";
    }
}
