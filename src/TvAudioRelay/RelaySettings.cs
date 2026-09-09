using System.Text.Json;
using System.Text.Json.Serialization;

namespace TvAudioRelay;

/// <summary>Everything the relay needs. Loaded from relay.json and/or command-line flags.</summary>
public sealed class RelaySettings
{
    /// <summary>Part of the TV's Bluetooth name as Windows shows it. Null picks the only paired source, if there is exactly one.</summary>
    public string? Tv { get; set; }

    /// <summary>Playback device Windows renders the TV's audio to. Null means the current Windows default output.</summary>
    public string? Source { get; set; }

    /// <summary>Playback devices to copy the audio to. Usually your Bluetooth headphones.</summary>
    public List<string> Outputs { get; set; } = new();

    /// <summary>Audio held per output to absorb Bluetooth jitter. Lower is less delay, higher is fewer dropouts.</summary>
    public int BufferMs { get; set; } = 80;

    /// <summary>WASAPI period requested per output device.</summary>
    public int LatencyMs { get; set; } = 40;

    /// <summary>Open the Bluetooth receiver so the TV can send audio here. Turn off to relay any other audio.</summary>
    public bool Receive { get; set; } = true;

    /// <summary>Play silence to the source device so Windows keeps its loopback stream alive.</summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>How often to print a status line.</summary>
    public int StatusSeconds { get; set; } = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static RelaySettings Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RelaySettings>(stream, JsonOptions)
               ?? throw new InvalidDataException($"{path} did not contain a settings object.");
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
    }

    public static RelaySettings Example() => new()
    {
        Tv = "TCL",
        Source = null,
        Outputs = new List<string> { "Headphones (WH-1000XM5 Stereo)", "Headphones (Galaxy Buds Stereo)" },
        BufferMs = 80,
        LatencyMs = 40,
        Receive = true,
        KeepAlive = true,
        StatusSeconds = 5,
    };

    public void Validate()
    {
        if (BufferMs < 10 || BufferMs > 2000) throw new ArgumentException("bufferMs must be between 10 and 2000.");
        if (LatencyMs < 10 || LatencyMs > 500) throw new ArgumentException("latencyMs must be between 10 and 500.");
        if (StatusSeconds < 1) StatusSeconds = 1;
        if (Outputs.Count == 0) throw new ArgumentException("At least one output is required. Use --out \"<device name>\" or the outputs list in relay.json.");
    }
}
