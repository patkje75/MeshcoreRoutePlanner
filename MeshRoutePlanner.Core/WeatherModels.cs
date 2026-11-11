using System.Text.Json.Serialization;

namespace MeshRoutePlanner.Core;

public class OpenMeteoResponse
{
    [JsonPropertyName("latitude")] public double Latitude { get; init; }
    [JsonPropertyName("longitude")] public double Longitude { get; init; }
    [JsonPropertyName("elevation")] public double Elevation { get; init; }
    [JsonPropertyName("current")] public OpenMeteoCurrent? Current { get; init; }
    [JsonPropertyName("hourly")] public OpenMeteoHourly? Hourly { get; init; }
}

public class OpenMeteoCurrent
{
    [JsonPropertyName("time")] public DateTimeOffset Time { get; init; }
    [JsonPropertyName("temperature_2m")] public double TemperatureC { get; init; }
    [JsonPropertyName("relative_humidity_2m")] public double RelativeHumidityPct { get; init; }
    [JsonPropertyName("dew_point_2m")] public double DewPointC { get; init; }
    [JsonPropertyName("pressure_msl")] public double PressureMslHpa { get; init; }
    [JsonPropertyName("surface_pressure")] public double SurfacePressureHpa { get; init; }
}

/// <summary>
/// Parallel arrays, one entry per forecast hour - matches Open-Meteo's "hourly" response shape.
/// 850 hPa is roughly 1.5 km up; comparing it against the surface gives a cheap single-layer
/// proxy for whether a temperature inversion (and therefore possible ducting) is present.
/// </summary>
public class OpenMeteoHourly
{
    [JsonPropertyName("time")] public List<DateTimeOffset> Time { get; init; } = [];
    [JsonPropertyName("temperature_850hPa")] public List<double?> Temperature850 { get; init; } = [];
    [JsonPropertyName("relative_humidity_850hPa")] public List<double?> RelativeHumidity850 { get; init; } = [];
    [JsonPropertyName("geopotential_height_850hPa")] public List<double?> GeopotentialHeight850 { get; init; } = [];
}

/// <summary>
/// Coarse classification of radio refractivity conditions, based on the standard thresholds
/// used in radio-propagation engineering (ITU-R P.453-style dN/dh bands). This is a rough,
/// self-computed proxy - NOT the Hepburn Tropo Index or F5LEN's forecast, which are proprietary
/// and have no public API or reuse licence.
/// </summary>
public enum DuctingLikelihood
{
    /// <summary>Gradient is flatter than standard - range may be reduced versus normal.</summary>
    SubRefraction,
    /// <summary>Close to the standard atmosphere (~-40 N-units/km).</summary>
    Normal,
    /// <summary>Stronger-than-standard bending - some extended range plausible.</summary>
    Enhanced,
    /// <summary>Gradient steep enough (&lt;= -157 N-units/km) that a trapping duct may be forming.</summary>
    Ducting,
}

public class AtmosphericConditions
{
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public double SurfaceTemperatureC { get; init; }
    public double SurfaceRelativeHumidityPct { get; init; }
    public double SurfacePressureHpa { get; init; }
    public double SurfaceRefractivityN { get; init; }
    public double? UpperRefractivityN { get; init; }
    public double? RefractivityGradientPerKm { get; init; }
    public DuctingLikelihood Likelihood { get; init; }
    public string Note { get; init; } =
        "Self-computed from Open-Meteo weather data, not a validated propagation forecast. " +
        "Treat as a rough hint, not a confirmed effect on any specific route.";
}
