namespace MicBooster.Audio.Dsp;

/// <summary>
/// A metering tap. It reads a mono block and never writes to it, which is why it is not an
/// <see cref="IAudioProcessor"/> — the chain places one before and one after processing.
/// </summary>
/// <remarks>
/// <see cref="Analyze"/> runs on the capture thread and follows the same rules as a
/// processing stage: no allocation, no locks, no throwing. The four readback properties are
/// written here once per block and read by the UI timer, so each is a single 32-bit field.
/// </remarks>
public sealed class LevelAnalyzer
{
    /// <summary>Sample magnitude at which we call it clipped. Just short of full scale.</summary>
    private const float ClipLevel = 0.999f;

    /// <summary>Ballistics for the peak readout: fast attack, and this much fall per second.</summary>
    private const float PeakDecayDbPerSecond = 20f;

    private const float RmsWindowMs = 300f;
    private const float LoudnessWindowMs = 400f;

    private volatile float _peakDb = DspMath.MinusInfinityDb;
    private volatile float _rmsDb = DspMath.MinusInfinityDb;
    private volatile float _loudnessDb = DspMath.MinusInfinityDb;
    private volatile bool _clipped;

    private float _peakHold;
    private float _peakDecay;
    private float _meanSquare;
    private float _loudnessMeanSquare;
    private float _rmsCoefficient;
    private float _loudnessCoefficient;
    private int _sampleRate;

    /// <summary>Sample peak with meter decay applied, in dBFS.</summary>
    public float PeakDb => _peakDb;

    /// <summary>Sliding RMS over roughly 300 ms, in dBFS.</summary>
    public float RmsDb => _rmsDb;

    /// <summary>
    /// Slower integration (~400 ms) standing in for programme loudness. There is no
    /// K-weighting, so it is not true LUFS; it only has to be stable and self-consistent
    /// for the auto-level rider and the loudness readout.
    /// </summary>
    public float LoudnessDb => _loudnessDb;

    /// <summary>Latches once any sample reaches full scale, until <see cref="ClearClip"/>.</summary>
    public bool Clipped => _clipped;

    /// <summary>Recompute ballistics for the device's rate. Safe to call while stopped only.</summary>
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 0;
        if (_sampleRate == 0)
        {
            _peakDecay = 0f;
            _rmsCoefficient = 0f;
            _loudnessCoefficient = 0f;
            return;
        }

        _peakDecay = DspMath.DbToLinear(-PeakDecayDbPerSecond / _sampleRate);
        _rmsCoefficient = DspMath.TimeConstantCoefficient(RmsWindowMs, _sampleRate);
        _loudnessCoefficient = DspMath.TimeConstantCoefficient(LoudnessWindowMs, _sampleRate);
    }

    /// <summary>Drop all history and park the readouts at silence.</summary>
    public void Reset()
    {
        _peakHold = 0f;
        _meanSquare = 0f;
        _loudnessMeanSquare = 0f;
        _peakDb = DspMath.MinusInfinityDb;
        _rmsDb = DspMath.MinusInfinityDb;
        _loudnessDb = DspMath.MinusInfinityDb;
        _clipped = false;
    }

    /// <summary>Clear the clip latch (the UI calls this when the user acknowledges it).</summary>
    public void ClearClip() => _clipped = false;

    /// <summary>
    /// Measure <paramref name="count"/> mono samples starting at <paramref name="offset"/>.
    /// The buffer is read only.
    /// </summary>
    public void Analyze(float[] buffer, int offset, int count)
    {
        if (_sampleRate <= 0) return;
        if (count <= 0 || offset < 0 || offset + count > buffer.Length) return;

        float peak = _peakHold;
        float decay = _peakDecay;
        float rmsCoefficient = _rmsCoefficient;
        float loudnessCoefficient = _loudnessCoefficient;
        float meanSquare = _meanSquare;
        float loudnessMeanSquare = _loudnessMeanSquare;
        bool clipped = false;

        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            float x = DspMath.Sanitize(buffer[i]);
            float magnitude = MathF.Abs(x);

            if (magnitude >= ClipLevel) clipped = true;

            if (magnitude > peak) peak = magnitude;
            else peak *= decay;

            // One-pole on the squared signal: y = a*y + (1-a)*x, arranged to save a multiply.
            float square = x * x;
            meanSquare = square + (meanSquare - square) * rmsCoefficient;
            loudnessMeanSquare = square + (loudnessMeanSquare - square) * loudnessCoefficient;
        }

        _peakHold = DspMath.FlushDenormal(DspMath.Sanitize(peak));
        _meanSquare = DspMath.FlushDenormal(DspMath.Sanitize(meanSquare));
        _loudnessMeanSquare = DspMath.FlushDenormal(DspMath.Sanitize(loudnessMeanSquare));

        _peakDb = DspMath.LinearToDb(_peakHold);
        _rmsDb = DspMath.LinearToDb(MathF.Sqrt(MathF.Max(_meanSquare, 0f)));
        _loudnessDb = DspMath.LinearToDb(MathF.Sqrt(MathF.Max(_loudnessMeanSquare, 0f)));

        // Only ever set the latch here; clearing is the UI's call.
        if (clipped) _clipped = true;
    }
}
