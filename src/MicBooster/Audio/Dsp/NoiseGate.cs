namespace MicBooster.Audio.Dsp;

/// <summary>
/// Downward expander that ducks the microphone between phrases, so a big input boost
/// does not also bring up room noise, fan hum and keyboard clatter.
/// </summary>
/// <remarks>
/// The gate opens when the detector envelope crosses <see cref="ThresholdDb"/> but only
/// closes once it falls back below <c>ThresholdDb - HysteresisDb</c>. A gate with a single
/// threshold chatters on any signal that happens to sit near it; the second threshold plus
/// <see cref="HoldMs"/> are what stop that, and stop word endings and breaths being chopped.
/// Closed means attenuated by <see cref="RangeDb"/>, never hard muted, because an abrupt
/// cut to digital silence is far more noticeable than a duck.
/// </remarks>
public sealed class NoiseGate : IAudioProcessor
{
    // The detector is deliberately independent of the gain ramp: it has to see a syllable
    // starting within a millisecond regardless of how slowly the user wants the gain to move.
    private const float DetectorAttackMs = 1f;
    private const float DetectorReleaseMs = 30f;

    /// <summary>How close to the ramp target counts as arrived (a one-pole never quite lands).</summary>
    private const float SettleDb = 0.05f;

    /// <summary>Per-buffer relaxation applied while the stage is disabled.</summary>
    private const float IdleDecay = 0.5f;

    private enum GateState : byte
    {
        Closed,
        Attacking,
        Open,
        Holding,
        Releasing
    }

    private volatile bool _enabled = true;
    private volatile float _thresholdDb = -45f;
    private volatile float _hysteresisDb = 6f;
    private volatile float _attackMs = 2f;
    private volatile float _holdMs = 120f;
    private volatile float _releaseMs = 180f;
    private volatile float _rangeDb = 24f;

    private volatile float _attenuationDb;
    private volatile bool _isOpen;

    private int _sampleRate = 48000;
    private float _detectorAttack;
    private float _detectorRelease;
    private float _attackCoefficient;
    private float _releaseCoefficient;
    private int _holdSamples;

    // Last values the coefficients were computed from, so Process only pays for MathF.Exp
    // when the user actually moved something.
    private float _appliedAttackMs = float.NaN;
    private float _appliedReleaseMs = float.NaN;
    private float _appliedHoldMs = float.NaN;

    private float _detector;
    private float _gainDb;
    private int _holdRemaining;
    private GateState _state = GateState.Closed;

    /// <summary>Creates a gate with valid coefficients, before any <see cref="Prepare"/> call.</summary>
    public NoiseGate()
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

