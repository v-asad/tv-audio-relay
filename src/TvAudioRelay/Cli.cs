using TvAudioRelay.Audio;
using TvAudioRelay.Bluetooth;

namespace TvAudioRelay;

public static class Cli
{
    private const string DefaultConfig = "relay.json";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            switch (args[0])
            {
                case "devices":
                case "list":
                    return await DevicesAsync();

                case "init":
                    return Init(args.Skip(1).FirstOrDefault() ?? DefaultConfig);

                case "run":
                    return await RunRelayAsync(args.Skip(1).ToArray());

                default:
                    Console.Error.WriteLine($"Unknown command \"{args[0]}\".");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> DevicesAsync()
    {
        Console.WriteLine("Bluetooth audio sources (paired devices that can send audio to this PC):");
        try
        {
            var sources = await PlaybackReceiver.ListSourcesAsync();
            if (sources.Count == 0) Console.WriteLine("  (none paired yet)");
            foreach (var (_, name) in sources) Console.WriteLine($"  - {name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (could not query: {ex.Message})");
        }

        Console.WriteLine();
        Console.WriteLine("Playback devices (* = Windows default = where the TV's audio arrives):");
        Console.WriteLine(AudioDevices.Describe(AudioDevices.ActiveRender()));
        Console.WriteLine();
        Console.WriteLine("Tip: for Bluetooth headphones pick the \"Headphones (... Stereo)\" entry, not the \"Headset\" one.");
        return 0;
    }

    private static int Init(string path)
    {
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} already exists; not overwriting.");
            return 1;
        }
        RelaySettings.Example().Save(path);
        Console.WriteLine($"Wrote {path}. Edit the device names, then run: tv-audio-relay run");
        return 0;
    }

    private static async Task<int> RunRelayAsync(string[] args)
    {
        string? configPath = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--config") configPath = args[i + 1];

        RelaySettings settings;
        if (configPath is not null)
        {
            settings = RelaySettings.Load(configPath);
        }
        else if (File.Exists(DefaultConfig))
        {
            Console.WriteLine($"Using {DefaultConfig} (flags override it).");
            settings = RelaySettings.Load(DefaultConfig);
        }
        else
        {
            settings = new RelaySettings();
        }

        bool outputsFromFlags = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{a} needs a value.");
                return args[++i];
            }

            switch (a)
            {
                case "--config": Next(); break;
                case "--tv": settings.Tv = Next(); break;
                case "--source": settings.Source = Next(); break;
                case "--out":
                    if (!outputsFromFlags) { settings.Outputs = new List<string>(); outputsFromFlags = true; }
                    settings.Outputs.Add(Next());
                    break;
                case "--buffer-ms": settings.BufferMs = ParseInt(a, Next()); break;
                case "--latency-ms": settings.LatencyMs = ParseInt(a, Next()); break;
                case "--status-seconds": settings.StatusSeconds = ParseInt(a, Next()); break;
                case "--no-receive": settings.Receive = false; break;
                case "--no-keepalive": settings.KeepAlive = false; break;
                default: throw new ArgumentException($"Unknown option \"{a}\". Run \"tv-audio-relay help\".");
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        return await Relay.RunAsync(settings, cts.Token);
    }

    private static int ParseInt(string flag, string value) =>
        int.TryParse(value, out int n) ? n : throw new ArgumentException($"{flag} expects a whole number, got \"{value}\".");

    private static void PrintHelp()
    {
        Console.WriteLine("""
tv-audio-relay  -  receive a TV's Bluetooth audio on this PC and relay it to several headphones

USAGE
  tv-audio-relay devices
      List paired Bluetooth audio sources and Windows playback devices.

  tv-audio-relay init [relay.json]
      Write a settings file you can edit instead of typing flags.

  tv-audio-relay run [options]
      Start the receiver and the relay. Reads relay.json from the current folder if present.

OPTIONS for run
  --tv <name>          Part of the TV's Bluetooth name. Optional when only one source is paired.
  --out <name>         Playback device to copy audio to. Repeat for each headphone.
  --source <name>      Device to copy from. Default: the Windows default playback device.
  --buffer-ms <n>      Audio held per output (default 80). Lower = less delay, higher = fewer dropouts.
  --latency-ms <n>     WASAPI period per output (default 40).
  --no-receive         Do not act as a Bluetooth receiver; just relay the source device.
  --no-keepalive       Do not play silence to the source to keep its loopback alive.
  --status-seconds <n> Status line interval (default 5).
  --config <path>      Settings file to use.

EXAMPLE
  tv-audio-relay run --tv "TCL" --out "Headphones (WH-1000XM5 Stereo)" --out "Headphones (Galaxy Buds Stereo)"

Step-by-step setup is in README.md.
""");
    }
}
