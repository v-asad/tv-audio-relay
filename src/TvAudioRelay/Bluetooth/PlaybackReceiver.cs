using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace TvAudioRelay.Bluetooth;

/// <summary>
/// Makes this PC a Bluetooth audio receiver for one paired device (the TV). Uses the same
/// Windows API the Store "audio receiver" apps use: <see cref="AudioPlaybackConnection"/>.
/// Once open, Windows renders the incoming audio on the default playback device.
/// </summary>
public sealed class PlaybackReceiver : IDisposable
{
    private AudioPlaybackConnection? _connection;

    public PlaybackReceiver(string deviceId, string name)
    {
        DeviceId = deviceId;
        Name = name;
    }

    public string DeviceId { get; }
    public string Name { get; }

    public bool IsOpen => _connection?.State == AudioPlaybackConnectionState.Opened;

    public event Action<string>? Log;

    /// <summary>Paired devices that can send audio to this PC (phones, TVs, tablets).</summary>
    public static async Task<List<(string Id, string Name)>> ListSourcesAsync()
    {
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.Select(d => (d.Id, d.Name)).OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Arms the receiver, then keeps trying to open the audio link until cancelled. Stays armed
    /// after the TV disconnects so the TV can reconnect on its own.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _connection = AudioPlaybackConnection.TryCreateFromId(DeviceId)
                      ?? throw new InvalidOperationException($"Windows refused to create an audio receiver for \"{Name}\". Is it paired?");

        _connection.StateChanged += (sender, _) =>
            Log?.Invoke(sender.State == AudioPlaybackConnectionState.Opened
                ? $"TV link OPEN: \"{Name}\" is now sending audio to this PC."
                : $"TV link closed: \"{Name}\" stopped sending audio.");

        await _connection.StartAsync();
        Log?.Invoke($"Receiver armed for \"{Name}\". Waiting for the TV to send audio...");

        int retryMs = 3000;
        while (!ct.IsCancellationRequested)
        {
            if (_connection.State != AudioPlaybackConnectionState.Opened)
            {
                try
                {
                    var result = await _connection.OpenAsync();
                    if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                    {
                        retryMs = 3000;
                    }
                    else
                    {
                        Log?.Invoke($"Open attempt: {result.Status}. Will retry in {retryMs / 1000}s. " +
                                    "If this keeps happening, pick this PC as the audio output on the TV (Settings > Remotes & Accessories).");
                        retryMs = Math.Min(retryMs * 2, 20000);
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Open attempt failed: {ex.Message}");
                    retryMs = Math.Min(retryMs * 2, 20000);
                }
            }

            try
            {
                await Task.Delay(_connection.State == AudioPlaybackConnectionState.Opened ? 5000 : retryMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _connection?.Dispose(); } catch { }
    }
}
