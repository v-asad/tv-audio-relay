using NAudio.CoreAudioApi;

namespace TvAudioRelay.Audio;

/// <summary>Thin helpers over the Windows Core Audio device list.</summary>
public static class AudioDevices
{
    public static MMDevice DefaultRender()
    {
        using var e = new MMDeviceEnumerator();
        return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public static string? DefaultRenderId()
    {
        try { return DefaultRender().ID; } catch { return null; }
    }

    public static List<MMDevice> ActiveRender()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public static MMDevice? ById(string id)
    {
        using var e = new MMDeviceEnumerator();
        try
        {
            var d = e.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds one active playback device by name. Exact match wins, then a single case-insensitive
    /// substring match. Anything else throws with the candidate list so the user can refine.
    /// </summary>
    public static MMDevice Resolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Device name is empty.");
        var all = ActiveRender();

        var exact = all.Where(d => string.Equals(d.FriendlyName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var partial = all.Where(d => d.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];

        if (partial.Count == 0)
        {
            throw new InvalidOperationException(
                $"No active playback device matches \"{query}\". Available:\n" + Describe(all));
        }

        throw new InvalidOperationException(
            $"\"{query}\" matches more than one playback device. Use a longer name:\n" + Describe(partial));
    }

    public static string Describe(IEnumerable<MMDevice> devices)
    {
        string? def = DefaultRenderId();
        return string.Join("\n", devices.Select(d => $"  {(d.ID == def ? "*" : "-")} {d.FriendlyName}"));
    }
}
