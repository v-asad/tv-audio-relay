# How tv-audio-relay works

## The problem it routes around

Google TV, like every Android TV, keeps one active Bluetooth A2DP sink. Third-party apps on the TV
cannot capture the TV's audio (HDMI and tuner audio never enters the Android mixer, and the loopback
device needs a signature-level permission) and cannot start an LE Audio broadcast (that API is
system-only). So the fan-out has to happen on a device the TV can talk to: a laptop.

## The three stages

### 1. Receive: `AudioPlaybackConnection`

Windows 10 version 2004 added the A2DP **sink** role, exposed through
`Windows.Media.Audio.AudioPlaybackConnection`. This is the API behind the Store "Bluetooth Audio
Receiver" apps. The flow is:

1. `GetDeviceSelector()` lists paired devices that can send audio to the PC.
2. `TryCreateFromId(id)` creates a connection object for the TV.
3. `StartAsync()` arms the system so the TV may connect.
4. `OpenAsync()` asks the TV to start streaming. The app retries this with backoff and stays armed
   after the TV disconnects, so the TV can reconnect on its own.

Windows renders the received audio on the **default playback device**. The API offers no way to pick
another device or to read the PCM directly, which leads to stage 2.

### 2. Capture: WASAPI loopback

`WasapiLoopbackCapture` (NAudio) opens a loopback stream on the device the TV's audio lands on and
hands the app 32-bit float PCM at the device's mix rate. Two details matter:

- Windows delivers no loopback data while nothing is being rendered. The app therefore plays a silent
  stream to the same device (`SilenceProvider` through `WasapiOut`) so the loopback never idles.
- Whether muting the device also mutes the loopback copy depends on the audio driver. The app measures
  the peak level of what it captures and prints a hint if the TV is linked but the copy is silent.

### 3. Fan out: one `WasapiOut` per headphone with clock trimming

Each output device is driven by its own `WasapiOut` in shared mode. Between the shared capture and each
output sits a `BufferedWaveProvider` plus a `DriftCompensatingProvider` (in `TvAudioRelay.Core`).

Every playback device has its own crystal. A Bluetooth headphone's clock differs from the capture
clock by tens to hundreds of parts per million. Left alone, the buffer in front of a slow device grows
without bound and a fast device runs dry every few minutes. The compensator fixes that:

- It converts sample rate and channel count with linear interpolation, so a 48 kHz capture can feed a
  44.1 kHz headphone directly.
- It measures the buffer depth, smooths it with a one-second exponential filter, and trims the
  consumption rate proportionally, up to plus or minus 0.5 percent. At the target depth the trim is
  zero. A device that is 300 ppm fast ends up with a steady plus 300 ppm trim and a steady buffer.
- On underrun it plays silence until the buffer refills to the target, then resumes.
- If the buffer ever exceeds the target by more than half a second (for instance after Windows
  suspended the device), it discards down to the target in one step instead of playing half a second
  behind forever.

The controller is deliberately slow (time constant around 16 s at default gain) so the trim never
wobbles audibly.

## Latency budget

| Stage | Typical |
|---|---|
| TV encodes and sends SBC to the laptop | 150 to 200 ms |
| Relay buffer (`--buffer-ms`) | 80 ms |
| WASAPI output period (`--latency-ms`) | 40 ms |
| Laptop encodes and sends to headphones | 100 to 200 ms (codec dependent) |

The TV compensates for its own hop at most; nothing on the TV knows about the second hop.

## Why not...

- **A virtual audio driver?** It would let the TV's audio land somewhere inaudible without muting
  anything, but shipping a kernel driver is not a one-evening project and needs signing.
- **Process loopback?** The received audio is rendered by the system, not by this process.
- **Bluetooth LE Audio broadcast from the laptop?** Windows 11 Shared Audio does exactly this on
  Copilot+ PCs with LE Audio radios, and Linux does it with an Intel BE200. Both need LE Audio
  headphones. This tool works with the headphones people already own.
