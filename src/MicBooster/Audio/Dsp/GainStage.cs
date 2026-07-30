namespace MicBooster.Audio.Dsp;

/// <summary>
/// A plain gain trim, used both for the main input boost and the final output trim.
/// </summary>
/// <remarks>
/// The gain is ramped through a <see cref="SmoothedParameter"/> because a slider writing
/// a new multiplier straight into the audio thread produces a step discontinuity, which
/// is audible as a click at every mouse-move event. Once the ramp has converged the loop
/// drops to a single flat multiply, and at exactly unity it does nothing at all.
/// </remarks>
public sealed class GainStage : IAudioProcessor
{
    private const float MinGainDb = DspMath.MinusInfinityDb;
    private const float MaxGainDb = 72f;

    private readonly SmoothedParameter _gain = new(1f);

    private volatile bool _enabled = true;
    private volatile float _gainDb;

    /// <inheritdoc/>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Gain in dB, 0 meaning unity. Garbage values are coerced rather than rejected, since
    /// this is fed straight from a settings file that may have been hand-edited.
    /// </summary>
    public float GainDb
    {
        get => _gainDb;
        set
        {
            float db = DspMath.IsFinite(value) ? DspMath.Clamp(value, MinGainDb, MaxGainDb) : 0f;
            _gainDb = db;
            _gain.Target = DspMath.DbToLinear(db);
        }
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate) => _gain.Prepare(sampleRate);

    /// <inheritdoc/>
    public void Process(float[] buffer, int offset, int count)
    {
        if (!_enabled) return;
        if (count <= 0 || offset < 0 || offset + count > buffer.Length) return;

        int end = offset + count;

        if (_gain.IsSettled())
        {
            // Pin the residual ramp error so the unity test below can actually hit.
            _gain.SnapToTarget();
            float gain = _gain.Current;
            if (gain == 1f) return;

            for (int i = offset; i < end; i++)
            {
                buffer[i] = DspMath.Sanitize(buffer[i]) * gain;
            }

            return;
        }

        for (int i = offset; i < end; i++)
        {
            buffer[i] = DspMath.Sanitize(buffer[i]) * _gain.Next();
        }
    }

    /// <inheritdoc/>
    public void Reset() => _gain.SnapToTarget();
}
