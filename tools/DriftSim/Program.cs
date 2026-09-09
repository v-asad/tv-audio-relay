using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TvAudioRelay.Core;

// driftsim: exercise TvAudioRelay.Core exactly the way the Windows app does, without Windows.
//
//   driftsim [--in music.wav] [--ppm 300] [--out-rate 44100] [--buffer-ms 80] [--seconds 30] [--output out.wav]
//
// The input is chopped into 10 ms chunks (like WASAPI loopback delivers them) and fed to the
// resampler while the "playback device" pulls 10 ms of output per step at a clock that differs
// from the input clock by --ppm parts per million. The result is written to --output so you can
// listen for artefacts, and the same statistics the app prints are reported at the end.

string? inPath = null;
double ppm = 300;
int outRate = 44100;
int bufferMs = 80;
double seconds = 30;
string outPath = "driftsim-out.wav";

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--in": inPath = Next(); break;
        case "--ppm": ppm = double.Parse(Next()); break;
        case "--out-rate": outRate = int.Parse(Next()); break;
        case "--buffer-ms": bufferMs = int.Parse(Next()); break;
        case "--seconds": seconds = double.Parse(Next()); break;
        case "--output": outPath = Next(); break;
        case "-h": case "--help":
            Console.WriteLine("driftsim [--in file.wav] [--ppm 300] [--out-rate 44100] [--buffer-ms 80] [--seconds 30] [--output out.wav]");
            return 0;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 2;
    }
}

// ---- source: a WAV file, or a synthetic test signal ---------------------------------------
ISampleProvider source;
WaveFormat inFormat;
if (inPath is not null)
{
    var reader = new WaveFileReader(inPath);
    source = reader.ToSampleProvider();
    inFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    Console.WriteLine($"input : {inPath} ({reader.WaveFormat.SampleRate} Hz, {reader.WaveFormat.Channels} ch, {reader.TotalTime:mm\\:ss})");
}
else
{
    inFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    source = new TestSignal(inFormat.SampleRate);
    Console.WriteLine("input : synthetic (1 kHz tone 5 s, sweep 100 Hz to 8 kHz 10 s, then repeat)");
}

var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, inFormat.Channels);
Console.WriteLine($"output: {outPath} ({outRate} Hz), buffer {bufferMs} ms, input clock {ppm:+0;-0} ppm vs output clock");

var buffer = new BufferedWaveProvider(inFormat) { BufferDuration = TimeSpan.FromSeconds(10), DiscardOnBufferOverflow = true };
var dsp = new DriftCompensatingProvider(buffer, outFormat, TimeSpan.FromMilliseconds(bufferMs));

using var writer = new WaveFileWriter(outPath, outFormat);

int stepOutFrames = outRate / 100;                       // 10 ms of output per step
double inFramesPerStep = inFormat.SampleRate * (1 + ppm / 1e6) * stepOutFrames / outRate;
double carry = 0;
int steps = (int)(seconds * 100);

var inFloats = new float[(int)Math.Ceiling(inFramesPerStep + 1) * inFormat.Channels];
var inBytes = new byte[inFloats.Length * sizeof(float)];
var outBytes = new byte[stepOutFrames * outFormat.BlockAlign];

double minBuf = double.MaxValue, maxBuf = 0;
long silentFrames = 0;
bool ended = false;

for (int s = 0; s < steps && !ended; s++)
{
    carry += inFramesPerStep;
    int frames = (int)carry;
    carry -= frames;

    int want = frames * inFormat.Channels;
    int got = source.Read(inFloats, 0, want);
    if (got < want) { ended = true; }
    if (got > 0)
    {
        Buffer.BlockCopy(inFloats, 0, inBytes, 0, got * sizeof(float));
        buffer.AddSamples(inBytes, 0, got * sizeof(float));
    }

    int n = dsp.Read(outBytes, 0, outBytes.Length);
    writer.Write(outBytes, 0, n);
    if (dsp.IsPriming) silentFrames += n / outFormat.BlockAlign;

    if (s > 200) // ignore the first two seconds while the buffer settles
    {
        minBuf = Math.Min(minBuf, dsp.BufferedMs);
        maxBuf = Math.Max(maxBuf, dsp.BufferedMs);
    }
}

Console.WriteLine();
Console.WriteLine($"steps          : {steps} x 10 ms{(ended ? " (input ended early)" : "")}");
Console.WriteLine($"buffer depth   : min {minBuf:F0} ms, max {maxBuf:F0} ms (target {bufferMs} ms)");
Console.WriteLine($"final trim     : {dsp.AdjustPpm:+0;-0} ppm (should approach {ppm:+0;-0})");
Console.WriteLine($"underruns      : {dsp.Underruns}");
Console.WriteLine($"hard drops     : {dsp.HardDrops}");
Console.WriteLine($"silence played : {1000.0 * silentFrames / outRate:F0} ms (includes the initial fill)");
Console.WriteLine();
Console.WriteLine(OperatingSystem.IsMacOS() ? $"listen: afplay {outPath}" : $"listen: open {outPath} in any player");
return 0;

/// <summary>1 kHz tone for 5 s, then a log sweep 100 Hz to 8 kHz over 10 s, repeating. Stereo, -6 dBFS.</summary>
sealed class TestSignal : ISampleProvider
{
    private readonly int _rate;
    private long _n;
    private double _phase;

    public TestSignal(int rate)
    {
        _rate = rate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i += 2)
        {
            double t = (_n % (15L * _rate)) / (double)_rate;
            double f = t < 5 ? 1000 : 100 * Math.Pow(80, (t - 5) / 10); // 100 Hz -> 8 kHz
            _phase += 2 * Math.PI * f / _rate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            float v = (float)(0.5 * Math.Sin(_phase));
            buffer[offset + i] = v;
            buffer[offset + i + 1] = v;
            _n++;
        }
        return count;
    }
}
