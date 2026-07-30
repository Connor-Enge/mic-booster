# Mic Booster

Real-time microphone loudness control for Windows. Built for the case where someone's mic is
just too quiet — or too loud, or wildly inconsistent — and the Windows slider alone isn't enough.

One self-contained `.exe`. No installer, no runtime to install, no admin rights.

---

## The one thing to understand first

There are **two separate layers of control** in this app, and they reach different places.
This trips everyone up, so it's worth 30 seconds:

| | What it does | What hears it | Needs anything extra? |
|---|---|---|---|
| **1. Microphone panel** (left) | Sets the actual Windows capture level and hardware boost for the device | **Every app on the system, immediately** | No |
| **2. Processor** (centre/right) | Boost, gate, compressor, auto-level, limiter — real audio processing | Whatever you route it to | For other apps: a free virtual cable, set up in one click |

If your friend just wants "make my mic louder in Discord" and nothing more, **layer 1 alone may
be enough** — move the Windows level slider and turn on hardware boost if his mic offers it.
Done, no setup.

Layer 2 is where the real quality lives: it can add 30+ dB of clean gain, hold a steady level
regardless of how far he sits from the mic, gate out keyboard and fan noise, and hard-limit so
boosting can never clip. To make *other* apps hear it, see
[Appearing as its own microphone](#appearing-as-its-own-microphone-in-discord).

---

## Quick start

1. Run `MicBooster.exe`.
2. Pick his microphone under **Microphone**. The input meter should move when he talks — that
   confirms the right device before anything else is configured.
3. If the mic is too quiet, do the cheap fix first: drag **Windows level** up, and set
   **Hardware boost** if it's available. This affects every app right away.
4. Want more, or want it to sound better? Choose a preset (start with **Quiet Mic Rescue**),
   set **Output** to his headphones, and press **Start**. He'll hear himself processed.
   Adjust **Boost** until the output meter sits in the green/amber, not pinned to red.
5. To make Discord and friends hear it, use the **Virtual microphone** panel — below.

---

## Appearing as its own microphone in Discord

**The honest constraint:** Windows only lets a *driver* create a microphone. A normal
program — this one included — cannot publish a recording device, and a driver that could has to be
signed by Microsoft before 64-bit Windows will load it. So Mic Booster doesn't pretend to be a
driver. It drives a free, already-signed **virtual audio cable** instead, which is exactly how
every streaming and voice-processing setup does this.

The app handles the whole thing for you in the **Virtual microphone** panel:

1. **Install the cable, once.** If none is found, press **Get VB-CABLE (free)**. Install it and
   restart the PC. ([VB-CABLE](https://vb-audio.com/Cable/) is free; VoiceMeeter, Virtual Audio
   Cable and even Steam's streaming devices are detected too, if he already has one.)
2. **Press "Set up".** One click: routing switches to the cable, the output device is selected,
   and processing starts.
3. **Optionally rename it.** A cable ships as `CABLE Output (VB-Audio Virtual Cable)`, which means
   nothing in a device list. Type a name and press **Rename** and it shows up as
   **"Mic Booster"** in Discord instead. Reversible with **Restore**.
4. **In Discord / Zoom / OBS / the game**, pick that device as the microphone.

The signal path:

```
his mic --> Mic Booster (boost, gate, compress, auto-level, limit) --> CABLE Input
                                                                          |
                                     Discord/Zoom/OBS  <--  CABLE Output ("Mic Booster")
```

Mic Booster has to be **running with Start pressed** for audio to flow.

**Also turn off Discord's own "Automatic Gain Control"** — it fights the compressor and undoes
the work. This is the single most common reason people think it isn't working.

**Two caveats, stated plainly:**

- **He can send to the cable or hear himself, not both.** One output device can't feed the cable
  and his headphones simultaneously. Monitoring *and* routing at once needs a mixer such as
  VoiceMeeter. That's a Windows audio limitation, not something this app can work around.
- **Renaming is machine-wide** (it's the same setting the Windows sound panel changes), and apps
  only read device names at startup, so Discord needs a restart to show the new name.

**Without any cable**, set **Output** to his headphones. He'll hear the processing, which is
genuinely useful for dialling settings in and proving the chain works — but other apps still get
the raw mic. Layer 1 is what affects them.

---

## The controls

### Microphone (works instantly, affects everything)

- **Input device** — any active capture device. The list refreshes automatically when hardware is
  plugged or unplugged.
- **Channel** — how a multi-channel input is folded to mono. `Mix all` averages; `Left`/`Right`
  pick one. **If he uses an audio interface with the mic on input 1 only, choose `Left`** —
  mixing would average in a silent channel and cost 6 dB.
- **Windows level** — the real OS capture level, the same value as the Windows Sound control panel.
  Changes apply immediately to every application.
- **Mute (Windows)** — mutes at the device level, so every app goes silent.
- **Hardware boost** — analogue gain inside the device, ahead of its converter. When a device
  offers it (typically onboard/Realtek inputs, in 0/+10/+20/+30 dB steps) this is the *best*
  boost available, because it raises the signal before digitisation instead of amplifying an
  already-quantised one. Most USB microphones don't expose it, and the app will say so plainly.

### Loudness (the processor)

- **Boost** — the main input gain, −24 to +48 dB. This is the headline control.
- **Auto Level** — a slow gain rider that holds long-term loudness at **Target**, within
  **Max boost** / **Max cut**. This is what makes him sound the same whether he's leaning into the
  mic or sitting back. It deliberately freezes while the gate is shut so it won't wind up on room
  noise during silence.
- **Output trim / volume** — level after all processing.

### Processor modules

- **High-pass** — removes rumble, desk thumps and plosives below the cutoff. 80–100 Hz suits most
  voices and costs nothing intelligibility-wise.
- **Gate** — silences the mic between phrases. `Threshold` is the open level; `Range` is how much
  it ducks when closed (20–30 dB ducks naturally, 80 dB is effectively a hard mute); `Hysteresis`
  is how far below threshold it must fall before closing, which is what stops the rapid
  open/close chatter a single-threshold gate produces; `Hold` keeps it open so word endings and
  breaths aren't chopped off.
- **Compressor** — narrows the gap between his quiet and loud speech. `Threshold` where it starts,
  `Ratio` how hard, `Knee` how gradually it engages, `Attack`/`Release` how fast. **Auto make-up**
  adds back the level compression removed, so raising the ratio doesn't make things quieter.
- **Limiter** — the safety net, and the reason large boosts stay clean. Lookahead lets it catch a
  transient *before* it arrives, so it never distorts, and output can never exceed `Ceiling`.
  Leave this on.

Every meter is live: input, output, gate attenuation, compressor and limiter gain reduction, and
the gain Auto Level is applying. Watch the gain-reduction meters — if the compressor is pinned,
back off Boost or raise its Threshold.

### Presets

| Preset | For |
|---|---|
| **Quiet Mic Rescue** | A mic that's simply far too quiet. Heavy gain, aggressive auto-level. |
| **Voice Chat** | Discord / Zoom / in-game. Consistent, tight gate to keep keys and fans out. |
| **Streaming** | Denser and louder, for platforms that re-encode audio. |
| **Podcast** | Gentler and slower, gate off. For recording rather than live. |
| **Maximum Loudness** | As loud as it goes, still limiter-protected. |
| **Natural** | Boost and safety only — nothing that changes how his voice sounds. |
| **Bypass** | Everything off, for an honest A/B against the raw mic. |

Custom presets: set things how you like, type a name, hit **Save**.

### Latency and buffer size

**Buffer** trades latency against stability. 30 ms is a good default. Drop to 10–20 ms if he
monitors himself and the delay bothers him; raise it to 50 ms+ if audio crackles. The
**Glitches** counter is the honest indicator — if it climbs while running, the buffer is too
small for his machine. It's reported rather than hidden, because silently dropping audio is
worse than saying so.

---

## Troubleshooting

**The input meter doesn't move.** Wrong device, or Windows is blocking access. Check
Settings → Privacy & security → Microphone → "Let desktop apps access your microphone".
Windows level at 0 also does this.

**It says the device has no hardware boost.** Normal — most USB mics don't. Use **Boost** in the
processor instead; it achieves the same loudness in software.

**Discord still sounds quiet.** Either Discord is still on the raw mic — check the **Virtual
microphone** panel and that Discord's input device is the cable — or Discord's **Automatic Gain
Control** is on and undoing the processing. Turn it off.

**The renamed device still shows its old name.** Restart the app that's listing it; device names
are read at startup.

**Crackling or dropouts.** Raise the buffer. Check the **Glitches** counter and **DSP load**.

**"Device was unplugged or changed."** Expected when hardware changes. Reselect the device and
press Start.

**Distorted / crunchy.** Boost is too high going into the limiter. Watch the limiter's gain
reduction — more than ~10 dB constantly means back off Boost.

**Robotic or pumping.** Auto Level too fast or the compressor too aggressive. Raise **Speed**,
lower **Ratio**.

**Words get cut off.** Gate threshold too high or **Hold** too short. Lower the threshold,
raise Hold, or disable the gate.

---

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
.\build.ps1                          # single self-contained exe -> dist\MicBooster.exe
.\build.ps1 -Run                     # build, then launch
.\build.ps1 -Configuration Debug     # debug configuration
.\build.ps1 -FrameworkDependent      # much smaller, needs .NET 9 Desktop Runtime installed
```

The result is one ~66 MB `.exe` with the runtime bundled in.

Or double-click `Build MicBooster.bat`.

The self-contained build is larger (it bundles the runtime) but runs on any Windows 10/11 x64
machine with nothing installed — which is the point, if you're sending it to a friend.

### Tests

```powershell
dotnet run --project tests\MicBooster.DspTests -c Release
```

32 offline checks over the real production classes — no audio hardware needed. They assert the
things a compiler can't: that the gain stage applies the gain it claims, that the limiter cannot
be pushed past its ceiling even by a step transient, that the gate doesn't chatter in its
hysteresis band, that the level rider converges and stays frozen during silence, that every bit
depth decodes to the right value, and that no stage can be made to emit a NaN.

---

## How it works

```
WASAPI capture (device's own format, any rate/depth/channels)
   |  MonoDownmixer      float32/64, PCM 8/16/24/32, Extensible -> mono float
   |  High-pass          2nd-order Butterworth
   |  Input gain         smoothed, click-free
   |  Noise gate         hysteretic state machine w/ hold
   |  Compressor         soft-knee, feed-forward, auto make-up
   |  Auto level         slow gain rider, frozen while gated
   |  Output gain
   |  Limiter            true lookahead brick wall
   |  resample           only when input and output rates differ
   |  channel fan-out    mono -> however many the output device wants
WASAPI render (shared mode, exact mix format)
```

Everything after capture runs in the audio callback with no allocation, no locks and no
exceptions. Parameters cross the UI/audio boundary as `volatile` 32-bit scalars; gains ramp rather
than jump so slider moves don't click; recursive filters flush denormals and sanitise NaN/Inf.

**Device agnostic by construction** — nothing about any particular machine is assumed. Sample rate,
bit depth, channel count and format are all read from the device at runtime; only `Active` devices
are offered; every COM property read is individually guarded, because real devices throw
`AUDCLNT_E_DEVICE_INVALIDATED` on ordinary reads and at least one reports a nonsensical decibel
range (which is why the user-facing level is driven by the linear scalar, never by dB).

Settings live in `%AppData%\MicBooster\settings.json`, written atomically. A corrupt file is moved
aside rather than blocking startup. Devices are remembered by ID *and* name, so his setup survives
Windows renumbering a USB port.

### Three things that turned out to matter

- **Hardware boost hides next to a trap.** The capture endpoint's own topology exposes no
  subunits, so finding the driver's boost control means crossing the connector into the adapter
  graph. Over there, the microphone path and the headphone monitor path share their first few
  parts and then fork — and *both* forks contain a `Volume` subunit. Grabbing "the first volume
  control" silently changes the user's headphone level while appearing to do nothing to the mic.
  So a part is only accepted when its name contains "boost", and any branch reaching a
  render-side part (`dac`, `speaker`, `headphone`, `output`) is abandoned.
- **A level rider needs two detectors, not one.** Putting a −60 dB floor on the loudness
  integrator does *not* stop it winding up during silence: when a phrase ends, the slow
  integrator follows the decaying tail down through −20, −30, −40 dB, staying above the floor the
  whole way while the rider boosts to chase it. A fast peak envelope decides *presence* and gates
  the integrator; the integrated loudness only decides the *ride*.
- **One bad sample used to be fatal.** Squaring a huge sample overflows to `Infinity`, and the
  usual one-pole update then computes `Inf - Inf`, which is `NaN`. `NaN` survives that update
  forever, so a single glitch silenced the app for the rest of the session. Envelope state is now
  stabilised rather than merely denormal-flushed, and the chain bounds its input.

### Layout

```
src/MicBooster/
  Audio/
    AudioEngine.cs                 capture -> DSP -> render, lifecycle, fault handling
    DeviceManager.cs               enumeration + hot-plug notifications
    EndpointVolumeController.cs    the Windows capture level (layer 1)
    HardwareBoostController.cs     hardware mic boost via device topology
    VirtualMicService.cs           virtual-cable detection, routing, endpoint renaming
    MonoDownmixer.cs               any input format -> mono float
    MonoToMultiChannelProvider.cs  mono -> N output channels
    Dsp/                           the processing stages + chain
  Models/                          settings, presets, engine types
  Services/                        settings persistence, global hotkeys
  ViewModels/                      MainViewModel
  Controls/                        level and gain-reduction meters
  Themes/Dark.xaml                 the visual identity
tests/MicBooster.DspTests/         offline DSP verification
```

## Licence

Do whatever you like with it.
