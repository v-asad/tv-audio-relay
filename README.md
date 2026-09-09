# tv-audio-relay

Turns a Windows laptop into the Bluetooth audio receiver for a TV, then relays that audio to
**several Bluetooth headphones at once**. No Store apps, no Voicemeeter, nothing attached to the TV,
no root on the TV.

```
TV ──Bluetooth──▶ Windows laptop ──Bluetooth──▶ Headphones A
                        │
                        └────────Bluetooth──▶ Headphones B (and C, ...)
```

Built for a TCL 55C6K (Google TV), which like every Google TV can send Bluetooth audio to exactly
one device. That one device becomes the laptop. Everything after that is under your control.

## What you need

| Item | Requirement |
|---|---|
| Laptop OS | Windows 11, or Windows 10 version 2004 (build 19041) or newer |
| Laptop radio | Built-in Bluetooth. Bluetooth 5.x recommended. |
| TV | Any TV that can output audio to a Bluetooth device |
| Headphones | Any Bluetooth headphones. Low-latency ones (aptX Low Latency, aptX Adaptive) sync better. |
| To build from source | .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` |

No admin rights are needed to run the app.

## Setup, step by step

### 1. Check Windows

Press `Win+R`, run `winver`. You need Windows 11, or Windows 10 version 2004 or later. Bluetooth must be on
(Settings > Bluetooth & devices).

### 2. Pair your headphones with the laptop

For each pair of headphones:

1. Put the headphones in pairing mode.
2. Settings > Bluetooth & devices > **Add device** > **Bluetooth** > pick the headphones.
3. Confirm they show up under Settings > System > Sound as `Headphones (<name> Stereo)`.

Windows also creates a `Headset (<name> Hands-Free)` entry for each. Ignore that one. It is mono and
low quality. Always use the **Stereo** entry.

### 3. Pair the TV with the laptop

1. On the laptop: Settings > Bluetooth & devices > **Add device** > **Bluetooth**. Leave this dialog open.
   While it is open, the laptop is discoverable.
2. On the TCL: Settings > **Remotes & Accessories** > **Pair accessory**. The laptop appears by its
   computer name. Select it.
3. Confirm the pairing code on both screens.

If the laptop does not appear on the TV, do it the other way round: leave the TV on the Pair accessory
screen (it is discoverable there) and pick the TV in the laptop's Add device dialog.

### 4. Decide where the TV's audio lands on the laptop

Windows plays received Bluetooth audio on the **default playback device**. The relay copies audio from
that device to your headphones. Pick one layout:

**Layout A: everyone equal (recommended).**
Default device = the laptop's built-in speakers. Turn their volume down or mute them. Every listener
gets a copy with the same delay.

**Layout B: one listener first.**
Default device = Headphones A. The relay copies from Headphones A to Headphones B, C... Listener A
hears it a fraction earlier than the others and nothing plays on the speakers.

Set the default device in Settings > System > Sound > Output, or click the speaker icon in the taskbar.
On some laptops muting the speakers also silences the copy the relay takes. The app tells you when
that happens. Lower the volume instead of muting, or use Layout B.

### 5. Get the app

**Option 1, download.** Grab `tv-audio-relay-win-x64.zip` from the
[Releases](../../releases) page, unzip it anywhere.

**Option 2, build it yourself.**

```powershell
winget install Microsoft.DotNet.SDK.10
git clone <this repo>
cd tv-audio-relay
.\scripts\publish.ps1
```

You get `dist\win-x64\tv-audio-relay.exe`, a single file with no installer.

### 6. First run

Open a terminal (Windows Terminal or PowerShell) in the folder with the exe.

```powershell
.\tv-audio-relay.exe devices
```

You should see the TV under "Bluetooth audio sources" and your headphones under "Playback devices",
with `*` marking the current default. Now start the relay:

```powershell
.\tv-audio-relay.exe run --tv "TCL" --out "Headphones (WH-1000XM5 Stereo)" --out "Headphones (Galaxy Buds Stereo)"
```

Names are matched case-insensitively and partially, so `--out "XM5"` works if only one device contains
it. If you would rather not type this every time:

```powershell
.\tv-audio-relay.exe init      # writes relay.json
notepad relay.json             # put your device names in
.\tv-audio-relay.exe run       # picks up relay.json automatically
```

### 7. Make the TV send its audio

When the app prints `Receiver armed`, the laptop is ready to accept the TV. Usually the TV connects on
its own within a few seconds and the app prints `TV link OPEN`. If it does not:

- On the TCL: Settings > Remotes & Accessories > select the laptop > **Connect**.
- Or set it as output: Settings > Display & Sound > Audio output > choose the laptop.

The TV's own speakers go silent while it streams to Bluetooth. That is how Google TV works and is
not something the relay can change.

### 8. Day to day

1. Open the laptop, run the same `run` command (or double-click a shortcut to it).
2. Turn the TV on. It reconnects to the last Bluetooth audio device on its own. If not, reconnect from
   Remotes & Accessories.
3. Turn the headphones on. Windows reconnects them and the relay picks them up automatically, even if
   they were off when you started.

Keep the laptop on mains power and set Settings > System > Power to not sleep while plugged in.

## Reading the status line

```
20:41:07  source peak  0.63  tv linked
           Headphones (WH-1000XM5 Stereo): playing, buffer    82 ms, trim   +140 ppm, underruns 0, drops 0
           Headphones (Galaxy Buds Stereo): playing, buffer    79 ms, trim    -35 ppm, underruns 0, drops 0
