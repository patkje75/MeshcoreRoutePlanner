using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MeshRoutePlanner.Core;

/// <summary>
/// Sketch client for pulling atmospheric data from the free, keyless Open-Meteo API and turning
/// it into a rough radio-refractivity / tropospheric-ducting indicator. This is an evaluation
/// prototype, not a validated propagation model - see <see cref="AtmosphericConditions.Note"/>.
///
/// Why self-computed: the well-known ducting forecasts (Hepburn Tropo Index, F5LEN) have no
/// public API and explicitly forbid scraping/reposting their output, so this computes the same
/// underlying physics (ITU-R style refractivity) directly from raw weather data instead.
/// </summary>
public class WeatherClient
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>Standard-atmosphere effective Earth-radius factor (dN/dh = -39 N-units/km).</summary>
    private const double StandardKFactor = 4.0 / 3.0;

    /// <summary>Radio horizon formula constant, for height in metres and distance in km (d = 3.57 * sqrt(k*h)).</summary>
    private const double HorizonConstant = 3.57;

    /// <summary>Default assumed antenna height above ground when the caller doesn't supply one.</summary>
    public const double DefaultAntennaHeightM = 10.0;

    private readonly HttpClient _http;
    private readonly IMemoryCache? _cache;

    public WeatherClient(HttpClient http, IMemoryCache? cache = null)
    {
        _http = http;
        _cache = cache;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.open-meteo.com");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRoutePlanner/0.1 (+personal route-finding tool)");
    }

    public async Task<AtmosphericConditions?> GetConditionsAsync(
        double lat, double lon, double antennaHeightM = DefaultAntennaHeightM, CancellationToken ct = default)
    {
        var key = $"wx:{lat.ToString("F2", CultureInfo.InvariantCulture)}:{lon.ToString("F2", CultureInfo.InvariantCulture)}:{antennaHeightM.ToString("F1", CultureInfo.InvariantCulture)}";
        if (_cache is not null && _cache.TryGetValue(key, out AtmosphericConditions? cached))
            return cached;

        var url = "/v1/forecast"
            + $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}"
            + "&current=temperature_2m,relative_humidity_2m,dew_point_2m,pressure_msl,surface_pressure"
            + "&hourly=temperature_850hPa,relative_humidity_850hPa,geopotential_height_850hPa"
            + "&forecast_days=1&timezone=UTC";

        var response = await _http.GetFromJsonAsync<OpenMeteoResponse>(url, ct);
        if (response?.Current is null)
            return null;

        var result = BuildConditions(lat, lon, antennaHeightM, response);
        _cache?.Set(key, result, Ttl);
        return result;
    }

    private static AtmosphericConditions BuildConditions(double lat, double lon, double antennaHeightM, OpenMeteoResponse response)
    {
        var current = response.Current!;
        var surfaceTempK = current.TemperatureC + 273.15;
        var surfaceVapor = VaporPressureHpa(current.DewPointC);
        var surfaceN = Refractivity(current.SurfacePressureHpa, surfaceTempK, surfaceVapor);

        double? upperN = null;
        double? gradient = null;
        var likelihood = DuctingLikelihood.Normal;

        var idx = FindMatchingHourIndex(response.Hourly, current.Time);
        if (idx is not null && response.Hourly is not null)
        {
            var upperTempC = response.Hourly.Temperature850[idx.Value];
            var upperRh = response.Hourly.RelativeHumidity850[idx.Value];
            var upperHeight = response.Hourly.GeopotentialHeight850[idx.Value];

            if (upperTempC is not null && upperRh is not null && upperHeight is not null)
            {
                var upperTempK = upperTempC.Value + 273.15;
                var upperDewC = DewPointFromRelativeHumidity(upperTempC.Value, upperRh.Value);
                var upperVapor = VaporPressureHpa(upperDewC);
                upperN = Refractivity(850.0, upperTempK, upperVapor);

                var heightDiffKm = (upperHeight.Value - response.Elevation) / 1000.0;
                if (heightDiffKm > 0.05)
                {
                    gradient = (upperN.Value - surfaceN) / heightDiffKm;
                    likelihood = Classify(gradient.Value);
                }
            }
        }

        var standardHorizonKm = HorizonConstant * Math.Sqrt(StandardKFactor * antennaHeightM);
        double? kFactor = null;
        double? radioHorizonKm = null;
        double? horizonExtensionPercent = null;

        if (gradient is not null)
        {
            // k = 157 / (157 + dN/dh): blows up as dN/dh approaches -157 (the ducting threshold),
            // where this simplified single-layer model stops being meaningful, so bail out near it.
            var denominator = 157.0 + gradient.Value;
            if (denominator > 1.0)
            {
                kFactor = 157.0 / denominator;
                radioHorizonKm = HorizonConstant * Math.Sqrt(kFactor.Value * antennaHeightM);
                horizonExtensionPercent = (radioHorizonKm.Value / standardHorizonKm - 1.0) * 100.0;
            }
        }

        return new AtmosphericConditions
        {
            Lat = lat,
            Lon = lon,
            ObservedAt = current.Time,
            SurfaceTemperatureC = current.TemperatureC,
            SurfaceRelativeHumidityPct = current.RelativeHumidityPct,
            SurfacePressureHpa = current.SurfacePressureHpa,
            SurfaceRefractivityN = surfaceN,
            UpperRefractivityN = upperN,
            RefractivityGradientPerKm = gradient,
            Likelihood = likelihood,
            KFactor = kFactor,
            AssumedAntennaHeightM = antennaHeightM,
            RadioHorizonKm = radioHorizonKm,
            StandardRadioHorizonKm = standardHorizonKm,
            HorizonExtensionPercent = horizonExtensionPercent,
        };
    }

    /// <summary>ITU-R-style radio refractivity: N = 77.6*(P/T) + 3.73e5*(e/T^2).</summary>
    private static double Refractivity(double pressureHpa, double tempK, double vaporPressureHpa)
        => 77.6 * (pressureHpa / tempK) + 3.73e5 * (vaporPressureHpa / (tempK * tempK));

    /// <summary>Magnus formula, dew point (°C) -> actual vapor pressure (hPa).</summary>
    private static double VaporPressureHpa(double dewPointC)
        => 6.1121 * Math.Exp(17.502 * dewPointC / (240.97 + dewPointC));

    /// <summary>Inverse Magnus formula, temperature + relative humidity -> dew point (°C).</summary>
    private static double DewPointFromRelativeHumidity(double tempC, double relativeHumidityPct)
    {
        const double a = 17.625;
        const double b = 243.04;
        var gamma = Math.Log(relativeHumidityPct / 100.0) + (a * tempC) / (b + tempC);
        return (b * gamma) / (a - gamma);
    }

    /// <summary>
    /// Thresholds follow the standard radio-propagation bands for dN/dh (N-units per km):
    /// normal ~ -40, super-refraction -40 to -157, ducting beyond -157 (trapping layer).
    /// </summary>
    private static DuctingLikelihood Classify(double gradientPerKm)
    {
        if (gradientPerKm <= -157) return DuctingLikelihood.Ducting;
        if (gradientPerKm <= -80) return DuctingLikelihood.Enhanced;
        if (gradientPerKm >= 0) return DuctingLikelihood.SubRefraction;
        return DuctingLikelihood.Normal;
    }

    private static int? FindMatchingHourIndex(OpenMeteoHourly? hourly, DateTimeOffset target)
    {
        if (hourly is null || hourly.Time.Count == 0)
            return null;

        var bestIdx = 0;
        var bestDiff = TimeSpan.MaxValue;
        for (var i = 0; i < hourly.Time.Count; i++)
        {
            var diff = (hourly.Time[i] - target).Duration();
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }
}
