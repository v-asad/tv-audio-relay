namespace TvAudioRelay.Core;

/// <summary>
/// Maps one interleaved audio frame from <c>inChannels</c> to <c>outChannels</c>.
/// Rules: same count copies; mono in duplicates to every output; mono out averages
/// every input; anything else copies the channels both sides have and zero-fills the rest.
/// </summary>
public static class ChannelMapper
{
    public static void Map(ReadOnlySpan<float> input, int inChannels, Span<float> output, int outChannels)
    {
        if (inChannels == outChannels)
        {
            input[..inChannels].CopyTo(output);
            return;
        }

        if (inChannels == 1)
        {
            output[..outChannels].Fill(input[0]);
            return;
        }

        if (outChannels == 1)
        {
            float sum = 0f;
            for (int c = 0; c < inChannels; c++) sum += input[c];
            output[0] = sum / inChannels;
            return;
        }

        int shared = Math.Min(inChannels, outChannels);
        input[..shared].CopyTo(output);
        if (outChannels > shared) output[shared..outChannels].Clear();
    }
}
