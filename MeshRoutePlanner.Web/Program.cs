using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshRoutePlanner.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<CoreScopeClient>();
builder.Services.AddHttpClient<WeatherClient>();
builder.Services.AddScoped<RouteFinder>();
builder.Services.AddSingleton<SearchRegistry>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
streamJsonOptions.Converters.Add(new JsonStringEnumConverter());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/search", async (string? q, CoreScopeClient client, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Ok(Array.Empty<NodeSummary>());

    var nodes = await client.SearchNodesAsync(q, ct);
    return Results.Ok(nodes);
});

app.MapGet("/api/node/{pubkey}", async (string pubkey, CoreScopeClient client, CancellationToken ct) =>
{
    var node = await client.GetNodeAsync(pubkey, ct);
    return node is null ? Results.NotFound(new { error = "Not found" }) : Results.Ok(node);
});

app.MapGet("/api/repeaters", async (CoreScopeClient client, CancellationToken ct) =>
{
    var repeaters = await client.GetAllRepeatersAsync(ct);
    return Results.Ok(repeaters);
});

// Streams search progress + the final result as newline-delimited JSON (one RouteSearchEvent per
// line), so the UI can show real-time feedback instead of a static "searching…" message, and so a
// user-requested stop (see /api/route/stop) can still return whatever was found so far instead of
// nothing. `searchId` ties this request to that stop endpoint.
app.MapGet("/api/route", async (
    HttpContext httpContext,
    string from, string to, RouteFinder finder, SearchRegistry registry, CancellationToken ct,
    string? searchId = null,
    string? fromName = null, double? fromLat = null, double? fromLon = null,
    string? toName = null, double? toLat = null, double? toLon = null) =>
{
    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Both 'from' and 'to' pubkeys are required." });
        return;
    }

    if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = "'from' and 'to' must be different nodes." });
        return;
    }

    // An older/cached client might not send one at all - fall back to a per-request id so the
    // search still runs (it just won't be stoppable via /api/route/stop).
    if (string.IsNullOrWhiteSpace(searchId))
        searchId = Guid.NewGuid().ToString();

    var manualStop = registry.Register(searchId);
    httpContext.Response.ContentType = "application/x-ndjson";

    try
    {
        await foreach (var evt in finder.FindRoutesStreamAsync(from, to, manualStop, ct: ct))
        {
            if (evt.Type == RouteSearchEventType.Done && evt.Result is not null)
            {
                // The caller already resolved these from /api/search; backfill the endpoint hops
                // with that metadata in case the route finder never needed to look either node's
                // own path data up.
                var routes = evt.Result.Best is null
                    ? evt.Result.Alternatives
                    : new[] { evt.Result.Best }.Concat(evt.Result.Alternatives);
                foreach (var route in routes)
                {
                    FillHop(route.Hops[0], from, fromName, fromLat, fromLon);
                    FillHop(route.Hops[^1], to, toName, toLat, toLon);
                }
            }

            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(evt, streamJsonOptions) + "\n", ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
    }
    finally
    {
        registry.Unregister(searchId);
    }
});

// Signals a running /api/route search (matched by searchId) to stop early and report whatever
// candidates it already has, instead of the client hard-aborting the HTTP request and getting nothing.
app.MapPost("/api/route/stop", (string searchId, SearchRegistry registry) =>
{
    registry.Stop(searchId);
    return Results.Ok();
});

// Sketch/evaluation endpoint for WeatherClient - not yet wired into route results.
app.MapGet("/api/weather", async (
    double lat, double lon, WeatherClient client, CancellationToken ct,
    double antennaHeightM = WeatherClient.DefaultAntennaHeightM) =>
{
    var conditions = await client.GetConditionsAsync(lat, lon, antennaHeightM, ct);
    return conditions is null
        ? Results.Problem("Weather data unavailable for this location.", statusCode: 502)
        : Results.Ok(conditions);
});

app.Run();

static void FillHop(RouteHop hop, string pubkey, string? name, double? lat, double? lon)
{
    if (!string.Equals(hop.Pubkey, pubkey, StringComparison.OrdinalIgnoreCase))
        return;
    hop.Name ??= name;
    hop.Lat ??= lat;
    hop.Lon ??= lon;
}

/// <summary>
/// Tracks a soft-cancellation token per in-flight /api/route search so a Stop button can ask the
/// server to wind down gracefully (and still report partial results) instead of just severing the
/// HTTP connection.
/// </summary>
public class SearchRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _searches = new();

    public CancellationToken Register(string searchId)
    {
        var cts = new CancellationTokenSource();
        _searches[searchId] = cts;
        return cts.Token;
    }

    public void Stop(string searchId)
    {
        if (_searches.TryGetValue(searchId, out var cts))
            cts.Cancel();
    }

    public void Unregister(string searchId)
    {
        if (_searches.TryRemove(searchId, out var cts))
            cts.Dispose();
    }
}
