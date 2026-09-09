using System.Diagnostics;
using NAudio.CoreAudioApi;
using TvAudioRelay.Audio;
using TvAudioRelay.Bluetooth;

namespace TvAudioRelay;

/// <summary>Wires the pieces together: receiver, loopback capture, N output sinks, status loop.</summary>
public static class Relay
{
    public static async Task<int> RunAsync(RelaySettings settings, CancellationToken ct)
    {
        settings.Validate();
        TryRaisePriority();

        // 1. Where Windows renders the TV's audio, and where we copy it from.
        MMDevice source = settings.Source is null ? AudioDevices.DefaultRender() : AudioDevices.Resolve(settings.Source);
        bool sourceIsDefault = source.ID == AudioDevices.DefaultRenderId();

        // 2. Where the copies go.
        var outputs = settings.Outputs.Select(AudioDevices.Resolve).ToList();
        var dup = outputs.GroupBy(o => o.ID).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"\"{dup.First().FriendlyName}\" is listed twice.");
        if (outputs.Any(o => o.ID == source.ID))
            throw new InvalidOperationException($"\"{source.FriendlyName}\" is the source; it cannot also be an output.");

        if (settings.Receive && !sourceIsDefault)
        {
            Console.WriteLine($"Note: Windows plays the TV's audio on the DEFAULT device, which is not \"{source.FriendlyName}\".");
            Console.WriteLine("      Either set that device as default in Windows Sound settings, or drop --source.");
        }

        Console.WriteLine($"Source : {source.FriendlyName}{(sourceIsDefault ? " (Windows default)" : "")}");
        foreach (var o in outputs) Console.WriteLine($"Output : {o.FriendlyName}");
        Console.WriteLine($"Buffer : {settings.BufferMs} ms per output, WASAPI period {settings.LatencyMs} ms");

        using var loopback = new LoopbackSource(source, settings.KeepAlive);
        Console.WriteLine($"Capture: {loopback.Format.SampleRate} Hz, {loopback.Format.Channels} ch, float");

        var sinks = new List<OutputSink>();
        var sinkLock = new object();
        foreach (var o in outputs)
            sinks.Add(CreateSink(o, loopback, settings));

        loopback.DataAvailable += (buf, n) =>
        {
            lock (sinkLock)
            {
                foreach (var s in sinks) s.Push(buf, n);
            }
        };
        loopback.Stopped += ex => Console.WriteLine($"Capture stopped{(ex is null ? "" : ": " + ex.Message)}.");

        lock (sinkLock) foreach (var s in sinks) s.Start();
        loopback.Start();

        // 3. Bluetooth receiver.
        PlaybackReceiver? receiver = null;
        Task receiverTask = Task.CompletedTask;
        if (settings.Receive)
        {
            var (id, name) = await PickTvAsync(settings.Tv);
            receiver = new PlaybackReceiver(id, name);
            receiver.Log += msg => Console.WriteLine($"[bt] {msg}");
            receiverTask = receiver.RunAsync(ct);
        }
        else
        {
            Console.WriteLine("Receiver off (--no-receive): relaying whatever plays on the source device.");
        }

        Console.WriteLine("Running. Press Ctrl+C to stop.");
        Console.WriteLine();

        // 4. Status and self-healing loop.
        var stopwatch = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(settings.StatusSeconds), ct); }
            catch (OperationCanceledException) { break; }

            lock (sinkLock)
            {
                for (int i = 0; i < sinks.Count; i++)
                {
                    if (!sinks[i].IsStopped) continue;
                    var dev = AudioDevices.ById(sinks[i].DeviceId);
                    if (dev is null) continue; // still gone; try again next tick
                    string name = sinks[i].Name;
                    sinks[i].Dispose();
                    try
                    {
                        sinks[i] = CreateSink(dev, loopback, settings);
                        sinks[i].Start();
                        Console.WriteLine($"[out] \"{name}\" is back; playback restarted.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[out] \"{name}\" restart failed: {ex.Message}");
                    }
                }

                float peak = loopback.TakePeak();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss}  source peak {peak,5:F2}  tv {(receiver is null ? "n/a" : receiver.IsOpen ? "linked" : "not linked")}");
                foreach (var s in sinks) Console.WriteLine("           " + s.StatusLine());
            }

            if (receiver is { IsOpen: true } && loopback.SinceLastSound > TimeSpan.FromSeconds(20) && stopwatch.Elapsed > TimeSpan.FromSeconds(20))
            {
                Console.WriteLine("           hint: the TV is linked but the source device has been silent for 20 s.");
                Console.WriteLine("           If the source is muted in Windows, unmute it and lower its volume instead, or use --source with another device.");
            }
        }

        Console.WriteLine("Stopping...");
        receiver?.Dispose();
        try { await receiverTask; } catch (OperationCanceledException) { }
        lock (sinkLock) foreach (var s in sinks) s.Dispose();
        return 0;
    }

    private static OutputSink CreateSink(MMDevice device, LoopbackSource loopback, RelaySettings settings)
    {
        var sink = new OutputSink(device, loopback.Format, settings.BufferMs, settings.LatencyMs);
        sink.Stopped += (s, ex) =>
            Console.WriteLine($"[out] \"{s.Name}\" stopped{(ex is null ? "" : ": " + ex.Message)}. Will restart when the device is back.");
        return sink;
    }

    private static async Task<(string Id, string Name)> PickTvAsync(string? query)
    {
        var sources = await PlaybackReceiver.ListSourcesAsync();
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "No paired Bluetooth device can send audio to this PC. Pair the TV first (see README, step 3).");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            if (sources.Count == 1) return sources[0];
            throw new InvalidOperationException(
                "Several paired devices can send audio here. Choose one with --tv \"<name>\":\n" +
                string.Join("\n", sources.Select(s => $"  - {s.Name}")));
        }

        var exact = sources.Where(s => string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = sources.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count == 0)
        {
            throw new InvalidOperationException(
                $"No paired audio source matches \"{query}\". Paired sources:\n" +
                string.Join("\n", sources.Select(s => $"  - {s.Name}")));
        }
        throw new InvalidOperationException(
            $"\"{query}\" matches several sources:\n" + string.Join("\n", partial.Select(s => $"  - {s.Name}")));
    }

    private static void TryRaisePriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch { /* not fatal */ }
    }
}
