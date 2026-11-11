using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace MeshRoutePlanner.Core;

/// <summary>
/// Read-only client for the public CoreScope API (https://meshcore.meshat.se), which tracks
/// real MeshCore mesh traffic sniffed off the air. No CORS headers are sent by that API, so
/// this must run server-side, not from browser JS.
///
/// A route search can fan out into dozens of these calls (one per node explored during the
/// backward search), so every endpoint is cached in-process. TTLs are picked per endpoint:
/// short for data that changes constantly (search), longer for data CoreScope itself only
/// recomputes occasionally (paths, reach) - reach in particular already has its own 5-minute
/// server-side cache and a hard cap of 2 concurrent uncached builds network-wide, so caching it
/// here too avoids tripping its 429 "busy" response during a single route search.
/// </summary>
public class CoreScopeClient
{
    private static readonly TimeSpan SearchTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PathsTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReachTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepeatersTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IMemoryCache? _cache;

    public CoreScopeClient(HttpClient http, IMemoryCache? cache = null)
    {
        _http = http;
        _cache = cache;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://meshcore.meshat.se");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRoutePlanner/0.1 (+personal route-finding tool)");
    }

    public Task<List<NodeSummary>> SearchNodesAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new List<NodeSummary>());

        return GetCachedAsync($"search:{query.ToLowerInvariant()}", SearchTtl, async () =>
        {
            var response = await _http.GetFromJsonAsync<NodeSearchResponse>(
                $"/api/nodes/search?q={Uri.EscapeDataString(query)}", ct);
            return response?.Nodes ?? [];
        });
    }

    /// <summary>
    /// Every repeater in the mesh that has a known GPS position, for the overview map. ~1,600
    /// repeaters network-wide at last check, fetched in pages and cached as one list since this
    /// is used for the landing page every visitor hits.
    /// </summary>
    public Task<List<NodeSummary>> GetAllRepeatersAsync(CancellationToken ct = default)
    {
        return GetCachedAsync("repeaters:all", RepeatersTtl, async () =>
        {
            var all = new List<NodeSummary>();
            const int pageSize = 500;
            var offset = 0;
            while (true)
            {
                var page = await _http.GetFromJsonAsync<NodeListResponse>(
                    $"/api/nodes?role=repeater&limit={pageSize}&offset={offset}", ct);
                if (page is null || page.Nodes.Count == 0)
                    break;

                all.AddRange(page.Nodes);
                offset += page.Nodes.Count;
                if (!page.HasMore)
                    break;
            }

            return all.Where(n => n.Lat is not null && n.Lon is not null).ToList();
        });
    }

    public Task<NodeSummary?> GetNodeAsync(string pubkey, CancellationToken ct = default)
    {
        return GetCachedAsync($"node:{pubkey.ToLowerInvariant()}", PathsTtl, async () =>
        {
            using var resp = await _http.GetAsync($"/api/nodes/{pubkey}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;
            resp.EnsureSuccessStatusCode();
            var detail = await resp.Content.ReadFromJsonAsync<NodeDetailResponse>(cancellationToken: ct);
            return detail?.Node;
        });
    }

    public Task<NodePathsResponse?> GetNodePathsAsync(string pubkey, CancellationToken ct = default)
    {
        return GetCachedAsync($"paths:{pubkey.ToLowerInvariant()}", PathsTtl, async () =>
        {
            using var resp = await _http.GetAsync($"/api/nodes/{pubkey}/paths", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<NodePathsResponse>(cancellationToken: ct);
        });
    }

    /// <summary>
    /// Neighbor-link quality report for a node. Callers must still use this sparingly (a handful
    /// of frontier nodes during a bounded search), never for every node in the mesh - the client
    /// cache here only shields against repeating the exact same lookup. Returns null on 404
    /// (unknown node) or 429 (busy - a 429 is deliberately NOT cached, so the next search retries
    /// the real endpoint rather than remembering a transient "busy").
    /// </summary>
    public async Task<NodeReachResponse?> GetNodeReachAsync(string pubkey, int days = 7, CancellationToken ct = default)
    {
        var key = $"reach:{pubkey.ToLowerInvariant()}:{days}";
        if (_cache is not null && _cache.TryGetValue(key, out NodeReachResponse? cached))
            return cached;

        using var resp = await _http.GetAsync($"/api/nodes/{pubkey}/reach?days={days}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            _cache?.Set<NodeReachResponse?>(key, null, ReachTtl);
            return null;
        }
        if (resp.StatusCode == (HttpStatusCode)429)
            return null; // transient - deliberately not cached, so the next search retries for real

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<NodeReachResponse>(cancellationToken: ct);
        _cache?.Set(key, result, ReachTtl);
        return result;
    }

    private async Task<T> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> fetch)
    {
        if (_cache is null)
            return await fetch();

        if (_cache.TryGetValue(key, out T? cached))
            return cached!;

        var value = await fetch();
        _cache.Set(key, value, ttl);
        return value;
    }
}
