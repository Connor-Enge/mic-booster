using System.Runtime.CompilerServices;

namespace MicBooster.Audio.Dsp;

/// <summary>
/// True lookahead brick wall. Last stage in the chain, and the reason the boost controls
/// can be pushed hard without the output ever clipping.
/// </summary>
/// <remarks>
/// The signal is delayed by the lookahead while the gain is computed from the peak of the
/// window that ends at the sample now being emitted. Because the offending peak is already
/// known when the gain is needed, the reduction is taken instantly instead of ramped, which
/// is what removes the distortion a zero-lookahead limiter produces on transients. The
/// window maximum is tracked with a monotonic wedge, so the cost per sample is constant and
/// no rescan can ever happen on the audio thread.
/// </remarks>
public sealed class Limiter : IAudioProcessor
{
    /// <summary>Hard cap on lookahead. The delay line is sized for this so nothing allocates later.</summary>
    private const float MaxLookaheadMs = 10f;

    /// <summary>Per-buffer decay of the metering readback while the stage is disabled.</summary>
    private const float IdleDecay = 0.5f;

    private volatile bool _enabled = true;
    private volatile float _ceilingDb = -1f;
    private volatile float _releaseMs = 60f;
    private volatile float _lookaheadMs = 3f;

    private volatile float _reductionReadbackDb;
    private volatile int _delaySamples;

    private int _sampleRate = 48000;
    private float[] _delay = Array.Empty<float>();
    private float[] _wedgeValues = Array.Empty<float>();
    private long[] _wedgeIndices = Array.Empty<long>();
    private int _capacity;
    private int _maxDelaySamples;
    private int _window = 1;
    private int _writeIndex;
    private int _wedgeHead;
    private int _wedgeCount;
    private long _sampleIndex;

    private float _gain = 1f;
    private float _releaseCoefficient;
    private float _appliedReleaseMs = float.NaN;
    private float _appliedLookaheadMs = float.NaN;

    /// <summary>True when the delay line holds audio from before a bypass, and must not be played out.</summary>
    private bool _delayStale;

    /// <summary>Creates a limiter with a delay line sized for the maximum lookahead at 48 kHz.</summary>
    public Limiter()
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

    /// <summary>Absolute output ceiling in dBFS. Nothing leaves this stage above it.</summary>
    public float CeilingDb
    {
        get => _ceilingDb;
        set => _ceilingDb = DspMath.Clamp(value, -12f, 0f);
    }

