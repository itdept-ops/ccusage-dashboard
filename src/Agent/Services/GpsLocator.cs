using Windows.Devices.Geolocation;

namespace UsageIq.Agent.Services;

/// <summary>
/// Obtains a PRECISE, consented GPS fix via the WinRT <see cref="Geolocator"/> (Windows.Devices.Geolocation)
/// for the agent's machineInfo. This is the only Windows-GPS-aware code in the solution — ReporterCore stays
/// portable; the agent injects a fix through <c>ReporterEngine.GpsProvider</c>.
///
/// <para>Privacy + graceful degradation: it first requests location access (the OS shows its own consent
/// prompt the first time / honors the Windows location-privacy setting). On <b>any</b> non-allowed status,
/// timeout, disabled location, or exception it returns <c>null</c> — the reporter then sends no coordinates
/// and the server falls back to coarse IP-geo of the observed public IP. It NEVER throws into the caller and
/// NEVER blocks indefinitely (the fix is bounded by a timeout).</para>
///
/// <para>The fix is cached for the process: location is requested once and reused, so we don't re-prompt or
/// re-poll the sensor on every sync pass.</para>
/// </summary>
public static class GpsLocator
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static (double Lat, double Lng, double? AccuracyM)? _cached;

    /// <summary>
    /// A synchronous provider suitable for <c>ReporterEngine.GpsProvider</c>. Returns the cached fix (or
    /// null). The actual sensor call is async + bounded; this wrapper blocks only the first time and only
    /// up to the internal timeout, then caches forever for the process.
    /// </summary>
    public static (double Lat, double Lng, double? AccuracyM)? TryGetFix()
    {
        lock (Gate)
        {
            if (_attempted) return _cached;
            _attempted = true;
        }

        try
        {
            // Bounded: a hung sensor / consent dialog must not stall a sync pass forever.
            _cached = GetFixAsync().GetAwaiter().GetResult();
        }
        catch
        {
            _cached = null; // denial / no sensor / OS error → fall back to IP-geo
        }
        return _cached;
    }

    private static async Task<(double, double, double?)?> GetFixAsync()
    {
        // Honors the Windows location-privacy setting and shows the OS consent prompt the first time.
        var access = await RequestAccessWithTimeoutAsync(TimeSpan.FromSeconds(10));
        if (access != GeolocationAccessStatus.Allowed) return null;

        var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Geoposition pos;
        try
        {
            pos = await locator.GetGeopositionAsync().AsTask(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null; // no fix within the window → IP-geo fallback
        }

        var coord = pos?.Coordinate?.Point?.Position;
        if (coord is null) return null;

        double? accuracy = pos!.Coordinate!.Accuracy is double a && !double.IsNaN(a) ? a : null;
        return (coord.Value.Latitude, coord.Value.Longitude, accuracy);
    }

    private static async Task<GeolocationAccessStatus> RequestAccessWithTimeoutAsync(TimeSpan timeout)
    {
        var request = Geolocator.RequestAccessAsync().AsTask();
        var done = await Task.WhenAny(request, Task.Delay(timeout));
        if (done != request) return GeolocationAccessStatus.Unspecified; // user never answered → degrade
        return await request;
    }
}
