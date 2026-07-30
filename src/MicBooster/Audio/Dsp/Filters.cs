namespace MicBooster.Audio.Dsp;

/// <summary>
/// Second-order Butterworth high-pass, the first thing in the chain. It removes desk
/// rumble, HVAC and plosive energy that would otherwise dominate the level detectors
/// and get amplified along with the voice.
/// </summary>
/// <remarks>
/// This is a hand-rolled biquad rather than <c>NAudio.Dsp.BiQuadFilter</c> because that
/// type exposes no way to clear its state, and a stale tail must not survive a device or
/// sample-rate change. Runs as a transposed direct form II, which keeps the two state
/// words in the same order of magnitude as the signal and so behaves well in float.
/// </remarks>
public sealed class HighPassFilter : IAudioProcessor
{
    /// <summary>Butterworth Q: maximally flat passband, no resonant bump at the corner.</summary>
    private const float Q = 0.70710678f;

    private const float MinCutoffHz = 20f;
    private const float MaxCutoffHz = 500f;
    private const float DefaultCutoffHz = 80f;

    private volatile bool _enabled = true;
    private volatile float _cutoffHz = DefaultCutoffHz;

    // Coefficients and state belong to the audio thread alone.
    private float _b0 = 1f;
    private float _b1;
    private float _b2;
    private float _a1;
    private float _a2;
    private float _s1;
    private float _s2;
    private int _sampleRate;

    // NaN never equals anything, so the first Process after construction always recomputes.
    private float _appliedCutoffHz = float.NaN;

    /// <inheritdoc/>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// Corner frequency in Hz. Clamped on use to 20..min(500, sampleRate * 0.45), so it
    /// can never be pushed past Nyquist on a low-rate device.
    /// </summary>
    public float CutoffHz
    {
        get => _cutoffHz;
        set => _cutoffHz = DspMath.IsFinite(value) ? value : DefaultCutoffHz;
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate;
        if (sampleRate > 0)
        {
            ApplyCutoff(ClampCutoff(_cutoffHz));
        }
    }

    /// <inheritdoc/>
    public void Process(float[] buffer, int offset, int count)
    {
        if (!_enabled || _sampleRate <= 0) return;
        if (count <= 0 || offset < 0 || offset + count > buffer.Length) return;

        // Coefficient work happens once per block, never per sample.
        float wanted = ClampCutoff(_cutoffHz);
        if (wanted != _appliedCutoffHz) ApplyCutoff(wanted);

        float b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
        float s1 = _s1, s2 = _s2;

        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float y = b0 * x + s1;
            s1 = b1 * x - a1 * y + s2;
            s2 = b2 * x - a2 * y;
            buffer[i] = y;
        }

        _s1 = DspMath.FlushDenormal(DspMath.Sanitize(s1));
        _s2 = DspMath.FlushDenormal(DspMath.Sanitize(s2));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _s1 = 0f;
        _s2 = 0f;
    }

    private float ClampCutoff(float hz)
    {
        float max = MathF.Min(MaxCutoffHz, _sampleRate * 0.45f);
        if (max < MinCutoffHz) max = MinCutoffHz; // pathological rate; keep the range non-empty
        return DspMath.Clamp(hz, MinCutoffHz, max);
    }

    /// <summary>Audio-EQ-cookbook high-pass, normalised by a0.</summary>
    private void ApplyCutoff(float cutoffHz)
    {
        float w0 = 2f * MathF.PI * cutoffHz / _sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * Q);
        float inverseA0 = 1f / (1f + alpha);
        float onePlusCos = 1f + cosW0;

        _b0 = 0.5f * onePlusCos * inverseA0;
        _b1 = -onePlusCos * inverseA0;
        _b2 = _b0;
        _a1 = -2f * cosW0 * inverseA0;
        _a2 = (1f - alpha) * inverseA0;
        _appliedCutoffHz = cutoffHz;
    }
}

/// <summary>
/// One-pole DC blocker sitting just under the audible band (~4 Hz). Some USB interfaces
/// hand back a constant offset, and a DC pedestal steals headroom from the limiter and
/// biases every level measurement upward without being audible.
/// </summary>
public sealed class DcBlocker : IAudioProcessor
{
    /// <summary>Pole radius at the reference rate; equates to roughly a 4 Hz corner.</summary>
    private const float ReferenceR = 0.9995f;

    private const float ReferenceRate = 48000f;

    private volatile bool _enabled = true;

    private float _r = ReferenceR;
    private float _x1;
    private float _y1;

    /// <inheritdoc/>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <inheritdoc/>
    public void Prepare(int sampleRate)
    {
        // R^(ref/fs) holds the corner at the same frequency in Hz on any device rate.
        _r = sampleRate > 0
            ? DspMath.Clamp(MathF.Pow(ReferenceR, ReferenceRate / sampleRate), 0.9f, 0.99999f)
            : ReferenceR;
    }

    /// <inheritdoc/>
    public void Process(float[] buffer, int offset, int count)
    {
        if (!_enabled) return;
        if (count <= 0 || offset < 0 || offset + count > buffer.Length) return;

        float r = _r;
        float x1 = _x1, y1 = _y1;

        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float y = x - x1 + r * y1;
            x1 = x;
            y1 = y;
            buffer[i] = y;
        }

        _x1 = DspMath.FlushDenormal(DspMath.Sanitize(x1));
        _y1 = DspMath.FlushDenormal(DspMath.Sanitize(y1));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _x1 = 0f;
        _y1 = 0f;
    }
}