    /// <summary>How fast gain recovers once the peak has passed, in ms.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set => _releaseMs = DspMath.Clamp(value, 1f, 1000f);
    }

    /// <summary>Lookahead in ms, 0 to 10. This is added latency, so it is the user's call.</summary>
    public float LookaheadMs
    {
        get => _lookaheadMs;
        set => _lookaheadMs = DspMath.Clamp(value, 0f, MaxLookaheadMs);
    }

    /// <summary>
    /// Largest gain reduction over the last processed block, as a positive dB amount.
    /// Metering readback.
    /// </summary>
    public float CurrentReductionDb => _reductionReadbackDb;

    /// <summary>
    /// Delay currently imposed on the signal, in samples. Zero while disabled or while the
    /// lookahead is zero, since in both cases nothing is delayed.
    /// </summary>
    public int LatencySamples => _enabled ? _delaySamples : 0;

    /// <inheritdoc />
    public void Prepare(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;

        // Size for the maximum lookahead, not the current setting: a later parameter change
        // then costs some book-keeping instead of an allocation on the audio thread.
        _maxDelaySamples = (int)MathF.Ceiling(MaxLookaheadMs * 0.001f * _sampleRate);
        if (_maxDelaySamples < 1) _maxDelaySamples = 1;

        // One spare slot so the wedge, which holds at most a full window, always has room to push.
        _capacity = _maxDelaySamples + 2;

        if (_delay.Length < _maxDelaySamples) _delay = new float[_maxDelaySamples];
        if (_wedgeValues.Length < _capacity)
        {
            _wedgeValues = new float[_capacity];
            _wedgeIndices = new long[_capacity];
        }

        _appliedReleaseMs = float.NaN;
        _appliedLookaheadMs = float.NaN;
        UpdateCoefficients();
        ApplyLookahead(_lookaheadMs);
    }

    /// <inheritdoc />
    public void Reset()
    {
        FlushDelay();
        _delayStale = false;
        _gain = 1f;
        _reductionReadbackDb = 0f;
    }

    /// <inheritdoc />
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer is null || count <= 0 || offset < 0) return;
        int end = offset + count;
        if (end > buffer.Length) return;

        if (!_enabled)
        {
            _gain = 1f;
            _delayStale = true;
            float decayed = _reductionReadbackDb * IdleDecay;
            _reductionReadbackDb = decayed < 0.01f ? 0f : decayed;
            return;
        }

        if (_delayStale)
        {
            // Whatever is in the delay line predates the bypass; emitting it would repeat
            // audio the listener already heard.
            FlushDelay();
            _delayStale = false;
        }

        UpdateCoefficients();

        float lookaheadMs = _lookaheadMs;
        if (lookaheadMs != _appliedLookaheadMs) ApplyLookahead(lookaheadMs);

        float ceiling = DspMath.DbToLinear(_ceilingDb);
        if (ceiling < DspMath.SilenceThreshold) ceiling = DspMath.SilenceThreshold; // never divide by zero

        float release = _releaseCoefficient;
        int delaySamples = _delaySamples;
        float minGain = 1f;

        if (delaySamples <= 0)
        {
            for (int i = offset; i < end; i++)
            {
                float x = DspMath.Sanitize(buffer[i]);
                float required = RequiredGain(MathF.Abs(x), ceiling);
                AdvanceGain(required, release);
                buffer[i] = Clip(x * _gain, ceiling);
                if (_gain < minGain) minGain = _gain;
            }
        }
        else
        {
            int window = _window;
            for (int i = offset; i < end; i++)
            {
                float x = DspMath.Sanitize(buffer[i]);

                // The window ends at this input sample and starts at the sample about to be
                // emitted, so the peak is seen exactly one lookahead before it is needed.
                float peak = SlidingMax(MathF.Abs(x), window);
                AdvanceGain(RequiredGain(peak, ceiling), release);

                float delayed = _delay[_writeIndex];
                _delay[_writeIndex] = x;
                if (++_writeIndex >= delaySamples) _writeIndex = 0;

                buffer[i] = Clip(delayed * _gain, ceiling);
                if (_gain < minGain) minGain = _gain;
            }
        }

        _reductionReadbackDb = DspMath.Clamp(-DspMath.LinearToDb(minGain), 0f, -DspMath.MinusInfinityDb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RequiredGain(float peak, float ceiling)
        => peak > ceiling ? ceiling / peak : 1f;

    /// <summary>
    /// Takes reduction immediately and gives it back over the release time. Instant attack is
    /// the whole point of lookahead, and it keeps the applied gain at or below what the
    /// upcoming peak requires.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceGain(float required, float release)
    {
        _gain = required < _gain ? required : required + (_gain - required) * release;
        if (_gain > 1f) _gain = 1f;
        else if (_gain < 0f) _gain = 0f;
    }

    /// <summary>Final backstop, so no pathological transient can put a sample over the ceiling.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clip(float value, float ceiling)
    {
        value = DspMath.Sanitize(value);
        if (value > ceiling) return ceiling;
        if (value < -ceiling) return -ceiling;
        return value;
    }

    /// <summary>
    /// Maximum of the last <paramref name="window"/> rectified samples, kept in a monotonic
    /// wedge: entries smaller than an incoming sample can never be the maximum again, so each
    /// sample is pushed and popped at most once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float SlidingMax(float value, int window)
    {
        long n = _sampleIndex++;
        int capacity = _capacity;

        while (_wedgeCount > 0 && _wedgeIndices[_wedgeHead] + window <= n)
        {
            if (++_wedgeHead >= capacity) _wedgeHead = 0;
            _wedgeCount--;
        }

        while (_wedgeCount > 0)
        {
            int tail = _wedgeHead + _wedgeCount - 1;
            if (tail >= capacity) tail -= capacity;
            if (_wedgeValues[tail] > value) break;
            _wedgeCount--;
        }

        int slot = _wedgeHead + _wedgeCount;
        if (slot >= capacity) slot -= capacity;
        _wedgeValues[slot] = value;
        _wedgeIndices[slot] = n;
        _wedgeCount++;

        return _wedgeValues[_wedgeHead];
    }

    /// <summary>
    /// Re-derives the delay length from the lookahead setting. Only ever shortens or lengthens
    /// within the buffer allocated by <see cref="Prepare"/>, so it is safe on the audio thread.
    /// </summary>
    private void ApplyLookahead(float milliseconds)
    {
        int samples = (int)MathF.Round(DspMath.Clamp(milliseconds, 0f, MaxLookaheadMs) * 0.001f * _sampleRate);
        if (samples < 0) samples = 0;
        if (samples > _maxDelaySamples) samples = _maxDelaySamples;

        _delaySamples = samples;
        _window = samples + 1;
        _appliedLookaheadMs = milliseconds;
        FlushDelay();
    }

    private void FlushDelay()
    {
        if (_delay.Length > 0) Array.Clear(_delay, 0, _delay.Length);
        _writeIndex = 0;
        _wedgeHead = 0;
        _wedgeCount = 0;
        _sampleIndex = 0;
    }

    private void UpdateCoefficients()
    {
        float releaseMs = _releaseMs;
        if (releaseMs != _appliedReleaseMs)
        {
            _releaseCoefficient = DspMath.TimeConstantCoefficient(releaseMs, _sampleRate);
            _appliedReleaseMs = releaseMs;
        }
    }
}
