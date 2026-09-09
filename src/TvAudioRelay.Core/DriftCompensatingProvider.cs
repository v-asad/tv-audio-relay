using System.Runtime.InteropServices;
using NAudio.Wave;

namespace TvAudioRelay.Core;

/// <summary>
/// Pulls 32-bit float PCM out of a <see cref="BufferedWaveProvider"/> that is being filled by
/// a capture clock, and produces 32-bit float PCM for a playback clock that runs at a slightly
/// different speed. It does three jobs:
/// <list type="bullet">
/// <item>Sample-rate and channel conversion (e.g. 48 kHz stereo in, 44.1 kHz stereo out).</item>
/// <item>Slow, inaudible rate trimming (a few hundred ppm at most) that keeps the buffer near
///       a target depth so the two clocks never drift apart.</item>
/// <item>Recovery: on underrun it plays silence until the buffer refills to target; on a large
///       backlog it discards down to target in one step.</item>
/// </list>
/// Linear interpolation is used for the fractional resampling. At ratios within a few tenths of
/// a percent of a whole rate conversion this is transparent for TV audio.
/// </summary>
public sealed class DriftCompensatingProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _input;
    private readonly WaveFormat _inFormat;
    private readonly WaveFormat _outFormat;
    private readonly int _inCh;
    private readonly int _outCh;
    private readonly double _nominalRatio;      // input frames consumed per output frame
    private readonly int _targetBytes;
    private readonly int _hardDropBytes;
    private readonly double _maxAdjust;
    private readonly double _gain;
    private readonly double _smoothingSeconds;

    private double _ratio;
    private double _frac;                       // position between _prev and _next, [0,1)
    private readonly float[] _prev;
    private readonly float[] _next;
    private readonly float[] _outFrame;
    private readonly float[] _mixed;
    private bool _started;
    private bool _priming = true;

    private float[] _work = new float[4096];
    private byte[] _workBytes = new byte[4096 * sizeof(float)];
    private int _workStart;                     // in frames
    private int _workCount;                     // in frames

    private double _smoothedErrorFrames;
    private bool _smoothedInit;

    private long _underruns;
    private long _hardDrops;
    private double _adjustPpm;
    private double _bufferedMs;

    /// <param name="input">Float32 buffer filled by the capture side. Its <c>ReadFully</c> is forced to false.</param>
    /// <param name="outputFormat">Float32 format the playback device wants.</param>
    /// <param name="target">Buffer depth to hold. Latency added by this stage is roughly this value.</param>
    /// <param name="maxAdjust">Maximum relative speed change, default 0.5 %.</param>
    /// <param name="gain">Relative speed change applied when the buffer error equals the target depth.</param>
    /// <param name="smoothingSeconds">Time constant of the buffer-level filter that feeds the controller.</param>
    public DriftCompensatingProvider(
        BufferedWaveProvider input,
        WaveFormat outputFormat,
        TimeSpan target,
        double maxAdjust = 0.005,
        double gain = 0.005,
        double smoothingSeconds = 1.0)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _inFormat = input.WaveFormat;
        _outFormat = outputFormat ?? throw new ArgumentNullException(nameof(outputFormat));

        if (_inFormat.Encoding != WaveFormatEncoding.IeeeFloat || _inFormat.BitsPerSample != 32)
            throw new ArgumentException("Input must be 32-bit IEEE float PCM.", nameof(input));
        if (_outFormat.Encoding != WaveFormatEncoding.IeeeFloat || _outFormat.BitsPerSample != 32)
            throw new ArgumentException("Output must be 32-bit IEEE float PCM.", nameof(outputFormat));
        if (target <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(target));

        _input.ReadFully = false;

        _inCh = _inFormat.Channels;
        _outCh = _outFormat.Channels;
        _nominalRatio = (double)_inFormat.SampleRate / _outFormat.SampleRate;
        _ratio = _nominalRatio;

        int targetFrames = (int)Math.Round(target.TotalSeconds * _inFormat.SampleRate);
        _targetBytes = Math.Max(1, targetFrames) * _inFormat.BlockAlign;
        _hardDropBytes = _targetBytes + (int)(0.5 * _inFormat.SampleRate) * _inFormat.BlockAlign;
        _maxAdjust = maxAdjust;
        _gain = gain;
        _smoothingSeconds = smoothingSeconds;

        _prev = new float[_inCh];
        _next = new float[_inCh];
        _outFrame = new float[_outCh];
        _mixed = new float[_inCh];
    }

    public WaveFormat WaveFormat => _outFormat;

    /// <summary>Times playback ran dry and had to re-buffer.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Times a large backlog was discarded in one step.</summary>
    public long HardDrops => Interlocked.Read(ref _hardDrops);

    /// <summary>Current speed trim in parts per million. Positive means consuming input faster.</summary>
    public double AdjustPpm => _adjustPpm;

    /// <summary>Input currently waiting in the buffer, in milliseconds.</summary>
    public double BufferedMs => _bufferedMs;

    /// <summary>True while playing silence and waiting for the buffer to reach target.</summary>
    public bool IsPriming => _priming;

    public TimeSpan Target => TimeSpan.FromSeconds((double)_targetBytes / _inFormat.AverageBytesPerSecond);

    public int Read(byte[] buffer, int offset, int count)
    {
        int outBlock = _outFormat.BlockAlign;
        int outFrames = count / outBlock;
        if (outFrames == 0) return 0;
        count = outFrames * outBlock;

        int available = _input.BufferedBytes;
        _bufferedMs = 1000.0 * available / _inFormat.AverageBytesPerSecond;

        if (_priming)
        {
            if (available < _targetBytes)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            _priming = false;
            _smoothedInit = false;
        }

        if (available > _hardDropBytes)
        {
            Discard(available - _targetBytes);
            available = _input.BufferedBytes;
            _bufferedMs = 1000.0 * available / _inFormat.AverageBytesPerSecond;
            Interlocked.Increment(ref _hardDrops);
            _smoothedInit = false;
        }

        UpdateRatio(available, outFrames);

        int pulls = (int)Math.Floor(_frac + outFrames * _ratio) + (_started ? 0 : 2) + 1;
        if (!EnsureWork(pulls))
        {
            _priming = true;
            Interlocked.Increment(ref _underruns);
            Array.Clear(buffer, offset, count);
            return count;
        }

        var dest = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        int di = 0;

        if (!_started)
        {
            Pull(_prev);
            Pull(_next);
            _frac = 0;
            _started = true;
        }

        for (int f = 0; f < outFrames; f++)
        {
            float t = (float)_frac;
            for (int c = 0; c < _inCh; c++)
                _mixed[c] = _prev[c] + (_next[c] - _prev[c]) * t;

            ChannelMapper.Map(_mixed, _inCh, _outFrame, _outCh);
            for (int c = 0; c < _outCh; c++) dest[di++] = _outFrame[c];

            _frac += _ratio;
            while (_frac >= 1.0)
            {
                Array.Copy(_next, _prev, _inCh);
                Pull(_next);
                _frac -= 1.0;
            }
        }

        _bufferedMs = 1000.0 * _input.BufferedBytes / _inFormat.AverageBytesPerSecond;
        return count;
    }

    private void UpdateRatio(int availableBytes, int outFramesThisCall)
    {
        int targetFrames = _targetBytes / _inFormat.BlockAlign;
        double errorFrames = (availableBytes - _targetBytes) / (double)_inFormat.BlockAlign
                             + _workCount; // frames already pulled but not yet played

        double dt = (double)outFramesThisCall / _outFormat.SampleRate;
        if (!_smoothedInit)
        {
            _smoothedErrorFrames = errorFrames;
            _smoothedInit = true;
        }
        else
        {
            double alpha = 1.0 - Math.Exp(-dt / _smoothingSeconds);
            _smoothedErrorFrames += alpha * (errorFrames - _smoothedErrorFrames);
        }

        double errorNorm = _smoothedErrorFrames / targetFrames;
        double adjust = Math.Clamp(errorNorm * _gain, -_maxAdjust, _maxAdjust);
        _ratio = _nominalRatio * (1.0 + adjust);
        _adjustPpm = adjust * 1e6;
    }

    private bool EnsureWork(int frames)
    {
        if (_workCount >= frames) return true;

        // Compact leftovers to the front.
        if (_workStart > 0 && _workCount > 0)
            Array.Copy(_work, _workStart * _inCh, _work, 0, _workCount * _inCh);
        _workStart = 0;

        int need = frames - _workCount;
        int needBytes = need * _inFormat.BlockAlign;
        if (_input.BufferedBytes < needBytes) return false;

        int requiredFloats = (_workCount + need) * _inCh;
        if (_work.Length < requiredFloats)
        {
            Array.Resize(ref _work, Math.Max(requiredFloats, _work.Length * 2));
        }
        if (_workBytes.Length < needBytes)
        {
            Array.Resize(ref _workBytes, Math.Max(needBytes, _workBytes.Length * 2));
        }

        int got = _input.Read(_workBytes, 0, needBytes);
        int gotFrames = got / _inFormat.BlockAlign;
        if (gotFrames < need) return false; // producer raced us; treat as underrun

        Buffer.BlockCopy(_workBytes, 0, _work, _workCount * _inCh * sizeof(float), gotFrames * _inFormat.BlockAlign);
        _workCount += gotFrames;
        return true;
    }

    private void Pull(float[] frame)
    {
        Array.Copy(_work, _workStart * _inCh, frame, 0, _inCh);
        _workStart++;
        _workCount--;
    }

    private void Discard(int bytes)
    {
        bytes -= bytes % _inFormat.BlockAlign;
        var scratch = new byte[Math.Min(bytes, 1 << 16)];
        while (bytes > 0)
        {
            int n = _input.Read(scratch, 0, Math.Min(bytes, scratch.Length));
            if (n <= 0) break;
            bytes -= n;
        }
    }
}
