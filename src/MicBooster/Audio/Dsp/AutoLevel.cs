namespace MicBooster.Audio.Dsp;

/// <summary>
/// Slow gain rider that holds long-term loudness at a target, so the microphone sounds the
/// same whether the speaker is leaning into it or sitting back from it.
/// </summary>
/// <remarks>
/// This is not a compressor and must not behave like one: it measures loudness over a long
/// window and walks a single broad gain toward the correction, so individual syllables are
/// left alone. Two things stop it doing damage. Anything below -60 dBFS counts as silence and
/// is not measured at all, and it can be told to hold via <see cref="Frozen"/> while the gate
/// is shut. Both matter for the same reason: a rider that keeps integrating through silence
/// winds itself up on room noise and then blasts the first word back.
/// </remarks>
public sealed class AutoLevel : IAudioProcessor
{
    /// <summary>Loudness integration time. Long on purpose; this is a programme-level measure.</summary>
    private const float IntegratorMs = 400f;

    /// <summary>
    /// Release of the fast envelope that decides whether anything is being said. The loudness
    /// integrator cannot make that call: being 400 ms long it follows a decaying tail down for
    /// a second afterwards, and the rider would boost all the way up on the way.
    /// </summary>
    private const float PresenceReleaseMs = 15f;

    /// <summary>Below this the signal counts as silence and the gain is held where it is.</summary>
    private const float SilenceFloorDb = -60f;

    /// <summary>Integration time used until the integrator has filled, straight after a reset.</summary>
    private const float PrimingMs = 25f;

    /// <summary>How long the fast priming integration lasts once signal appears.</summary>
    private const float PrimingWindowMs = 100f;

    /// <summary>
    /// How often the loudness measurement is turned into a new gain target. A few tenths of a
    /// millisecond of granularity is far finer than a 400 ms integrator can resolve, and it
    /// keeps the per-sample cost down to the gain ramp itself.
    /// </summary>
    private const int TargetIntervalSamples = 32;

    private const float RampEpsilonDb = 0.01f;

    /// <summary>Per-buffer relaxation applied while the stage is disabled.</summary>
    private const float IdleDecay = 0.5f;

    private volatile bool _enabled = true;
    private volatile float _targetDb = -16f;
    private volatile float _maxBoostDb = 18f;
    private volatile float _maxCutDb = 12f;
    private volatile float _speedMs = 400f;
    private volatile bool _frozen;
    private volatile bool _resetPending;

    private volatile float _gainReadbackDb;

    private int _sampleRate = 48000;
    private float _integratorCoefficient;
    private float _presenceReleaseCoefficient;
    private float _primingCoefficient;
    private int _primingWindowSamples;
    private float _speedCoefficient;
    private float _appliedSpeedMs = float.NaN;

    private float _meanSquare;
    private float _presence;
    private int _primingRemaining;
    private float _gainDb;
    private float _desiredGainDb;
    private bool _signalPresent;
    private bool _measurementValid;

    /// <summary>Creates a rider with valid coefficients, before any <see cref="Prepare"/> call.</summary>
    public AutoLevel()
    {
        Prepare(_sampleRate);
        Reset();
    }

    /// <inheritdoc />
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Programme loudness the rider aims for, in dBFS.</summary>
    public float TargetDb
    {
        get => _targetDb;
        set => _targetDb = DspMath.Clamp(value, -40f, -6f);
    }

    /// <summary>Most the rider may turn the signal up, in dB.</summary>
    public float MaxBoostDb
    {
        get => _maxBoostDb;
        set => _maxBoostDb = DspMath.Clamp(value, 0f, 40f);
    }

    /// <summary>Most the rider may turn the signal down, in dB.</summary>
    public float MaxCutDb
    {
        get => _maxCutDb;
        set => _maxCutDb = DspMath.Clamp(value, 0f, 40f);
    }

    /// <summary>How quickly the applied gain walks toward the correction, in ms.</summary>
    public float SpeedMs
    {
        get => _speedMs;
        set => _speedMs = DspMath.Clamp(value, 50f, 5000f);
    }

    /// <summary>
    /// While true the rider holds its current gain and stops measuring. The chain sets this
    /// whenever the gate is shut, so silence cannot drag the measurement around.
    /// </summary>
    public bool Frozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    /// <summary>Gain currently being applied, positive or negative dB. Metering readback.</summary>
    public float CurrentGainDb => _gainReadbackDb;

    /// <summary>
    /// Asks the rider to drop back to unity. Safe to call from the UI thread while running;
    /// the audio thread performs the clear at the next block boundary.
    /// </summary>
    public void ResetGain()
    {
        _resetPending = true;
        _gainReadbackDb = 0f; // so the meter answers immediately even when the engine is stopped
    }

