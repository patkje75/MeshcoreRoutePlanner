using System.Text.Json.Serialization;
using MeshRoutePlanner.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<CoreScopeClient>();
builder.Services.AddScoped<RouteFinder>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

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

app.MapGet("/api/route", async (
    string from, string to, RouteFinder finder, CancellationToken ct,
    string? fromName = null, double? fromLat = null, double? fromLon = null,
    string? toName = null, double? toLat = null, double? toLon = null) =>
{
    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        return Results.BadRequest(new { error = "Both 'from' and 'to' pubkeys are required." });

    if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "'from' and 'to' must be different nodes." });

    var routes = await finder.FindRoutesAsync(from, to, ct: ct);

    // The caller already resolved these from /api/search; backfill the endpoint hops with that
    // metadata in case the route finder never needed to look either node's own path data up.
    foreach (var route in routes)
    {
        FillHop(route.Hops[0], from, fromName, fromLat, fromLon);
        FillHop(route.Hops[^1], to, toName, toLat, toLon);
    }

    return Results.Ok(RouteFinder.SelectBest(routes));
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
