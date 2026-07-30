using System.Diagnostics;
using Microsoft.Win32;

namespace MicBooster.Audio;

/// <summary>One known virtual audio cable product and the endpoint names it installs.</summary>
/// <param name="ProviderName">Product name shown to the user.</param>
/// <param name="DownloadUrl">Where to get it, or an empty string when it ships with something else.</param>
/// <param name="RenderPatterns">Substrings identifying its playback (input-to-the-cable) endpoint.</param>
/// <param name="CapturePatterns">Substrings identifying its recording (output-of-the-cable) endpoint.</param>
/// <param name="Priority">Lower wins when several are installed.</param>
public sealed record VirtualCableDefinition(
    string ProviderName,
    string DownloadUrl,
    string[] RenderPatterns,
    string[] CapturePatterns,
    int Priority);

/// <summary>
/// A matched pair of endpoints forming a usable loop: we play into
/// <see cref="RenderDevice"/>, and other applications record from <see cref="CaptureDevice"/>.
/// </summary>
public sealed record VirtualMicRoute(
    VirtualCableDefinition Definition,
    AudioDeviceInfo RenderDevice,
    AudioDeviceInfo CaptureDevice);

/// <summary>What happened when we tried to rename an endpoint.</summary>
public enum RenameOutcome
{
    Success,
    AccessDenied,
    NotFound,
    InvalidName,
    Failed
}

/// <summary>Result of a rename attempt, with a message fit to show the user.</summary>
public sealed record RenameResult(RenameOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == RenameOutcome.Success;
}

/// <summary>
/// Makes the processed microphone appear to other applications (Discord, Zoom, OBS, games)
/// as its own selectable input device.
/// </summary>
/// <remarks>
/// <para>Windows will not let a normal program publish a recording device. Only a kernel-mode
/// audio driver can create a capture endpoint, and on 64-bit Windows 10/11 such a driver has to
/// be signed by Microsoft before it will load. So this app does not try to be that driver.
/// Instead it drives an existing, already-signed virtual audio cable: we render the processed
/// audio into the cable's playback endpoint, and the other application records from the cable's
/// recording endpoint. That is the same mechanism every streaming setup uses.</para>
/// <para>The one thing that makes it feel native is the name. A cable ships as something like
/// "CABLE Output (VB-Audio Virtual Cable)", which is meaningless in Discord's device list.
/// <see cref="TryRename"/> retitles the endpoint so it shows up as "Mic Booster" instead.
/// The registry key involved grants SetValue to ordinary users - that is how renaming in the
/// Windows sound panel works without a prompt - so this normally needs no elevation, and the
/// change is fully reversible with <see cref="TryRestoreName"/>.</para>
/// </remarks>
public sealed class VirtualMicService
{
    /// <summary>The name we give the cable's recording endpoint so it reads clearly elsewhere.</summary>
    public const string PreferredDisplayName = "Mic Booster";

    /// <summary>The product we point people at: free, signed, and the smallest possible install.</summary>
    public const string RecommendedProvider = "VB-CABLE";

    /// <summary>Official download page for <see cref="RecommendedProvider"/>.</summary>
    public const string RecommendedDownloadUrl = "https://vb-audio.com/Cable/";

    // PKEY_Device_FriendlyName. Windows composes a display name from PKEY_Device_DeviceDesc and
    // the interface name when this value is absent; writing it overrides the whole string, which
    // is exactly what renaming in the sound panel does.
    private const string FriendlyNameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";

    private const string CaptureKeyRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

    /// <summary>
    /// Cables we can recognise. Matching is on the endpoint name because these products keep
    /// their names stable across versions, whereas their device IDs are per-machine.
    /// </summary>
    public IReadOnlyList<VirtualCableDefinition> Known { get; } = new List<VirtualCableDefinition>
    {
        new(RecommendedProvider, RecommendedDownloadUrl,
            new[] { "CABLE Input" },
            new[] { "CABLE Output" },
            0),

        // The multi-cable VB-Audio products, for someone who already runs a bigger routing setup.
        new("VB-CABLE A/B", "https://vb-audio.com/Cable/",
            new[] { "CABLE-A Input", "CABLE-B Input", "CABLE-C Input", "CABLE-D Input" },
            new[] { "CABLE-A Output", "CABLE-B Output", "CABLE-C Output", "CABLE-D Output" },
            1),

        new("VoiceMeeter", "https://vb-audio.com/Voicemeeter/",
            new[] { "VoiceMeeter Input", "VoiceMeeter Aux Input", "VoiceMeeter VAIO3 Input" },
            new[] { "VoiceMeeter Output", "VoiceMeeter Aux Output", "VoiceMeeter VAIO3 Output" },
            2),

        new("Virtual Audio Cable", "https://vac.muzychenko.net/en/",
            new[] { "Virtual Audio Cable", "Line 1 (Virtual Audio Cable)" },
            new[] { "Virtual Audio Cable", "Line 1 (Virtual Audio Cable)" },
            3),

        // Ships with Steam, so a gamer may already have it even though it is not advertised
        // as a general-purpose cable.
        new("Steam Streaming", string.Empty,
            new[] { "Steam Streaming Speakers" },
            new[] { "Steam Streaming Microphone" },
            4)
    };

