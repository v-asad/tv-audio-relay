using NAudio.Wave;

namespace TvAudioRelay.Core;

/// <summary>
/// WASAPI mix formats arrive as WAVEFORMATEXTENSIBLE with an IEEE-float sub-format rather than the
/// plain IEEE-float tag. Both describe the same 32-bit float samples, so treat them alike.
/// </summary>
public static class FloatFormat
{
    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    public static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool Is32BitFloat(WaveFormat format)
    {
        if (format is null || format.BitsPerSample != 32) return false;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat;
    }
}
