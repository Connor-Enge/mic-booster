using System.Runtime.CompilerServices;

namespace MicBooster.Audio.Dsp;

/// <summary>
/// Shared real-time-safe math helpers for the DSP chain.
/// Everything here is allocation-free and safe to call from the audio thread.
/// </summary>
public static class DspMath
{
    /// <summary>Level treated as digital silence. Anything quieter reports as this.</summary>
    public const float MinusInfinityDb = -100f;

    /// <summary>Smallest linear amplitude we bother representing (matches <see cref="MinusInfinityDb"/>).</summary>
    public const float SilenceThreshold = 1e-5f;

    private const float Ln10Over20 = 0.11512925464970229f; // ln(10)/20
    private const float TwentyOverLn10 = 8.685889638065035f; // 20/ln(10)

    /// <summary>Decibels (dBFS, 0 dB == unity) to a linear gain multiplier.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DbToLinear(float db)
    {
        if (db <= MinusInfinityDb) return 0f;
        return MathF.Exp(db * Ln10Over20);
    }

    /// <summary>Linear amplitude to dBFS. Returns <see cref="MinusInfinityDb"/> for silence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearToDb(float linear)
    {
        linear = MathF.Abs(linear);
        if (linear < SilenceThreshold) return MinusInfinityDb;
        return MathF.Log(linear) * TwentyOverLn10;
    }

    /// <summary>
    /// One-pole smoothing coefficient for a given time constant.
    /// Returns the per-sample decay factor a, used as: y += (x - y) * (1 - a), or y = a*y + (1-a)*x.
    /// A time of 0 (or less) yields 0, i.e. instant response.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float TimeConstantCoefficient(float milliseconds, int sampleRate)
    {
        if (milliseconds <= 0f || sampleRate <= 0) return 0f;
        // Classic exp(-1 / (tau * fs)) form; reaches ~63% of target in `milliseconds`.
        return MathF.Exp(-1f / (milliseconds * 0.001f * sampleRate));
    }

    /// <summary>Clamp helper (float).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float value, float min, float max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>
    /// Kills denormal floats, which can cost orders of magnitude in CPU inside
    /// recursive filters and envelope followers once a signal decays to silence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FlushDenormal(float value)
        => MathF.Abs(value) < 1e-18f ? 0f : value;

    /// <summary>True when the value is finite (guards against NaN/Inf poisoning the chain).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    /// <summary>
    /// Replaces NaN/Infinity with 0. A single NaN entering a recursive filter
    /// permanently poisons its state, so every stage sanitises what it emits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sanitize(float value)
        => IsFinite(value) ? value : 0f;

    /// <summary>
    /// Conditions the recursive state of an envelope follower or filter: non-finite values
    /// collapse to 0 and denormals are flushed.
    /// </summary>
    /// <remarks>
    /// Use this rather than <see cref="FlushDenormal"/> for anything fed back into itself.
    /// Squaring a large sample overflows to Infinity, and the usual one-pole update
    /// <c>x + (state - x) * coefficient</c> then evaluates <c>Inf - Inf</c>, which is NaN.
    /// NaN is absorbing under that update, so without this the follower stays NaN for the
    /// rest of the session and silences everything downstream.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float StabilizeState(float value)
    {
        if (!IsFinite(value)) return 0f;
        return MathF.Abs(value) < 1e-18f ? 0f : value;
    }

    /// <summary>
    /// Largest input magnitude the chain accepts, about +12 dBFS. Anything legitimate is
    /// well inside unity; this only exists so a misbehaving driver cannot hand us a value
    /// whose square overflows a float.
    /// </summary>
    public const float MaxInputMagnitude = 4f;

    /// <summary>Makes a raw input sample safe to feed to the detectors: finite and bounded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ConditionInput(float value)
    {
        if (!IsFinite(value)) return 0f;
        if (value > MaxInputMagnitude) return MaxInputMagnitude;
        if (value < -MaxInputMagnitude) return -MaxInputMagnitude;
        return value;
    }
}

/// <summary>
/// A parameter that ramps toward its target instead of jumping, which prevents
/// the audible "zipper" clicks you get when a UI slider writes straight into a gain.
/// Written from the UI thread, read from the audio thread.
/// </summary>
public sealed class SmoothedParameter
{
    private volatile float _target;
    private float _current;
    private float _coefficient;
    private readonly float _rampMilliseconds;
    private bool _initialised;

    public SmoothedParameter(float initialValue, float rampMilliseconds = 15f)
    {
        _target = initialValue;
        _current = initialValue;
        _rampMilliseconds = rampMilliseconds;
        _initialised = true;
    }

    /// <summary>The value the parameter is heading toward. Safe to set from any thread.</summary>
    public float Target
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>The instantaneous smoothed value (audio thread).</summary>
    public float Current => _current;

    public void Prepare(int sampleRate)
    {
        _coefficient = DspMath.TimeConstantCoefficient(_rampMilliseconds, sampleRate);
        if (!_initialised)
        {
            _current = _target;
            _initialised = true;
        }
    }

    /// <summary>Snap immediately to the target (use on reset / device change).</summary>
    public void SnapToTarget() => _current = _target;

    /// <summary>Advance one sample and return the smoothed value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        float target = _target;
        _current = target + (_current - target) * _coefficient;
        _current = DspMath.FlushDenormal(_current);
        return _current;
    }

    /// <summary>
    /// True when the smoothed value has effectively converged, letting callers
    /// take a cheap flat-gain path instead of ramping per sample.
    /// </summary>
    public bool IsSettled(float epsilon = 1e-6f) => MathF.Abs(_current - _target) < epsilon;
}
