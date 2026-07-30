using NAudio.Wave;

namespace MicBooster.Audio;

/// <summary>
/// Fans a mono sample provider out to any number of output channels by copying each
/// sample to every channel of the frame.
/// </summary>
/// <remarks>
/// NAudio only ships <c>MonoToStereoSampleProvider</c>, which is hard-wired to two
/// channels. Output devices in the wild are 1, 2, 4, 6 or 8 channels - a mono virtual
/// cable and a 7.1 receiver are both normal - so the fan-out width has to be dynamic.
/// <see cref="Read"/> runs on the render thread, so its working buffer is sized in the
/// constructor and only ever grows if a host asks for more than we anticipated.
/// </remarks>
public sealed class MonoToMultiChannelProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outputChannels;
    private float[] _monoBuffer;

    /// <summary>
    /// Wraps <paramref name="mono"/> so it presents <paramref name="outputChannels"/> channels.
    /// </summary>
    /// <param name="mono">A single-channel source.</param>
    /// <param name="outputChannels">Channel count to present; 1 is a pass-through.</param>
    public MonoToMultiChannelProvider(ISampleProvider mono, int outputChannels)
    {
        ArgumentNullException.ThrowIfNull(mono);
        if (outputChannels < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputChannels), outputChannels, "Output channel count must be at least 1.");
        }
        if (mono.WaveFormat is null || mono.WaveFormat.Channels != 1)
        {
            throw new ArgumentException(
                "Source must be a single-channel provider.", nameof(mono));
        }

        _source = mono;
        _outputChannels = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, outputChannels);

        // ~100 ms of mono audio covers any realistic WASAPI request in one go, so the
        // grow path below stays dormant and the render thread never allocates.
        int initial = Math.Max(1024, Math.Max(0, mono.WaveFormat.SampleRate) / 10);
        _monoBuffer = new float[initial];
    }

    /// <summary>IEEE float at the source's sample rate, with the requested channel count.</summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Reads up to <paramref name="count"/> interleaved floats, replicating each mono
    /// sample across the output channels.
    /// </summary>
    /// <returns>
    /// The exact number of floats written, which is short whenever the source ran dry.
    /// </returns>
    public int Read(float[] buffer, int offset, int count)
    {
        if (buffer is null || offset < 0 || count <= 0) return 0;
        if (offset + count > buffer.Length) count = buffer.Length - offset;
        if (count <= 0) return 0;

        int channels = _outputChannels;
        if (channels == 1) return _source.Read(buffer, offset, count);

        int framesWanted = (count + channels - 1) / channels;
        if (_monoBuffer.Length < framesWanted) _monoBuffer = new float[framesWanted];

        int framesRead = _source.Read(_monoBuffer, 0, framesWanted);
        if (framesRead <= 0) return 0;

        // A request that is not a whole number of frames can only ever be satisfied up to
        // `count`; the ceiling above means the last frame may be truncated rather than dropped.
        int total = framesRead * channels;
        if (total > count) total = count;

        int written = 0;
        int frame = 0;
        while (written < total)
        {
            float sample = _monoBuffer[frame++];
            int frameEnd = written + channels;
            if (frameEnd > total) frameEnd = total;
            while (written < frameEnd) buffer[offset + written++] = sample;
        }

        return written;
    }
}