    /// <inheritdoc />
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _integratorCoefficient = DspMath.TimeConstantCoefficient(IntegratorMs, _sampleRate);
        _presenceReleaseCoefficient = DspMath.TimeConstantCoefficient(PresenceReleaseMs, _sampleRate);
        _primingCoefficient = DspMath.TimeConstantCoefficient(PrimingMs, _sampleRate);
        _primingWindowSamples = (int)(PrimingWindowMs * 0.001f * _sampleRate);
        _appliedSpeedMs = float.NaN;
        UpdateCoefficients();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _resetPending = false;
        ClearMeasurement();
        _gainDb = 0f;
        _desiredGainDb = 0f;
        _gainReadbackDb = 0f;
    }

    /// <inheritdoc />
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer is null || count <= 0 || offset < 0) return;
        int end = offset + count;
        if (end > buffer.Length) return;

        if (_resetPending)
        {
            _resetPending = false;
            ClearMeasurement();
            _gainDb = 0f;
            _desiredGainDb = 0f;
        }

        if (!_enabled)
        {
            // Unwind the held gain so re-enabling does not reintroduce it in one step.
            _gainDb *= IdleDecay;
            if (MathF.Abs(_gainDb) < RampEpsilonDb) _gainDb = 0f;
            _desiredGainDb = 0f;
            ClearMeasurement();
            _gainReadbackDb = _gainDb;
            return;
        }

        UpdateCoefficients();

        float targetLoudnessDb = _targetDb;
        float maxBoostDb = _maxBoostDb;
        float maxCutDb = _maxCutDb;
        bool frozen = _frozen;
        float integratorCoefficient = _integratorCoefficient;
        float primingCoefficient = _primingCoefficient;
        float presenceRelease = _presenceReleaseCoefficient;
        float speedCoefficient = _speedCoefficient;

        // The limits may have been tightened while we were sitting on an extreme, so bring the
        // target inside them and allow the ramp to run even through silence until we are legal.
        _desiredGainDb = DspMath.Clamp(_desiredGainDb, -maxCutDb, maxBoostDb);
        bool outOfRange = _gainDb > maxBoostDb + RampEpsilonDb || _gainDb < -maxCutDb - RampEpsilonDb;

        int countdown = 0;

        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float rectified = MathF.Abs(x);

            _presence = rectified > _presence ? rectified : _presence * presenceRelease;
            _presence = DspMath.StabilizeState(_presence);

            if (--countdown <= 0)
            {
                countdown = TargetIntervalSamples;
                _signalPresent = DspMath.LinearToDb(_presence) > SilenceFloorDb;

                float loudnessDb = DspMath.LinearToDb(MathF.Sqrt(_meanSquare));
                _measurementValid = _signalPresent && loudnessDb > SilenceFloorDb;
                if (_measurementValid && !frozen)
                {
                    _desiredGainDb = DspMath.Clamp(targetLoudnessDb - loudnessDb, -maxCutDb, maxBoostDb);
                }
            }

            if (_signalPresent && !frozen)
            {
                float square = x * x;

                // Straight after a reset the integrator holds nothing, so fill it fast rather
                // than letting the rider act on a measurement that is still on its way up.
                float coefficient = integratorCoefficient;
                if (_primingRemaining > 0)
                {
                    _primingRemaining--;
                    coefficient = primingCoefficient;
                }

                _meanSquare = square + (_meanSquare - square) * coefficient;
                if (_meanSquare < 0f) _meanSquare = 0f;
                _meanSquare = DspMath.StabilizeState(_meanSquare);
            }

            if (outOfRange || (_measurementValid && !frozen))
            {
                _gainDb = _desiredGainDb + (_gainDb - _desiredGainDb) * speedCoefficient;
                _gainDb = DspMath.StabilizeState(_gainDb);
            }

            float gain = MathF.Abs(_gainDb) < RampEpsilonDb ? 1f : DspMath.DbToLinear(_gainDb);
            buffer[i] = DspMath.Sanitize(x * gain);
        }

        _gainReadbackDb = DspMath.Sanitize(_gainDb);
    }

    private void ClearMeasurement()
    {
        _meanSquare = 0f;
        _presence = 0f;
        _primingRemaining = _primingWindowSamples;
        _signalPresent = false;
        _measurementValid = false;
    }

    private void UpdateCoefficients()
    {
        float speedMs = _speedMs;
        if (speedMs != _appliedSpeedMs)
        {
            _speedCoefficient = DspMath.TimeConstantCoefficient(speedMs, _sampleRate);
            _appliedSpeedMs = speedMs;
        }
    }
}
