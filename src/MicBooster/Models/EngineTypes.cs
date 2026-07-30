namespace MicBooster.Models;

/// <summary>Lifecycle of the audio processing engine.</summary>
public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

/// <summary>
/// How to fold a multi-channel capture device down to the single mono stream we process.
/// Interfaces commonly leave the mic on input 1 only, in which case mixing would
/// halve the level and pull in a silent channel, so this is user-selectable.
/// </summary>
public enum ChannelMode
{
    /// <summary>Average every channel.</summary>
    MixAll,
    /// <summary>Use channel 0 only.</summary>
    Left,
    /// <summary>Use channel 1 only (falls back to 0 on a mono device).</summary>
    Right,
    /// <summary>Use <see cref="EngineOptions.SourceChannelIndex"/>, clamped to what the device has.</summary>
    Specific
}

/// <summary>Where the processed audio is sent.</summary>
public enum RoutingMode
{
    /// <summary>Process only; do not open an output device. Meters still work.</summary>
    None,
    /// <summary>Send to the selected output device (a virtual cable, or headphones to monitor).</summary>
    Output
}

/// <summary>Immutable start-up parameters for the engine.</summary>
public sealed class EngineOptions
{
    public string? InputDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }
    public ChannelMode ChannelMode { get; init; } = ChannelMode.MixAll;
    public int SourceChannelIndex { get; init; }
    public RoutingMode Routing { get; init; } = RoutingMode.Output;

    /// <summary>
    /// Requested buffer size per side, in milliseconds. Smaller is lower latency but
    /// more likely to glitch on a busy machine. Clamped by the engine to a sane range.
    /// </summary>
    public int BufferMilliseconds { get; init; } = 30;

    /// <summary>Linear output trim applied after the chain, 0..1, before the device.</summary>
    public float OutputVolume { get; init; } = 1f;
}

/// <summary>
/// A snapshot of live meter values. A readonly struct so the ~30 Hz metering path
/// never allocates.
/// </summary>
public readonly struct EngineMetrics
{
    public float InputPeakDb { get; init; }
    public float InputRmsDb { get; init; }
    public float OutputPeakDb { get; init; }
    public float OutputRmsDb { get; init; }

    /// <summary>Programme loudness estimate of the output, in LUFS-like units.</summary>
    public float OutputLoudnessDb { get; init; }

    /// <summary>How much the gate is currently ducking, as a negative dB value (0 = fully open).</summary>
    public float GateAttenuationDb { get; init; }
    public bool GateOpen { get; init; }

    /// <summary>Compressor gain reduction as a positive dB amount.</summary>
    public float CompressorReductionDb { get; init; }

    /// <summary>Limiter gain reduction as a positive dB amount.</summary>
    public float LimiterReductionDb { get; init; }

    /// <summary>Gain the auto-level rider is currently applying, positive or negative dB.</summary>
    public float AutoLevelGainDb { get; init; }

    public bool InputClipping { get; init; }
    public bool OutputClipping { get; init; }

    /// <summary>Measured round-trip buffer latency in ms, as configured.</summary>
    public float LatencyMs { get; init; }

    /// <summary>Count of buffer overruns/underruns since start; a non-zero value means audible glitches.</summary>
    public long GlitchCount { get; init; }

    /// <summary>Fraction of the available audio-callback budget being used, 0..1.</summary>
    public float DspLoad { get; init; }

    public static EngineMetrics Idle => new()
    {
        InputPeakDb = DspMathConstants.MinusInfinityDb,
        InputRmsDb = DspMathConstants.MinusInfinityDb,
        OutputPeakDb = DspMathConstants.MinusInfinityDb,
        OutputRmsDb = DspMathConstants.MinusInfinityDb,
        OutputLoudnessDb = DspMathConstants.MinusInfinityDb,
        GateOpen = false
    };
}

/// <summary>
/// Mirror of the DSP silence floor, kept here so <see cref="Models"/> does not have to
/// reference the DSP namespace.
/// </summary>
internal static class DspMathConstants
{
    public const float MinusInfinityDb = -100f;
}
