using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicBooster.Models;

namespace MicBooster.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON at <see cref="SettingsPath"/>.
/// </summary>
/// <remarks>
/// Nothing in here throws. Settings are a convenience, so a missing, locked, or hand-mangled
/// file must never be the reason the app refuses to start or refuses to close. Failures are
/// reported through <see cref="LastError"/> and the caller gets usable defaults instead.
/// </remarks>
public sealed class SettingsStore : IDisposable
{
    /// <summary>
    /// Debounce window for <see cref="SaveDebounced"/>. Long enough that dragging a slider
    /// produces one write at the end rather than one per pixel.
    /// </summary>
    private const int DebounceMilliseconds = 800;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enum names, not ordinals: inserting a member into ChannelMode or RoutingMode later
        // must not silently reinterpret an existing file, and the file stays hand-editable.
        Converters = { new JsonStringEnumConverter() },
        // Read-side tolerance, for exactly that hand-editing.
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Guards <see cref="_pending"/> and <see cref="_debounce"/>.</summary>
    private readonly object _pendingGate = new();

    /// <summary>
    /// Serialises the file writes themselves, so an explicit save and a debounced save can
    /// never both be using the single temp file name at once.
    /// </summary>
    private readonly object _writeGate = new();

    private System.Threading.Timer? _debounce;
    private AppSettings? _pending;
    private bool _disposed;

    /// <summary>Full path of the settings file: <c>%AppData%\MicBooster\settings.json</c>.</summary>
    public static string SettingsPath { get; } = BuildSettingsPath();

    /// <summary>
    /// Description of the most recent load or save problem, or null when the last operation
    /// succeeded. Suitable for showing in the status bar.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Reads the settings file. Returns defaults for every failure mode: no file, an
    /// unreadable file, invalid JSON, or a file containing literal <c>null</c>. A file that
    /// cannot be parsed is moved aside to <c>settings.corrupt.json</c> first, so the user's
    /// old values are recoverable rather than quietly overwritten on the next save.
    /// </summary>
    public AppSettings Load()
    {
        LastError = null;
        var path = SettingsPath;

        string json;
        try
        {
            if (!File.Exists(path)) return CreateDefault();
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            LastError = $"Could not read settings, using defaults: {ex.Message}";
            return CreateDefault();
        }

        if (string.IsNullOrWhiteSpace(json)) return CreateDefault();

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            LastError = $"Settings file was not valid and has been kept as settings.corrupt.json: {ex.Message}";
            Quarantine(path);
            return CreateDefault();
        }

        if (loaded is null)
        {
            LastError = "Settings file was empty and has been kept as settings.corrupt.json.";
            Quarantine(path);
            return CreateDefault();
        }

