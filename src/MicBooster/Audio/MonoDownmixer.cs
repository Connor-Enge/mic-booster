using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MicBooster.Audio.Dsp;
using NAudio.Wave;

// NAudio.Wave has its own unrelated ChannelMode (an MP3 stereo mode), so the model type
// is aliased rather than imported to keep the reference unambiguous.
using ChannelMode = MicBooster.Models.ChannelMode;

namespace MicBooster.Audio;

/// <summary>
/// Decodes raw capture bytes into mono float32 samples in [-1, 1], folding a
/// multi-channel device down to the single channel the DSP chain processes.
/// </summary>
/// <remarks>
/// Every format decision is made once in the constructor, because <see cref="Convert"/>
/// runs on the WASAPI capture thread and must not allocate, lock, or throw.
/// This is the only place in the app that knows about sample formats; if a device offers
/// something we cannot decode, <see cref="IsSupported"/> is false and
/// <see cref="UnsupportedReason"/> explains what the user can do about it.
/// </remarks>
public sealed class MonoDownmixer
{
    /// <summary>The decode strategies we support, resolved once from the wave format.</summary>
    private enum SampleKind
    {
        Unsupported,
        Float32,
        Float64,
        Pcm8,
        Pcm16,
        Pcm24,
        Pcm32
    }

    // KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT. WaveFormatExtensible's outer encoding tag is
    // always 0xFFFE, so the subformat GUID is the only thing that identifies the real format.
    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly SampleKind _kind;
    private readonly int _bytesPerSample;
    private readonly int _frameBytes;
    private readonly bool _mixAll;
    private readonly int _pickByteOffset;
    private readonly float _mixScale;

    /// <summary>
    /// Inspects <paramref name="sourceFormat"/> and locks in a decode strategy.
    /// </summary>
    /// <param name="sourceFormat">The capture device's actual format.</param>
    /// <param name="mode">How to fold multiple channels into one.</param>
    /// <param name="specificChannelIndex">
    /// Channel to take when <paramref name="mode"/> is <see cref="ChannelMode.Specific"/>;
    /// clamped to the channels the device really has.
    /// </param>
    public MonoDownmixer(WaveFormat sourceFormat, ChannelMode mode, int specificChannelIndex)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        SourceChannels = sourceFormat.Channels;
        SampleRate = sourceFormat.SampleRate;

        int bits = sourceFormat.BitsPerSample;
        WaveFormatEncoding encoding = ResolveEncoding(sourceFormat, bits);

        _kind = encoding switch
        {
            WaveFormatEncoding.IeeeFloat when bits == 32 => SampleKind.Float32,
            WaveFormatEncoding.IeeeFloat when bits == 64 => SampleKind.Float64,
            WaveFormatEncoding.Pcm when bits == 8 => SampleKind.Pcm8,
            WaveFormatEncoding.Pcm when bits == 16 => SampleKind.Pcm16,
            WaveFormatEncoding.Pcm when bits == 24 => SampleKind.Pcm24,
            WaveFormatEncoding.Pcm when bits == 32 => SampleKind.Pcm32,
            _ => SampleKind.Unsupported
        };

        if (SourceChannels <= 0 || SampleRate <= 0)
        {
            _kind = SampleKind.Unsupported;
            UnsupportedReason =
                $"This device reports an unusable audio format ({SourceChannels} channel(s) at {SampleRate} Hz). " +
                "Unplug and reconnect it, or choose a different input device.";
        }
        else if (_kind == SampleKind.Unsupported)
        {
            // A bit depth of 0 shows up on compressed formats, where quoting it would just confuse.
            string depth = bits > 0 ? $", {bits}-bit" : string.Empty;
            UnsupportedReason =
                $"This device's audio format ({DescribeEncoding(sourceFormat.Encoding, encoding)}{depth}) " +
                "is not one Mic Booster can decode. In Windows Sound settings open this microphone's " +
                "properties, and on the Advanced tab pick a 16-bit or 24-bit PCM format - or choose a " +
                "different input device.";
        }

        // Deliberately derive the frame stride from the bit depth rather than trusting
        // BlockAlign: the decode arithmetic has to stay self-consistent even when a driver
        // reports a block alignment that disagrees with its own bit depth.
        _bytesPerSample = _kind == SampleKind.Unsupported ? 1 : Math.Max(1, bits / 8);
        _frameBytes = _kind == SampleKind.Unsupported ? 1 : _bytesPerSample * SourceChannels;

        int channels = Math.Max(1, SourceChannels);
        _mixAll = mode == ChannelMode.MixAll && channels > 1;
        _mixScale = 1f / channels;

