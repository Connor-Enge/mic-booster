namespace MicBooster.Models;

/// <summary>Rumble / plosive removal ahead of the gain stage.</summary>
public sealed class HighPassSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Corner frequency in Hz (20..500).</summary>
    public float CutoffHz { get; set; } = 80f;

    public HighPassSettings Clone() => new() { Enabled = Enabled, CutoffHz = CutoffHz };

    public void Clamp() => CutoffHz = Math.Clamp(CutoffHz, 20f, 500f);
}

/// <summary>Downward expander that silences the mic between phrases.</summary>
public sealed class GateSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Level the signal must exceed to open the gate, in dBFS (-90..0).</summary>
    public float ThresholdDb { get; set; } = -45f;
    /// <summary>How far below the threshold the gate must fall before closing, in dB (0..24).</summary>
    public float HysteresisDb { get; set; } = 6f;
    public float AttackMs { get; set; } = 2f;
    public float HoldMs { get; set; } = 120f;
    public float ReleaseMs { get; set; } = 180f;
    /// <summary>Maximum attenuation when closed, in dB (0..80). Lower values duck rather than mute.</summary>
    public float RangeDb { get; set; } = 24f;

    public GateSettings Clone() => new()
    {
        Enabled = Enabled, ThresholdDb = ThresholdDb, HysteresisDb = HysteresisDb,
        AttackMs = AttackMs, HoldMs = HoldMs, ReleaseMs = ReleaseMs, RangeDb = RangeDb
    };

    public void Clamp()
    {
        ThresholdDb = Math.Clamp(ThresholdDb, -90f, 0f);
        HysteresisDb = Math.Clamp(HysteresisDb, 0f, 24f);
        AttackMs = Math.Clamp(AttackMs, 0.1f, 200f);
        HoldMs = Math.Clamp(HoldMs, 0f, 2000f);
        ReleaseMs = Math.Clamp(ReleaseMs, 1f, 3000f);
        RangeDb = Math.Clamp(RangeDb, 0f, 80f);
    }
}

/// <summary>Evens out the difference between quiet and loud speech.</summary>
public sealed class CompressorSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Level above which compression starts, in dBFS (-60..0).</summary>
    public float ThresholdDb { get; set; } = -20f;
    /// <summary>Compression ratio (1..20). 1 means no compression.</summary>
    public float Ratio { get; set; } = 3f;
    public float AttackMs { get; set; } = 8f;
    public float ReleaseMs { get; set; } = 120f;
    /// <summary>Soft-knee width in dB (0..24). 0 is a hard knee.</summary>
    public float KneeDb { get; set; } = 6f;
    /// <summary>Manual make-up gain in dB (-12..+36), ignored when <see cref="AutoMakeup"/> is set.</summary>
    public float MakeupDb { get; set; }
    /// <summary>Derive make-up gain from threshold and ratio so loudness stays roughly constant.</summary>
    public bool AutoMakeup { get; set; } = true;

    public CompressorSettings Clone() => new()
    {
        Enabled = Enabled, ThresholdDb = ThresholdDb, Ratio = Ratio, AttackMs = AttackMs,
        ReleaseMs = ReleaseMs, KneeDb = KneeDb, MakeupDb = MakeupDb, AutoMakeup = AutoMakeup
    };

    public void Clamp()
    {
        ThresholdDb = Math.Clamp(ThresholdDb, -60f, 0f);
        Ratio = Math.Clamp(Ratio, 1f, 20f);
        AttackMs = Math.Clamp(AttackMs, 0.1f, 200f);
        ReleaseMs = Math.Clamp(ReleaseMs, 5f, 3000f);
        KneeDb = Math.Clamp(KneeDb, 0f, 24f);
        MakeupDb = Math.Clamp(MakeupDb, -12f, 36f);
    }
}

/// <summary>
/// Slow gain rider that holds long-term loudness at a target, so the mic sounds the
/// same whether he is leaning in or sitting back.
/// </summary>
public sealed class AutoLevelSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Target programme loudness in dBFS (-40..-6).</summary>
    public float TargetDb { get; set; } = -16f;
    /// <summary>Most it may turn things up, in dB (0..40).</summary>
    public float MaxBoostDb { get; set; } = 18f;
    /// <summary>Most it may turn things down, in dB (0..40).</summary>
    public float MaxCutDb { get; set; } = 12f;
    /// <summary>Reaction time in ms (50..5000). Larger is smoother and less pumpy.</summary>
    public float SpeedMs { get; set; } = 400f;

    public AutoLevelSettings Clone() => new()
    {
        Enabled = Enabled, TargetDb = TargetDb, MaxBoostDb = MaxBoostDb,
        MaxCutDb = MaxCutDb, SpeedMs = SpeedMs
    };

    public void Clamp()
    {
        TargetDb = Math.Clamp(TargetDb, -40f, -6f);
        MaxBoostDb = Math.Clamp(MaxBoostDb, 0f, 40f);
        MaxCutDb = Math.Clamp(MaxCutDb, 0f, 40f);
        SpeedMs = Math.Clamp(SpeedMs, 50f, 5000f);
    }
}

/// <summary>Final brick wall so boosting can never produce clipped, crunchy audio.</summary>
public sealed class LimiterSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Absolute output ceiling in dBFS (-12..0).</summary>
    public float CeilingDb { get; set; } = -1f;
    public float ReleaseMs { get; set; } = 60f;
    /// <summary>Lookahead in ms (0..10). Lets it catch transients without distortion, at the cost of latency.</summary>
    public float LookaheadMs { get; set; } = 3f;

    public LimiterSettings Clone() => new()
    {
        Enabled = Enabled, CeilingDb = CeilingDb, ReleaseMs = ReleaseMs, LookaheadMs = LookaheadMs
    };

    public void Clamp()
    {
        CeilingDb = Math.Clamp(CeilingDb, -12f, 0f);
        ReleaseMs = Math.Clamp(ReleaseMs, 1f, 1000f);
        LookaheadMs = Math.Clamp(LookaheadMs, 0f, 10f);
    }
}

/// <summary>The complete processing configuration. This is what a preset stores.</summary>
public sealed class ProcessorSettings
{
    /// <summary>Master bypass for the whole chain. When false, audio passes through clean.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The main boost control, in dB (-24..+48).</summary>
    public float InputGainDb { get; set; } = 12f;

    /// <summary>Trim after all processing, in dB (-24..+24).</summary>
    public float OutputGainDb { get; set; }

    public HighPassSettings HighPass { get; set; } = new();
    public GateSettings Gate { get; set; } = new();
    public CompressorSettings Compressor { get; set; } = new();
    public AutoLevelSettings AutoLevel { get; set; } = new();
    public LimiterSettings Limiter { get; set; } = new();

    public ProcessorSettings Clone() => new()
    {
        Enabled = Enabled,
        InputGainDb = InputGainDb,
        OutputGainDb = OutputGainDb,
        HighPass = HighPass.Clone(),
        Gate = Gate.Clone(),
        Compressor = Compressor.Clone(),
        AutoLevel = AutoLevel.Clone(),
        Limiter = Limiter.Clone()
    };

    /// <summary>
    /// Pins every value into its legal range. Called after loading from disk, because a
    /// hand-edited or older settings file must never be able to produce a broken chain.
    /// </summary>
    public void Clamp()
    {
        InputGainDb = Math.Clamp(InputGainDb, -24f, 48f);
        OutputGainDb = Math.Clamp(OutputGainDb, -24f, 24f);
        HighPass.Clamp();
        Gate.Clamp();
        Compressor.Clamp();
        AutoLevel.Clamp();
        Limiter.Clamp();
    }
}
