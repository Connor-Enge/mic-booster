using System.Diagnostics;
using MicBooster.Audio.Dsp;
using MicBooster.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicBooster.Audio;

/// <summary>
/// Owns the live signal path: WASAPI capture, mono downmix, the <see cref="DspChain"/>, and
/// optional WASAPI playback into a monitor or virtual cable.
/// </summary>
/// <remarks>
/// Nothing here touches WPF. Events are raised on whichever thread the audio stack or the
/// metrics timer happens to be on, and the view-model marshals them. Every COM call is wrapped,
/// because an endpoint can be pulled out of the machine at any moment and a raw HRESULT
/// escaping into the capture thread would take the process down with it.
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    // Below ~10 ms a shared-mode event-driven client starts refusing the requested period on
    // real hardware, and above 200 ms the monitor path is unusable, so the request is pinned.
    private const int MinBufferMilliseconds = 10;
    private const int MaxBufferMilliseconds = 200;
    private const int MetricsIntervalMilliseconds = 33;

    // Only used when the render endpoint refuses to report its mix format at all.
    private const int FallbackSampleRate = 48000;
    private const int FallbackChannels = 2;

    private const float DspLoadSmoothing = 0.1f;

    private readonly DeviceManager _devices;
    private readonly object _sync = new();

    private volatile EngineState _state = EngineState.Stopped;
    private volatile string? _lastError;
    private volatile bool _disposed;

    /// <summary>Set while a deliberate teardown runs, so a late stop callback is not read as a fault.</summary>
    private volatile bool _teardown = true;

    private volatile WasapiCapture? _capture;
    private volatile WasapiOut? _output;
    private volatile BufferedWaveProvider? _bufferedProvider;
    private volatile VolumeSampleProvider? _volumeProvider;
    private volatile MonoDownmixer? _downmixer;

    // Control-thread only, always under _sync.
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;
    private System.Threading.Timer? _metricsTimer;

    // Scratch buffers for the capture callback; allocated in Start, grown only if a block
    // larger than the worst case we predicted ever turns up.
    private volatile float[]? _monoBuffer;
    private volatile byte[]? _outputBytes;

    private volatile int _sampleRate;
    private volatile int _inputChannels;
    private volatile float _bufferLatencyMs;
    private volatile string? _inputFormatDescription;
    private volatile string? _outputFormatDescription;
    private volatile float _outputVolume = 1f;

    private long _glitchCount;
    private volatile float _dspLoad;
    private float _dspLoadSmoothed;   // audio thread only
    private int _metricsBusy;

    /// <summary>Creates an engine that resolves its endpoints through <paramref name="devices"/>.</summary>
    public AudioEngine(DeviceManager devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <summary>The processing chain. Live for the whole lifetime of the engine, so the UI can bind to it before the first start.</summary>
    public DspChain Chain { get; } = new();

    /// <summary>Current lifecycle state.</summary>
    public EngineState State => _state;

    /// <summary>Message for the most recent fault, cleared on a successful start.</summary>
    public string? LastError => _lastError;

    /// <summary>Silences the processed signal without tearing the stream down.</summary>
    public bool Muted
    {
        get => Chain.Muted;
        set => Chain.Muted = value;
    }

    /// <summary>Linear trim applied after the chain, 0..1, just before the render device.</summary>
    public float OutputVolume
    {
        get => _outputVolume;
        set
        {
            float trim = DspMath.IsFinite(value) ? DspMath.Clamp(value, 0f, 1f) : 1f;
            _outputVolume = trim;

            // Null whenever routing is None, or before the graph exists.
            VolumeSampleProvider? provider = _volumeProvider;
            if (provider is not null) provider.Volume = trim;
        }
    }

    /// <summary>Sample rate of the running capture device, or 0 when stopped.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Channel count the capture device delivers, before the downmix. 0 when stopped.</summary>
    public int InputChannels => _inputChannels;

    /// <summary>Buffering plus lookahead latency in milliseconds. Tracks the limiter's lookahead live.</summary>
    public float LatencyMs
    {
        get
        {
            int rate = _sampleRate;
            float chainMs = rate > 0 ? Chain.LatencySamples * 1000f / rate : 0f;
            return _bufferLatencyMs + chainMs;
        }
    }

    /// <summary>Short description of the capture format, e.g. "48 kHz - 2 ch - 32-bit float". Null when stopped.</summary>
    public string? InputFormatDescription => _inputFormatDescription;

    /// <summary>Short description of what is being fed to the render device. Null when stopped or not routing.</summary>
    public string? OutputFormatDescription => _outputFormatDescription;

    /// <summary>Raised for every lifecycle transition, on the calling or callback thread.</summary>
    public event EventHandler<EngineState>? StateChanged;

    /// <summary>Raised with a message meant for the user, never a bare HRESULT.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Raised about 30 times a second while running, and once with <see cref="EngineMetrics.Idle"/> after stopping.</summary>
    public event EventHandler<EngineMetrics>? MetricsUpdated;

    /// <summary>
    /// Opens the devices and starts processing. Safe from any thread; a running engine is
    /// stopped first. On failure the engine ends up <see cref="EngineState.Faulted"/> with
    /// <see cref="LastError"/> set, rather than throwing.
    /// </summary>
    public void Start(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed) return;

        var transitions = new List<EngineState>(4);
        string? failure;

        lock (_sync)
        {
            TransitionToStoppedLocked(transitions);

            _state = EngineState.Starting;
            transitions.Add(EngineState.Starting);

            failure = StartLocked(options);

            if (failure is null)
            {
                _lastError = null;
                _state = EngineState.Running;
                transitions.Add(EngineState.Running);
            }
            else
            {
                TeardownLocked();
                _lastError = failure;
                _state = EngineState.Faulted;
                transitions.Add(EngineState.Faulted);
            }
        }

        Publish(transitions, failure, raiseIdleMetrics: failure is not null);
    }

    /// <summary>
    /// Stops processing and releases both endpoints. Safe from any thread and safe to call
    /// when already stopped.
    /// </summary>
    public void Stop()
    {
        var transitions = new List<EngineState>(2);
        lock (_sync)
        {
            TransitionToStoppedLocked(transitions);
        }

        Publish(transitions, null, raiseIdleMetrics: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Builds the whole graph. Returns null on success, or a user-facing message.</summary>
    private string? StartLocked(EngineOptions options)
    {
        _teardown = false;

        int bufferMs = Math.Clamp(options.BufferMilliseconds, MinBufferMilliseconds, MaxBufferMilliseconds);

        MMDevice? captureDevice;
        try
        {
            captureDevice = _devices.ResolveCapture(options.InputDeviceId, null);
        }
        catch (Exception ex)
        {
            return DescribeFailure(ex, "The microphone", "could not be opened");
        }

        if (captureDevice is null)
            return "No microphone available. Connect an input device, then start again.";

        _captureDevice = captureDevice;

        WasapiCapture capture;
        WaveFormat? captureFormat;
        try
        {
            // Event sync ties the callback to the device clock instead of a polling timer,
            // which is what keeps a small buffer viable.
            capture = new WasapiCapture(captureDevice, true, bufferMs);
            _capture = capture;
            captureFormat = capture.WaveFormat;
        }
        catch (Exception ex)
        {
            return DescribeFailure(ex, "The microphone", "could not be opened");
        }

        if (captureFormat is null || captureFormat.SampleRate <= 0 || captureFormat.Channels <= 0)
            return "The microphone reported an audio format this app cannot use.";

        MonoDownmixer downmixer;
        try
        {
            downmixer = new MonoDownmixer(captureFormat, options.ChannelMode, options.SourceChannelIndex);
        }
        catch (Exception ex)
        {
            return $"The microphone's audio format is not supported: {ex.Message}";
        }

        if (!downmixer.IsSupported)
            return downmixer.UnsupportedReason ?? "The microphone's audio format is not supported.";

        _downmixer = downmixer;
        _sampleRate = captureFormat.SampleRate;
        _inputChannels = captureFormat.Channels;
        _inputFormatDescription = DescribeFormat(captureFormat);

        Chain.Prepare(captureFormat.SampleRate);
        Chain.Reset();

        AllocateScratchBuffers(captureFormat, downmixer, bufferMs);

        // Push the requested trim through the property so the graph picks it up as it is built.
        OutputVolume = options.OutputVolume;

        if (options.Routing == RoutingMode.Output)
        {
            string? outputFailure = StartOutputLocked(options, captureFormat, bufferMs);
            if (outputFailure is not null) return outputFailure;
            _bufferLatencyMs = bufferMs * 2f;
        }
        else
        {
            _outputFormatDescription = null;
            _bufferLatencyMs = bufferMs;
        }

        Interlocked.Exchange(ref _glitchCount, 0L);
        _dspLoadSmoothed = 0f;
        _dspLoad = 0f;

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        try
        {
            // The client is initialised here, so an unsupported buffer size surfaces now.
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            return DescribeFailure(ex, "The microphone", "could not be started");
        }

        _metricsTimer = new System.Threading.Timer(
            OnMetricsTick, null, MetricsIntervalMilliseconds, MetricsIntervalMilliseconds);

        return null;
    }

    /// <summary>
    /// Builds the render side. The graph is assembled so the final format is IEEE float at the
    /// endpoint's own mix rate and channel count, which is the one combination WASAPI shared
    /// mode is guaranteed to take without silently inserting a converter.
    /// </summary>
    private string? StartOutputLocked(EngineOptions options, WaveFormat captureFormat, int bufferMs)
    {
        MMDevice? renderDevice;
        try
        {
            renderDevice = _devices.ResolveRender(options.OutputDeviceId, null);
        }
        catch (Exception ex)
        {
            return DescribeFailure(ex, "The output device", "could not be opened");
        }

        if (renderDevice is null)
            return "No playback device available. Pick an output device, or turn routing off to use the meters only.";

        _renderDevice = renderDevice;

        int outputRate = FallbackSampleRate;
        int outputChannels = FallbackChannels;
        try
        {
            WaveFormat mix = renderDevice.AudioClient.MixFormat;
            if (mix.SampleRate is >= 8000 and <= 384000) outputRate = mix.SampleRate;
            if (mix.Channels is >= 1 and <= 16) outputChannels = mix.Channels;
        }
        catch
        {
            // MixFormat throws outright on an endpoint that has just been invalidated, and the
            // ranges above reject the garbage some drivers report. Either way the fallback pair
            // is what practically every endpoint accepts in shared mode.
        }

        outputChannels = ChooseOutputChannels(renderDevice, outputRate, outputChannels);

        var buffered = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, 1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            // Deep enough to ride out a scheduling hiccup, shallow enough that the queue itself
            // never becomes the dominant latency.
            BufferDuration = TimeSpan.FromMilliseconds(Math.Max(bufferMs * 4, 250))
        };

        ISampleProvider samples = new WaveToSampleProvider(buffered);
        if (captureFormat.SampleRate != outputRate)
            samples = new WdlResamplingSampleProvider(samples, outputRate);
        if (outputChannels != 1)
            samples = new MonoToMultiChannelProvider(samples, outputChannels);

        var volume = new VolumeSampleProvider(samples) { Volume = _outputVolume };
        IWaveProvider waveProvider = new SampleToWaveProvider(volume);

        _bufferedProvider = buffered;
        _volumeProvider = volume;
        _outputFormatDescription = DescribeFormat(waveProvider.WaveFormat);

        try
        {
            var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, bufferMs);
            _output = output;
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(waveProvider);
            output.Play();
        }
        catch (Exception ex)
        {
            return DescribeFailure(ex, "The output device", "could not be started");
        }

        return null;
    }

    /// <summary>
    /// Sizes the capture-callback scratch space for a block several times larger than the one
    /// we asked for, so the grow-in-the-callback path stays theoretical.
    /// </summary>
    private void AllocateScratchBuffers(WaveFormat captureFormat, MonoDownmixer downmixer, int bufferMs)
    {
        int bytesPerSecond = captureFormat.AverageBytesPerSecond > 0
            ? captureFormat.AverageBytesPerSecond
            : Math.Max(captureFormat.BlockAlign, 4) * captureFormat.SampleRate;

        long worstCase = (long)bytesPerSecond * Math.Max(bufferMs * 4, 250) / 1000L;
        int capacityBytes = (int)Math.Clamp(worstCase, 16 * 1024, 8 * 1024 * 1024);

        int monoSamples;
        try
        {
            monoSamples = downmixer.MaxMonoSamples(capacityBytes);
        }
        catch
        {
            monoSamples = 0;
        }

        monoSamples = Math.Max(monoSamples, 8192);
        _monoBuffer = new float[monoSamples];
        _outputBytes = new byte[monoSamples * sizeof(float)];
    }

    /// <summary>
    /// Moves the engine to <see cref="EngineState.Stopped"/>, recording the transitions it made
    /// so the caller can raise them outside the lock. Idempotent.
    /// </summary>
    private void TransitionToStoppedLocked(List<EngineState> transitions)
    {
        if (_state == EngineState.Stopped)
        {
            TeardownLocked();
            return;
        }

        _state = EngineState.Stopping;
        transitions.Add(EngineState.Stopping);

        TeardownLocked();

        _state = EngineState.Stopped;
        transitions.Add(EngineState.Stopped);
    }

    /// <summary>
    /// Releases everything the last start acquired: capture, then output, then the metrics timer,
    /// then the endpoint handles. Every step tolerates failure, because a half-released engine is
    /// worse than a noisy one.
    /// </summary>
    private void TeardownLocked()
    {
        _teardown = true;

        WasapiCapture? capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            SafeDispose(capture);
        }

        WasapiOut? output = _output;
        _output = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { }
            SafeDispose(output);
        }

        System.Threading.Timer? timer = _metricsTimer;
        _metricsTimer = null;
        SafeDispose(timer);

        _bufferedProvider = null;
        _volumeProvider = null;
        _downmixer = null;
        _monoBuffer = null;
        _outputBytes = null;

        // Only safe once the capture thread has been joined by its Dispose above.
        SafeDispose(_captureDevice);
        _captureDevice = null;
        SafeDispose(_renderDevice);
        _renderDevice = null;

        _sampleRate = 0;
        _inputChannels = 0;
        _bufferLatencyMs = 0f;
        _inputFormatDescription = null;
        _outputFormatDescription = null;
        _dspLoad = 0f;
        _dspLoadSmoothed = 0f;
    }

    /// <summary>Raises the queued notifications. Never called while the start/stop lock is held.</summary>
    private void Publish(List<EngineState> transitions, string? error, bool raiseIdleMetrics)
    {
        if (transitions.Count == 0) return;

        EventHandler<EngineState>? stateHandler = StateChanged;
        if (stateHandler is not null)
        {
            for (int i = 0; i < transitions.Count; i++) stateHandler(this, transitions[i]);
        }

        if (error is not null) ErrorOccurred?.Invoke(this, error);

        // Meters would otherwise freeze at their last reading and look like a live signal.
        if (raiseIdleMetrics) MetricsUpdated?.Invoke(this, EngineMetrics.Idle);
    }

    /// <summary>
    /// Faults the engine from a device callback. The teardown runs on the caller's thread, so
    /// this must only ever be reached from a pool thread: disposing the capture joins its own
    /// thread, which would deadlock if called from inside a capture callback.
    /// </summary>
    private void Fault(string message)
    {
        var transitions = new List<EngineState>(2);
        lock (_sync)
        {
            if (_state is EngineState.Stopped or EngineState.Faulted) return;

            _state = EngineState.Stopping;
            transitions.Add(EngineState.Stopping);

            TeardownLocked();

            _lastError = message;
            _state = EngineState.Faulted;
            transitions.Add(EngineState.Faulted);
        }

        Publish(transitions, message, raiseIdleMetrics: true);
    }

    /// <summary>
    /// The capture callback. Allocation-free, lock-free and exception-free by construction:
    /// anything that goes wrong is counted as a glitch and the block is dropped.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            MonoDownmixer? downmixer = _downmixer;
            int byteCount = e.BytesRecorded;
            if (downmixer is null || byteCount <= 0) return;

            float[]? mono = _monoBuffer;
            int needed = downmixer.MaxMonoSamples(byteCount);
            if (mono is null || mono.Length < needed)
            {
                // A block bigger than the predicted worst case. Allocating on the audio thread is
                // a glitch in itself, so take headroom and never come back here.
                mono = new float[needed + (needed >> 1) + 1024];
                _monoBuffer = mono;
                Interlocked.Increment(ref _glitchCount);
            }

            int samples = downmixer.Convert(e.Buffer, 0, byteCount, mono);
            if (samples <= 0) return;

            Chain.Process(mono, 0, samples);

            BufferedWaveProvider? buffered = _bufferedProvider;
            if (buffered is not null)
            {
                int outBytes = samples * sizeof(float);
                byte[]? scratch = _outputBytes;
                if (scratch is null || scratch.Length < outBytes)
                {
                    scratch = new byte[outBytes + 4096];
                    _outputBytes = scratch;
                }

                // Little-endian float32 is exactly the wire format the graph expects.
                Buffer.BlockCopy(mono, 0, scratch, 0, outBytes);

                // DiscardOnBufferOverflow keeps AddSamples quiet about drops, and audio
                // disappearing with no indication is worse than a visible glitch counter.
                if (buffered.BufferedBytes + outBytes > buffered.BufferLength)
                    Interlocked.Increment(ref _glitchCount);

                buffered.AddSamples(scratch, 0, outBytes);
            }

            UpdateDspLoad(started, samples);
        }
        catch
        {
            Interlocked.Increment(ref _glitchCount);
        }
    }

    /// <summary>
    /// Tracks how much of each block's wall-clock duration was spent processing it. A load
    /// creeping toward 1 is the cheapest possible explanation for crackle.
    /// </summary>
    private void UpdateDspLoad(long startedTimestamp, int samples)
    {
        int rate = _sampleRate;
        if (rate <= 0 || samples <= 0) return;

        float blockSeconds = samples / (float)rate;
        if (blockSeconds <= 0f) return;

        float busySeconds = (float)((Stopwatch.GetTimestamp() - startedTimestamp) / (double)Stopwatch.Frequency);
        float instant = DspMath.Sanitize(busySeconds / blockSeconds);

        _dspLoadSmoothed += (instant - _dspLoadSmoothed) * DspLoadSmoothing;
        _dspLoadSmoothed = DspMath.FlushDenormal(_dspLoadSmoothed);
        _dspLoad = DspMath.Clamp(_dspLoadSmoothed, 0f, 10f);
    }

    private void OnMetricsTick(object? state)
    {
        // A tick that overruns its period must not queue up behind itself.
        if (Interlocked.Exchange(ref _metricsBusy, 1) == 1) return;

        try
        {
            if (_state != EngineState.Running) return;

            EventHandler<EngineMetrics>? handler = MetricsUpdated;
            if (handler is null) return;

            handler(this, Chain.BuildMetrics(LatencyMs, Interlocked.Read(ref _glitchCount), _dspLoad));
        }
        catch
        {
            // An escaping exception on a timer thread would tear down the process.
        }
        finally
        {
            Interlocked.Exchange(ref _metricsBusy, 0);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _teardown) return;
        if (!ReferenceEquals(sender, _capture)) return;   // a stale callback from a previous session

        string message = DescribeFailure(e.Exception, "The microphone", "stopped unexpectedly");

        // NAudio may deliver this on the capture thread itself, and the teardown joins that
        // thread, so the fault has to happen somewhere else.
        ThreadPool.QueueUserWorkItem(_ => Fault(message));
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _teardown) return;
        if (!ReferenceEquals(sender, _output)) return;

        string message = DescribeFailure(e.Exception, "The output device", "stopped unexpectedly");

        ThreadPool.QueueUserWorkItem(_ => Fault(message));
    }

    /// <summary>
    /// Picks the channel count to feed the render endpoint. Shared mode wants
    /// WAVEFORMATEXTENSIBLE above two channels and the graph emits a plain WAVEFORMATEX, so a
    /// multichannel endpoint gets probed and falls back to stereo, which its own mixer will
    /// spread across the real speaker layout.
    /// </summary>
    private static int ChooseOutputChannels(MMDevice device, int sampleRate, int mixChannels)
    {
        if (mixChannels <= 2) return mixChannels;

        try
        {
            AudioClient client = device.AudioClient;
            if (client.IsFormatSupported(AudioClientShareMode.Shared,
                    WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, mixChannels)))
                return mixChannels;

            if (client.IsFormatSupported(AudioClientShareMode.Shared,
                    WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
                return 2;
        }
        catch
        {
            // Probing is best-effort; matching the mix format is still the better guess.
        }

        return mixChannels;
    }

    /// <summary>Builds a short, human-readable format string for the UI.</summary>
    private static string DescribeFormat(WaveFormat format)
    {
        WaveFormat described = format;
        if (format is WaveFormatExtensible extensible)
        {
            try
            {
                described = extensible.ToStandardWaveFormat();
            }
            catch
            {
                described = format;   // an exotic subformat; the wrapper's own fields still read fine
            }
        }

        string rate = (described.SampleRate / 1000f).ToString("0.###");
        string depth = described.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => $"{described.BitsPerSample}-bit float",
            WaveFormatEncoding.Pcm => $"{described.BitsPerSample}-bit PCM",
            _ => $"{described.BitsPerSample}-bit"
        };

        return $"{rate} kHz - {described.Channels} ch - {depth}";
    }

    /// <summary>
    /// Turns an audio-stack exception into something a user can act on. An invalidated device is
    /// by far the most common cause and deserves its own wording rather than a raw HRESULT.
    /// </summary>
    private static string DescribeFailure(Exception ex, string subject, string action)
    {
        if (DeviceManager.IsDeviceInvalidated(ex))
            return $"{subject} was unplugged or its settings changed. Pick a device and start again.";

        return $"{subject} {action}: {ex.Message}";
    }

    /// <summary>Disposes without letting a dying COM object block the rest of the teardown.</summary>
    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable is null) return;
        try { disposable.Dispose(); } catch { }
    }
}