    /// <summary>
    /// Finds every usable cable across the given endpoint lists, best first.
    /// </summary>
    /// <param name="renderDevices">Active playback endpoints.</param>
    /// <param name="captureDevices">Active recording endpoints.</param>
    public IReadOnlyList<VirtualMicRoute> FindAll(
        IReadOnlyList<AudioDeviceInfo>? renderDevices,
        IReadOnlyList<AudioDeviceInfo>? captureDevices)
    {
        var routes = new List<VirtualMicRoute>();
        if (renderDevices is null || captureDevices is null) return routes;

        foreach (var definition in Known.OrderBy(d => d.Priority))
        {
            // Pair by matching suffix position so "CABLE-A Input" pairs with "CABLE-A Output"
            // rather than with whichever variant happens to enumerate first.
            foreach (var renderPattern in definition.RenderPatterns)
            {
                var render = renderDevices.FirstOrDefault(d => Matches(d, renderPattern));
                if (render is null) continue;

                string capturePattern = PairedCapturePattern(definition, renderPattern);
                var capture = captureDevices.FirstOrDefault(d => Matches(d, capturePattern));
                if (capture is null) continue;

                // Virtual Audio Cable uses the same name on both sides, so guard against
                // pairing an endpoint with itself.
                if (string.Equals(render.Id, capture.Id, StringComparison.OrdinalIgnoreCase)) continue;

                routes.Add(new VirtualMicRoute(definition, render, capture));
            }
        }

        return routes;
    }

    /// <summary>The cable we would use, or null when none is installed.</summary>
    public VirtualMicRoute? FindBest(
        IReadOnlyList<AudioDeviceInfo>? renderDevices,
        IReadOnlyList<AudioDeviceInfo>? captureDevices)
        => FindAll(renderDevices, captureDevices).FirstOrDefault();

    private static string PairedCapturePattern(VirtualCableDefinition definition, string renderPattern)
    {
        // The products name their two halves symmetrically, so the capture pattern is the
        // render pattern with Input swapped for Output where that applies.
        if (renderPattern.Contains("Input", StringComparison.OrdinalIgnoreCase))
            return renderPattern.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase);

