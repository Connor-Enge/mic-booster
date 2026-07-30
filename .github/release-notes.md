## Download

Grab **`MicBooster.exe`** below and run it. That's the whole install — one file, nothing to set
up, no .NET runtime needed, no admin rights.

Windows will probably show **"Windows protected your PC"** the first time, because the file isn't
code-signed (a signing certificate costs money). Click **More info** → **Run anyway**.

You can verify the download against `MicBooster.exe.sha256` if you like:

```powershell
Get-FileHash MicBooster.exe -Algorithm SHA256
```

## Make your mic louder in 30 seconds

1. Run it and pick your microphone under **Microphone** — the meter should move when you talk.
2. Drag **Windows level** up.
3. If **Hardware boost** isn't greyed out, set it to +10 or +20 dB.

That affects every app on the PC immediately. You don't even need to press Start.

## Make it sound good

Set **Output device** to your headphones, choose the **Quiet Mic Rescue** preset, press **Start**,
and drag **Boost** until the output meter sits in the green and amber. You'll hear yourself.

## Make Discord / Zoom / OBS hear it

Windows only lets a *driver* create a microphone, so no ordinary program can invent one. Instead
Mic Booster sends the processed audio down a free virtual cable and the other app listens to the
far end. The **Virtual microphone** panel does the setup for you:

1. If no cable is found, press **Get VB-CABLE (free)**, install it, and restart the PC.
2. Press **Set up** — one click.
3. Optionally press **Rename** so it appears in Discord as **"Mic Booster"** instead of
   `CABLE Output (VB-Audio Virtual Cable)`. Restart Discord for the name to show.
4. In Discord, pick that device as your input, and **turn off Automatic Gain Control** — it
   fights the processing and undoes it. This is the most common reason people think it isn't
   working.

Leave Mic Booster running with **Start** pressed.

## What's in the box

Real-time boost, high-pass, hysteretic noise gate, soft-knee compressor with auto make-up, a slow
auto-level rider, and a true lookahead brick-wall limiter — so large boosts stay clean instead of
clipping. Live metering throughout, seven presets, global mute hotkey, tray icon.

Fully device agnostic: any sample rate, bit depth and channel count, with hot-plug handling.

`READ-ME-FIRST.txt` below is the short version. The full manual is in
[README.md](https://github.com/Connor-Enge/mic-booster/blob/main/README.md).
