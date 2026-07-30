namespace MicBooster.Audio.Dsp;

/// <summary>
/// Feed-forward compressor with a soft knee, used to pull the quiet parts of speech up
/// close to the loud parts so the whole signal can then be boosted without the peaks
/// hitting the ceiling.
/// </summary>
/// <remarks>
/// The detector is a hybrid: an RMS envelope gives the natural, un-pumpy loudness tracking
/// that suits a voice, and a peak follower with instant attack is mixed in so plosives and
/// consonant transients are not simply averaged away. Gain is computed in the dB domain and
/// then smoothed on the gain-reduction envelope, which is what makes attack and release
/// behave the way the numbers on the UI promise.
/// </remarks>
public sealed class Compressor : IAudioProcessor
{
    private const float RmsWindowMs = 20f;
    private const float PeakReleaseMs = 25f;

    /// <summary>Share of the detector taken from the peak follower rather than the RMS envelope.</summary>
    private const float PeakWeight = 0.35f;

    /// <summary>
    /// Auto make-up returns only part of the gain the threshold point loses. Returning all of
    /// it consistently overshoots, because speech spends most of its time below the threshold.
    /// </summary>
    private const float AutoMakeupScale = 0.6f;

    private const float MakeupRampMs = 25f;

    /// <summary>Per-buffer decay of the metering readback while the stage is disabled.</summary>
    private const float IdleDecay = 0.5f;

    private volatile bool _enabled = true;
    private volatile float _thresholdDb = -20f;
    private volatile float _ratio = 3f;
    private volatile float _attackMs = 8f;
    private volatile float _releaseMs = 120f;
    private volatile float _kneeDb = 6f;
    private volatile float _makeupDb;
    private volatile bool _autoMakeup = true;

    private volatile float _reductionReadbackDb;

    // Make-up is ramped rather than applied straight, because it jumps whenever the user
    // moves threshold or ratio with auto make-up on.
    private readonly SmoothedParameter _makeup = new(0f, MakeupRampMs);

    private int _sampleRate = 48000;
    private float _msCoefficient;
    private float _peakReleaseCoefficient;
    private float _attackCoefficient;
    private float _releaseCoefficient;
    private float _appliedAttackMs = float.NaN;
    private float _appliedReleaseMs = float.NaN;

    private float _msEnvelope;
    private float _peakEnvelope;
    private float _reductionDb;

    /// <summary>Creates a compressor with valid coefficients, before any <see cref="Prepare"/> call.</summary>
    public Compressor()
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

