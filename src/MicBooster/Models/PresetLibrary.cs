namespace MicBooster.Models;

/// <summary>
/// Built-in starting points. These exist so the app is useful in one click without
/// anyone needing to know what a compressor ratio is.
/// </summary>
public static class PresetLibrary
{
    public const string QuietMicRescue = "Quiet Mic Rescue";
    public const string VoiceChat = "Voice Chat";
    public const string Streaming = "Streaming";
    public const string Podcast = "Podcast";
    public const string MaximumLoudness = "Maximum Loudness";
    public const string Natural = "Natural";
    public const string Bypass = "Bypass";

    public static IReadOnlyList<NamedPreset> BuiltIn { get; } = new List<NamedPreset>
    {
        // His mic is too quiet: a lot of clean gain, an aggressive level rider, and a
        // firm limiter so the extra gain can never turn into clipping.
        new()
        {
            Name = QuietMicRescue,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 24f,
                OutputGainDb = 0f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 85f },
                Gate = new GateSettings
                {
                    Enabled = true, ThresholdDb = -50f, HysteresisDb = 6f,
                    AttackMs = 2f, HoldMs = 140f, ReleaseMs = 200f, RangeDb = 22f
                },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -22f, Ratio = 4f, AttackMs = 6f,
                    ReleaseMs = 130f, KneeDb = 8f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings
                {
                    Enabled = true, TargetDb = -14f, MaxBoostDb = 24f, MaxCutDb = 10f, SpeedMs = 350f
                },
                Limiter = new LimiterSettings
                {
                    Enabled = true, CeilingDb = -1f, ReleaseMs = 60f, LookaheadMs = 3f
                }
            }
        },

        // Discord / Zoom / in-game comms: consistent and intelligible, gate tight enough
        // to keep keyboard and fan noise out of the channel.
        new()
        {
            Name = VoiceChat,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 12f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 90f },
                Gate = new GateSettings
                {
                    Enabled = true, ThresholdDb = -44f, HysteresisDb = 7f,
                    AttackMs = 2f, HoldMs = 120f, ReleaseMs = 160f, RangeDb = 28f
                },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -20f, Ratio = 3f, AttackMs = 8f,
                    ReleaseMs = 120f, KneeDb = 6f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings
                {
                    Enabled = true, TargetDb = -16f, MaxBoostDb = 16f, MaxCutDb = 12f, SpeedMs = 400f
                },
                Limiter = new LimiterSettings { Enabled = true, CeilingDb = -1.5f, ReleaseMs = 70f, LookaheadMs = 3f }
            }
        },

        // Broadcast: denser and louder, because platform audio gets re-encoded and
        // quiet sources lose out.
        new()
        {
            Name = Streaming,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 15f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 80f },
                Gate = new GateSettings
                {
                    Enabled = true, ThresholdDb = -46f, HysteresisDb = 6f,
                    AttackMs = 1.5f, HoldMs = 150f, ReleaseMs = 180f, RangeDb = 20f
                },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -18f, Ratio = 4.5f, AttackMs = 5f,
                    ReleaseMs = 110f, KneeDb = 6f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings
                {
                    Enabled = true, TargetDb = -14f, MaxBoostDb = 18f, MaxCutDb = 14f, SpeedMs = 300f
                },
                Limiter = new LimiterSettings { Enabled = true, CeilingDb = -1f, ReleaseMs = 50f, LookaheadMs = 4f }
            }
        },

        // Spoken word, recorded rather than live: gentler, slower, no gate chatter.
        new()
        {
            Name = Podcast,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 10f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 100f },
                Gate = new GateSettings
                {
                    Enabled = false, ThresholdDb = -55f, HysteresisDb = 8f,
                    AttackMs = 5f, HoldMs = 250f, ReleaseMs = 400f, RangeDb = 12f
                },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -24f, Ratio = 2.5f, AttackMs = 15f,
                    ReleaseMs = 200f, KneeDb = 10f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings
                {
                    Enabled = true, TargetDb = -18f, MaxBoostDb = 14f, MaxCutDb = 10f, SpeedMs = 800f
                },
                Limiter = new LimiterSettings { Enabled = true, CeilingDb = -1.5f, ReleaseMs = 90f, LookaheadMs = 5f }
            }
        },

        // The "just make it as loud as possible" setting, still protected by the limiter.
        new()
        {
            Name = MaximumLoudness,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 32f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 95f },
                Gate = new GateSettings
                {
                    Enabled = true, ThresholdDb = -42f, HysteresisDb = 8f,
                    AttackMs = 1f, HoldMs = 120f, ReleaseMs = 150f, RangeDb = 30f
                },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -26f, Ratio = 8f, AttackMs = 3f,
                    ReleaseMs = 90f, KneeDb = 4f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings
                {
                    Enabled = true, TargetDb = -10f, MaxBoostDb = 30f, MaxCutDb = 8f, SpeedMs = 250f
                },
                Limiter = new LimiterSettings { Enabled = true, CeilingDb = -0.5f, ReleaseMs = 40f, LookaheadMs = 4f }
            }
        },

        // Boost and safety only. Nothing that changes how the voice sounds.
        new()
        {
            Name = Natural,
            Settings = new ProcessorSettings
            {
                Enabled = true,
                InputGainDb = 9f,
                HighPass = new HighPassSettings { Enabled = true, CutoffHz = 70f },
                Gate = new GateSettings { Enabled = false, RangeDb = 10f },
                Compressor = new CompressorSettings
                {
                    Enabled = true, ThresholdDb = -14f, Ratio = 1.8f, AttackMs = 20f,
                    ReleaseMs = 250f, KneeDb = 12f, AutoMakeup = true
                },
                AutoLevel = new AutoLevelSettings { Enabled = false, TargetDb = -18f },
                Limiter = new LimiterSettings { Enabled = true, CeilingDb = -1f, ReleaseMs = 80f, LookaheadMs = 3f }
            }
        },

        // Everything off, for an honest A/B against the unprocessed microphone.
        new()
        {
            Name = Bypass,
            Settings = new ProcessorSettings
            {
                Enabled = false,
                InputGainDb = 0f,
                HighPass = new HighPassSettings { Enabled = false },
                Gate = new GateSettings { Enabled = false },
                Compressor = new CompressorSettings { Enabled = false },
                AutoLevel = new AutoLevelSettings { Enabled = false },
                Limiter = new LimiterSettings { Enabled = false }
            }
        }
    };

    /// <summary>Fetch a built-in preset's settings by name, or null when unknown.</summary>
    public static ProcessorSettings? Find(string name)
    {
        foreach (var preset in BuiltIn)
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
                return preset.Settings.Clone();
        }
        return null;
    }

    /// <summary>The preset a first-time user starts on.</summary>
    public static ProcessorSettings Default => Find(VoiceChat) ?? new ProcessorSettings();
}
