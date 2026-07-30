using MicBooster.Audio;
using MicBooster.Audio.Dsp;
using MicBooster.Models;
using NAudio.Wave;

// NAudio.Wave declares an unrelated ChannelMode (an MP3 stereo mode), so the model type is
// aliased to keep the reference unambiguous.
using ChannelMode = MicBooster.Models.ChannelMode;

namespace MicBooster.DspTests;

/// <summary>
/// Offline checks on the processing chain. These run with no audio hardware and assert the
/// things a compiler cannot: that the gain stage actually applies the gain, that the limiter
/// genuinely cannot be pushed past its ceiling, that the gate does not chatter, that the level
/// rider converges, and that no stage can emit a NaN. Exit code 0 means everything passed.
/// </summary>
internal static class Program
{
    private const int Rate = 48000;
    private const int Block = 480; // 10 ms

    private static readonly List<string> Failures = new();
    private static int _checks;

    private static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("  Mic Booster - DSP verification");
        Console.WriteLine("  ------------------------------");

        Run("gain stage applies the requested gain", GainStageAppliesGain);
        Run("gain stage ramps instead of jumping", GainStageRamps);
        Run("high-pass rejects rumble and passes voice", HighPassShape);
        Run("compressor reduces level above threshold", CompressorReduces);
        Run("compressor leaves quiet signal alone", CompressorBelowThreshold);
        Run("compressor auto make-up restores loudness", CompressorAutoMakeup);
        Run("limiter never exceeds its ceiling", LimiterHoldsCeiling);
        Run("limiter catches a sudden transient", LimiterCatchesTransient);
        Run("limiter reports its lookahead latency", LimiterReportsLatency);
        Run("gate opens on speech and closes on silence", GateOpensAndCloses);
        Run("gate hysteresis prevents chatter", GateDoesNotChatter);
        Run("gate range bounds the attenuation", GateRespectsRange);
        Run("auto level converges on its target", AutoLevelConverges);
        Run("auto level holds still while frozen", AutoLevelFreezes);
        Run("auto level ignores near-silence", AutoLevelIgnoresSilence);
        Run("level analyzer measures peak and rms", AnalyzerMeasures);
        Run("level analyzer latches clipping", AnalyzerLatchesClip);
        Run("chain bypass is a true pass-through", ChainBypassIsClean);
        Run("chain mute ramps to silence", ChainMuteSilences);
        Run("chain rescues a very quiet mic", ChainRescuesQuietMic);
        Run("chain never emits NaN or infinity", ChainSanitisesGarbage);
        Run("chain survives every built-in preset", ChainHandlesAllPresets);
        Run("chain works at every common sample rate", ChainHandlesAllRates);
        Run("downmixer decodes 32-bit float", DownmixFloat32);
        Run("downmixer decodes 16-bit PCM", DownmixPcm16);
        Run("downmixer decodes 24-bit PCM", DownmixPcm24);
        Run("downmixer decodes 32-bit PCM", DownmixPcm32);
        Run("downmixer decodes 8-bit PCM", DownmixPcm8);
        Run("downmixer honours channel selection", DownmixChannelModes);
        Run("downmixer rejects a format it cannot decode", DownmixRejectsUnknown);
        Run("downmixer ignores a partial trailing frame", DownmixPartialFrame);
        Run("mono fan-out fills every output channel", FanOutChannels);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"  All {_checks} checks passed.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  {Failures.Count} of {_checks} checks FAILED:");
        foreach (var f in Failures) Console.WriteLine($"    - {f}");
        Console.WriteLine();
        return 1;
    }

    // ---------------------------------------------------------------- harness

    private static void Run(string name, Func<string?> check)
    {
        _checks++;
        string? problem;
        try
        {
            problem = check();
        }
        catch (Exception ex)
        {
            problem = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        if (problem is null)
        {
            Console.WriteLine($"  [ ok ] {name}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {name}: {problem}");
            Failures.Add($"{name}: {problem}");
        }
    }

    private static string? Expect(bool condition, string message) => condition ? null : message;

    // ---------------------------------------------------------------- signals

    private static float[] Sine(float amplitude, float freq, int samples, double phase = 0)
    {
        var buffer = new float[samples];
        double step = 2 * Math.PI * freq / Rate;
        for (int i = 0; i < samples; i++)
            buffer[i] = (float)(amplitude * Math.Sin(phase + step * i));
        return buffer;
    }

    private static void FillSine(float[] buffer, float amplitude, float freq, ref double phase)
    {
        double step = 2 * Math.PI * freq / Rate;
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)(amplitude * Math.Sin(phase));
            phase += step;
        }
    }

    private static float Peak(float[] b)
    {
        float p = 0f;
        foreach (var v in b) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    private static float Rms(float[] b)
    {
        double sum = 0;
        foreach (var v in b) sum += (double)v * v;
        return (float)Math.Sqrt(sum / Math.Max(1, b.Length));
    }

    private static float Db(float linear) => DspMath.LinearToDb(linear);

    private static bool AllFinite(float[] b)
    {
        foreach (var v in b) if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return true;
    }

    /// <summary>Streams a steady sine through a stage and returns the settled final block.</summary>
    private static float[] Stream(IAudioProcessor stage, float amplitude, float freq, int blocks, int rate = Rate)
    {
        stage.Prepare(rate);
        stage.Reset();
        var buffer = new float[Block];
        double phase = 0;
        for (int i = 0; i < blocks; i++)
        {
            FillSine(buffer, amplitude, freq, ref phase);
            stage.Process(buffer, 0, buffer.Length);
        }
        return buffer;
    }

    // ---------------------------------------------------------------- gain

    private static string? GainStageAppliesGain()
    {
        var gain = new GainStage { GainDb = 12f };
        var outBlock = Stream(gain, 0.1f, 1000f, 60);
        float expected = 0.1f * DspMath.DbToLinear(12f);
        float actual = Peak(outBlock);
        return Expect(MathF.Abs(actual - expected) < expected * 0.03f,
            $"expected peak ~{expected:F4}, got {actual:F4}");
    }

    private static string? GainStageRamps()
    {
        // A 24 dB jump applied mid-stream must not appear fully in the first sample, or a
        // slider drag would click audibly.
        var gain = new GainStage { GainDb = 0f };
        gain.Prepare(Rate);
        gain.Reset();
        var buffer = new float[Block];
        double phase = 0;
        FillSine(buffer, 0.2f, 1000f, ref phase);
        gain.Process(buffer, 0, buffer.Length);

        gain.GainDb = 24f;
        var dc = new float[Block];
        Array.Fill(dc, 0.2f);
        gain.Process(dc, 0, dc.Length);

        float target = 0.2f * DspMath.DbToLinear(24f);
        return Expect(dc[0] < target * 0.5f && dc[^1] > dc[0],
            $"first sample {dc[0]:F4} should be well below the {target:F4} target and rise across the block");
    }

    private static string? HighPassShape()
    {
        var hp = new HighPassFilter { CutoffHz = 100f };
        float lowOut = Peak(Stream(hp, 0.5f, 20f, 40));
        var hp2 = new HighPassFilter { CutoffHz = 100f };
        float voiceOut = Peak(Stream(hp2, 0.5f, 1000f, 40));

        return Expect(lowOut < 0.5f * 0.25f && voiceOut > 0.5f * 0.9f,
            $"20 Hz should be strongly cut (got {Db(lowOut / 0.5f):F1} dB) and 1 kHz nearly untouched (got {Db(voiceOut / 0.5f):F1} dB)");
    }

    // ---------------------------------------------------------------- compressor

    private static Compressor MakeCompressor(float threshold, float ratio, bool autoMakeup = false) => new()
    {
        ThresholdDb = threshold,
        Ratio = ratio,
        AttackMs = 5f,
        ReleaseMs = 50f,
        KneeDb = 0f,
        MakeupDb = 0f,
        AutoMakeup = autoMakeup
    };

    private static string? CompressorReduces()
    {
        var comp = MakeCompressor(-20f, 4f);
        float inputPeak = 0.316f; // about -10 dBFS
        var outBlock = Stream(comp, inputPeak, 1000f, 200);
        float outDb = Db(Peak(outBlock));
        float inDb = Db(inputPeak);

        return Expect(outDb < inDb - 3f && comp.CurrentReductionDb > 3f && outDb > -40f,
            $"in {inDb:F1} dB -> out {outDb:F1} dB, reduction {comp.CurrentReductionDb:F1} dB");
    }

    private static string? CompressorBelowThreshold()
    {
        var comp = MakeCompressor(-20f, 4f);
        float inputPeak = 0.01f; // about -40 dBFS, far below threshold
        var outBlock = Stream(comp, inputPeak, 1000f, 200);
        float outDb = Db(Peak(outBlock));

        return Expect(MathF.Abs(outDb - Db(inputPeak)) < 1.5f && comp.CurrentReductionDb < 1.5f,
            $"a signal below threshold should pass: in {Db(inputPeak):F1} dB -> out {outDb:F1} dB, reduction {comp.CurrentReductionDb:F1} dB");
    }

    private static string? CompressorAutoMakeup()
    {
        var plain = MakeCompressor(-24f, 6f, autoMakeup: false);
        var auto = MakeCompressor(-24f, 6f, autoMakeup: true);
        float amp = 0.316f;
        float plainDb = Db(Peak(Stream(plain, amp, 1000f, 200)));
        float autoDb = Db(Peak(Stream(auto, amp, 1000f, 200)));

        return Expect(autoDb > plainDb + 3f,
            $"auto make-up should recover level: plain {plainDb:F1} dB vs auto {autoDb:F1} dB");
    }

    // ---------------------------------------------------------------- limiter

    private static string? LimiterHoldsCeiling()
    {
        // Deliberately slam it: a full-scale sine against a -6 dB ceiling.
        var lim = new Limiter { CeilingDb = -6f, ReleaseMs = 50f, LookaheadMs = 3f };
        var outBlock = Stream(lim, 1.0f, 500f, 200);
        float ceiling = DspMath.DbToLinear(-6f);
        float peak = Peak(outBlock);

        return Expect(peak <= ceiling * 1.02f && peak > ceiling * 0.5f,
            $"peak {Db(peak):F2} dB must not exceed the -6.00 dB ceiling (linear {peak:F4} vs {ceiling:F4})");
    }

    private static string? LimiterCatchesTransient()
    {
        var lim = new Limiter { CeilingDb = -3f, ReleaseMs = 80f, LookaheadMs = 5f };
        lim.Prepare(Rate);
        lim.Reset();

        // Silence, then an abrupt full-scale burst - the case a feed-back limiter overshoots on.
        var quiet = new float[Block];
        lim.Process(quiet, 0, quiet.Length);

        float ceiling = DspMath.DbToLinear(-3f);
        float worst = 0f;
        for (int i = 0; i < 20; i++)
        {
            var burst = Sine(1.0f, 800f, Block, i * 0.7);
            lim.Process(burst, 0, burst.Length);
            worst = MathF.Max(worst, Peak(burst));
        }

        return Expect(worst <= ceiling * 1.05f,
            $"transient overshoot: peak {Db(worst):F2} dB against a -3.00 dB ceiling");
    }

    private static string? LimiterReportsLatency()
    {
        var lim = new Limiter { LookaheadMs = 4f, CeilingDb = -1f };
        lim.Prepare(Rate);
        int expected = (int)(Rate * 0.004f);
        int actual = lim.LatencySamples;

        var off = new Limiter { Enabled = false, LookaheadMs = 4f };
        off.Prepare(Rate);

        return Expect(Math.Abs(actual - expected) <= 2 && off.LatencySamples == 0,
            $"expected ~{expected} samples of lookahead, got {actual}; disabled reported {off.LatencySamples}");
    }

    // ---------------------------------------------------------------- gate

    private static NoiseGate MakeGate() => new()
    {
        ThresholdDb = -40f,
        HysteresisDb = 6f,
        AttackMs = 2f,
        HoldMs = 50f,
        ReleaseMs = 60f,
        RangeDb = 40f
    };

    private static string? GateOpensAndCloses()
    {
        var gate = MakeGate();
        gate.Prepare(Rate);
        gate.Reset();

        var buffer = new float[Block];
        double phase = 0;

        // Loud enough to open (-20 dBFS).
        for (int i = 0; i < 40; i++)
        {
            FillSine(buffer, 0.1f, 1000f, ref phase);
            gate.Process(buffer, 0, buffer.Length);
        }
        bool openedOnSpeech = gate.IsOpen;
        float passThrough = Peak(buffer);

        // Then silence for well past hold + release.
        for (int i = 0; i < 60; i++)
        {
            Array.Clear(buffer);
            gate.Process(buffer, 0, buffer.Length);
        }
        bool closedOnSilence = !gate.IsOpen;

        return Expect(openedOnSpeech && closedOnSilence && passThrough > 0.09f,
            $"open on speech={openedOnSpeech} (passed {passThrough:F3}), closed on silence={closedOnSilence}, attenuation {gate.CurrentAttenuationDb:F1} dB");
    }

    private static string? GateDoesNotChatter()
    {
        // Sit the signal in the hysteresis band: above the close point, below nothing else.
        // A single-threshold gate flips state constantly here; a hysteretic one must not.
        var gate = MakeGate();
        gate.Prepare(Rate);
        gate.Reset();

        var buffer = new float[Block];
        double phase = 0;

        // Open it first with a clearly loud signal.
        for (int i = 0; i < 30; i++)
        {
            FillSine(buffer, 0.2f, 1000f, ref phase);
            gate.Process(buffer, 0, buffer.Length);
        }
        if (!gate.IsOpen) return "gate failed to open on a loud signal";

        // Now hover just under the open threshold but above the close threshold.
        float hover = DspMath.DbToLinear(-42f);
        int transitions = 0;
        bool last = gate.IsOpen;
        for (int i = 0; i < 100; i++)
        {
            FillSine(buffer, hover, 1000f, ref phase);
            gate.Process(buffer, 0, buffer.Length);
            if (gate.IsOpen != last) { transitions++; last = gate.IsOpen; }
        }

        return Expect(transitions <= 1, $"gate changed state {transitions} times while the level hovered in the hysteresis band");
    }

    private static string? GateRespectsRange()
    {
        var gate = MakeGate();
        gate.RangeDb = 12f; // duck by 12 dB, not a full mute
        gate.Prepare(Rate);
        gate.Reset();

        var buffer = new float[Block];
        for (int i = 0; i < 200; i++)
        {
            Array.Fill(buffer, 0f);
            gate.Process(buffer, 0, buffer.Length);
        }

        float attenuation = gate.CurrentAttenuationDb;
        return Expect(attenuation <= 0f && attenuation >= -13f,
            $"closed attenuation should settle near -12 dB, got {attenuation:F2} dB");
    }

    // ---------------------------------------------------------------- auto level

    private static string? AutoLevelConverges()
    {
        var al = new AutoLevel { TargetDb = -16f, MaxBoostDb = 30f, MaxCutDb = 20f, SpeedMs = 100f };
        // A quiet source, roughly -36 dBFS, needs about +20 dB to reach the target.
        var outBlock = Stream(al, 0.0158f, 1000f, 1500);
        float gain = al.CurrentGainDb;

        return Expect(gain > 8f && gain <= 30.5f && AllFinite(outBlock),
            $"expected a meaningful positive gain toward the target, got {gain:F1} dB");
    }

    private static string? AutoLevelFreezes()
    {
        var al = new AutoLevel { TargetDb = -16f, MaxBoostDb = 30f, MaxCutDb = 20f, SpeedMs = 100f };
        al.Prepare(Rate);
        al.Reset();

        var buffer = new float[Block];
        double phase = 0;
        for (int i = 0; i < 400; i++)
        {
            FillSine(buffer, 0.0158f, 1000f, ref phase);
            al.Process(buffer, 0, buffer.Length);
        }
        float before = al.CurrentGainDb;

        // Frozen plus silence is exactly the between-phrases case that makes a naive rider
        // wind gain up onto room noise.
        al.Frozen = true;
        for (int i = 0; i < 600; i++)
        {
            Array.Clear(buffer);
            al.Process(buffer, 0, buffer.Length);
        }
        float after = al.CurrentGainDb;

        return Expect(MathF.Abs(after - before) < 1.0f,
            $"frozen gain drifted from {before:F2} dB to {after:F2} dB");
    }

    private static string? AutoLevelIgnoresSilence()
    {
        var al = new AutoLevel { TargetDb = -16f, MaxBoostDb = 30f, MaxCutDb = 20f, SpeedMs = 60f };
        al.Prepare(Rate);
        al.Reset();

        // Not frozen, but far below any sensible speech level: it must still not wind up.
        var buffer = new float[Block];
        double phase = 0;
        for (int i = 0; i < 1200; i++)
        {
            FillSine(buffer, 0.0002f, 1000f, ref phase); // about -74 dBFS
            al.Process(buffer, 0, buffer.Length);
        }

        return Expect(al.CurrentGainDb < 12f,
            $"near-silence should not be chased; gain reached {al.CurrentGainDb:F1} dB");
    }

    // ---------------------------------------------------------------- analyzer

    private static string? AnalyzerMeasures()
    {
        var an = new LevelAnalyzer();
        an.Prepare(Rate);
        an.Reset();

        var buffer = new float[Block];
        double phase = 0;
        for (int i = 0; i < 200; i++)
        {
            FillSine(buffer, 0.5f, 1000f, ref phase);
            an.Analyze(buffer, 0, buffer.Length);
        }

        float expectedPeak = Db(0.5f);              // about -6 dB
        float expectedRms = Db(0.5f / 1.41421f);    // about -9 dB

        return Expect(MathF.Abs(an.PeakDb - expectedPeak) < 1.5f && MathF.Abs(an.RmsDb - expectedRms) < 2.5f,
            $"peak {an.PeakDb:F2} (expected ~{expectedPeak:F2}), rms {an.RmsDb:F2} (expected ~{expectedRms:F2})");
    }

    private static string? AnalyzerLatchesClip()
    {
        var an = new LevelAnalyzer();
        an.Prepare(Rate);
        an.Reset();

        var hot = new float[Block];
        Array.Fill(hot, 1.0f);
        an.Analyze(hot, 0, hot.Length);
        bool latched = an.Clipped;

        an.ClearClip();
        bool cleared = !an.Clipped;

        var quiet = new float[Block];
        an.Analyze(quiet, 0, quiet.Length);

        return Expect(latched && cleared && !an.Clipped,
            $"latched={latched}, cleared={cleared}, re-latched on silence={an.Clipped}");
    }

    // ---------------------------------------------------------------- chain

    private static DspChain MakeChain(ProcessorSettings settings, int rate = Rate)
    {
        var chain = new DspChain();
        chain.ApplySettings(settings);
        chain.Prepare(rate);
        return chain;
    }

    private static string? ChainBypassIsClean()
    {
        var settings = PresetLibrary.Find(PresetLibrary.Bypass)!;
        var chain = MakeChain(settings);

        var input = Sine(0.25f, 700f, Block);
        var work = (float[])input.Clone();
        chain.Process(work, 0, work.Length);

        float maxDelta = 0f;
        for (int i = 0; i < input.Length; i++) maxDelta = MathF.Max(maxDelta, MathF.Abs(work[i] - input[i]));

        return Expect(maxDelta < 1e-6f, $"bypass altered the signal by up to {maxDelta:E2}");
    }

    private static string? ChainMuteSilences()
    {
        var chain = MakeChain(PresetLibrary.Default);
        chain.Muted = true;

        var buffer = new float[Block];
        double phase = 0;
        for (int i = 0; i < 20; i++)
        {
            FillSine(buffer, 0.3f, 700f, ref phase);
            chain.Process(buffer, 0, buffer.Length);
        }

        return Expect(Peak(buffer) < 1e-5f, $"muted output still had a peak of {Peak(buffer):E2}");
    }

    private static string? ChainRescuesQuietMic()
    {
        // The headline use case: a mic running about -45 dBFS should come out substantially
        // louder, and must not clip on the way.
        var settings = PresetLibrary.Find(PresetLibrary.QuietMicRescue)!;
        var chain = MakeChain(settings);

        var buffer = new float[Block];
        double phase = 0;
        float inputAmp = 0.0056f; // about -45 dBFS
        float worstPeak = 0f;
        float lastRms = 0f;

        for (int i = 0; i < 1200; i++)
        {
            FillSine(buffer, inputAmp, 300f, ref phase);
            chain.Process(buffer, 0, buffer.Length);
            if (!AllFinite(buffer)) return $"chain emitted a non-finite sample at block {i}";
            worstPeak = MathF.Max(worstPeak, Peak(buffer));
            lastRms = Rms(buffer);
        }

        float ceiling = DspMath.DbToLinear(settings.Limiter.CeilingDb);
        float gainDb = Db(lastRms) - Db(inputAmp / 1.41421f);

        return Expect(gainDb > 15f && worstPeak <= ceiling * 1.05f,
            $"gained {gainDb:F1} dB (want > 15) with a worst peak of {Db(worstPeak):F2} dB against a {settings.Limiter.CeilingDb:F2} dB ceiling");
    }

    private static string? ChainSanitisesGarbage()
    {
        var chain = MakeChain(PresetLibrary.Default);

        var buffer = new float[Block];
        double phase = 0;
        FillSine(buffer, 0.2f, 700f, ref phase);
        buffer[10] = float.NaN;
        buffer[11] = float.PositiveInfinity;
        buffer[12] = float.NegativeInfinity;
        buffer[13] = 1e30f;

        chain.Process(buffer, 0, buffer.Length);
        if (!AllFinite(buffer)) return "a NaN/Inf input produced a non-finite output";

        // The real risk is that garbage poisons recursive state permanently, so keep going
        // with clean audio and confirm it recovers.
        for (int i = 0; i < 50; i++)
        {
            FillSine(buffer, 0.2f, 700f, ref phase);
            chain.Process(buffer, 0, buffer.Length);
            if (!AllFinite(buffer)) return $"chain state stayed poisoned at block {i}";
        }

        return Expect(Peak(buffer) > 1e-4f, "chain went permanently silent after garbage input");
    }

    private static string? ChainHandlesAllPresets()
    {
        foreach (var preset in PresetLibrary.BuiltIn)
        {
            var chain = MakeChain(preset.Settings.Clone());
            var buffer = new float[Block];
            double phase = 0;
            for (int i = 0; i < 200; i++)
            {
                FillSine(buffer, 0.05f, 400f, ref phase);
                chain.Process(buffer, 0, buffer.Length);
                if (!AllFinite(buffer)) return $"preset '{preset.Name}' produced a non-finite sample";
            }

            float ceiling = DspMath.DbToLinear(preset.Settings.Limiter.CeilingDb);
            if (preset.Settings.Limiter.Enabled && Peak(buffer) > ceiling * 1.05f)
                return $"preset '{preset.Name}' exceeded its limiter ceiling ({Db(Peak(buffer)):F2} dB)";
        }
        return null;
    }

    private static string? ChainHandlesAllRates()
    {
        foreach (int rate in new[] { 8000, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 192000 })
        {
            var chain = MakeChain(PresetLibrary.Default, rate);
            var buffer = new float[Block];
            double step = 2 * Math.PI * 440.0 / rate;
            double phase = 0;
            for (int i = 0; i < 100; i++)
            {
                for (int j = 0; j < buffer.Length; j++) { buffer[j] = (float)(0.1 * Math.Sin(phase)); phase += step; }
                chain.Process(buffer, 0, buffer.Length);
                if (!AllFinite(buffer)) return $"{rate} Hz produced a non-finite sample";
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- formats

    private static string? CheckDownmix(WaveFormat format, byte[] bytes, float expected, string label)
    {
        var mixer = new MonoDownmixer(format, ChannelMode.Left, 0);
        if (!mixer.IsSupported) return $"{label}: reported unsupported ({mixer.UnsupportedReason})";

        var dest = new float[16];
        int n = mixer.Convert(bytes, 0, bytes.Length, dest);
        if (n < 1) return $"{label}: decoded {n} samples";

        return Expect(MathF.Abs(dest[0] - expected) < 0.01f,
            $"{label}: expected {expected:F3}, got {dest[0]:F3}");
    }

    private static string? DownmixFloat32()
    {
        var bytes = BitConverter.GetBytes(0.5f);
        return CheckDownmix(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1), bytes, 0.5f, "float32");
    }

    private static string? DownmixPcm16()
    {
        var bytes = BitConverter.GetBytes((short)16384); // half of full scale
        return CheckDownmix(new WaveFormat(Rate, 16, 1), bytes, 0.5f, "pcm16");
    }

    private static string? DownmixPcm24()
    {
        // 0x400000 == 4194304 == half of 2^23
        var bytes = new byte[] { 0x00, 0x00, 0x40 };
        return CheckDownmix(new WaveFormat(Rate, 24, 1), bytes, 0.5f, "pcm24");
    }

    private static string? DownmixPcm32()
    {
        var bytes = BitConverter.GetBytes(1073741824); // half of 2^31
        return CheckDownmix(new WaveFormat(Rate, 32, 1), bytes, 0.5f, "pcm32");
    }

    private static string? DownmixPcm8()
    {
        var bytes = new byte[] { 192 }; // (192-128)/128 == 0.5
        return CheckDownmix(new WaveFormat(Rate, 8, 1), bytes, 0.5f, "pcm8");
    }

    private static string? DownmixChannelModes()
    {
        // Stereo float: left 0.8, right 0.2. This is the interface-with-mic-on-input-1 case.
        var bytes = new byte[8];
        BitConverter.GetBytes(0.8f).CopyTo(bytes, 0);
        BitConverter.GetBytes(0.2f).CopyTo(bytes, 4);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        var dest = new float[8];

        var left = new MonoDownmixer(format, ChannelMode.Left, 0);
        left.Convert(bytes, 0, bytes.Length, dest);
        float l = dest[0];

        var right = new MonoDownmixer(format, ChannelMode.Right, 0);
        right.Convert(bytes, 0, bytes.Length, dest);
        float r = dest[0];

        var mix = new MonoDownmixer(format, ChannelMode.MixAll, 0);
        mix.Convert(bytes, 0, bytes.Length, dest);
        float m = dest[0];

        var specific = new MonoDownmixer(format, ChannelMode.Specific, 1);
        specific.Convert(bytes, 0, bytes.Length, dest);
        float s = dest[0];

        return Expect(MathF.Abs(l - 0.8f) < 0.01f && MathF.Abs(r - 0.2f) < 0.01f
                      && MathF.Abs(m - 0.5f) < 0.01f && MathF.Abs(s - 0.2f) < 0.01f,
            $"left={l:F3} (want .8) right={r:F3} (want .2) mix={m:F3} (want .5) specific[1]={s:F3} (want .2)");
    }

    private static string? DownmixRejectsUnknown()
    {
        // A-law is a real capture format we do not decode; it must be reported, not silently
        // treated as PCM and turned into noise.
        var format = WaveFormat.CreateALawFormat(8000, 1);
        var mixer = new MonoDownmixer(format, ChannelMode.MixAll, 0);
        return Expect(!mixer.IsSupported && !string.IsNullOrWhiteSpace(mixer.UnsupportedReason),
            $"A-law should be rejected with an explanation; supported={mixer.IsSupported}, reason='{mixer.UnsupportedReason}'");
    }

    private static string? DownmixPartialFrame()
    {
        // 10 bytes of a 4-byte stereo frame == 2 whole frames plus a stray 2 bytes.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        var mixer = new MonoDownmixer(format, ChannelMode.Left, 0);
        var bytes = new byte[18];
        var dest = new float[16];
        int n = mixer.Convert(bytes, 0, bytes.Length, dest);
        return Expect(n == 2, $"expected 2 complete frames from 18 bytes of 8-byte frames, got {n}");
    }

    private static string? FanOutChannels()
    {
        foreach (int channels in new[] { 1, 2, 4, 6, 8 })
        {
            var source = new ConstantSampleProvider(0.25f, Rate);
            var fan = new MonoToMultiChannelProvider(source, channels);

            if (fan.WaveFormat.Channels != channels)
                return $"{channels} ch: provider reported {fan.WaveFormat.Channels}";

            var buffer = new float[channels * 32];
            int read = fan.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length) return $"{channels} ch: read {read} of {buffer.Length}";
            foreach (var v in buffer)
                if (MathF.Abs(v - 0.25f) > 1e-6f) return $"{channels} ch: got {v:F4}, expected 0.25 in every channel";
        }
        return null;
    }

    /// <summary>A never-ending constant-value mono source, for exercising the fan-out.</summary>
    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;

        public ConstantSampleProvider(float value, int rate)
        {
            _value = value;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = _value;
            return count;
        }
    }
}