    /// <summary>Level above which gain reduction starts, in dBFS.</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set => _thresholdDb = DspMath.Clamp(value, -60f, 0f);
    }

    /// <summary>Compression ratio. 1 passes the signal through with make-up only.</summary>
    public float Ratio
    {
        get => _ratio;
        set => _ratio = DspMath.Clamp(value, 1f, 20f);
    }

    /// <summary>How fast gain reduction is applied, in ms.</summary>
    public float AttackMs
    {
        get => _attackMs;
        set => _attackMs = DspMath.Clamp(value, 0.1f, 200f);
    }

    /// <summary>How fast gain reduction recovers, in ms.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set => _releaseMs = DspMath.Clamp(value, 5f, 3000f);
    }

    /// <summary>Width of the soft knee centred on the threshold, in dB. Zero is a hard knee.</summary>
    public float KneeDb
    {
        get => _kneeDb;
        set => _kneeDb = DspMath.Clamp(value, 0f, 24f);
    }

    /// <summary>Manual make-up gain in dB, ignored while <see cref="AutoMakeup"/> is set.</summary>
    public float MakeupDb
    {
        get => _makeupDb;
        set => _makeupDb = DspMath.Clamp(value, -12f, 36f);
    }

    /// <summary>Derive make-up gain from threshold and ratio instead of using <see cref="MakeupDb"/>.</summary>
    public bool AutoMakeup
    {
        get => _autoMakeup;
        set => _autoMakeup = value;
    }

    /// <summary>
    /// Largest gain reduction over the last processed block, as a positive dB amount.
    /// Metering readback.
    /// </summary>
    public float CurrentReductionDb => _reductionReadbackDb;

    /// <inheritdoc />
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _msCoefficient = DspMath.TimeConstantCoefficient(RmsWindowMs, _sampleRate);
        _peakReleaseCoefficient = DspMath.TimeConstantCoefficient(PeakReleaseMs, _sampleRate);
        _appliedAttackMs = float.NaN;
        _appliedReleaseMs = float.NaN;
        UpdateCoefficients();
        _makeup.Prepare(_sampleRate);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _msEnvelope = 0f;
        _peakEnvelope = 0f;
        _reductionDb = 0f;
        _reductionReadbackDb = 0f;
        _makeup.Target = ComputeMakeupDb();
        _makeup.SnapToTarget();
    }

    /// <inheritdoc />
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer is null || count <= 0 || offset < 0) return;
        int end = offset + count;
        if (end > buffer.Length) return;

        if (!_enabled)
        {
            // Let the reduction unwind rather than holding it, so re-enabling does not
            // start out ducking by however much it was ducking when it was switched off.
            _reductionDb *= IdleDecay;
            if (_reductionDb < 0.01f) _reductionDb = 0f;
            _msEnvelope = 0f;
            _peakEnvelope = 0f;
            _reductionReadbackDb = _reductionDb;
            return;
        }

        UpdateCoefficients();

        float thresholdDb = _thresholdDb;
        float kneeDb = _kneeDb;
        float slope = 1f - 1f / _ratio;
        float halfKnee = kneeDb * 0.5f;
        float kneeScale = kneeDb > 0f ? 1f / (2f * kneeDb) : 0f;

        _makeup.Target = ComputeMakeupDb();

        float msCoefficient = _msCoefficient;
        float peakRelease = _peakReleaseCoefficient;
        float attackCoefficient = _attackCoefficient;
        float releaseCoefficient = _releaseCoefficient;
        float maxReductionDb = 0f;

        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float rectified = MathF.Abs(x);

            _peakEnvelope = rectified > _peakEnvelope
                ? rectified
                : rectified + (_peakEnvelope - rectified) * peakRelease;
            _peakEnvelope = DspMath.StabilizeState(_peakEnvelope);

            float square = x * x;
            _msEnvelope = square + (_msEnvelope - square) * msCoefficient;
            _msEnvelope = DspMath.StabilizeState(_msEnvelope);
            if (_msEnvelope < 0f) _msEnvelope = 0f; // rounding can push the one-pole barely negative

            float detector = PeakWeight * _peakEnvelope + (1f - PeakWeight) * MathF.Sqrt(_msEnvelope);
            float overshootDb = DspMath.LinearToDb(detector) - thresholdDb;

            float targetReductionDb;
            if (overshootDb <= -halfKnee)
            {
                targetReductionDb = 0f;
            }
            else if (kneeDb > 0f && overshootDb < halfKnee)
            {
                // Quadratic interpolation across the knee: the curve and its slope are both
                // continuous at each end, which is what keeps the onset of compression inaudible.
                float t = overshootDb + halfKnee;
                targetReductionDb = slope * t * t * kneeScale;
            }
            else
            {
                targetReductionDb = slope * overshootDb;
            }

            _reductionDb = targetReductionDb + (_reductionDb - targetReductionDb) *
                           (targetReductionDb > _reductionDb ? attackCoefficient : releaseCoefficient);
            if (_reductionDb < 0f) _reductionDb = 0f;
            _reductionDb = DspMath.StabilizeState(_reductionDb);

            float gainDb = _makeup.Next() - _reductionDb;
            buffer[i] = DspMath.Sanitize(x * DspMath.DbToLinear(gainDb));

            if (_reductionDb > maxReductionDb) maxReductionDb = _reductionDb;
        }

        _reductionReadbackDb = DspMath.Sanitize(maxReductionDb);
    }

    private float ComputeMakeupDb()
    {
        if (!_autoMakeup) return _makeupDb;

        // Gain the threshold point itself loses, scaled back so the result does not overshoot.
        float thresholdDb = _thresholdDb;
        return -thresholdDb * (1f - 1f / _ratio) * AutoMakeupScale;
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
    }
}