```

- **source peak**: loudest sample in the last interval. `0.00` for a long time with `tv linked` means the
  source device is muted or the TV is paused.
- **buffer**: audio waiting for that headphone. Should hover around `--buffer-ms`.
- **trim**: how much the relay is speeding up or slowing down that headphone's stream to keep the buffer
  steady. A few hundred ppm is normal and inaudible.
- **underruns**: times the buffer ran dry and a short silence was inserted. If this climbs, raise
  `--buffer-ms`.
- **drops**: times a large backlog was discarded (for example after Windows paused the device).

## Latency and lip sync

Two Bluetooth hops add up. Roughly: TV to laptop 150 to 200 ms, relay buffer 80 ms plus a 40 ms WASAPI
period, laptop to headphones 100 to 200 ms depending on codec. Expect 350 to 500 ms behind the picture.
The TV's own Audio Delay slider cannot help, it only pushes audio later.

To shave it down:

- `--buffer-ms 40 --latency-ms 20` if underruns stay at 0.
- Layout B gives one listener the shortest path.
- Headphones that negotiate aptX Low Latency or aptX Adaptive with Windows cut the last hop a lot.

## Limits you should know

- **One Bluetooth radio.** Windows uses a single Bluetooth adapter, and adding a USB one disables the
  built-in one. One incoming stream plus two outgoing streams is the practical ceiling. A third headphone
  may stutter. Keep the laptop close to the TV and headphones.
- **The TV speakers mute** while the TV streams to Bluetooth. Everyone in the room needs headphones.
- **Not a TV app.** Nothing is installed on the TV. Google TV does not let third-party apps capture
  the TV's audio or drive more than one Bluetooth device, which is why this lives on the laptop.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `No paired Bluetooth device can send audio to this PC` | The TV is not paired, or the pairing lost its audio role. Remove the laptop on the TV and the TV on the laptop, then pair again (step 3). |
| `Open attempt: RequestTimedOut` repeating | The TV is not trying to connect. Connect from the TV (step 7). |
| `Open attempt: DeniedBySystem` | Another program holds the audio receiver (for example a Store receiver app). Close it. |
| `TV link OPEN` but headphones are silent | The source device is muted or is not the default device. Unmute and lower the volume, or run `devices` and check the `*`. |
| Crackling or gaps on one headphone | Raise `--buffer-ms` (try 150). Move closer. Check you picked the Stereo entry, not Headset. |
| Everything stutters when the second headphone joins | Radio bandwidth. Disconnect other Bluetooth devices (mouse, keyboard) and stay near the laptop. |
| Headphones switched off, then on, no audio | The relay restarts them within a few seconds. Watch for `is back; playback restarted`. |

## Development

```
src/TvAudioRelay.Core/     Platform-neutral: drift-compensating resampler, channel mapping (net10.0)
src/TvAudioRelay/          The Windows app: Bluetooth receiver, WASAPI capture, outputs, CLI
tests/                     xUnit tests for Core, run anywhere
```

```powershell
dotnet test tests/TvAudioRelay.Core.Tests
dotnet build src/TvAudioRelay -c Release
```

GitHub Actions builds the app on Windows on every push and attaches a zip; tags matching `v*` publish
a release. See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) for the design.

## License

MIT.