        // Otherwise fall back to whatever capture patterns the definition lists.
        return definition.CapturePatterns.Length > 0 ? definition.CapturePatterns[0] : renderPattern;
    }

    private static bool Matches(AudioDeviceInfo device, string pattern)
        => device.IsActive
           && (device.FriendlyName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
               || (device.Description?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>A one-line summary of whether a virtual microphone is ready.</summary>
    public static string DescribeStatus(VirtualMicRoute? route)
        => route is null
            ? "No virtual audio cable found. Install one and other apps can use the processed microphone."
            : $"Ready via {route.Definition.ProviderName}. Other apps should select \"{route.CaptureDevice.FriendlyName}\" as their microphone.";

    /// <summary>
    /// The exact steps for the other application, naming the endpoint it must pick.
    /// </summary>
    public static string DescribeSteps(VirtualMicRoute? route)
    {
        if (route is null)
        {
            return "Windows only lets a driver create a microphone, so Mic Booster uses a free "
                 + "virtual audio cable to hand the processed sound to other programs. Install "
                 + $"{RecommendedProvider}, restart the PC, then come back here and press Set up.";
        }

        return $"1. Press \"Set up\" - the processed audio is sent to \"{route.RenderDevice.FriendlyName}\".\n"
             + $"2. In Discord, Zoom, OBS or your game, choose \"{route.CaptureDevice.FriendlyName}\" as the microphone.\n"
             + "3. Leave Mic Booster running with Start pressed.\n"
             + "In Discord also turn off Automatic Gain Control, or it will undo the processing.";
    }

    // ------------------------------------------------------------------ renaming

    /// <summary>
    /// Reads the override name currently set on a recording endpoint, if any.
    /// </summary>
    /// <returns>The custom name, or null when the endpoint uses its original name.</returns>
    public static string? GetCustomName(string? endpointId)
    {
        if (!TryGetEndpointGuid(endpointId, out string guid)) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CaptureKeyRoot}\{guid}\Properties", writable: false);
            return key?.GetValue(FriendlyNameValue) as string;
        }
        catch (Exception)
        {
            // A missing key or a locked-down machine both just mean "no custom name".
            return null;
        }
    }

    /// <summary>
    /// Renames a recording endpoint so other applications list it under
    /// <paramref name="displayName"/>.
    /// </summary>
    /// <param name="endpointId">Endpoint ID of the cable's recording device.</param>
    /// <param name="displayName">The new name, e.g. "Mic Booster".</param>
    /// <remarks>
    /// This edits the same machine-wide setting the Windows sound panel edits, and applications
    /// generally only re-read endpoint names when they start, so Discord needs a restart to show
    /// the new name.
    /// </remarks>
    public static RenameResult TryRename(string? endpointId, string displayName)
    {
        string name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl))
        {
            return new RenameResult(RenameOutcome.InvalidName,
                "Pick a name between 1 and 120 characters with no control characters.");
        }

        if (!TryGetEndpointGuid(endpointId, out string guid))
        {
            return new RenameResult(RenameOutcome.NotFound,
                "That recording device could not be identified. Refresh the device list and try again.");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CaptureKeyRoot}\{guid}\Properties", writable: true);
            if (key is null)
            {
                return new RenameResult(RenameOutcome.NotFound,
                    "Windows has no settings stored for that recording device.");
            }

            key.SetValue(FriendlyNameValue, name, RegistryValueKind.String);
            return new RenameResult(RenameOutcome.Success,
                $"Renamed to \"{name}\". Restart Discord (or whichever app) for the new name to appear.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RenameResult(RenameOutcome.AccessDenied,
                "Windows would not allow the rename. You can do it by hand instead: open the sound "
              + "control panel, Recording tab, right-click the device, Properties, and change its name.");
        }
        catch (Exception ex)
        {
            return new RenameResult(RenameOutcome.Failed, $"The rename failed: {ex.Message}");
        }
    }

    /// <summary>Removes a custom name, putting the endpoint back to what Windows called it.</summary>
    public static RenameResult TryRestoreName(string? endpointId)
    {
        if (!TryGetEndpointGuid(endpointId, out string guid))
        {
            return new RenameResult(RenameOutcome.NotFound, "That recording device could not be identified.");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CaptureKeyRoot}\{guid}\Properties", writable: true);
            if (key is null)
            {
                return new RenameResult(RenameOutcome.NotFound,
                    "Windows has no settings stored for that recording device.");
            }

            if (key.GetValue(FriendlyNameValue) is null)
            {
                return new RenameResult(RenameOutcome.Success, "That device already uses its original name.");
            }

            key.DeleteValue(FriendlyNameValue, throwOnMissingValue: false);
            return new RenameResult(RenameOutcome.Success,
                "Original name restored. Restart the other app for it to notice.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new RenameResult(RenameOutcome.AccessDenied, "Windows would not allow the change.");
        }
        catch (Exception ex)
        {
            return new RenameResult(RenameOutcome.Failed, $"Restoring the name failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pulls the endpoint GUID out of an ID like
    /// <c>{0.0.1.00000000}.{2ad0439e-a3c7-4903-9c8c-8308e865ba2a}</c>, which is the registry
    /// subkey name.
    /// </summary>
    private static bool TryGetEndpointGuid(string? endpointId, out string guid)
    {
        guid = string.Empty;
        if (string.IsNullOrWhiteSpace(endpointId)) return false;

        int brace = endpointId.LastIndexOf('{');
        if (brace < 0) return false;

        string candidate = endpointId[brace..];

        // Insist on a real GUID rather than trusting the shape of the string, so a malformed ID
        // can never be turned into a registry path.
        if (!Guid.TryParseExact(candidate, "B", out _)) return false;

        guid = candidate;
        return true;
    }

    // ------------------------------------------------------------------ shell helpers

    /// <summary>Opens the recording tab of the classic sound control panel, for a manual rename.</summary>
    public static bool OpenRecordingDevicesPanel()
        => TryShellExecute("control", "mmsys.cpl,,1");

    /// <summary>Opens the download page for a cable.</summary>
    public static bool OpenDownloadPage(string? url)
        => TryShellExecute(string.IsNullOrWhiteSpace(url) ? RecommendedDownloadUrl : url, null);

    private static bool TryShellExecute(string fileName, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(arguments)) info.Arguments = arguments;
            Process.Start(info);
            return true;
        }
        catch (Exception)
        {
            // No browser, no shell association, or a policy block. The UI already shows the
            // URL as text, so failing quietly is fine.
            return false;
        }
    }
}
