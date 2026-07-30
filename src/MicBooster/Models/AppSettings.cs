namespace MicBooster.Models;

/// <summary>A user-saved or built-in processing preset.</summary>
public sealed class NamedPreset
{
    public string Name { get; set; } = "Untitled";
    public ProcessorSettings Settings { get; set; } = new();

    public NamedPreset Clone() => new() { Name = Name, Settings = Settings.Clone() };
}

/// <summary>
/// Everything persisted between runs. Written to
/// <c>%AppData%\MicBooster\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Schema version, so a future build can migrate an old file instead of discarding it.</summary>
    public int Version { get; set; } = 1;

    // ---- Device selection -------------------------------------------------
    // Both the ID and the friendly name are stored. IDs are exact but change when a
    // device is reinstalled or moved to another USB port; the name is the fallback so his
    // setup keeps working instead of silently reverting to the wrong microphone.

    public string? InputDeviceId { get; set; }
    public string? InputDeviceName { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }

    public ChannelMode ChannelMode { get; set; } = ChannelMode.MixAll;
    public int SourceChannelIndex { get; set; }
    public RoutingMode Routing { get; set; } = RoutingMode.Output;

    /// <summary>Requested per-side buffer in ms. 10..200.</summary>
    public int BufferMilliseconds { get; set; } = 30;

    // ---- Windows-level control -------------------------------------------

    /// <summary>Last Windows capture level applied, 0..100.</summary>
    public float WindowsMicLevelPercent { get; set; } = 100f;

    /// <summary>Re-apply <see cref="WindowsMicLevelPercent"/> to the device when the app starts.</summary>
    public bool ApplyWindowsLevelOnStartup { get; set; }

    /// <summary>Last hardware boost in dB, when the device exposes one.</summary>
    public float HardwareBoostDb { get; set; }

    // ---- Processing -------------------------------------------------------

    public ProcessorSettings Processor { get; set; } = new();

    /// <summary>Name of the preset last selected, for display only.</summary>
    public string? ActivePresetName { get; set; }

    public List<NamedPreset> CustomPresets { get; set; } = new();

    /// <summary>Linear output trim, 0..1.</summary>
    public float OutputVolume { get; set; } = 1f;

    public bool Muted { get; set; }

    // ---- Application behaviour -------------------------------------------

    public bool StartEngineOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    /// <summary>Global hotkey for mute toggle, e.g. "Ctrl+Alt+M". Empty disables it.</summary>
    public string MuteHotkey { get; set; } = "Ctrl+Alt+M";
    public string BoostUpHotkey { get; set; } = "";
    public string BoostDownHotkey { get; set; } = "";

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>Pins everything into legal ranges after a load.</summary>
    public void Clamp()
    {
        BufferMilliseconds = Math.Clamp(BufferMilliseconds, 10, 200);
        WindowsMicLevelPercent = Math.Clamp(WindowsMicLevelPercent, 0f, 100f);
        HardwareBoostDb = Math.Clamp(HardwareBoostDb, -100f, 100f);
        OutputVolume = Math.Clamp(OutputVolume, 0f, 1f);
        SourceChannelIndex = Math.Clamp(SourceChannelIndex, 0, 63);
        Processor ??= new ProcessorSettings();
        Processor.Clamp();
        CustomPresets ??= new List<NamedPreset>();
        foreach (var preset in CustomPresets)
        {
            preset.Settings ??= new ProcessorSettings();
            preset.Settings.Clamp();
        }
    }

    public EngineOptions ToEngineOptions() => new()
    {
        InputDeviceId = InputDeviceId,
        OutputDeviceId = OutputDeviceId,
        ChannelMode = ChannelMode,
        SourceChannelIndex = SourceChannelIndex,
        Routing = Routing,
        BufferMilliseconds = BufferMilliseconds,
        OutputVolume = OutputVolume
    };
}
