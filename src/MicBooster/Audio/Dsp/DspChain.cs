using MicBooster.Models;

namespace MicBooster.Audio.Dsp;

/// <summary>
/// The whole mono processing chain: high-pass, input gain, gate, compressor, auto-level,
/// output gain, limiter, plus a meter either side of the processing and a click-free mute.
/// </summary>
/// <remarks>
/// <see cref="Process"/> runs on the WASAPI capture thread and allocates nothing.
/// <see cref="ApplySettings"/>, <see cref="Bypassed"/> and <see cref="Muted"/> are written from
/// the UI thread while audio is running; they only touch volatile scalars and
/// <see cref="SmoothedParameter.Target"/>, so the audio thread never takes a lock.
/// <see cref="Prepare"/> and <see cref="Reset"/> belong to the control thread, with the stream
/// stopped.
/// </remarks>
public sealed class DspChain
{
    /// <summary>Every stage, for the control-thread-only Prepare/Reset sweeps.</summary>
    private readonly IAudioProcessor[] _stages;

    // 10 ms: long enough that a mute toggle cannot click, short enough to feel instant.
    private readonly SmoothedParameter _muteGain = new(1f, 10f);

    private volatile bool _bypassed;
    private volatile bool _muted;
    private volatile int _sampleRate;

    /// <summary>Creates the chain with every stage at its own defaults.</summary>
    public DspChain()
    {
        _stages = new IAudioProcessor[]
        {
            HighPass, InputGain, Gate, Compressor, AutoLevel, OutputGain, Limiter
        };
    }

    /// <summary>Rumble and plosive filter, ahead of the boost so it never amplifies subsonics.</summary>
    public HighPassFilter HighPass { get; } = new();

    /// <summary>The main boost, applied before the dynamics so their thresholds mean something.</summary>
    public GainStage InputGain { get; } = new();

    /// <summary>Gate that silences the mic between phrases.</summary>
    public NoiseGate Gate { get; } = new();

    /// <summary>Evens out loud and quiet speech.</summary>
    public Compressor Compressor { get; } = new();

    /// <summary>Slow rider that holds long-term loudness at the target.</summary>
    public AutoLevel AutoLevel { get; } = new();

    /// <summary>Post-processing trim.</summary>
    public GainStage OutputGain { get; } = new();

    /// <summary>Brick wall, so no amount of boost can produce clipped audio.</summary>
    public Limiter Limiter { get; } = new();

    /// <summary>Meters the raw capture, before any gain, so the input meter shows the real mic level.</summary>
    public LevelAnalyzer InputAnalyzer { get; } = new();

    /// <summary>Meters what actually leaves the chain, mute included.</summary>
    public LevelAnalyzer OutputAnalyzer { get; } = new();

    /// <summary>
    /// Master bypass. Both meters and the mute keep working while bypassed, which is what
    /// makes an A/B against the processed signal honest.
    /// </summary>
    public bool Bypassed
    {
        get => _bypassed;
        set => _bypassed = value;
    }

