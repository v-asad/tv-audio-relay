using TvAudioRelay.Core;
using Xunit;

public class ChannelMapperTests
{
    [Fact]
    public void SameChannelCount_Copies()
    {
        float[] input = { 0.1f, -0.2f };
        float[] output = new float[2];
        ChannelMapper.Map(input, 2, output, 2);
        Assert.Equal(input, output);
    }

    [Fact]
    public void MonoToStereo_Duplicates()
    {
        float[] output = new float[2];
        ChannelMapper.Map(new[] { 0.5f }, 1, output, 2);
        Assert.Equal(new[] { 0.5f, 0.5f }, output);
    }

    [Fact]
    public void StereoToMono_Averages()
    {
        float[] output = new float[1];
        ChannelMapper.Map(new[] { 1.0f, 0.0f }, 2, output, 1);
        Assert.Equal(0.5f, output[0], 5);
    }

    [Fact]
    public void StereoToSurround_CopiesFrontAndZeroFillsRest()
    {
        float[] output = { 9, 9, 9, 9, 9, 9 };
        ChannelMapper.Map(new[] { 0.3f, 0.4f }, 2, output, 6);
        Assert.Equal(new[] { 0.3f, 0.4f, 0, 0, 0, 0 }, output);
    }
}