        Normalize(loaded);
        loaded.Clamp();
        return loaded;
    }

    /// <summary>
    /// Writes the settings immediately. Serialises to a temp file in the same directory and
    /// renames it over the target, so losing power mid-write leaves the previous file intact
    /// rather than a truncated one.
    /// </summary>
    public void Save(AppSettings settings)
    {
        if (settings is null) return;

        var path = SettingsPath;
        lock (_writeGate)
        {
            var temp = path + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = $"Could not save settings: {ex.Message}";
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                    // A stray temp file is harmless; the next successful save overwrites it.
                }
            }
        }
    }

    /// <summary>
    /// Requests a save in about <see cref="DebounceMilliseconds"/> ms, replacing any request
    /// already pending. Cheap enough to call from the UI thread on every property change; only
    /// the most recently supplied instance is written.
    /// </summary>
    public void SaveDebounced(AppSettings settings)
    {
        if (settings is null) return;

        var writeNow = false;
        lock (_pendingGate)
        {
            if (_disposed)
            {
                // Nothing is left to fire the timer, so honour the request rather than drop it.
                writeNow = true;
            }
            else
            {
                _pending = settings;
                _debounce ??= new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
                try
                {
                    _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    _pending = null;
                    writeNow = true;
                }
            }
        }

        if (writeNow) Save(settings);
    }

    /// <summary>
    /// Writes any pending debounced save right now. Call before shutting down so the last
    /// change a user made is not lost to the debounce window.
    /// </summary>
    public void Flush()
    {
        AppSettings? pending;
        lock (_pendingGate)
        {
            pending = _pending;
            _pending = null;
            try
            {
                _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down; the write below is all that is left to do.
            }
        }

        if (pending is not null) Save(pending);
    }

    /// <summary>
    /// Stops the debounce timer and writes a pending save synchronously, so closing the
    /// window immediately after a change still persists it.
    /// </summary>
    public void Dispose()
    {
        System.Threading.Timer? timer;
        AppSettings? pending;

        lock (_pendingGate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _debounce;
            _debounce = null;
            pending = _pending;
            _pending = null;
        }

        if (timer is not null)
        {
            try
            {
                // Wait for any callback already running, so it cannot write after we do and
                // resurrect older values.
                using var finished = new ManualResetEvent(false);
                if (timer.Dispose(finished)) finished.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Teardown must not throw out of a window-close handler.
            }
        }

        if (pending is not null) Save(pending);
    }

    private void OnDebounceElapsed(object? state)
    {
        AppSettings? pending;
        lock (_pendingGate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not null) Save(pending);
    }

    private static string BuildSettingsPath()
    {
        string root;
        try
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        catch (Exception)
        {
            root = string.Empty;
        }

        // A blank roaming profile path would otherwise fail at type load, which is the one
        // failure this class cannot report. Fall back next to the executable.
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root)) root = ".";

        return Path.Combine(root, "MicBooster", "settings.json");
    }

    private static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            Processor = PresetLibrary.Default,
            // Matches what Processor was populated from, so the preset list shows a selection
            // on a first run instead of looking like a custom setup.
            ActivePresetName = PresetLibrary.VoiceChat
        };
        settings.Clamp();
        return settings;
    }

    private static void Quarantine(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            var target = string.IsNullOrEmpty(directory)
                ? "settings.corrupt.json"
                : Path.Combine(directory, "settings.corrupt.json");
            File.Move(path, target, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort only. Starting up matters more than preserving the bad file.
        }
    }

    /// <summary>
    /// Replaces nulls that a hand-edited or truncated file can produce. This has to happen
    /// before <see cref="AppSettings.Clamp"/>, which dereferences every sub-section and each
    /// preset unconditionally.
    /// </summary>
    private static void Normalize(AppSettings settings)
    {
        if (settings.Version <= 0) settings.Version = 1;

        settings.Processor = OrNew(settings.Processor);
        NormalizeProcessor(settings.Processor);

        settings.CustomPresets = OrNew(settings.CustomPresets);
        for (var i = settings.CustomPresets.Count - 1; i >= 0; i--)
        {
            var preset = settings.CustomPresets[i];
            if (preset is null)
            {
                settings.CustomPresets.RemoveAt(i);
                continue;
            }

            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "Untitled";
            preset.Settings = OrNew(preset.Settings);
            NormalizeProcessor(preset.Settings);
        }

        settings.MuteHotkey = OrEmpty(settings.MuteHotkey);
        settings.BoostUpHotkey = OrEmpty(settings.BoostUpHotkey);
        settings.BoostDownHotkey = OrEmpty(settings.BoostDownHotkey);

        // Zero is the "never stored" value the window-restore logic checks for, so anything
        // unusable becomes zero rather than a size that breaks WPF layout.
        if (!double.IsFinite(settings.WindowWidth) || settings.WindowWidth < 0) settings.WindowWidth = 0;
        if (!double.IsFinite(settings.WindowHeight) || settings.WindowHeight < 0) settings.WindowHeight = 0;
    }

    private static void NormalizeProcessor(ProcessorSettings processor)
    {
        processor.HighPass = OrNew(processor.HighPass);
        processor.Gate = OrNew(processor.Gate);
        processor.Compressor = OrNew(processor.Compressor);
        processor.AutoLevel = OrNew(processor.AutoLevel);
        processor.Limiter = OrNew(processor.Limiter);
    }

    private static T OrNew<T>(T? value) where T : class, new() => value ?? new T();

    private static string OrEmpty(string? value) => value ?? string.Empty;
}
