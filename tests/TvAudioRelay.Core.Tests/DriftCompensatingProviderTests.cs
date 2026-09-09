using NAudio.Wave;
using TvAudioRelay.Core;
using Xunit;

public class DriftCompensatingProviderTests
{
    private static BufferedWaveProvider NewInput(int rate, int channels) =>
        new(WaveFormat.CreateIeeeFloatWaveFormat(rate, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
        };

    private static void Feed(BufferedWaveProvider input, float[] interleaved)
    {
        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        input.AddSamples(bytes, 0, bytes.Length);
    }

    private static float[] Read(DriftCompensatingProvider p, int frames)
    {
        int block = p.WaveFormat.BlockAlign;
        var bytes = new byte[frames * block];
        int n = p.Read(bytes, 0, bytes.Length);
        Assert.Equal(bytes.Length, n);
        var floats = new float[n / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, n);
        return floats;
    }

    [Fact]
    public void PrimesWithSilence_ThenPassesSignalThroughUnchanged()
    {
        var input = NewInput(48000, 2);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), TimeSpan.FromMilliseconds(20));

        // Below target: must output silence and report priming.
        Feed(input, new float[2 * 480]); // 10 ms of zeros
        var silent = Read(p, 480);
        Assert.True(p.IsPriming);
        Assert.All(silent, s => Assert.Equal(0f, s));

        // Stream a ramp in 10 ms chunks, the way a live capture arrives, and read 10 ms at a time.
        int total = 48000; // 1 s
        var ramp = new float[total * 2];
        for (int i = 0; i < total; i++) { ramp[2 * i] = i / (float)total; ramp[2 * i + 1] = -ramp[2 * i]; }

        var got = new List<float>();
        for (int chunk = 0; chunk < total / 480; chunk++)
        {
            Feed(input, ramp.AsSpan(chunk * 480 * 2, 480 * 2).ToArray());
            got.AddRange(Read(p, 480));
        }
        Assert.False(p.IsPriming);
        Assert.Equal(0, p.Underruns);

        // First 480 output frames are the queued zeros; after that the ramp must come through.
        var frames = got.Count / 2 - 480;
        double maxError = 0;
        for (int i = 0; i < frames; i++)
        {
            maxError = Math.Max(maxError, Math.Abs(ramp[2 * i] - got[2 * (i + 480)]));
            maxError = Math.Max(maxError, Math.Abs(ramp[2 * i + 1] - got[2 * (i + 480) + 1]));
        }
        Assert.True(maxError < 1e-4, $"max error {maxError}");
        Assert.InRange(p.AdjustPpm, -200, 200);
    }

    [Fact]
    public void ResamplesBetweenRates_WithNominalRatio()
    {
        var input = NewInput(44100, 2);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), TimeSpan.FromMilliseconds(40));

        // Stream a 1 kHz tone: 10 ms in (441 frames) per 10 ms out (480 frames).
        int phase = 0;
        float[] NextChunk(int frames)
        {
            var buf = new float[frames * 2];
            for (int i = 0; i < frames; i++, phase++)
            {
                float v = MathF.Sin(2 * MathF.PI * 1000 * phase / 44100f);
                buf[2 * i] = v; buf[2 * i + 1] = v;
            }
            return buf;
        }

        var outAll = new List<float>();
        long fedFrames = 0;
        for (int k = 0; k < 300; k++) // 3 s
        {
            Feed(input, NextChunk(441));
            fedFrames += 441;
            outAll.AddRange(Read(p, 480));
        }
        long consumedFrames = fedFrames - input.BufferedBytes / input.WaveFormat.BlockAlign;
        double ratio = consumedFrames / (300 * 480.0);
        Assert.InRange(ratio, 44100.0 / 48000 * 0.99, 44100.0 / 48000 * 1.01);

        // Skip the first second (priming + settling), then count positive zero crossings of the left channel.
        int startFrame = 48000;
        int crossings = 0;
        for (int i = startFrame * 2 + 2; i < outAll.Count; i += 2)
            if (outAll[i - 2] <= 0 && outAll[i] > 0) crossings++;
        double seconds = (outAll.Count / 2 - startFrame) / 48000.0;
        Assert.InRange(crossings / seconds, 990, 1010);
        Assert.Equal(0, p.Underruns);
    }

    [Fact]
    public void FastInputClock_BufferStaysBounded_AndTrimConverges()
    {
        const int rate = 48000;
        var input = NewInput(rate, 2);
        var target = TimeSpan.FromMilliseconds(80);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), target);

        // Input clock runs 300 ppm fast relative to the output clock.
        double inRate = rate * (1 + 300e-6);
        double carry = 0;
        double maxBufferedMs = 0;
        int stepFrames = 480; // 10 ms of output per step
        int steps = 100 * 240; // 240 simulated seconds

        for (int s = 0; s < steps; s++)
        {
            carry += inRate * stepFrames / rate;
            int inFrames = (int)carry;
            carry -= inFrames;
            Feed(input, new float[inFrames * 2]);
            Read(p, stepFrames);
            if (s > 2000) maxBufferedMs = Math.Max(maxBufferedMs, p.BufferedMs);
        }

        Assert.Equal(0, p.HardDrops);
        Assert.True(maxBufferedMs < target.TotalMilliseconds + 60, $"buffer grew to {maxBufferedMs} ms");
        Assert.InRange(p.AdjustPpm, 200, 400);
    }

    [Fact]
    public void SlowInputClock_RecoversWithoutRepeatedUnderruns()
    {
        const int rate = 48000;
        var input = NewInput(rate, 2);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), TimeSpan.FromMilliseconds(80));

        double inRate = rate * (1 - 300e-6);
        double carry = 0;
        int stepFrames = 480;
        int steps = 100 * 240;

        for (int s = 0; s < steps; s++)
        {
            carry += inRate * stepFrames / rate;
            int inFrames = (int)carry;
            carry -= inFrames;
            Feed(input, new float[inFrames * 2]);
            Read(p, stepFrames);
        }

        // One initial priming is expected; the trim must prevent a steady stream of dry-outs.
        Assert.True(p.Underruns <= 1, $"underruns: {p.Underruns}");
        Assert.InRange(p.AdjustPpm, -400, -200);
    }

    [Fact]
    public void Underrun_PlaysSilence_ThenResumes()
    {
        var input = NewInput(48000, 2);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), TimeSpan.FromMilliseconds(20));

        Feed(input, Enumerable.Repeat(0.25f, 2 * 960).ToArray()); // 20 ms
        Read(p, 480);
        Assert.False(p.IsPriming);

        Read(p, 480);                 // drains the rest
        var dry = Read(p, 480);       // nothing left: silence + priming
        Assert.True(p.IsPriming);
        Assert.Equal(1, p.Underruns);
        Assert.All(dry, s => Assert.Equal(0f, s));

        Feed(input, Enumerable.Repeat(0.5f, 2 * 960).ToArray());
        var back = Read(p, 480);
        Assert.False(p.IsPriming);
        Assert.Contains(back, s => s != 0f);
    }

    [Fact]
    public void LargeBacklog_IsDroppedToTarget()
    {
        var input = NewInput(48000, 2);
        var target = TimeSpan.FromMilliseconds(80);
        var p = new DriftCompensatingProvider(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), target);

        Feed(input, new float[2 * 48000 * 2]); // 2 s backlog
        Read(p, 480);
        Assert.Equal(1, p.HardDrops);
        Assert.InRange(p.BufferedMs, 0, target.TotalMilliseconds + 20);
    }
}
