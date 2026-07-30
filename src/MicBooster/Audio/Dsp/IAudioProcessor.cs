namespace MicBooster.Audio.Dsp;

/// <summary>
/// A single stage in the mono processing chain.
/// </summary>
/// <remarks>
/// Threading contract, which every implementation must honour:
/// <list type="bullet">
/// <item><see cref="Process"/> runs on the WASAPI capture thread. It must not allocate,
/// lock, log, or throw. All buffers it needs are allocated in <see cref="Prepare"/>.</item>
/// <item><see cref="Prepare"/> and <see cref="Reset"/> are called from the control thread
/// while the stream is stopped, or before the first callback. Allocation is fine here.</item>
/// <item>Public parameter properties are written from the UI thread while
/// <see cref="Process"/> is running. Back them with <c>volatile float</c> / <c>volatile bool</c>
/// (32-bit reads and writes are atomic) and never with a struct or double.</item>
/// <item>Readback properties (metering) are written by the audio thread and read by the UI
/// thread, and follow the same rule.</item>
/// </list>
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>
    /// Whether this stage processes audio. When false the stage must pass the buffer
    /// through untouched, and should keep its metering readbacks at rest.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Called when the sample rate is known or changes. Recompute coefficients and
    /// allocate any internal buffers here. Always followed by a <see cref="Reset"/>.
    /// </summary>
    void Prepare(int sampleRate);

    /// <summary>
    /// Process <paramref name="count"/> mono samples in place, starting at
    /// <paramref name="offset"/> in <paramref name="buffer"/>.
    /// </summary>
    void Process(float[] buffer, int offset, int count);

    /// <summary>
    /// Clear all internal state (filter memory, envelopes, lookahead delay lines) so the
    /// stage starts clean. Must not allocate.
    /// </summary>
    void Reset();
}
