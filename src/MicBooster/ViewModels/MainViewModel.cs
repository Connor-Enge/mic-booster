using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using MicBooster.Audio;
using MicBooster.Audio.Dsp;
using MicBooster.Models;
using MicBooster.Services;
using WpfApplication = System.Windows.Application;

namespace MicBooster.ViewModels;

/// <summary>
/// The single view model behind the main window. It owns the audio engine, the Windows
/// endpoint controls, persistence and the preset list, and exposes the whole feature set as
/// flat bindable properties so the XAML can bind two-way with no converters or sub-view-models.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly ChannelMode[] ChannelModeOptions =
    {
        ChannelMode.MixAll, ChannelMode.Left, ChannelMode.Right, ChannelMode.Specific
    };

    private static readonly RoutingMode[] RoutingModeOptions = { RoutingMode.Output, RoutingMode.None };

    private static readonly int[] BufferOptionValues = { 10, 15, 20, 30, 40, 50, 75, 100, 150, 200 };

    /// <summary>
    /// Every flat processing property, so a preset load can notify them in one pass instead of
    /// each setter having to know about the others.
    /// </summary>
    private static readonly string[] ProcessorPropertyNames =
    {
        nameof(ProcessorEnabled), nameof(InputGainDb), nameof(OutputGainDb),
        nameof(HighPassEnabled), nameof(HighPassCutoffHz),
        nameof(GateEnabled), nameof(GateThresholdDb), nameof(GateRangeDb), nameof(GateHysteresisDb),
        nameof(GateAttackMs), nameof(GateHoldMs), nameof(GateReleaseMs),
        nameof(CompressorEnabled), nameof(CompressorThresholdDb), nameof(CompressorRatio),
        nameof(CompressorAttackMs), nameof(CompressorReleaseMs), nameof(CompressorKneeDb),
        nameof(CompressorAutoMakeup), nameof(CompressorMakeupDb),
        nameof(AutoLevelEnabled), nameof(AutoLevelTargetDb), nameof(AutoLevelMaxBoostDb),
        nameof(AutoLevelMaxCutDb), nameof(AutoLevelSpeedMs),
        nameof(LimiterEnabled), nameof(LimiterCeilingDb), nameof(LimiterReleaseMs), nameof(LimiterLookaheadMs)
    };

    private readonly DeviceManager _devices;
    private readonly bool _ownsDevices;
    private readonly SettingsStore _settingsStore;
    private readonly AudioEngine _engine;
    private readonly EndpointVolumeController _endpointVolume = new();
    private readonly HardwareBoostController _hardwareBoost = new();
    private readonly VirtualMicService _virtualMics = new();
    private readonly DispatcherTimer _idleMeterTimer;
    private readonly StringBuilder _statusBuilder = new(160);

    private AppSettings _settings = new();
    private EngineState _engineState = EngineState.Stopped;

    private bool _initializing;
    private bool _initialized;
    private bool _disposed;
    private bool _shutdownDone;

    /// <summary>Set while a device/format change is tearing the engine down and back up.</summary>
    private bool _restarting;

    /// <summary>Set while the device lists are being rebuilt, so selection churn has no side effects.</summary>
    private bool _suppressDeviceChange;

    /// <summary>Set while echoing a device-originated level change back into the properties.</summary>
    private bool _suppressEndpointWrite;

    private bool _suppressPresetApply;
    private bool _suppressHardwareBoostWrite;

    /// <summary>False until the idle meter has taken its first reading, so it can seed rather than ramp.</summary>
    private bool _idleMeterPrimed;

    private string? _attachedInputId;

    private AudioDeviceInfo? _selectedInputDevice;
    private AudioDeviceInfo? _selectedOutputDevice;
    private ChannelMode _selectedChannelMode = ChannelMode.MixAll;
    private RoutingMode _selectedRouting = RoutingMode.Output;
    private int _selectedBufferMs = 30;

    private string _inputFormatText = "unknown";
    private string _outputFormatText = "unknown";
    private string _statusText = "Stopped";
    private string _statusDetail = string.Empty;
    private string _errorText = string.Empty;
    private bool _hasError;
    private string _latencyText = "--";
    private string _glitchText = "--";

    private bool _windowsLevelSupported;
    private float _windowsMicLevelPercent = 100f;
    private string _windowsLevelText = "n/a";
    private string _windowsLevelDbText = string.Empty;
    private bool _windowsMuted;

    private bool _hardwareBoostAvailable;
    private string _hardwareBoostStatus = "Not probed";
    private IReadOnlyList<HardwareBoostStep> _hardwareBoostSteps = Array.Empty<HardwareBoostStep>();
    private HardwareBoostStep? _selectedHardwareBoost;

    private VirtualMicRoute? _virtualMicRoute;
    private string _virtualMicStatus = string.Empty;
    private string _virtualMicSteps = string.Empty;
    private string _virtualMicDisplayName = VirtualMicService.PreferredDisplayName;
    private string _virtualMicMessage = string.Empty;
    private bool _virtualMicRenamed;

    /// <summary>Set once the user types their own endpoint name, so detection stops re-seeding it.</summary>
    private bool _virtualMicNameEdited;

    private double _inputPeakDb = DspMath.MinusInfinityDb;
    private double _inputRmsDb = DspMath.MinusInfinityDb;
    private double _outputPeakDb = DspMath.MinusInfinityDb;
    private double _outputRmsDb = DspMath.MinusInfinityDb;
    private double _gateAttenuationDb;
    private double _compressorReductionDb;
    private double _limiterReductionDb;
    private double _autoLevelGainDb;
    private bool _inputClipping;
    private bool _outputClipping;
    private bool _gateOpen;
    private string _inputLevelText = "-inf dB";
    private string _outputLevelText = "-inf dB";
    private string _autoLevelGainText = "0.0 dB";

    private NamedPreset? _selectedPreset;
    private string _newPresetName = string.Empty;

    /// <summary>Creates a view model that owns its own device manager and settings store.</summary>
    public MainViewModel() : this(new DeviceManager(), new SettingsStore(), true)
    {
    }

    /// <summary>Creates a view model over externally owned services (the caller disposes them).</summary>
    public MainViewModel(DeviceManager devices, SettingsStore settingsStore)
        : this(devices, settingsStore, false)
    {
    }

    private MainViewModel(DeviceManager devices, SettingsStore settingsStore, bool ownsDevices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ownsDevices = ownsDevices;

        _engine = new AudioEngine(_devices);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.ErrorOccurred += OnEngineError;
        _engine.MetricsUpdated += OnEngineMetrics;
        _devices.DevicesChanged += OnDevicesChanged;
        _endpointVolume.ExternalLevelChanged += OnExternalLevelChanged;
        _endpointVolume.ExternalMuteChanged += OnExternalMuteChanged;

        // Bind the timer to the application dispatcher when there is one; CurrentDispatcher keeps
        // the designer and unit hosts working.
        Dispatcher dispatcher = WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _idleMeterTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _idleMeterTimer.Tick += OnIdleMeterTick;

        ToggleEngineCommand = new RelayCommand(ToggleEngine, () => !_restarting && !IsTransitioning);
        StartCommand = new RelayCommand(StartEngine, () => !_restarting && IsStopped);
        StopCommand = new RelayCommand(StopEngine, () => !_restarting && IsRunning);
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        RefreshDevicesCommand = new RelayCommand(() => RefreshDevices(false));
        SavePresetCommand = new RelayCommand(SavePreset, () => !string.IsNullOrWhiteSpace(NewPresetName));
        DeletePresetCommand = new RelayCommand(DeletePreset, () => IsSelectedPresetCustom);
        ResetProcessorCommand = new RelayCommand(ResetProcessor);
        OpenSoundSettingsCommand = new RelayCommand(OpenSoundSettings);
        ApplyWindowsLevelCommand = new RelayCommand(ApplyWindowsLevel, () => WindowsLevelSupported);
        InstallVirtualCableCommand = new RelayCommand(InstallVirtualCable);
        SetUpVirtualMicCommand = new RelayCommand(SetUpVirtualMic, () => VirtualMicAvailable);
        RenameVirtualMicCommand = new RelayCommand(RenameVirtualMic, () => VirtualMicAvailable);
        RestoreVirtualMicNameCommand = new RelayCommand(RestoreVirtualMicName, () => VirtualMicAvailable);
        OpenRecordingDevicesCommand = new RelayCommand(OpenRecordingDevices);
    }

    // ---- Devices and routing ---------------------------------------------

    /// <summary>Active capture devices offered in the input picker.</summary>
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();

    /// <summary>Active render devices offered in the output picker.</summary>
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    /// <summary>The microphone being processed. Re-attaches the Windows controls and restarts the engine.</summary>
    public AudioDeviceInfo? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!SetProperty(ref _selectedInputDevice, value)) return;
            if (_suppressDeviceChange) return;

            _settings.InputDeviceId = value?.Id;
            _settings.InputDeviceName = value?.FriendlyName;
            AttachInputDevice();
            UpdateFormatTexts();
            UpdateStatus();
            Persist();
            RestartIfRunning();
        }
    }

    /// <summary>Where processed audio is sent. Restarts the engine when it is running.</summary>
    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value)) return;
            if (_suppressDeviceChange) return;

            _settings.OutputDeviceId = value?.Id;
            _settings.OutputDeviceName = value?.FriendlyName;
            UpdateFormatTexts();
            UpdateStatus();
            Persist();
            RestartIfRunning();
        }
    }

    /// <summary>How a multi-channel interface is folded to mono.</summary>
    public IReadOnlyList<ChannelMode> ChannelModes => ChannelModeOptions;

    /// <inheritdoc cref="ChannelModes" />
    public ChannelMode SelectedChannelMode
    {
        get => _selectedChannelMode;
        set
        {
            if (!SetProperty(ref _selectedChannelMode, value)) return;
            _settings.ChannelMode = value;
            UpdateStatus();
            Persist();
            RestartIfRunning();
        }
    }

    /// <summary>Whether processed audio goes to an output device at all.</summary>
    public IReadOnlyList<RoutingMode> RoutingModes => RoutingModeOptions;

    /// <inheritdoc cref="RoutingModes" />
    public RoutingMode SelectedRouting
    {
        get => _selectedRouting;
        set
        {
            if (!SetProperty(ref _selectedRouting, value)) return;
            _settings.Routing = value;
            UpdateFormatTexts();
            UpdateStatus();
            Persist();
            RestartIfRunning();
        }
    }

    /// <summary>Selectable per-side buffer sizes in milliseconds.</summary>
    public IReadOnlyList<int> BufferOptions => BufferOptionValues;

    /// <inheritdoc cref="BufferOptions" />
    public int SelectedBufferMs
    {
        get => _selectedBufferMs;
        set
        {
            if (!SetProperty(ref _selectedBufferMs, value)) return;
            _settings.BufferMilliseconds = Math.Clamp(value, 10, 200);
            UpdateStatus();
            Persist();
            RestartIfRunning();
        }
    }

    /// <summary>Negotiated capture format, or the device's advertised one before Start.</summary>
    public string InputFormatText
    {
        get => _inputFormatText;
        private set => SetProperty(ref _inputFormatText, value);
    }

    /// <summary>Negotiated render format, or the device's advertised one before Start.</summary>
    public string OutputFormatText
    {
        get => _outputFormatText;
        private set => SetProperty(ref _outputFormatText, value);
    }

    /// <summary>
    /// The one explanation users need: monitoring is not the same as being heard by other apps.
    /// </summary>
    public string RoutingHelpText =>
        "Sending the output to your headphones only lets you hear yourself - other programs still " +
        "get the raw microphone. To make Discord, Zoom or OBS hear the processed audio, install a " +
        "virtual audio cable (VB-CABLE or VoiceMeeter), select it as the output device here, then " +
        "choose that same cable as the microphone inside the other program. \"None\" processes and " +
        "meters locally without opening an output, which is useful while you set levels.";

    // ---- Virtual microphone ----------------------------------------------

    /// <summary>The virtual audio cable we can drive, or null when none is installed.</summary>
    public VirtualMicRoute? VirtualMic
    {
        get => _virtualMicRoute;
        private set => SetProperty(ref _virtualMicRoute, value);
    }

    /// <summary>True when a cable was found, so the one-press set-up can do anything.</summary>
    public bool VirtualMicAvailable => _virtualMicRoute is not null;

    /// <summary>One line on whether other applications can hear the processed audio yet.</summary>
    public string VirtualMicStatus
    {
        get => _virtualMicStatus;
        private set => SetProperty(ref _virtualMicStatus, value);
    }

    /// <summary>What to do, in order, including inside the other application.</summary>
    public string VirtualMicSteps
    {
        get => _virtualMicSteps;
        private set => SetProperty(ref _virtualMicSteps, value);
    }

    /// <summary>The recording device other applications must pick, or "" when there is no cable.</summary>
    public string VirtualMicTargetName => _virtualMicRoute?.CaptureDevice.FriendlyName ?? string.Empty;

    /// <summary>
    /// True when the processed audio is going into the cable right now. Everything else about
    /// this feature can be set up correctly and still be silent if this is false.
    /// </summary>
    public bool VirtualMicIsActive
        => _virtualMicRoute is not null
           && SelectedRouting == RoutingMode.Output
           && string.Equals(SelectedOutputDevice?.Id, _virtualMicRoute.RenderDevice.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name to give the cable's recording endpoint. Seeded from whatever name the endpoint
    /// already carries, so renaming reads as an edit rather than a fresh guess.
    /// </summary>
    public string VirtualMicDisplayName
    {
        get => _virtualMicDisplayName;
        set
        {
            if (!SetProperty(ref _virtualMicDisplayName, value ?? string.Empty)) return;

            // Typing here is a decision, so a later device change must not overwrite it.
            _virtualMicNameEdited = true;
        }
    }

    /// <summary>True when the endpoint currently carries a name someone gave it, not Windows'.</summary>
    public bool VirtualMicRenamed
    {
        get => _virtualMicRenamed;
        private set => SetProperty(ref _virtualMicRenamed, value);
    }

    /// <summary>Outcome of the last set-up or rename, or "" when there is nothing to say.</summary>
    public string VirtualMicMessage
    {
        get => _virtualMicMessage;
        private set
        {
            if (SetProperty(ref _virtualMicMessage, value)) OnPropertyChanged(nameof(HasVirtualMicMessage));
        }
    }

    /// <summary>Whether <see cref="VirtualMicMessage"/> holds something worth showing.</summary>
    public bool HasVirtualMicMessage => _virtualMicMessage.Length > 0;

    // ---- Engine state ----------------------------------------------------

    /// <summary>True while the engine is starting or running.</summary>
    public bool IsRunning => _engineState is EngineState.Running or EngineState.Starting;

    /// <summary>True while the engine is stopped or faulted, i.e. Start is meaningful.</summary>
    public bool IsStopped => _engineState is EngineState.Stopped or EngineState.Faulted;

    private bool IsTransitioning => _engineState is EngineState.Starting or EngineState.Stopping;

    /// <summary>Short state line, e.g. "Running" or "Stopped after an error".</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The devices, formats, resampling notice and latency, in one sentence.</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>Whether <see cref="ErrorText"/> holds something worth showing.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>The most recent failure, in plain language.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>Round-trip latency while running, or the configured buffer while stopped.</summary>
    public string LatencyText
    {
        get => _latencyText;
        private set => SetProperty(ref _latencyText, value);
    }

    /// <summary>Dropout count, which is the honest signal that the buffer is too small.</summary>
    public string GlitchText
    {
        get => _glitchText;
        private set => SetProperty(ref _glitchText, value);
    }

    /// <summary>Caption for the single Start/Stop button.</summary>
    public string EngineButtonText => _engineState switch
    {
        EngineState.Starting => "Starting...",
        EngineState.Running => "Stop",
        EngineState.Stopping => "Stopping...",
        _ => "Start"
    };

    // ---- Windows level and hardware boost --------------------------------

    /// <summary>True when the endpoint let us read or write its capture level.</summary>
    public bool WindowsLevelSupported
    {
        get => _windowsLevelSupported;
        private set
        {
            if (SetProperty(ref _windowsLevelSupported, value))
            {
                ApplyWindowsLevelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The Windows capture level, 0..100. Writes straight through to the endpoint, so this
    /// control works with no engine running and no setup at all.
    /// </summary>
    public float WindowsMicLevelPercent
    {
        get => _windowsMicLevelPercent;
        set
        {
            float clamped = Math.Clamp(value, 0f, 100f);
            if (!SetProperty(ref _windowsMicLevelPercent, clamped)) return;

            _settings.WindowsMicLevelPercent = clamped;
            if (!_suppressEndpointWrite)
            {
                try
                {
                    _endpointVolume.LevelPercent = clamped;
                }
                catch (Exception ex)
                {
                    ReportError("Windows would not accept that microphone level: " + ex.Message);
                }
            }

            // Deliberately not re-reading the device here: endpoints quantise to their own steps,
            // and pushing the quantised value back would fight the user mid-drag.
            UpdateWindowsLevelTexts(false);
            Persist();
        }
    }

    /// <summary>The level as text, or a note that the device does not expose one.</summary>
    public string WindowsLevelText
    {
        get => _windowsLevelText;
        private set => SetProperty(ref _windowsLevelText, value);
    }

    /// <summary>The dB figure, shown only when the device reports a range that makes sense.</summary>
    public string WindowsLevelDbText
    {
        get => _windowsLevelDbText;
        private set => SetProperty(ref _windowsLevelDbText, value);
    }

    /// <summary>The endpoint's own mute, which is what other applications see.</summary>
    public bool WindowsMuted
    {
        get => _windowsMuted;
        set
        {
            if (!SetProperty(ref _windowsMuted, value)) return;
            if (_suppressEndpointWrite) return;

            try
            {
                _endpointVolume.Mute = value;
            }
            catch (Exception ex)
            {
                ReportError("This device does not allow muting from software: " + ex.Message);
            }
        }
    }

    /// <summary>True when the driver exposes a hardware boost control we can drive.</summary>
    public bool HardwareBoostAvailable
    {
        get => _hardwareBoostAvailable;
        private set => SetProperty(ref _hardwareBoostAvailable, value);
    }

    /// <summary>What we found while probing the device topology, for display.</summary>
    public string HardwareBoostStatus
    {
        get => _hardwareBoostStatus;
        private set => SetProperty(ref _hardwareBoostStatus, value);
    }

    /// <summary>The discrete boost steps the driver accepts.</summary>
    public IReadOnlyList<HardwareBoostStep> HardwareBoostSteps
    {
        get => _hardwareBoostSteps;
        private set => SetProperty(ref _hardwareBoostSteps, value);
    }

    /// <inheritdoc cref="HardwareBoostSteps" />
    public HardwareBoostStep? SelectedHardwareBoost
    {
        get => _selectedHardwareBoost;
        set
        {
            if (!SetProperty(ref _selectedHardwareBoost, value)) return;
            if (_suppressHardwareBoostWrite || value is null) return;

            try
            {
                _hardwareBoost.CurrentDb = value.Db;
                _settings.HardwareBoostDb = value.Db;
                Persist();
            }
            catch (Exception ex)
            {
                ReportError("The driver refused that boost setting: " + ex.Message);
            }
        }
    }

    // ---- Processing ------------------------------------------------------

    /// <summary>Master switch for the whole chain. Off means clean pass-through.</summary>
    public bool ProcessorEnabled
    {
        get => _settings.Processor.Enabled;
        set => SetProcessor(_settings.Processor.Enabled, value, v => _settings.Processor.Enabled = v);
    }

    /// <summary>The main boost, in dB.</summary>
    public float InputGainDb
    {
        get => _settings.Processor.InputGainDb;
        set => SetProcessor(_settings.Processor.InputGainDb, value, v => _settings.Processor.InputGainDb = v);
    }

    /// <summary>Trim after all processing, in dB.</summary>
    public float OutputGainDb
    {
        get => _settings.Processor.OutputGainDb;
        set => SetProcessor(_settings.Processor.OutputGainDb, value, v => _settings.Processor.OutputGainDb = v);
    }

    /// <summary>Linear output trim as a percentage, applied by the engine after the chain.</summary>
    public float OutputVolumePercent
    {
        get => _settings.OutputVolume * 100f;
        set
        {
            float linear = Math.Clamp(value, 0f, 100f) * 0.01f;
            if (MathF.Abs(linear - _settings.OutputVolume) < 1e-4f) return;

            _settings.OutputVolume = linear;
            try
            {
                _engine.OutputVolume = linear;
            }
            catch (Exception ex)
            {
                ReportError("Could not change the output volume: " + ex.Message);
            }

            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>Mutes the processed stream without touching the Windows device mute.</summary>
    public bool Muted
    {
        get => _settings.Muted;
        set
        {
            if (_settings.Muted == value) return;
            _settings.Muted = value;
            try
            {
                _engine.Muted = value;
            }
            catch (Exception ex)
            {
                ReportError("Could not change mute: " + ex.Message);
            }

            OnPropertyChanged();
            UpdateStatus();
            Persist();
        }
    }

    /// <summary>Enables the rumble filter.</summary>
    public bool HighPassEnabled
    {
        get => _settings.Processor.HighPass.Enabled;
        set => SetProcessor(_settings.Processor.HighPass.Enabled, value, v => _settings.Processor.HighPass.Enabled = v);
    }

    /// <summary>High-pass corner frequency in Hz.</summary>
    public float HighPassCutoffHz
    {
        get => _settings.Processor.HighPass.CutoffHz;
        set => SetProcessor(_settings.Processor.HighPass.CutoffHz, value, v => _settings.Processor.HighPass.CutoffHz = v);
    }

    /// <summary>Enables the noise gate.</summary>
    public bool GateEnabled
    {
        get => _settings.Processor.Gate.Enabled;
        set => SetProcessor(_settings.Processor.Gate.Enabled, value, v => _settings.Processor.Gate.Enabled = v);
    }

    /// <summary>Level the signal must exceed to open the gate, dBFS.</summary>
    public float GateThresholdDb
    {
        get => _settings.Processor.Gate.ThresholdDb;
        set => SetProcessor(_settings.Processor.Gate.ThresholdDb, value, v => _settings.Processor.Gate.ThresholdDb = v);
    }

    /// <summary>How far the gate ducks when closed, dB.</summary>
    public float GateRangeDb
    {
        get => _settings.Processor.Gate.RangeDb;
        set => SetProcessor(_settings.Processor.Gate.RangeDb, value, v => _settings.Processor.Gate.RangeDb = v);
    }

    /// <summary>Gate close margin below the threshold, dB.</summary>
    public float GateHysteresisDb
    {
        get => _settings.Processor.Gate.HysteresisDb;
        set => SetProcessor(_settings.Processor.Gate.HysteresisDb, value, v => _settings.Processor.Gate.HysteresisDb = v);
    }

    /// <summary>Gate open time in ms.</summary>
    public float GateAttackMs
    {
        get => _settings.Processor.Gate.AttackMs;
        set => SetProcessor(_settings.Processor.Gate.AttackMs, value, v => _settings.Processor.Gate.AttackMs = v);
    }

    /// <summary>How long the gate stays open after the signal drops, ms.</summary>
    public float GateHoldMs
    {
        get => _settings.Processor.Gate.HoldMs;
        set => SetProcessor(_settings.Processor.Gate.HoldMs, value, v => _settings.Processor.Gate.HoldMs = v);
    }

    /// <summary>Gate close time in ms.</summary>
    public float GateReleaseMs
    {
        get => _settings.Processor.Gate.ReleaseMs;
        set => SetProcessor(_settings.Processor.Gate.ReleaseMs, value, v => _settings.Processor.Gate.ReleaseMs = v);
    }

    /// <summary>Enables the compressor.</summary>
    public bool CompressorEnabled
    {
        get => _settings.Processor.Compressor.Enabled;
        set => SetProcessor(_settings.Processor.Compressor.Enabled, value, v => _settings.Processor.Compressor.Enabled = v);
    }

    /// <summary>Compression threshold in dBFS.</summary>
    public float CompressorThresholdDb
    {
        get => _settings.Processor.Compressor.ThresholdDb;
        set => SetProcessor(_settings.Processor.Compressor.ThresholdDb, value, v => _settings.Processor.Compressor.ThresholdDb = v);
    }

    /// <summary>Compression ratio, 1 means off.</summary>
    public float CompressorRatio
    {
        get => _settings.Processor.Compressor.Ratio;
        set => SetProcessor(_settings.Processor.Compressor.Ratio, value, v => _settings.Processor.Compressor.Ratio = v);
    }

    /// <summary>Compressor attack in ms.</summary>
    public float CompressorAttackMs
    {
        get => _settings.Processor.Compressor.AttackMs;
        set => SetProcessor(_settings.Processor.Compressor.AttackMs, value, v => _settings.Processor.Compressor.AttackMs = v);
    }

    /// <summary>Compressor release in ms.</summary>
    public float CompressorReleaseMs
    {
        get => _settings.Processor.Compressor.ReleaseMs;
        set => SetProcessor(_settings.Processor.Compressor.ReleaseMs, value, v => _settings.Processor.Compressor.ReleaseMs = v);
    }

    /// <summary>Soft-knee width in dB.</summary>
    public float CompressorKneeDb
    {
        get => _settings.Processor.Compressor.KneeDb;
        set => SetProcessor(_settings.Processor.Compressor.KneeDb, value, v => _settings.Processor.Compressor.KneeDb = v);
    }

    /// <summary>Derive make-up gain from threshold and ratio.</summary>
    public bool CompressorAutoMakeup
    {
        get => _settings.Processor.Compressor.AutoMakeup;
        set => SetProcessor(_settings.Processor.Compressor.AutoMakeup, value, v => _settings.Processor.Compressor.AutoMakeup = v);
    }

    /// <summary>Manual make-up gain in dB, used when auto make-up is off.</summary>
    public float CompressorMakeupDb
    {
        get => _settings.Processor.Compressor.MakeupDb;
        set => SetProcessor(_settings.Processor.Compressor.MakeupDb, value, v => _settings.Processor.Compressor.MakeupDb = v);
    }

    /// <summary>Enables the slow loudness rider.</summary>
    public bool AutoLevelEnabled
    {
        get => _settings.Processor.AutoLevel.Enabled;
        set => SetProcessor(_settings.Processor.AutoLevel.Enabled, value, v => _settings.Processor.AutoLevel.Enabled = v);
    }

    /// <summary>Target programme loudness in dBFS.</summary>
    public float AutoLevelTargetDb
    {
        get => _settings.Processor.AutoLevel.TargetDb;
        set => SetProcessor(_settings.Processor.AutoLevel.TargetDb, value, v => _settings.Processor.AutoLevel.TargetDb = v);
    }

    /// <summary>Most the rider may turn things up, dB.</summary>
    public float AutoLevelMaxBoostDb
    {
        get => _settings.Processor.AutoLevel.MaxBoostDb;
        set => SetProcessor(_settings.Processor.AutoLevel.MaxBoostDb, value, v => _settings.Processor.AutoLevel.MaxBoostDb = v);
    }

    /// <summary>Most the rider may turn things down, dB.</summary>
    public float AutoLevelMaxCutDb
    {
        get => _settings.Processor.AutoLevel.MaxCutDb;
        set => SetProcessor(_settings.Processor.AutoLevel.MaxCutDb, value, v => _settings.Processor.AutoLevel.MaxCutDb = v);
    }

    /// <summary>Rider reaction time in ms.</summary>
    public float AutoLevelSpeedMs
    {
        get => _settings.Processor.AutoLevel.SpeedMs;
        set => SetProcessor(_settings.Processor.AutoLevel.SpeedMs, value, v => _settings.Processor.AutoLevel.SpeedMs = v);
    }

    /// <summary>Enables the output limiter.</summary>
    public bool LimiterEnabled
    {
        get => _settings.Processor.Limiter.Enabled;
        set => SetProcessor(_settings.Processor.Limiter.Enabled, value, v => _settings.Processor.Limiter.Enabled = v);
    }

    /// <summary>Absolute output ceiling in dBFS.</summary>
    public float LimiterCeilingDb
    {
        get => _settings.Processor.Limiter.CeilingDb;
        set => SetProcessor(_settings.Processor.Limiter.CeilingDb, value, v => _settings.Processor.Limiter.CeilingDb = v);
    }

    /// <summary>Limiter release in ms.</summary>
    public float LimiterReleaseMs
    {
        get => _settings.Processor.Limiter.ReleaseMs;
        set => SetProcessor(_settings.Processor.Limiter.ReleaseMs, value, v => _settings.Processor.Limiter.ReleaseMs = v);
    }

    /// <summary>Limiter lookahead in ms. Costs latency, so it is user-visible.</summary>
    public float LimiterLookaheadMs
    {
        get => _settings.Processor.Limiter.LookaheadMs;
        set => SetProcessor(_settings.Processor.Limiter.LookaheadMs, value, v => _settings.Processor.Limiter.LookaheadMs = v);
    }

    // ---- Meters ----------------------------------------------------------

    /// <summary>Input peak level in dBFS.</summary>
    public double InputPeakDb
    {
        get => _inputPeakDb;
        private set => SetProperty(ref _inputPeakDb, value);
    }

    /// <summary>Input RMS level in dBFS.</summary>
    public double InputRmsDb
    {
        get => _inputRmsDb;
        private set => SetProperty(ref _inputRmsDb, value);
    }

    /// <summary>Output peak level in dBFS.</summary>
    public double OutputPeakDb
    {
        get => _outputPeakDb;
        private set => SetProperty(ref _outputPeakDb, value);
    }

    /// <summary>Output RMS level in dBFS.</summary>
    public double OutputRmsDb
    {
        get => _outputRmsDb;
        private set => SetProperty(ref _outputRmsDb, value);
    }

    /// <summary>Current gate ducking, negative dB, 0 when fully open.</summary>
    public double GateAttenuationDb
    {
        get => _gateAttenuationDb;
        private set => SetProperty(ref _gateAttenuationDb, value);
    }

    /// <summary>Current compressor gain reduction, positive dB.</summary>
    public double CompressorReductionDb
    {
        get => _compressorReductionDb;
        private set => SetProperty(ref _compressorReductionDb, value);
    }

    /// <summary>Current limiter gain reduction, positive dB.</summary>
    public double LimiterReductionDb
    {
        get => _limiterReductionDb;
        private set => SetProperty(ref _limiterReductionDb, value);
    }

    /// <summary>Gain the rider is applying right now, positive or negative dB.</summary>
    public double AutoLevelGainDb
    {
        get => _autoLevelGainDb;
        private set => SetProperty(ref _autoLevelGainDb, value);
    }

    /// <summary>True when the raw capture is hitting full scale, i.e. the Windows level is too high.</summary>
    public bool InputClipping
    {
        get => _inputClipping;
        private set => SetProperty(ref _inputClipping, value);
    }

    /// <summary>True when our own output hit full scale, which the limiter should prevent.</summary>
    public bool OutputClipping
    {
        get => _outputClipping;
        private set => SetProperty(ref _outputClipping, value);
    }

    /// <summary>True while the gate is passing audio.</summary>
    public bool GateOpen
    {
        get => _gateOpen;
        private set => SetProperty(ref _gateOpen, value);
    }

    /// <summary>Input peak and RMS as one readable string.</summary>
    public string InputLevelText
    {
        get => _inputLevelText;
        private set => SetProperty(ref _inputLevelText, value);
    }

    /// <summary>Output peak and RMS as one readable string.</summary>
    public string OutputLevelText
    {
        get => _outputLevelText;
        private set => SetProperty(ref _outputLevelText, value);
    }

    /// <summary>Signed rider gain as text.</summary>
    public string AutoLevelGainText
    {
        get => _autoLevelGainText;
        private set => SetProperty(ref _autoLevelGainText, value);
    }

    // ---- Presets and application behaviour -------------------------------

    /// <summary>Built-in presets first, then the user's own.</summary>
    public ObservableCollection<NamedPreset> Presets { get; } = new();

    /// <summary>Selecting a preset applies its settings to every processing property.</summary>
    public NamedPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            DeletePresetCommand.RaiseCanExecuteChanged();
            if (_suppressPresetApply || value is null) return;

            _settings.ActivePresetName = value.Name;
            ApplyProcessorSettings(value.Settings);
        }
    }

    /// <summary>Name used by <see cref="SavePresetCommand"/>.</summary>
    public string NewPresetName
    {
        get => _newPresetName;
        set
        {
            if (SetProperty(ref _newPresetName, value))
            {
                SavePresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Hide to the notification area instead of closing.</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value) return;
            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>Start processing as soon as the app launches.</summary>
    public bool StartEngineOnLaunch
    {
        get => _settings.StartEngineOnLaunch;
        set
        {
            if (_settings.StartEngineOnLaunch == value) return;
            _settings.StartEngineOnLaunch = value;
            OnPropertyChanged();
            Persist();
        }
    }

    /// <summary>Global mute hotkey gesture, e.g. "Ctrl+Alt+M". The window registers it.</summary>
    public string MuteHotkey
    {
        get => _settings.MuteHotkey;
        set
        {
            string gesture = value ?? string.Empty;
            if (string.Equals(_settings.MuteHotkey, gesture, StringComparison.Ordinal)) return;
            _settings.MuteHotkey = gesture;
            OnPropertyChanged();
            Persist();
        }
    }

    // ---- Commands --------------------------------------------------------

    /// <summary>Start when stopped, stop when running.</summary>
    public RelayCommand ToggleEngineCommand { get; }

    /// <summary>Start the engine.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Stop the engine.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Flip <see cref="Muted"/>. Also the target of the global hotkey.</summary>
    public RelayCommand ToggleMuteCommand { get; }

    /// <summary>Re-enumerate devices.</summary>
    public RelayCommand RefreshDevicesCommand { get; }

    /// <summary>Save the current processing settings under <see cref="NewPresetName"/>.</summary>
    public RelayCommand SavePresetCommand { get; }

    /// <summary>Delete the selected custom preset.</summary>
    public RelayCommand DeletePresetCommand { get; }

    /// <summary>Restore the default processing settings.</summary>
    public RelayCommand ResetProcessorCommand { get; }

    /// <summary>Open the Windows sound settings page.</summary>
    public RelayCommand OpenSoundSettingsCommand { get; }

    /// <summary>Push the current slider value at the endpoint again, and remember it for next launch.</summary>
    public RelayCommand ApplyWindowsLevelCommand { get; }

    /// <summary>Open a virtual cable's download page. Never downloads or installs anything itself.</summary>
    public RelayCommand InstallVirtualCableCommand { get; }

    /// <summary>Point the output path at the cable and get audio flowing, in one press.</summary>
    public RelayCommand SetUpVirtualMicCommand { get; }

    /// <summary>Give the cable's recording endpoint the name in <see cref="VirtualMicDisplayName"/>.</summary>
    public RelayCommand RenameVirtualMicCommand { get; }

    /// <summary>Put the endpoint's original Windows name back.</summary>
    public RelayCommand RestoreVirtualMicNameCommand { get; }

    /// <summary>Open the sound control panel's Recording tab, for doing any of this by hand.</summary>
    public RelayCommand OpenRecordingDevicesCommand { get; }

    // ---- Lifecycle -------------------------------------------------------

    /// <summary>
    /// Loads settings, populates the device lists, attaches the Windows controls and gets the
    /// idle meter running. Safe to call once; later calls are ignored.
    /// </summary>
    public void Initialize()
    {
        if (_initialized || _initializing || _disposed) return;
        _initializing = true;
        try
        {
            try
            {
                _settings = _settingsStore.Load();
            }
            catch (Exception ex)
            {
                _settings = new AppSettings();
                ReportError("Could not read your saved settings, starting from defaults: " + ex.Message);
            }

            _settings.Clamp();

            // Assign the backing fields directly: the public setters would try to restart an
            // engine that has not been configured yet.
            _selectedChannelMode = _settings.ChannelMode;
            _selectedRouting = _settings.Routing;
            _selectedBufferMs = NearestBufferOption(_settings.BufferMilliseconds);
            _settings.BufferMilliseconds = _selectedBufferMs;
            _windowsMicLevelPercent = _settings.WindowsMicLevelPercent;

            OnPropertyChanged(nameof(SelectedChannelMode));
            OnPropertyChanged(nameof(SelectedRouting));
            OnPropertyChanged(nameof(SelectedBufferMs));

            RefreshDevices(true);

            if (_settings.ApplyWindowsLevelOnStartup && WindowsLevelSupported)
            {
                try
                {
                    _endpointVolume.LevelPercent = _settings.WindowsMicLevelPercent;
                    UpdateWindowsLevelTexts();
                }
                catch (Exception ex)
                {
                    ReportError("Could not restore the saved microphone level: " + ex.Message);
                }
            }

            RebuildPresets();

            try
            {
                _engine.Muted = _settings.Muted;
                _engine.OutputVolume = _settings.OutputVolume;
                _engine.Chain.ApplySettings(_settings.Processor);
            }
            catch (Exception ex)
            {
                ReportError("Could not apply your processing settings: " + ex.Message);
            }

            RaiseAllProcessorProperties();
            OnPropertyChanged(nameof(Muted));
            OnPropertyChanged(nameof(OutputVolumePercent));
            OnPropertyChanged(nameof(MinimizeToTray));
            OnPropertyChanged(nameof(StartEngineOnLaunch));
            OnPropertyChanged(nameof(MuteHotkey));

            UpdateWindowsLevelTexts();
            UpdateFormatTexts();
            ResetMeters();
            UpdateStatus();
            StartIdleMetering();
        }
        finally
        {
            _initializing = false;
            _initialized = true;
        }

        if (_settings.StartEngineOnLaunch)
        {
            StartEngine();
        }

        RaiseCommandStates();
    }

    /// <summary>Stops audio and writes settings out synchronously. Safe to call more than once.</summary>
    public void Shutdown()
    {
        if (_shutdownDone) return;
        _shutdownDone = true;

        StopIdleMetering();

        try
        {
            _engine.Stop();
        }
        catch
        {
            // Nothing useful to do at shutdown; the engine disposes below either way.
        }

        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
            // Losing the last settings write is preferable to blocking exit.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Shutdown();

        _idleMeterTimer.Tick -= OnIdleMeterTick;
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.ErrorOccurred -= OnEngineError;
        _engine.MetricsUpdated -= OnEngineMetrics;
        _devices.DevicesChanged -= OnDevicesChanged;
        _endpointVolume.ExternalLevelChanged -= OnExternalLevelChanged;
        _endpointVolume.ExternalMuteChanged -= OnExternalMuteChanged;

        SafeDispose(_engine);
        SafeDispose(_endpointVolume);
        SafeDispose(_hardwareBoost);
        if (_ownsDevices) SafeDispose(_devices);
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // COM teardown on an unplugged device throws routinely and means nothing here.
        }
    }

    // ---- Engine control --------------------------------------------------

    private void ToggleEngine()
    {
        if (IsRunning) StopEngine();
        else StartEngine();
    }

    private void StartEngine()
    {
        if (_disposed) return;
        if (SelectedInputDevice is null)
        {
            ReportError("Pick a microphone first.");
            return;
        }

        if (SelectedRouting == RoutingMode.Output && SelectedOutputDevice is null)
        {
            ReportError("Pick an output device, or set routing to None to process without one.");
            return;
        }

        ClearError();
        try
        {
            _engine.Muted = _settings.Muted;
            _engine.OutputVolume = _settings.OutputVolume;
            _engine.Chain.ApplySettings(_settings.Processor);
            _engine.Start(BuildEngineOptions());
        }
        catch (Exception ex)
        {
            ReportError("Could not start audio: " + ex.Message);
        }

        UpdateFormatTexts();
        UpdateStatus();
        RaiseCommandStates();
    }

    private void StopEngine()
    {
        if (_disposed) return;
        try
        {
            _engine.Stop();
        }
        catch (Exception ex)
        {
            ReportError("Could not stop audio cleanly: " + ex.Message);
        }

        UpdateStatus();
        RaiseCommandStates();
    }

    /// <summary>
    /// Applies a change that the engine can only pick up at start-up. The guard matters because
    /// stopping raises StateChanged, which is one of the places that can call back in here.
    /// </summary>
    private void RestartIfRunning()
    {
        if (_disposed || _restarting || _initializing) return;
        if (!IsRunning) return;

        _restarting = true;
        RaiseCommandStates();
        try
        {
            StopEngine();
            StartEngine();
        }
        finally
        {
            _restarting = false;
            RaiseCommandStates();
        }
    }

    private EngineOptions BuildEngineOptions() => new()
    {
        InputDeviceId = SelectedInputDevice?.Id,
        OutputDeviceId = SelectedOutputDevice?.Id,
        ChannelMode = SelectedChannelMode,
        SourceChannelIndex = _settings.SourceChannelIndex,
        Routing = SelectedRouting,
        BufferMilliseconds = SelectedBufferMs,
        OutputVolume = _settings.OutputVolume
    };

    private void OnEngineStateChanged(object? sender, EngineState state) => OnUi(() =>
    {
        _engineState = state;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(EngineButtonText));

        if (state == EngineState.Running)
        {
            StopIdleMetering();
        }
        else if (state is EngineState.Stopped or EngineState.Faulted)
        {
            ResetMeters();
            StartIdleMetering();
        }

        if (state == EngineState.Faulted)
        {
            string? last = _engine.LastError;
            if (!string.IsNullOrWhiteSpace(last)) ReportError(last);
        }

        UpdateFormatTexts();
        UpdateStatus();
        RaiseCommandStates();
    });

    private void OnEngineError(object? sender, string message) => OnUi(() => ReportError(message));

    private void OnEngineMetrics(object? sender, EngineMetrics metrics) => OnUi(() => ApplyMetrics(metrics));

    // ---- Devices ---------------------------------------------------------

    private void OnDevicesChanged(object? sender, EventArgs e) => OnUi(() => RefreshDevices(false));

    /// <summary>
    /// Rebuilds both device lists and re-resolves the selection, preferring the saved ID and
    /// falling back to the saved friendly name so a reinstalled or re-plugged device is still found.
    /// </summary>
    private void RefreshDevices(bool initial)
    {
        if (_disposed) return;

        IReadOnlyList<AudioDeviceInfo> inputs;
        IReadOnlyList<AudioDeviceInfo> outputs;
        try
        {
            inputs = _devices.GetCaptureDevices();
            outputs = _devices.GetRenderDevices();
        }
        catch (Exception ex)
        {
            ReportError("Could not list audio devices: " + ex.Message);
            return;
        }

        string? wantInputId = initial ? _settings.InputDeviceId : _selectedInputDevice?.Id ?? _settings.InputDeviceId;
        string? wantInputName = initial ? _settings.InputDeviceName : _selectedInputDevice?.FriendlyName ?? _settings.InputDeviceName;
        string? wantOutputId = initial ? _settings.OutputDeviceId : _selectedOutputDevice?.Id ?? _settings.OutputDeviceId;
        string? wantOutputName = initial ? _settings.OutputDeviceName : _selectedOutputDevice?.FriendlyName ?? _settings.OutputDeviceName;

        bool inputVanished = !initial
            && _selectedInputDevice is not null
            && Match(inputs, _selectedInputDevice.Id, null) is null;

        _suppressDeviceChange = true;
        try
        {
            SyncDeviceList(InputDevices, inputs);
            SyncDeviceList(OutputDevices, outputs);

            AudioDeviceInfo? input = Match(inputs, wantInputId, wantInputName) ?? DefaultOrFirst(inputs);
            AudioDeviceInfo? output = Match(outputs, wantOutputId, wantOutputName) ?? DefaultOrFirst(outputs);

            _selectedInputDevice = input;
            _selectedOutputDevice = output;
            OnPropertyChanged(nameof(SelectedInputDevice));
            OnPropertyChanged(nameof(SelectedOutputDevice));
        }
        finally
        {
            _suppressDeviceChange = false;
        }

        _settings.InputDeviceId = _selectedInputDevice?.Id;
        _settings.InputDeviceName = _selectedInputDevice?.FriendlyName;
        _settings.OutputDeviceId = _selectedOutputDevice?.Id;
        _settings.OutputDeviceName = _selectedOutputDevice?.FriendlyName;

        if (initial || _selectedInputDevice?.Id != _attachedInputId)
        {
            AttachInputDevice();
        }

        if (inputVanished)
        {
            // Restart first: a successful start clears the error banner, so the notice about the
            // lost device has to be written after it, or it would vanish immediately.
            RestartIfRunning();
            ReportError("The microphone you were using disappeared. " +
                        (_selectedInputDevice is null
                            ? "No capture device is available."
                            : "Switched to \"" + _selectedInputDevice.FriendlyName + "\"."));
        }

        UpdateFormatTexts();
        UpdateStatus();
        UpdateVirtualMic();
        RaiseCommandStates();
        Persist();
    }

    private static void SyncDeviceList(ObservableCollection<AudioDeviceInfo> target, IReadOnlyList<AudioDeviceInfo> source)
    {
        // Records give value equality, so an unchanged enumeration leaves the ComboBox alone
        // instead of collapsing and re-opening its selection.
        if (target.Count == source.Count)
        {
            bool same = true;
            for (int i = 0; i < target.Count; i++)
            {
                if (!Equals(target[i], source[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same) return;
        }

        target.Clear();
        for (int i = 0; i < source.Count; i++) target.Add(source[i]);
    }

    private static AudioDeviceInfo? Match(IReadOnlyList<AudioDeviceInfo> devices, string? id, string? name)
    {
        if (!string.IsNullOrEmpty(id))
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].Id, id, StringComparison.OrdinalIgnoreCase)) return devices[i];
            }
        }

        if (!string.IsNullOrEmpty(name))
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].FriendlyName, name, StringComparison.OrdinalIgnoreCase)) return devices[i];
            }
        }

        return null;
    }

    private static AudioDeviceInfo? DefaultOrFirst(IReadOnlyList<AudioDeviceInfo> devices)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].IsDefault) return devices[i];
        }

        return devices.Count > 0 ? devices[0] : null;
    }

    /// <summary>
    /// Points the endpoint volume and hardware boost controllers at the selected microphone.
    /// Each gets its own MMDevice handle so one controller's teardown cannot invalidate the other.
    /// </summary>
    private void AttachInputDevice()
    {
        _attachedInputId = _selectedInputDevice?.Id;

        if (_selectedInputDevice is null)
        {
            try { _endpointVolume.Detach(); } catch { /* endpoint already gone */ }
            try { _hardwareBoost.Release(); } catch { /* topology already gone */ }
            UpdateWindowsLevelTexts();
            UpdateHardwareBoost();
            return;
        }

        string id = _selectedInputDevice.Id;
        string name = _selectedInputDevice.FriendlyName;

        try
        {
            _endpointVolume.Attach(_devices.ResolveCapture(id, name));
        }
        catch (Exception ex)
        {
            ReportError("Could not read the Windows level for this microphone: " + ex.Message);
        }

        try
        {
            _hardwareBoost.Probe(_devices.ResolveCapture(id, name));
        }
        catch
        {
            // A driver that will not describe its topology is normal, not an error worth showing.
        }

        UpdateWindowsLevelTexts();
        UpdateHardwareBoost();
    }

    /// <summary>
    /// Refreshes the Windows level readbacks. <paramref name="readFromDevice"/> is false when the
    /// change came from our own slider and the device's rounded answer would only cause a fight.
    /// </summary>
    private void UpdateWindowsLevelTexts(bool readFromDevice = true)
    {
        bool supported;
        try
        {
            supported = _endpointVolume.IsAttached && _endpointVolume.SupportsVolume;
        }
        catch
        {
            supported = false;
        }

        WindowsLevelSupported = supported;

        if (supported)
        {
            if (readFromDevice)
            {
                float actual = _windowsMicLevelPercent;
                try
                {
                    actual = _endpointVolume.LevelPercent;
                }
                catch
                {
                    // Keep whatever we last knew rather than snapping the slider to zero.
                }

                if (MathF.Abs(actual - _windowsMicLevelPercent) > 0.05f)
                {
                    _windowsMicLevelPercent = Math.Clamp(actual, 0f, 100f);
                    OnPropertyChanged(nameof(WindowsMicLevelPercent));
                }
            }

            WindowsLevelText = _windowsMicLevelPercent.ToString("0") + "%";

            float? db = null;
            try
            {
                db = _endpointVolume.LevelDb;
            }
            catch
            {
                // Devices with a nonsense dB range simply do not get a dB readout.
            }

            WindowsLevelDbText = db.HasValue ? db.Value.ToString("0.0") + " dB" : string.Empty;

            bool muted = false;
            try
            {
                muted = _endpointVolume.SupportsMute && _endpointVolume.Mute;
            }
            catch
            {
                // Not every endpoint answers; assume unmuted.
            }

            if (muted != _windowsMuted)
            {
                WithoutEndpointWrite(() => WindowsMuted = muted);
            }
        }
        else
        {
            WindowsLevelText = "not adjustable on this device";
            WindowsLevelDbText = string.Empty;
        }
    }

    private void UpdateHardwareBoost()
    {
        bool available;
        try
        {
            available = _hardwareBoost.IsAvailable;
        }
        catch
        {
            available = false;
        }

        HardwareBoostAvailable = available;

        try
        {
            HardwareBoostStatus = _hardwareBoost.StatusText;
        }
        catch
        {
            HardwareBoostStatus = "Unavailable";
        }

        IReadOnlyList<HardwareBoostStep> steps;
        try
        {
            steps = _hardwareBoost.Steps;
        }
        catch
        {
            steps = Array.Empty<HardwareBoostStep>();
        }

        HardwareBoostSteps = steps;

        // Show what the driver currently reports rather than forcing the saved value back into
        // hardware on launch: silently changing a hardware boost behind the user's back is worse
        // than losing the setting.
        float current = 0f;
        try
        {
            if (available) current = _hardwareBoost.CurrentDb;
        }
        catch
        {
            current = 0f;
        }

        HardwareBoostStep? nearest = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < steps.Count; i++)
        {
            float distance = MathF.Abs(steps[i].Db - current);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = steps[i];
            }
        }

        _suppressHardwareBoostWrite = true;
        try
        {
            SelectedHardwareBoost = nearest;
        }
        finally
        {
            _suppressHardwareBoostWrite = false;
        }
    }

    private void ApplyWindowsLevel()
    {
        if (!WindowsLevelSupported) return;

        try
        {
            _endpointVolume.LevelPercent = _windowsMicLevelPercent;
        }
        catch (Exception ex)
        {
            ReportError("Windows would not accept that microphone level: " + ex.Message);
            return;
        }

        // Pressing Apply is the user asking for this level to stick, so opt them in to
        // having it restored next launch.
        _settings.WindowsMicLevelPercent = _windowsMicLevelPercent;
        _settings.ApplyWindowsLevelOnStartup = true;
        UpdateWindowsLevelTexts();
        Persist();
    }

    // ---- Virtual microphone ----------------------------------------------

    /// <summary>
    /// Re-runs cable detection over the lists the pickers already hold and refreshes everything
    /// derived from it. It is only name matching over those lists, so it is cheap enough to run
    /// on every device change.
    /// </summary>
    private void UpdateVirtualMic()
    {
        VirtualMicRoute? route;
        try
        {
            route = _virtualMics.FindBest(OutputDevices, InputDevices);
        }
        catch (Exception ex)
        {
            route = null;
            ReportError("Could not check for a virtual audio cable: " + ex.Message);
        }

        VirtualMic = route;
        VirtualMicStatus = VirtualMicService.DescribeStatus(route);
        VirtualMicSteps = VirtualMicService.DescribeSteps(route);

        string? custom = VirtualMicService.GetCustomName(route?.CaptureDevice.Id);
        VirtualMicRenamed = !string.IsNullOrWhiteSpace(custom);

        if (!_virtualMicNameEdited)
        {
            _virtualMicDisplayName = VirtualMicRenamed ? custom! : VirtualMicService.PreferredDisplayName;
            OnPropertyChanged(nameof(VirtualMicDisplayName));
        }

        OnPropertyChanged(nameof(VirtualMicAvailable));
        OnPropertyChanged(nameof(VirtualMicTargetName));
        OnPropertyChanged(nameof(VirtualMicIsActive));
    }

    private void InstallVirtualCable()
    {
        // Only ever opens the page. Fetching or running a driver installer on someone's behalf is
        // not a decision this app gets to make.
        string url = VirtualMic?.Definition.DownloadUrl ?? string.Empty;
        if (url.Length == 0) url = VirtualMicService.RecommendedDownloadUrl;

        if (!VirtualMicService.OpenDownloadPage(url))
        {
            VirtualMicMessage = "No browser opened. The address is " + url;
        }
    }

    /// <summary>
    /// The whole job in one press: send the processed audio into the cable, remember that, get it
    /// running, then name exactly what to pick in the other application.
    /// </summary>
    private void SetUpVirtualMic()
    {
        VirtualMicRoute? route = VirtualMic;
        if (route is null || _disposed || _restarting) return;

        try
        {
            // The ordinary setters: each one writes through to settings and restarts a running
            // engine by itself, so there is nothing to duplicate here.
            SelectedRouting = RoutingMode.Output;
            SelectedOutputDevice = Match(OutputDevices, route.RenderDevice.Id, route.RenderDevice.FriendlyName)
                                   ?? route.RenderDevice;
            SaveNow();

            if (IsStopped) StartEngine();
        }
        catch (Exception ex)
        {
            ReportError("Could not send the processed audio to the virtual cable: " + ex.Message);
            return;
        }

        VirtualMicMessage = "Sending processed audio to \"" + route.RenderDevice.FriendlyName
                          + "\". In Discord choose \"" + route.CaptureDevice.FriendlyName
                          + "\" as your microphone.";
    }

    private void RenameVirtualMic()
    {
        VirtualMicRoute? route = VirtualMic;
        if (route is null) return;

        RenameResult result;
        try
        {
            result = VirtualMicService.TryRename(route.CaptureDevice.Id, VirtualMicDisplayName);
        }
        catch (Exception ex)
        {
            ReportError("Could not rename the recording device: " + ex.Message);
            return;
        }

        ShowRenameResult(result);
    }

    private void RestoreVirtualMicName()
    {
        VirtualMicRoute? route = VirtualMic;
        if (route is null) return;

        RenameResult result;
        try
        {
            result = VirtualMicService.TryRestoreName(route.CaptureDevice.Id);
        }
        catch (Exception ex)
        {
            ReportError("Could not restore the recording device's name: " + ex.Message);
            return;
        }

        // The box is now showing a name the endpoint no longer has, so let detection re-seed it.
        _virtualMicNameEdited = false;
        ShowRenameResult(result);
    }

    /// <summary>
    /// Re-enumerates and then reports. Windows builds the endpoint's display name from the value
    /// that was just written, so our own pickers pick the change up only on a fresh enumeration,
    /// and the refresh must not be allowed to overwrite the message.
    /// </summary>
    private void ShowRenameResult(RenameResult result)
    {
        RefreshDevices(false);
        VirtualMicMessage = result.Message;
    }

    private void OpenRecordingDevices()
    {
        if (!VirtualMicService.OpenRecordingDevicesPanel())
        {
            VirtualMicMessage = "Windows would not open the sound control panel. "
                              + "Running \"control mmsys.cpl\" from the Start menu does the same thing.";
        }
    }

    // ---- Processing plumbing ---------------------------------------------

    /// <summary>
    /// The single write path for every processing property: store, clamp, push to the live
    /// chain, notify, save. Having one of these is why none of the thirty setters can drift.
    /// </summary>
    private void SetProcessor<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;

        assign(value);
        _settings.Processor.Clamp();
        PushProcessorToChain();
        OnPropertyChanged(name);
        Persist();
    }

    private void PushProcessorToChain()
    {
        try
        {
            _engine.Chain.ApplySettings(_settings.Processor);
        }
        catch (Exception ex)
        {
            ReportError("Could not apply that setting: " + ex.Message);
        }
    }

    private void ApplyProcessorSettings(ProcessorSettings source)
    {
        _settings.Processor = source.Clone();
        _settings.Processor.Clamp();
        PushProcessorToChain();
        RaiseAllProcessorProperties();
        Persist();
    }

    private void RaiseAllProcessorProperties()
    {
        for (int i = 0; i < ProcessorPropertyNames.Length; i++)
        {
            OnPropertyChanged(ProcessorPropertyNames[i]);
        }
    }

    // ---- Presets ---------------------------------------------------------

    private static bool IsBuiltInName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (NamedPreset preset in PresetLibrary.BuiltIn)
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private bool IsSelectedPresetCustom => _selectedPreset is not null && !IsBuiltInName(_selectedPreset.Name);

    private void RebuildPresets(string? preferredName = null)
    {
        string? wanted = preferredName ?? _selectedPreset?.Name ?? _settings.ActivePresetName;

        Presets.Clear();
        foreach (NamedPreset builtIn in PresetLibrary.BuiltIn) Presets.Add(builtIn.Clone());
        foreach (NamedPreset custom in _settings.CustomPresets) Presets.Add(custom);

        NamedPreset? match = null;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            foreach (NamedPreset preset in Presets)
            {
                if (string.Equals(preset.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    match = preset;
                    break;
                }
            }
        }

        // Selecting here must not apply anything: the live settings are the user's own tweaks,
        // which may deliberately differ from the preset they started from.
        _suppressPresetApply = true;
        try
        {
            SelectedPreset = match;
        }
        finally
        {
            _suppressPresetApply = false;
        }
    }

    private void SavePreset()
    {
        string name = (NewPresetName ?? string.Empty).Trim();
        if (name.Length == 0) return;

        if (IsBuiltInName(name))
        {
            ReportError("\"" + name + "\" is a built-in preset name. Choose a different one.");
            return;
        }

        NamedPreset? existing = null;
        foreach (NamedPreset preset in _settings.CustomPresets)
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                existing = preset;
                break;
            }
        }

        if (existing is null)
        {
            _settings.CustomPresets.Add(new NamedPreset { Name = name, Settings = _settings.Processor.Clone() });
        }
        else
        {
            existing.Name = name;
            existing.Settings = _settings.Processor.Clone();
        }

        _settings.ActivePresetName = name;
        RebuildPresets(name);
        NewPresetName = string.Empty;
        ClearError();
        SaveNow();
        RaiseCommandStates();
    }

    private void DeletePreset()
    {
        NamedPreset? selected = _selectedPreset;
        if (selected is null) return;

        if (IsBuiltInName(selected.Name))
        {
            ReportError("Built-in presets cannot be deleted.");
            return;
        }

        for (int i = _settings.CustomPresets.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_settings.CustomPresets[i].Name, selected.Name, StringComparison.OrdinalIgnoreCase))
            {
                _settings.CustomPresets.RemoveAt(i);
            }
        }

        _settings.ActivePresetName = null;
        RebuildPresets();
        ClearError();
        SaveNow();
        RaiseCommandStates();
    }

    private void ResetProcessor()
    {
        _settings.ActivePresetName = PresetLibrary.VoiceChat;
        ApplyProcessorSettings(PresetLibrary.Default);
        RebuildPresets(PresetLibrary.VoiceChat);
        ClearError();
    }

    private void OpenSoundSettings()
    {
        try
        {
            // UseShellExecute is what lets the ms-settings: protocol handler resolve at all.
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ReportError("Could not open the Windows sound settings: " + ex.Message);
        }
    }

    // ---- Metering --------------------------------------------------------

    private void StartIdleMetering()
    {
        if (_disposed || _idleMeterTimer.IsEnabled) return;
        _idleMeterPrimed = false;
        _idleMeterTimer.Start();
    }

    private void StopIdleMetering()
    {
        if (_idleMeterTimer.IsEnabled) _idleMeterTimer.Stop();
    }

    /// <summary>
    /// While the engine is stopped the input meter follows the endpoint's own peak meter. It
    /// proves the microphone is alive before the user commits to pressing Start.
    /// </summary>
    private void OnIdleMeterTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (IsRunning)
        {
            StopIdleMetering();
            return;
        }

        float peakPercent;
        try
        {
            peakPercent = _endpointVolume.IsAttached ? _endpointVolume.PeakPercent : 0f;
        }
        catch
        {
            peakPercent = 0f;
        }

        double db = DspMath.LinearToDb(Math.Clamp(peakPercent, 0f, 100f) * 0.01f);

        double peak;
        double rms;
        if (_idleMeterPrimed)
        {
            // Instant rise, eased fall, so a 33 ms sample of a fast meter stays readable.
            peak = db > _inputPeakDb ? db : _inputPeakDb + (db - _inputPeakDb) * 0.30;
            rms = _inputRmsDb + (db - _inputRmsDb) * 0.15;
        }
        else
        {
            // Start from the measured level: easing up from -100 dB would take seconds and make
            // a working microphone look dead for the first moment the user is watching.
            peak = db;
            rms = db;
            _idleMeterPrimed = true;
        }

        InputPeakDb = peak;
        InputRmsDb = rms;
        InputClipping = db > -0.2;
        InputLevelText = FormatLevelPair(peak, rms);
    }

    private void ApplyMetrics(EngineMetrics metrics)
    {
        if (_disposed) return;

        InputPeakDb = metrics.InputPeakDb;
        InputRmsDb = metrics.InputRmsDb;
        OutputPeakDb = metrics.OutputPeakDb;
        OutputRmsDb = metrics.OutputRmsDb;
        GateAttenuationDb = metrics.GateAttenuationDb;
        GateOpen = metrics.GateOpen;
        CompressorReductionDb = metrics.CompressorReductionDb;
        LimiterReductionDb = metrics.LimiterReductionDb;
        AutoLevelGainDb = metrics.AutoLevelGainDb;
        InputClipping = metrics.InputClipping;
        OutputClipping = metrics.OutputClipping;

        InputLevelText = FormatLevelPair(metrics.InputPeakDb, metrics.InputRmsDb);
        OutputLevelText = FormatLevelPair(metrics.OutputPeakDb, metrics.OutputRmsDb);
        AutoLevelGainText = metrics.AutoLevelGainDb.ToString("+0.0;-0.0;0.0") + " dB";
        LatencyText = metrics.LatencyMs > 0f ? metrics.LatencyMs.ToString("0.#") + " ms" : "--";
        GlitchText = metrics.GlitchCount == 0
            ? "no dropouts"
            : metrics.GlitchCount + (metrics.GlitchCount == 1 ? " dropout" : " dropouts");
    }

    private void ResetMeters()
    {
        InputPeakDb = DspMath.MinusInfinityDb;
        InputRmsDb = DspMath.MinusInfinityDb;
        OutputPeakDb = DspMath.MinusInfinityDb;
        OutputRmsDb = DspMath.MinusInfinityDb;
        GateAttenuationDb = 0;
        GateOpen = false;
        CompressorReductionDb = 0;
        LimiterReductionDb = 0;
        AutoLevelGainDb = 0;
        InputClipping = false;
        OutputClipping = false;
        InputLevelText = FormatLevelPair(DspMath.MinusInfinityDb, DspMath.MinusInfinityDb);
        OutputLevelText = InputLevelText;
        AutoLevelGainText = "0.0 dB";
        LatencyText = "~" + SelectedBufferMs.ToString("0") + " ms buffer";
        GlitchText = "--";
    }

    private static string FormatLevelPair(double peakDb, double rmsDb)
        => "peak " + FormatDb(peakDb) + " / RMS " + FormatDb(rmsDb);

    private static string FormatDb(double db)
        => db <= DspMath.MinusInfinityDb + 0.5 || double.IsNaN(db) ? "-inf dB" : db.ToString("0.0") + " dB";

    // ---- Status ----------------------------------------------------------

    private void UpdateFormatTexts()
    {
        string? engineInput = null;
        string? engineOutput = null;
        try
        {
            engineInput = _engine.InputFormatDescription;
            engineOutput = _engine.OutputFormatDescription;
        }
        catch
        {
            // Reading a description should not throw, but the engine holds COM objects.
        }

        InputFormatText = !string.IsNullOrWhiteSpace(engineInput)
            ? engineInput!
            : SelectedInputDevice?.FormatSummary ?? "unknown";

        if (SelectedRouting == RoutingMode.None)
        {
            OutputFormatText = "no output device";
        }
        else
        {
            OutputFormatText = !string.IsNullOrWhiteSpace(engineOutput)
                ? engineOutput!
                : SelectedOutputDevice?.FormatSummary ?? "unknown";
        }
    }

    private void UpdateStatus()
    {
        StatusText = _engineState switch
        {
            EngineState.Starting => "Starting",
            EngineState.Running => Muted ? "Running (muted)" : "Running",
            EngineState.Stopping => "Stopping",
            EngineState.Faulted => "Stopped after an error",
            _ => "Stopped"
        };

        StringBuilder sb = _statusBuilder;
        sb.Clear();
        sb.Append(SelectedInputDevice?.FriendlyName ?? "no microphone selected");
        sb.Append(" -> ");
        sb.Append(SelectedRouting == RoutingMode.None
            ? "processing only (nothing is sent out)"
            : SelectedOutputDevice?.FriendlyName ?? "no output selected");

        sb.Append("  |  in ").Append(InputFormatText);
        if (SelectedRouting == RoutingMode.Output)
        {
            sb.Append(", out ").Append(OutputFormatText);
        }

        int inRate = SelectedInputDevice?.SampleRate ?? 0;
        int outRate = SelectedOutputDevice?.SampleRate ?? 0;
        if (SelectedRouting == RoutingMode.Output && inRate > 0 && outRate > 0 && inRate != outRate)
        {
            sb.Append("  |  resampling ").Append(FormatRate(inRate)).Append(" to ").Append(FormatRate(outRate));
        }

        if (SelectedChannelMode != ChannelMode.MixAll)
        {
            sb.Append("  |  channel ").Append(SelectedChannelMode);
        }

        sb.Append("  |  ");
        if (IsRunning)
        {
            float latency = 0f;
            try
            {
                latency = _engine.LatencyMs;
            }
            catch
            {
                latency = 0f;
            }

            sb.Append(latency > 0f ? latency.ToString("0.#") + " ms latency" : SelectedBufferMs + " ms buffer");
        }
        else
        {
            sb.Append(SelectedBufferMs).Append(" ms buffer, press Start");
        }

        if (!ProcessorEnabled)
        {
            sb.Append("  |  processing bypassed");
        }

        StatusDetail = sb.ToString();

        // Whether the cable is actually being fed is a function of the routing and the chosen
        // output, which is exactly what has just been re-read above.
        OnPropertyChanged(nameof(VirtualMicIsActive));

        if (IsStopped)
        {
            LatencyText = "~" + SelectedBufferMs.ToString("0") + " ms buffer";
        }
    }

    private static string FormatRate(int hz)
    {
        if (hz <= 0) return "unknown";
        double khz = hz / 1000.0;
        return khz == Math.Floor(khz) ? khz.ToString("0") + " kHz" : khz.ToString("0.0##") + " kHz";
    }

    // ---- Windows level notifications -------------------------------------

    // The value came from the device, or from another application moving the same slider, so it
    // must land in the properties without being echoed back to the endpoint.
    private void OnExternalLevelChanged(object? sender, float percent)
        => OnUi(() => WithoutEndpointWrite(() => WindowsMicLevelPercent = percent));

    private void OnExternalMuteChanged(object? sender, bool muted)
        => OnUi(() => WithoutEndpointWrite(() => WindowsMuted = muted));

    private void WithoutEndpointWrite(Action action)
    {
        bool previous = _suppressEndpointWrite;
        _suppressEndpointWrite = true;
        try
        {
            action();
        }
        finally
        {
            _suppressEndpointWrite = previous;
        }
    }

    // ---- Infrastructure --------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Device notifications, engine metrics and
    /// COM callbacks all arrive on arbitrary threads, and a null dispatcher (designer, teardown)
    /// must not be fatal.
    /// </summary>
    private void OnUi(Action action)
    {
        if (_disposed) return;

        Dispatcher? dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ReportErrorDirect("Unexpected problem updating the display: " + ex.Message);
            }

            return;
        }

        try
        {
            dispatcher.BeginInvoke(action);
        }
        catch
        {
            // The dispatcher is shutting down; there is nothing left to update.
        }
    }

    private void RaiseCommandStates()
    {
        ToggleEngineCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
        ApplyWindowsLevelCommand.RaiseCanExecuteChanged();
        SetUpVirtualMicCommand.RaiseCanExecuteChanged();
        RenameVirtualMicCommand.RaiseCanExecuteChanged();
        RestoreVirtualMicNameCommand.RaiseCanExecuteChanged();
    }

    private void ReportError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ReportErrorDirect(message);
    }

    private void ReportErrorDirect(string message)
    {
        ErrorText = message;
        HasError = true;
    }

    private void ClearError()
    {
        ErrorText = string.Empty;
        HasError = false;
    }

    private void Persist()
    {
        if (!_initialized || _disposed) return;
        try
        {
            _settingsStore.SaveDebounced(_settings);
        }
        catch
        {
            // A failed settings write must never interrupt what the user is doing.
        }
    }

    private void SaveNow()
    {
        if (_disposed) return;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ReportError("Could not save your settings: " + ex.Message);
        }
    }

    private static int NearestBufferOption(int milliseconds)
    {
        int best = BufferOptionValues[0];
        int bestDistance = int.MaxValue;
        foreach (int option in BufferOptionValues)
        {
            int distance = Math.Abs(option - milliseconds);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = option;
            }
        }

        return best;
    }
}
