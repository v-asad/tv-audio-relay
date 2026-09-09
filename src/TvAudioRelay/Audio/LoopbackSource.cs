using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TvAudioRelay.Audio;

/// <summary>
/// Copies whatever Windows is rendering to one playback device. Optionally plays silence to that
/// device so the loopback stream never goes idle (Windows stops delivering loopback data when
/// nothing is being rendered, which would stall the outputs).
/// </summary>
public sealed class LoopbackSource : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly WasapiOut? _keepAlive;
    private float _peak;
    private long _lastLoudTicks = DateTime.UtcNow.Ticks;

    public LoopbackSource(MMDevice device, bool keepAlive)
    {
        Device = device;
        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);

        if (keepAlive)
        {
            _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            _keepAlive.Init(new SilenceProvider(_keepAlive.OutputWaveFormat));
        }
    }

    public MMDevice Device { get; }

    /// <summary>Format of the captured audio. Always 32-bit float at the device's mix rate.</summary>
    public WaveFormat Format => _capture.WaveFormat;

    /// <summary>Raised on the capture thread with (buffer, byteCount).</summary>
    public event Action<byte[], int>? DataAvailable;

    public event Action<Exception?>? Stopped;

    /// <summary>Loudest sample seen since the last call, then reset.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);

    public TimeSpan SinceLastSound => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastLoudTicks));

    public void Start()
    {
        _keepAlive?.Play();
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Peak meter on the float samples, used only to warn about a muted or silent source.
        var floats = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
        float max = 0f;
        for (int i = 0; i < floats.Length; i++)
        {
            float a = Math.Abs(floats[i]);
            if (a > max) max = a;
        }
        if (max > _peak) _peak = max;
        if (max > 0.001f) Interlocked.Exchange(ref _lastLoudTicks, DateTime.UtcNow.Ticks);

        DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        try { _keepAlive?.Stop(); } catch { }
        _keepAlive?.Dispose();
    }
}