    /// <summary>Level the detector must exceed for the gate to open, in dBFS.</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set => _thresholdDb = DspMath.Clamp(value, -90f, 0f);
    }

    /// <summary>How far below <see cref="ThresholdDb"/> the signal must fall before closing, in dB.</summary>
    public float HysteresisDb
    {
        get => _hysteresisDb;
        set => _hysteresisDb = DspMath.Clamp(value, 0f, 24f);
    }

    /// <summary>Time the gain takes to open, in ms.</summary>
    public float AttackMs
    {
        get => _attackMs;
        set => _attackMs = DspMath.Clamp(value, 0.1f, 200f);
    }

    /// <summary>How long the gate stays open after the signal drops below the close threshold, in ms.</summary>
    public float HoldMs
    {
        get => _holdMs;
        set => _holdMs = DspMath.Clamp(value, 0f, 2000f);
    }

    /// <summary>Time the gain takes to close once the hold has expired, in ms.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set => _releaseMs = DspMath.Clamp(value, 1f, 3000f);
    }

    /// <summary>Attenuation applied when closed, in dB. Small values duck, large values effectively mute.</summary>
    public float RangeDb
    {
        get => _rangeDb;
        set => _rangeDb = DspMath.Clamp(value, 0f, 80f);
    }

    /// <summary>
    /// How much the gate is ducking right now, as a value at or below zero.
    /// Zero means fully open. Metering readback.
    /// </summary>
    public float CurrentAttenuationDb => _attenuationDb;

    /// <summary>
    /// True while the gate is holding the signal path open (opening, open, or in its hold
    /// window). It goes false as soon as the gate decides to close, and while disabled.
    /// </summary>
    public bool IsOpen => _isOpen;

    /// <inheritdoc />
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _detectorAttack = DspMath.TimeConstantCoefficient(DetectorAttackMs, _sampleRate);
        _detectorRelease = DspMath.TimeConstantCoefficient(DetectorReleaseMs, _sampleRate);
        _appliedAttackMs = float.NaN;
        _appliedReleaseMs = float.NaN;
        _appliedHoldMs = float.NaN;
        UpdateCoefficients();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _detector = 0f;
        _holdRemaining = 0;
        _state = GateState.Closed;
        _gainDb = -_rangeDb;
        _attenuationDb = 0f;
        _isOpen = false;
    }

    /// <inheritdoc />
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer is null || count <= 0 || offset < 0) return;
        int end = offset + count;
        if (end > buffer.Length) return;

        float rangeDb = _rangeDb;

        if (!_enabled)
        {
            // Relax toward fully open, so switching the gate back on cannot slam it shut
            // in the middle of a word, and let the meter fall back to rest.
            _gainDb *= IdleDecay;
            if (MathF.Abs(_gainDb) < SettleDb) _gainDb = 0f;
            _state = GateState.Open;
            _holdRemaining = 0;
            _detector = 0f;
            _attenuationDb = 0f;
            _isOpen = false;
            return;
        }

        UpdateCoefficients();

        float openDb = _thresholdDb;
        float closeDb = openDb - _hysteresisDb;
        float detectorAttack = _detectorAttack;
        float detectorRelease = _detectorRelease;
        float attackCoefficient = _attackCoefficient;
        float releaseCoefficient = _releaseCoefficient;
        int holdSamples = _holdSamples;

        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float rectified = MathF.Abs(x);

            _detector = rectified + (_detector - rectified) *
                        (rectified > _detector ? detectorAttack : detectorRelease);
            _detector = DspMath.FlushDenormal(_detector);

            float detectorDb = DspMath.LinearToDb(_detector);
            bool above = detectorDb >= openDb;
            bool below = detectorDb < closeDb;

            switch (_state)
            {
                case GateState.Closed:
                    if (above) _state = GateState.Attacking;
                    break;

                case GateState.Attacking:
                    if (_gainDb > -SettleDb)
                    {
                        _gainDb = 0f;
                        _state = GateState.Open;
                    }
                    else if (below)
                    {
                        _state = GateState.Holding;
                        _holdRemaining = holdSamples;
                    }
                    break;

                case GateState.Open:
                    if (below)
                    {
                        _state = GateState.Holding;
                        _holdRemaining = holdSamples;
                    }
                    break;

                case GateState.Holding:
                    if (above)
                    {
                        _state = _gainDb > -SettleDb ? GateState.Open : GateState.Attacking;
                    }
                    else if (--_holdRemaining <= 0)
                    {
                        _state = GateState.Releasing;
                    }
                    break;

                case GateState.Releasing:
                    if (above)
                    {
                        _state = GateState.Attacking;
                    }
                    else if (_gainDb <= -rangeDb + SettleDb)
                    {
                        _gainDb = -rangeDb;
                        _state = GateState.Closed;
                    }
                    break;
            }

            bool closing = _state == GateState.Releasing || _state == GateState.Closed;
            float target = closing ? -rangeDb : 0f;
            _gainDb = target + (_gainDb - target) * (closing ? releaseCoefficient : attackCoefficient);
            if (_gainDb < -rangeDb) _gainDb = -rangeDb; // RangeDb may have just been reduced
            _gainDb = DspMath.FlushDenormal(_gainDb);

            // Skipping the exp while fully open matters: that is the state we are in
            // for most of every spoken phrase.
            float gain = _gainDb > -SettleDb ? 1f : DspMath.DbToLinear(_gainDb);
            buffer[i] = DspMath.Sanitize(x * gain);
        }

        _attenuationDb = DspMath.Clamp(_gainDb, -rangeDb, 0f);
        _isOpen = _state is GateState.Attacking or GateState.Open or GateState.Holding;
    }

    private void UpdateCoefficients()
    {
        float attackMs = _attackMs;
        if (attackMs != _appliedAttackMs)
        {
            _attackCoefficient = DspMath.TimeConstantCoefficient(attackMs, _sampleRate);
            _appliedAttackMs = attackMs;
        }

        float releaseMs = _releaseMs;
        if (releaseMs != _appliedReleaseMs)
        {
            _releaseCoefficient = DspMath.TimeConstantCoefficient(releaseMs, _sampleRate);
            _appliedReleaseMs = releaseMs;
        }

        float holdMs = _holdMs;
        if (holdMs != _appliedHoldMs)
        {
            _holdSamples = (int)(holdMs * 0.001f * _sampleRate);
            if (_holdSamples < 0) _holdSamples = 0;
            _appliedHoldMs = holdMs;
        }
    }
}