    /// <summary>Mutes the output by ramping a gain to zero rather than blanking the buffer.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            _muteGain.Target = value ? 0f : 1f;
        }
    }

    /// <summary>Samples of delay the chain adds. Only the limiter's lookahead delays anything.</summary>
    public int LatencySamples => Limiter.LatencySamples;

    /// <summary>The rate last passed to <see cref="Prepare"/>, or 0 before the first call.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Recomputes every stage for a new sample rate and clears all state. Control thread only,
    /// with the stream stopped, because the stages allocate their delay lines here.
    /// </summary>
    /// <param name="sampleRate">Capture rate in Hz.</param>
    public void Prepare(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _sampleRate = sampleRate;

        for (int i = 0; i < _stages.Length; i++)
        {
            _stages[i].Prepare(sampleRate);
        }

        InputAnalyzer.Prepare(sampleRate);
        OutputAnalyzer.Prepare(sampleRate);
        _muteGain.Prepare(sampleRate);

        Reset();
    }

    /// <summary>Clears filter memory, envelopes, delay lines and meters, and snaps the mute ramp.</summary>
    public void Reset()
    {
        for (int i = 0; i < _stages.Length; i++)
        {
            _stages[i].Reset();
        }

        _muteGain.SnapToTarget();
        InputAnalyzer.Reset();
        OutputAnalyzer.Reset();
    }

    /// <summary>
    /// Pushes a settings tree onto the stages. Safe to call from the UI thread while audio is
    /// running: it only writes the stages' volatile parameters and never reallocates, so it
    /// deliberately does not call <see cref="Prepare"/>.
    /// </summary>
    /// <param name="settings">The configuration to apply.</param>
    public void ApplySettings(ProcessorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Bypassed = !settings.Enabled;

        HighPass.Enabled = settings.HighPass.Enabled;
        HighPass.CutoffHz = settings.HighPass.CutoffHz;

        // The gain stages have no switch of their own; 0 dB is already a pass-through.
        InputGain.Enabled = true;
        InputGain.GainDb = settings.InputGainDb;

        Gate.Enabled = settings.Gate.Enabled;
        Gate.ThresholdDb = settings.Gate.ThresholdDb;
        Gate.HysteresisDb = settings.Gate.HysteresisDb;
        Gate.AttackMs = settings.Gate.AttackMs;
        Gate.HoldMs = settings.Gate.HoldMs;
        Gate.ReleaseMs = settings.Gate.ReleaseMs;
        Gate.RangeDb = settings.Gate.RangeDb;

        Compressor.Enabled = settings.Compressor.Enabled;
        Compressor.ThresholdDb = settings.Compressor.ThresholdDb;
        Compressor.Ratio = settings.Compressor.Ratio;
        Compressor.AttackMs = settings.Compressor.AttackMs;
        Compressor.ReleaseMs = settings.Compressor.ReleaseMs;
        Compressor.KneeDb = settings.Compressor.KneeDb;
        Compressor.MakeupDb = settings.Compressor.MakeupDb;
        Compressor.AutoMakeup = settings.Compressor.AutoMakeup;

        AutoLevel.Enabled = settings.AutoLevel.Enabled;
        AutoLevel.TargetDb = settings.AutoLevel.TargetDb;
        AutoLevel.MaxBoostDb = settings.AutoLevel.MaxBoostDb;
        AutoLevel.MaxCutDb = settings.AutoLevel.MaxCutDb;
        AutoLevel.SpeedMs = settings.AutoLevel.SpeedMs;

        OutputGain.Enabled = true;
        OutputGain.GainDb = settings.OutputGainDb;

        Limiter.Enabled = settings.Limiter.Enabled;
        Limiter.CeilingDb = settings.Limiter.CeilingDb;
        Limiter.ReleaseMs = settings.Limiter.ReleaseMs;
        Limiter.LookaheadMs = settings.Limiter.LookaheadMs;
    }

    /// <summary>
    /// Processes a block of mono samples in place. Audio thread: no allocation, no lock,
    /// nothing that can throw. Assumes <see cref="Prepare"/> has run.
    /// </summary>
    /// <param name="buffer">Mono sample buffer, modified in place.</param>
    /// <param name="offset">First sample to process.</param>
    /// <param name="count">Number of samples to process.</param>
    public void Process(float[] buffer, int offset, int count)
    {
        // A bad range here would raise on the capture thread and take the stream down with it,
        // so it is dropped rather than thrown.
        if (buffer is null || count <= 0 || offset < 0 || offset + count > buffer.Length)
        {
            return;
        }

        ConditionInput(buffer, offset, count);

        InputAnalyzer.Analyze(buffer, offset, count);

        if (!_bypassed)
        {
            HighPass.Process(buffer, offset, count);
            InputGain.Process(buffer, offset, count);
            Gate.Process(buffer, offset, count);

            // Hold the rider steady while the gate is shut, otherwise it spends the gaps
            // between phrases winding gain up to chase room noise.
            AutoLevel.Frozen = Gate.Enabled && !Gate.IsOpen;

            Compressor.Process(buffer, offset, count);
            AutoLevel.Process(buffer, offset, count);
            OutputGain.Process(buffer, offset, count);
            Limiter.Process(buffer, offset, count);
        }

        ApplyMute(buffer, offset, count);

        OutputAnalyzer.Analyze(buffer, offset, count);
    }

    /// <summary>
    /// Reads the live meters and stage readbacks into a metrics snapshot. Called from the
    /// metering timer thread, never the audio thread.
    /// </summary>
    /// <param name="latencyMs">Configured buffer latency, for display.</param>
    /// <param name="glitchCount">Overruns/underruns counted by the engine since start.</param>
    /// <param name="dspLoad">Fraction of the callback budget in use, 0..1.</param>
    /// <returns>A fully populated snapshot.</returns>
    public EngineMetrics BuildMetrics(float latencyMs, long glitchCount, float dspLoad)
    {
        bool processing = !_bypassed;
        bool gateRunning = processing && Gate.Enabled;

        // A stage that is bypassed or switched off keeps whatever readback it last wrote, so
        // the resting values are decided here instead of trusting each stage to zero them.
        return new EngineMetrics
        {
            InputPeakDb = Finite(InputAnalyzer.PeakDb, DspMath.MinusInfinityDb),
            InputRmsDb = Finite(InputAnalyzer.RmsDb, DspMath.MinusInfinityDb),
            OutputPeakDb = Finite(OutputAnalyzer.PeakDb, DspMath.MinusInfinityDb),
            OutputRmsDb = Finite(OutputAnalyzer.RmsDb, DspMath.MinusInfinityDb),
            OutputLoudnessDb = Finite(OutputAnalyzer.LoudnessDb, DspMath.MinusInfinityDb),

            GateAttenuationDb = gateRunning ? MathF.Min(0f, Finite(Gate.CurrentAttenuationDb)) : 0f,
            // With no gate in the path the signal is passing unattenuated, which reads as open.
            GateOpen = !gateRunning || Gate.IsOpen,

            CompressorReductionDb = processing && Compressor.Enabled
                ? MathF.Max(0f, Finite(Compressor.CurrentReductionDb))
                : 0f,
            LimiterReductionDb = processing && Limiter.Enabled
                ? MathF.Max(0f, Finite(Limiter.CurrentReductionDb))
                : 0f,
            AutoLevelGainDb = processing && AutoLevel.Enabled ? Finite(AutoLevel.CurrentGainDb) : 0f,

            InputClipping = InputAnalyzer.Clipped,
            OutputClipping = OutputAnalyzer.Clipped,

            LatencyMs = Finite(latencyMs),
            GlitchCount = glitchCount,
            DspLoad = Finite(dspLoad)
        };
    }

    /// <summary>
    /// Bounds the incoming block to finite, sane magnitudes before any detector sees it.
    /// </summary>
    /// <remarks>
    /// The capture path already clamps float samples, so for real audio this is a no-op. It
    /// exists because a single huge sample from a misbehaving driver used to be enough to
    /// silence the app for the rest of the session: squaring it overflowed to Infinity, the
    /// envelope followers evaluated <c>Inf - Inf</c> to NaN, and NaN survives a one-pole
    /// update forever. Bounding the input removes that whole class of failure at the source.
    /// </remarks>
    private static void ConditionInput(float[] buffer, int offset, int count)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            buffer[i] = DspMath.ConditionInput(buffer[i]);
        }
    }

    private void ApplyMute(float[] buffer, int offset, int count)
    {
        // Fully unmuted and not ramping: the multiply would be by exactly 1.
        if (_muteGain.Target >= 1f && _muteGain.IsSettled())
        {
            return;
        }

        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            buffer[i] *= _muteGain.Next();
        }
    }

    /// <summary>Keeps a NaN or Infinity from a faulty readback out of the UI bindings.</summary>
    private static float Finite(float value, float fallback = 0f)
        => DspMath.IsFinite(value) ? value : fallback;
}
