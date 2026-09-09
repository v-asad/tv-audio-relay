using NAudio.CoreAudioApi;
using NAudio.Wave;
using TvAudioRelay.Core;

namespace TvAudioRelay.Audio;

/// <summary>One playback device fed from the shared capture, with its own buffer and clock trim.</summary>
public sealed class OutputSink : IDisposable
{
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private readonly DriftCompensatingProvider _dsp;
    private volatile bool _stopped;

    public OutputSink(MMDevice device, WaveFormat inputFormat, int bufferMs, int latencyMs)
    {
        DeviceId = device.ID;
        Name = device.FriendlyName;

        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        var mix = _out.OutputWaveFormat;
        var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);

        _buffer = new BufferedWaveProvider(inputFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _dsp = new DriftCompensatingProvider(_buffer, outFormat, TimeSpan.FromMilliseconds(bufferMs));

        _out.Init(_dsp);
        _out.PlaybackStopped += (_, e) =>
        {
            _stopped = true;
            Stopped?.Invoke(this, e.Exception);
        };
    }

    public string DeviceId { get; }
    public string Name { get; }
    public bool IsStopped => _stopped;

    public event Action<OutputSink, Exception?>? Stopped;

    public void Push(byte[] data, int count)
    {
        if (_stopped) return;
        _buffer.AddSamples(data, 0, count);
    }

    public void Start() => _out.Play();

    public string StatusLine()
    {
        string state = _stopped ? "stopped" : _dsp.IsPriming ? "buffering" : "playing";
        return $"{Name}: {state}, buffer {_dsp.BufferedMs,5:F0} ms, trim {_dsp.AdjustPpm,+6:F0} ppm, underruns {_dsp.Underruns}, drops {_dsp.HardDrops}";
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