        int pick = mode switch
        {
            // Right falls back to channel 0 so a mono interface is not silent.
            ChannelMode.Right => channels > 1 ? 1 : 0,
            ChannelMode.Specific => Math.Clamp(specificChannelIndex, 0, channels - 1),
            _ => 0
        };
        _pickByteOffset = pick * _bytesPerSample;
    }

    /// <summary>False when the device's format cannot be decoded; see <see cref="UnsupportedReason"/>.</summary>
    public bool IsSupported => _kind != SampleKind.Unsupported;

    /// <summary>A user-facing explanation when <see cref="IsSupported"/> is false, otherwise null.</summary>
    public string? UnsupportedReason { get; }

    /// <summary>Channel count of the capture format, as the device reported it.</summary>
    public int SourceChannels { get; }

    /// <summary>Sample rate of the capture format, as the device reported it.</summary>
    public int SampleRate { get; }

    /// <summary>
    /// Worst-case number of mono samples <see cref="Convert"/> can produce from
    /// <paramref name="byteCount"/> bytes, for sizing a destination buffer up front.
    /// </summary>
    public int MaxMonoSamples(int byteCount)
    {
        if (byteCount <= 0 || !IsSupported) return 0;
        return byteCount / _frameBytes;
    }

    /// <summary>
    /// Decodes and downmixes a capture buffer into <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of mono samples written.</returns>
    /// <remarks>
    /// Real-time safe: no allocation, no locks, and no throwing. A trailing partial frame
    /// is ignored, and the write is clamped to the destination's length, so a short or
    /// oversized buffer degrades to fewer samples instead of an exception on the audio thread.
    /// </remarks>
    public int Convert(byte[] source, int byteOffset, int byteCount, float[] destination)
    {
        if (_kind == SampleKind.Unsupported) return 0;
        if (source is null || destination is null) return 0;
        if (byteOffset < 0 || byteCount <= 0 || byteOffset >= source.Length) return 0;

        int available = source.Length - byteOffset;
        if (byteCount > available) byteCount = available;

        int frames = byteCount / _frameBytes;
        if (frames > destination.Length) frames = destination.Length;
        if (frames <= 0) return 0;

        ReadOnlySpan<byte> bytes = source.AsSpan(byteOffset, frames * _frameBytes);

        if (_mixAll)
        {
            // Average rather than sum: correlated channels (the same mic wired to both
            // inputs) would clip a summed mix while averaging leaves the level intact.
            int channels = SourceChannels;
            int frameStart = 0;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                int index = frameStart;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += ReadSample(bytes, index);
                    index += _bytesPerSample;
                }
                destination[i] = sum * _mixScale;
                frameStart += _frameBytes;
            }
        }
        else
        {
            int index = _pickByteOffset;
            for (int i = 0; i < frames; i++)
            {
                destination[i] = ReadSample(bytes, index);
                index += _frameBytes;
            }
        }

        return frames;
    }

    /// <summary>Decodes one sample at a byte offset inside the buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ReadSample(ReadOnlySpan<byte> bytes, int index)
    {
        switch (_kind)
        {
            case SampleKind.Float32:
                return ToUnit(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(index, 4)));

            case SampleKind.Float64:
                return ToUnit((float)BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(index, 8)));

            case SampleKind.Pcm16:
                return BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index, 2)) * (1f / 32768f);

            case SampleKind.Pcm24:
            {
                // Three-byte little-endian, sign-extended from bit 23.
                int packed = bytes[index] | (bytes[index + 1] << 8) | (bytes[index + 2] << 16);
                if ((packed & 0x00800000) != 0) packed |= unchecked((int)0xFF000000);
                return packed * (1f / 8388608f);
            }

            case SampleKind.Pcm32:
                return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index, 4)) * (1f / 2147483648f);

            case SampleKind.Pcm8:
                // 8-bit PCM is unsigned with a 128 bias.
                return (bytes[index] - 128) * (1f / 128f);

            default:
                return 0f;
        }
    }

    /// <summary>
    /// Guards the float paths only: a driver that hands us NaN, Infinity, or a wildly
    /// out-of-range value would permanently poison the envelope followers downstream.
    /// Integer formats are in range by construction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToUnit(float value)
        => DspMath.Clamp(DspMath.Sanitize(value), -1f, 1f);

    /// <summary>
    /// Unwraps <see cref="WaveFormatEncoding.Extensible"/> to the encoding its subformat
    /// GUID actually describes.
    /// </summary>
    private static WaveFormatEncoding ResolveEncoding(WaveFormat format, int bits)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible) return format.Encoding;

        // A device can report the Extensible tag on a plain WaveFormat, in which case there
        // is no GUID to read and the bit-depth fallback below decides.
        Guid subFormat = format is WaveFormatExtensible extensible ? extensible.SubFormat : Guid.Empty;

        if (subFormat == SubTypeIeeeFloat) return WaveFormatEncoding.IeeeFloat;
        if (subFormat == SubTypePcm) return WaveFormatEncoding.Pcm;

        // Unrecognised subtype, so fall back to the bit depth. At these widths a capture
        // endpoint realistically only offers linear PCM, and 64-bit only exists as float.
        return bits == 64 ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
    }

    /// <summary>Names the format for the error message, mentioning both tags when they differ.</summary>
    private static string DescribeEncoding(WaveFormatEncoding declared, WaveFormatEncoding resolved)
        => declared == resolved ? declared.ToString() : $"{declared} / {resolved}";
}
