using System.Globalization;

namespace MicBooster.Audio;

/// <summary>
/// An inert snapshot of one audio endpoint: everything the UI needs to list and pick a
/// device, with no live COM handle attached.
/// </summary>
/// <remarks>
/// Nothing here touches the device after construction, which is deliberate. A cached live
/// <c>MMDevice</c> for hardware that has since been unplugged is what makes audio apps hang,
/// so <see cref="DeviceManager"/> reads what it needs, disposes the device, and hands out
/// one of these instead.
/// </remarks>
public sealed record AudioDeviceInfo
{
    /// <summary>The endpoint ID string, stable until the device is reinstalled or moved.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Endpoint name as Windows shows it, e.g. "Microphone (USB Audio CODEC)".</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Underlying device name, e.g. "USB Audio CODEC". Null when the device would not report it.</summary>
    public string? Description { get; init; }

    /// <summary>True when this is the Windows default endpoint for its role.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True when the endpoint reported <c>DeviceState.Active</c>.</summary>
    public bool IsActive { get; init; }

    /// <summary>Mix-format sample rate in Hz, or 0 when the device refused to report its format.</summary>
    public int SampleRate { get; init; }

    /// <summary>Mix-format channel count, or 0 when the device refused to report its format.</summary>
    public int Channels { get; init; }

    /// <summary>
    /// Short human-readable format, e.g. "48 kHz / 2 ch". Returns "unknown" when the format
    /// could not be probed, which happens on real hardware even while it is active.
    /// </summary>
    public string FormatSummary
    {
        get
        {
            if (SampleRate <= 0 || Channels <= 0) return "unknown";

            // "0.###" keeps 44.1 kHz honest without printing "48.000 kHz" for the common case.
            string kilohertz = (SampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            return $"{kilohertz} kHz / {Channels} ch";
        }
    }

    /// <summary>Returns <see cref="FriendlyName"/> so the record can be bound straight to a ComboBox.</summary>
    public override string ToString() => FriendlyName;
}
